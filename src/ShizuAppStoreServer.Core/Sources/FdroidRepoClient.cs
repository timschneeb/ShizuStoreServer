using System.Collections.Concurrent;
using System.Net;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Fetches F-Droid-compatible repo indexes (<c>{base}/index.xml</c>) with
/// conditional GET. Thin over <see cref="HttpClient"/> so tests can stub
/// the handler; parsing + caching live in <see cref="FdroidIndexParser"/>
/// and <see cref="FdroidIndexProvider"/>.
/// </summary>
public sealed class FdroidRepoClient(HttpClient http)
{
    /// <returns>Index ETag + raw XML, or null on <c>304 Not Modified</c>.</returns>
    /// <exception cref="HttpRequestException">Non-success status (unknown repo, …).</exception>
    public async Task<(string? Etag, byte[] Xml)?> GetIndexAsync(
        string repoBase, string? etag, CancellationToken ct = default)
    {
        var url = $"{repoBase.TrimEnd('/')}/index.xml";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.ApplyIfNoneMatch(etag);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"F-Droid index {url} answered HTTP {(int)response.StatusCode}.");
        }

        return (response.Headers.ETag?.ToString(), await response.Content.ReadAsByteArrayAsync(ct));
    }

    /// <returns>Index-v2 ETag + raw JSON, or null on <c>304 Not Modified</c>.</returns>
    /// <exception cref="HttpRequestException">Non-success status (unknown repo, …).</exception>
    public async Task<(string? Etag, byte[] Json)?> GetIndexV2Async(
        string repoBase, string? etag, CancellationToken ct = default)
    {
        var url = $"{repoBase.TrimEnd('/')}/index-v2.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.ApplyIfNoneMatch(etag);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"F-Droid index-v2 {url} answered HTTP {(int)response.StatusCode}.");
        }

        return (response.Headers.ETag?.ToString(), await response.Content.ReadAsByteArrayAsync(ct));
    }
}

/// <summary>
/// Shared cache over repo indexes, keyed by repo base. Registered as a
/// singleton: the M6 loop enriches apps in parallel per-app scopes, so the
/// cache must outlive any one scope (previously it was scope-lived with one
/// fetch per repo per scope).
/// </summary>
/// <remarks>
/// Every call revalidates with a conditional GET (cached ETag, else the
/// app's stored ETag as seed): indexes are small and 304s are cheap, and
/// this keeps passes correct no matter how long the process lives. One
/// in-flight fetch per repo (gated) so parallel enrichments don't stampede.
/// Only two repos exist in practice (F-Droid + Izzy), so the cache is bounded.
/// </remarks>
public sealed class FdroidIndexProvider(FdroidRepoClient client)
{
    private sealed record CachedIndex(string? Etag, IReadOnlyDictionary<string, IReadOnlyList<FdroidPackageInfo>> Packages);

    private sealed record CachedScreenshots(string? Etag, IReadOnlyDictionary<string, IReadOnlyList<string>> Packages);

    private readonly ConcurrentDictionary<string, CachedIndex> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedScreenshots> _screenshotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _screenshotGates = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>
    /// Package entry (null when absent from the index) + index ETag, or
    /// null when the index answered 304 with nothing cached, the index is
    /// then unchanged since the app's last enrich, so the caller treats the
    /// app as up-to-date.
    /// </returns>
    public async Task<(FdroidPackageInfo? Package, string? IndexEtag)?> GetPackageAsync(
        string repoBase, string packageId, string? seedEtag, CancellationToken ct = default)
    {
        var cached = await LoadIndexAsync(repoBase, seedEtag, ct);
        if (cached is null)
        {
            return null;
        }

        cached.Packages.TryGetValue(packageId, out var packages);
        return (packages?.FirstOrDefault(), cached.Etag);
    }

    /// <returns>
    /// Every package entry for the app (document order, newest versionCode
    /// first; empty when absent) + index ETag, or null when the index
    /// answered 304 with nothing cached.
    /// </returns>
    public async Task<(IReadOnlyList<FdroidPackageInfo> Packages, string? IndexEtag)?> GetPackagesAsync(
        string repoBase, string packageId, string? seedEtag, CancellationToken ct = default)
    {
        var cached = await LoadIndexAsync(repoBase, seedEtag, ct);
        if (cached is null)
        {
            return null;
        }

        cached.Packages.TryGetValue(packageId, out var packages);
        return (packages ?? [], cached.Etag);
    }

    /// <summary>
    /// Finds the package whose upstream <c>&lt;source&gt;</c> matches the given
    /// forge repo key (see <see cref="SourceClassifier.RepoKey"/>). Used for
    /// the F-Droid fallback of apps whose forge publishes no APK. Null when no
    /// package matches (or the index is unchanged since the seed).
    /// </summary>
    public async Task<FdroidPackageInfo?> FindPackageBySourceAsync(
        string repoBase, string repoKey, CancellationToken ct = default)
    {
        var cached = await LoadIndexAsync(repoBase, seedEtag: null, ct);
        return cached?.Packages.Values
            .SelectMany(packages => packages)
            .FirstOrDefault(p => SourceClassifier.RepoKey(p.SourceUrl) == repoKey);
    }

    /// <summary>
    /// Whole-repo package map for the fast-path release poll: one
    /// conditional fetch per repo per pass instead of one per app. Null
    /// only when the index answered 304 with nothing cached yet.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FdroidPackageInfo>?> GetAllPackagesAsync(
        string repoBase, CancellationToken ct = default)
    {
        var cached = await LoadIndexAsync(repoBase, seedEtag: null, ct);
        return cached?.Packages.ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.Ordinal);
    }

    private async Task<CachedIndex?> LoadIndexAsync(string repoBase, string? seedEtag, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(repoBase, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            _cache.TryGetValue(repoBase, out var cached);
            var fetched = await client.GetIndexAsync(repoBase, cached?.Etag ?? seedEtag, ct);
            if (fetched is not null)
            {
                using var xml = new MemoryStream(fetched.Value.Xml);
                cached = new CachedIndex(
                    fetched.Value.Etag,
                    FdroidIndexParser.Parse(xml));
                _cache[repoBase] = cached;
            }

            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Package id to screenshot paths for one repo, from the cached
    /// <c>index-v2.json</c>. Null only when the index answered 304 with
    /// nothing cached yet, so the caller treats screenshots as unavailable.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> GetScreenshotsAsync(
        string repoBase, CancellationToken ct = default)
    {
        var gate = _screenshotGates.GetOrAdd(repoBase, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            _screenshotCache.TryGetValue(repoBase, out var cached);
            var fetched = await client.GetIndexV2Async(repoBase, cached?.Etag, ct);
            if (fetched is not null)
            {
                cached = new CachedScreenshots(
                    fetched.Value.Etag,
                    FdroidIndexV2Parser.ParseScreenshots(fetched.Value.Json));
                _screenshotCache[repoBase] = cached;
            }

            return cached?.Packages;
        }
        finally
        {
            gate.Release();
        }
    }
}
