using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Fetches F-Droid-compatible repo indexes (<c>{base}/index.xml</c>) with
/// conditional GET. Thin over <see cref="HttpClient"/> so tests can stub
/// the handler; parsing + caching live in <see cref="FdroidIndexParser"/>
/// and <see cref="FdroidIndexProvider"/>.
/// </summary>
public sealed class FdroidRepoClient(HttpClient http, IRunLog? runLog = null)
{
    private readonly IRunLog _runLog = runLog ?? NullRunLog.Instance;

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // The f-droid.org A set mixes fast mirrors with a throttled CDN node, and
    // the IPv4 connect callback takes the first address that accepts a socket.
    // Retrying with a fresh connection re-resolves the set, so one slow node
    // cannot pin a whole pass; only the final attempt is unbounded.
    private const int MaxAttempts = 4;
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(15);

    /// <returns>Index ETag + raw XML, or null on <c>304 Not Modified</c>.</returns>
    /// <exception cref="HttpRequestException">Non-success status (unknown repo, …).</exception>
    public Task<(string? Etag, byte[] Xml)?> GetIndexAsync(
        string repoBase, string? etag, CancellationToken ct = default) =>
        FetchAsync(repoBase, "index.xml", "fdroid index", "F-Droid index", etag, ct);

    /// <returns>Index-v2 ETag + raw JSON, or null on <c>304 Not Modified</c>.</returns>
    /// <exception cref="HttpRequestException">Non-success status (unknown repo, …).</exception>
    public Task<(string? Etag, byte[] Json)?> GetIndexV2Async(
        string repoBase, string? etag, CancellationToken ct = default) =>
        FetchAsync(repoBase, "index-v2.json", "fdroid index-v2", "F-Droid index-v2", etag, ct);

    /// <summary>
    /// Fetches through the primary base and, when that base refuses the host
    /// (IzzyOnDroid blocks datacenter IPs), once through its configured
    /// fallback mirror.
    /// </summary>
    private async Task<(string? Etag, byte[] Bytes)?> FetchAsync(
        string repoBase, string file, string label, string errorPrefix, string? etag,
        CancellationToken ct)
    {
        try
        {
            return await FetchFromBaseAsync(repoBase, file, label, errorPrefix, etag, ct);
        }
        catch (HttpRequestException ex)
        {
            var fallback = FdroidRepos.FallbackFor(repoBase);
            if (fallback is null || string.Equals(fallback, repoBase, StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            _runLog.Detail($"{label} falling back to {fallback}: {ex.Message}");
            return await FetchFromBaseAsync(fallback, file, label, errorPrefix, etag, ct);
        }
    }

    private async Task<(string? Etag, byte[] Bytes)?> FetchFromBaseAsync(
        string repoBase, string file, string label, string errorPrefix, string? etag,
        CancellationToken ct)
    {
        var url = $"{repoBase.TrimEnd('/')}/{file}";
        var started = Stopwatch.GetTimestamp();
        _runLog.Detail($"{label} start {url}");
        for (var attempt = 1; ; attempt++)
        {
            var last = attempt == MaxAttempts;
            using var attemptCts = last
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (attemptCts is not null)
            {
                attemptCts.CancelAfter(AttemptBudget);
            }

            var attemptToken = attemptCts?.Token ?? ct;
            var attemptStarted = Stopwatch.GetTimestamp();
            var statusCode = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.ApplyIfNoneMatch(etag);
                // A pooled connection could still point at the slow node, so
                // every attempt opens a fresh one and resolves DNS again.
                request.Headers.ConnectionClose = true;
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, attemptToken);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    _runLog.Detail($"{label} {url} 304 in {Elapsed(started)}ms");
                    return null;
                }

                statusCode = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(attemptToken);
                    _runLog.Detail($"{label} done {bytes.Length}B in {Elapsed(started)}ms");
                    return (response.Headers.ETag?.ToString(), bytes);
                }
            }
            catch (Exception ex) when (!last
                && ex is (OperationCanceledException or IOException or HttpRequestException))
            {
                _runLog.Detail($"{label} {url} attempt {attempt}/{MaxAttempts} gave up after "
                    + $"{Elapsed(attemptStarted)}ms, retrying");
                continue;
            }

            // Non-success status: fail fast, a retry cannot fix a 404.
            _runLog.Detail($"{label} {url} HTTP {statusCode} after {Elapsed(started)}ms");
            throw new HttpRequestException($"{errorPrefix} {url} answered HTTP {statusCode}.");
        }
    }
}

/// <summary>
/// Shared cache over repo indexes, keyed by repo base. Registered as a
/// singleton: the M6 loop enriches apps in parallel per-app scopes, so the
/// cache must outlive any one scope (previously it was scope-lived with one
/// fetch per repo per scope).
/// </summary>
/// <remarks>
/// At most one fetch per repo per pass: <c>BeginRun</c> (called by the sync
/// engine at pass start) clears the run memo, the first caller fetches with
/// a conditional GET, and every later caller in the same pass reads the
/// cached index. A failed fetch is remembered too, so a refusing host is
/// attempted once instead of once per app. One in-flight fetch per repo
/// (gated) so parallel enrichments don't stampede. Only two repos exist in
/// practice (F-Droid + Izzy), so the cache is bounded.
/// </remarks>
public sealed class FdroidIndexProvider(FdroidRepoClient client)
{
    private sealed record CachedIndex(string? Etag, IReadOnlyDictionary<string, IReadOnlyList<FdroidPackageInfo>> Packages);

    private sealed record CachedScreenshots(string? Etag, IReadOnlyDictionary<string, IReadOnlyList<string>> Packages);

    private readonly ConcurrentDictionary<string, CachedIndex> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedScreenshots> _screenshotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _screenshotGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _validatedIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _validatedScreenshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a new pass: the next index read may fetch once per repo, after
    /// that this run's memo answers from cache.
    /// </summary>
    public void BeginRun()
    {
        _validatedIndex.Clear();
        _validatedScreenshots.Clear();
    }

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
    /// <param name="force">
    /// Bypasses the once-per-run memo; the permission-heal refetch needs an
    /// unconditional read even after the seed read answered 304.
    /// </param>
    public async Task<(IReadOnlyList<FdroidPackageInfo> Packages, string? IndexEtag)?> GetPackagesAsync(
        string repoBase, string packageId, string? seedEtag, CancellationToken ct = default,
        bool force = false)
    {
        var cached = await LoadIndexAsync(repoBase, seedEtag, ct, force);
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

    private async Task<CachedIndex?> LoadIndexAsync(
        string repoBase, string? seedEtag, CancellationToken ct, bool force = false)
    {
        var gate = _gates.GetOrAdd(repoBase, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            _cache.TryGetValue(repoBase, out var cached);
            if (!force && _validatedIndex.ContainsKey(repoBase))
            {
                return cached;
            }

            // Mark before the fetch: a failing repo must not be retried for
            // every app in the pass.
            _validatedIndex[repoBase] = 0;
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
            if (_validatedScreenshots.ContainsKey(repoBase))
            {
                return cached?.Packages;
            }

            _validatedScreenshots[repoBase] = 0;
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
