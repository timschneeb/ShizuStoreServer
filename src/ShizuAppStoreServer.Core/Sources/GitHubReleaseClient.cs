using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>Non-success response from the GitHub API (4xx/5xx, e.g. 404 unknown repo, 403 rate limit).</summary>
public sealed class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Subset of <c>GET /repos/{owner}/{repo}</c> used by enrichment: popularity
/// (stars) plus the owner profile for developer details.
/// </summary>
public sealed record GitHubRepoStats(
    int? Stars,
    string? OwnerLogin,
    string? OwnerUrl);

/// <summary>
/// One Sunday week of stars GAINED from the stargazers/history feed; the
/// totals sum to the repo's stargazer count. Days holds the per-day gains
/// Sunday-first (empty when the feed omits the breakdown).
/// </summary>
public sealed record GitHubStarWeek(DateOnly WeekStart, int Total, IReadOnlyList<int>? Days = null);

public interface IGitHubReleaseClient : IAppSource
{
    /// <summary>
    /// Repo metadata via <c>GET /repos/{owner}/{repo}</c>. Null on any
    /// failure; popularity and developer details are best-effort and never
    /// fail an enrich. Default impl keeps test doubles that only care about
    /// releases simple.
    /// </summary>
    Task<GitHubRepoStats?> GetRepoStatsAsync(string owner, string repo, CancellationToken ct = default) =>
        Task.FromResult<GitHubRepoStats?>(null);

    /// <summary>
    /// Weekly star gains (stars earned that week, not a running total) via
    /// <c>GET /repos/{owner}/{repo}/stargazers/history</c>
    /// (the feed behind GitHub's stargazers page), walking pages newest-first
    /// until the lookback is covered or <c>maxPages</c> pages were read (a
    /// caller refreshing only the newest weeks passes 1). Each week carries
    /// the per-day breakdown so callers can reconstruct daily levels. Null on
    /// any failure so a backfill gap never fails an enrich; the daily snapshot
    /// writer still records progress. Default impl keeps test doubles simple.
    /// </summary>
    Task<IReadOnlyList<GitHubStarWeek>?> GetStarHistoryAsync(
        string owner, string repo, int lookbackDays, int maxPages = int.MaxValue, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GitHubStarWeek>?>(null);

    /// <summary>
    /// README markdown via <c>GET /repos/{owner}/{repo}/readme</c>, plus the
    /// raw.githubusercontent.com URL the client can refetch live. The client
    /// renders markdown, so the server ships the source, not GitHub's rendered
    /// HTML. Null on any failure. Default impl keeps test doubles simple.
    /// </summary>
    Task<ReadmeDocument?> GetReadmeMarkdownAsync(string owner, string repo, CancellationToken ct = default) =>
        Task.FromResult<ReadmeDocument?>(null);

    /// <summary>
    /// Markdown of a README the list entry links directly (localized
    /// <c>README_EN.md</c> on projects whose landing README is non-English),
    /// plus its raw URL. Null on any failure. Default impl keeps test doubles
    /// simple.
    /// </summary>
    Task<ReadmeDocument?> GetLinkedMarkdownAsync(string url, CancellationToken ct = default) =>
        Task.FromResult<ReadmeDocument?>(null);

    /// <summary>
    /// Every non-draft release, newest first, each with its own assets. Used
    /// only for repos that ship several distinct apps, one release per app.
    /// Empty by default so test doubles that only serve the latest release
    /// keep compiling.
    /// </summary>
    Task<IReadOnlyList<SourceRelease>> GetAllReleasesAsync(SourceTarget target, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceRelease>>([]);

    /// <summary>
    /// Recursive blob listing of the default branch via
    /// <c>GET /repos/{owner}/{repo}/git/trees/HEAD?recursive=1</c>. Null on any
    /// failure. Default impl keeps test doubles simple.
    /// </summary>
    Task<RepoTree?> GetRepoTreeAsync(string owner, string repo, CancellationToken ct = default) =>
        Task.FromResult<RepoTree?>(null);

    /// <summary>
    /// Raw content of one blob by its tree SHA via
    /// <c>GET /repos/{owner}/{repo}/git/blobs/{sha}</c>. Null on any failure.
    /// Default impl keeps test doubles simple.
    /// </summary>
    Task<string?> GetRawBlobAsync(string owner, string repo, string blobSha, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Minimal GitHub Releases client over <see cref="HttpClient"/> +
/// <c>System.Text.Json</c>; deliberately <i>not</i> Octokit: the plan
/// requires resolver tests with a stubbed <c>HttpClient</c> (no network),
/// which a hand-rolled client supports directly with zero extra deps.
/// PAT (optional, raises rate limits) comes from <c>SHIZU_GITHUB_TOKEN</c>.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    // Stable releases are so rare for these repos that users are better served
    // by the newest prerelease: a prerelease-only repo would otherwise sit on a
    // months-old build between tagged releases.
    private static readonly HashSet<string> PrereleasePreferredRepos =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Jman-Github/Universal-ReVanced-Manager",
        };

    // stargazers/history caps per_page at 30 and page at 100; four pages
    // (120 weeks) is more than any lookback needs while bounding request cost.
    private const int StarHistoryPageSize = 30;
    private const int StarHistoryMaxPages = 4;

    private readonly HttpClient _http;

    public GitHubReleaseClient(HttpClient http, string? token = null)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShizuAppStoreServer", "1.0"));
        }

        if (!_http.DefaultRequestHeaders.Accept.Any())
        {
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        if (token is not null && _http.DefaultRequestHeaders.Authorization is null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<SourceRelease?> GetLatestReleaseAsync(
        SourceTarget target, string? etag, CancellationToken ct = default)
    {
        var (owner, repo) = target.SplitRepoKey();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.ApplyIfNoneMatch(etag);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodySafe(response, ct);
            throw new GitHubApiException(response.StatusCode,
                $"GitHub API {(int)response.StatusCode} for {owner}/{repo}: {body}");
        }

        var releases = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(
            await response.Content.ReadAsStreamAsync(ct), Json, ct)
            ?? throw new GitHubApiException(response.StatusCode, $"GitHub API returned no JSON for {owner}/{repo}.");

        // GitHub returns releases newest-first. Prefer the newest stable
        // release that actually ships an installable artifact: repos with
        // automatic prereleases would otherwise keep users (and the AI
        // pipeline) on a release-per-commit treadmill. A repo without a
        // servable stable on the first page falls back to its newest servable
        // prerelease, and a release without assets is only used as a last
        // resort so the existing index/Play fallbacks still run. Repos in
        // PrereleasePreferredRepos flip the first two tiers.
        var preferPrerelease = PrereleasePreferredRepos.Contains($"{owner}/{repo}");
        var candidates = releases
            .Where(r => !r.Draft)
            .Select(r => (Release: r, Assets: r.Assets
                .Select(a => new SourceAsset(
                    a.Name, a.BrowserDownloadUrl, Size: a.Size,
                    Sha256: NormalizeDigest(a.Digest), ReleasedAt: r.PublishedAt))
                .ToList()))
            .ToList();
        static bool Servable(IReadOnlyList<SourceAsset> assets) =>
            ApkAssetSelector.PickApk(assets) is not null || ApkAssetSelector.PickZip(assets) is not null;
        var picked = preferPrerelease
            ? candidates.FirstOrDefault(c => c.Release.Prerelease && Servable(c.Assets))
            : candidates.FirstOrDefault(c => !c.Release.Prerelease && Servable(c.Assets));
        if (picked.Release is null)
        {
            picked = candidates.FirstOrDefault(c => Servable(c.Assets));
        }

        if (picked.Release is null)
        {
            picked = preferPrerelease
                ? candidates.FirstOrDefault(c => c.Release.Prerelease)
                : candidates.FirstOrDefault(c => !c.Release.Prerelease);
        }

        if (picked.Release is null)
        {
            picked = candidates.FirstOrDefault();
        }

        if (picked.Release is null)
        {
            throw new GitHubApiException(HttpStatusCode.NotFound, $"No release found for {owner}/{repo}.");
        }

        // The picked release can be older than the newest release of its own
        // channel when the newer ones ship no installable artifact (for
        // example after binaries moved to F-Droid); callers prefer an
        // alternative source instead of serving that stale build.
        var newestInChannel = candidates.FirstOrDefault(c => c.Release.Prerelease == picked.Release.Prerelease);
        var isOlderFallback = newestInChannel.Release is not null
            && !ReferenceEquals(newestInChannel.Release, picked.Release)
            && !Servable(newestInChannel.Assets);

        var responseEtag = response.Headers.ETag?.ToString();
        var totalDownloads = releases
            .Where(r => !r.Draft)
            .Sum(r => r.Assets.Sum(a => a.DownloadCount));
        return new SourceRelease(
            picked.Release.TagName,
            picked.Release.PublishedAt,
            responseEtag,
            ApkAssetSelector.MarkPrimary(picked.Assets),
            totalDownloads,
            picked.Release.Body,
            picked.Release.HtmlUrl,
            picked.Release.Prerelease,
            isOlderFallback);
    }

    public async Task<IReadOnlyList<SourceRelease>> GetAllReleasesAsync(
        SourceTarget target, CancellationToken ct = default)
    {
        var (owner, repo) = target.SplitRepoKey();
        var result = new List<SourceRelease>();
        const int pageSize = 100;
        // Repos with one release per app stay well under this; the cap stops a
        // pathological history from paging forever.
        const int maxPages = 5;

        for (var page = 1; page <= maxPages; page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={pageSize}&page={page}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodySafe(response, ct);
                throw new GitHubApiException(response.StatusCode,
                    $"GitHub API {(int)response.StatusCode} for {owner}/{repo}: {body}");
            }

            var releases = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(
                await response.Content.ReadAsStreamAsync(ct), Json, ct)
                ?? [];

            foreach (var release in releases.Where(r => !r.Draft))
            {
                var assets = release.Assets
                    .Select(a => new SourceAsset(
                        a.Name, a.BrowserDownloadUrl, Size: a.Size,
                        Sha256: NormalizeDigest(a.Digest), ReleasedAt: release.PublishedAt))
                    .ToList();
                result.Add(new SourceRelease(release.TagName, release.PublishedAt, null, assets, Changelog: release.Body, WebUrl: release.HtmlUrl, IsPrerelease: release.Prerelease));
            }

            if (releases.Count < pageSize)
            {
                break;
            }
        }

        return result;
    }

    public async Task<GitHubRepoStats?> GetRepoStatsAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? stars = root.TryGetProperty("stargazers_count", out var starsElement)
                && starsElement.TryGetInt32(out var starsValue)
                    ? starsValue
                    : null;

            string? ownerLogin = null;
            string? ownerUrl = null;
            if (root.TryGetProperty("owner", out var ownerElement)
                && ownerElement.ValueKind == JsonValueKind.Object)
            {
                ownerLogin = ReadString(ownerElement, "login");
                ownerUrl = ReadString(ownerElement, "html_url");
            }

            return new GitHubRepoStats(
                stars,
                ownerLogin,
                ownerUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    public async Task<IReadOnlyList<GitHubStarWeek>?> GetStarHistoryAsync(
        string owner, string repo, int lookbackDays, int maxPages = int.MaxValue, CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-lookbackDays);
            var weeks = new List<GitHubStarWeek>();

            // The feed pages newest-first, so reaching the cutoff usually
            // takes two pages at the 30-per-page cap; the page caps bound the
            // worst case no matter how the feed behaves.
            for (var page = 1; page <= StarHistoryMaxPages && page <= maxPages; page++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/repos/{owner}/{repo}/stargazers/history" +
                    $"?per_page={StarHistoryPageSize}&page={page}");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return weeks.Count > 0 ? weeks : null;
                }

                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    return weeks.Count > 0 ? weeks : null;
                }

                var entries = 0;
                foreach (var entry in root.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("week", out var weekElement)
                        || !weekElement.TryGetInt64(out var weekSeconds)
                        || !entry.TryGetProperty("total", out var totalElement)
                        || !totalElement.TryGetInt32(out var total))
                    {
                        continue;
                    }

                    entries++;
                    var days = new List<int>(7);
                    if (entry.TryGetProperty("days", out var daysElement)
                        && daysElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var day in daysElement.EnumerateArray())
                        {
                            if (day.TryGetInt32(out var gain))
                            {
                                days.Add(gain);
                            }
                        }
                    }

                    weeks.Add(new GitHubStarWeek(
                        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(weekSeconds).UtcDateTime),
                        total,
                        days.Count > 0 ? days : null));
                }

                if (entries < StarHistoryPageSize)
                {
                    // Short page: nothing older exists.
                    break;
                }

                var oldest = weeks.Min(w => w.WeekStart);
                if (oldest <= cutoff)
                {
                    break;
                }
            }

            return weeks.Count > 0 ? weeks : null;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    public async Task<ReadmeDocument?> GetReadmeMarkdownAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/readme");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), default, ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || ReadString(document.RootElement, "path") is not { Length: > 0 } path)
            {
                return null;
            }

            // HEAD keeps the link valid across default-branch renames.
            var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/HEAD/{path}";
            if (ReadString(document.RootElement, "encoding") == "base64"
                && ReadString(document.RootElement, "content") is { Length: > 0 } content)
            {
                var bytes = Convert.FromBase64String(content.Replace("\n", string.Empty, StringComparison.Ordinal));
                return new ReadmeDocument(System.Text.Encoding.UTF8.GetString(bytes), rawUrl);
            }

            // Files above the API's inline limit report no base64 content; the
            // download_url still serves the raw bytes.
            if (ReadString(document.RootElement, "download_url") is not { Length: > 0 } downloadUrl)
            {
                return null;
            }

            using var raw = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!raw.IsSuccessStatusCode)
            {
                return null;
            }

            return new ReadmeDocument(await raw.Content.ReadAsStringAsync(ct), rawUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or FormatException
            or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    public Task<ReadmeDocument?> GetLinkedMarkdownAsync(string url, CancellationToken ct = default) =>
        ReadmeLink.FetchAsync(_http, url, ct);

    public async Task<RepoTree?> GetRepoTreeAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/git/trees/HEAD?recursive=1");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), default, ct);
            if (!document.RootElement.TryGetProperty("tree", out var tree)
                || tree.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<RepoTreeEntry>();
            foreach (var item in tree.EnumerateArray())
            {
                if (ReadString(item, "type") != "blob" || ReadString(item, "path") is not { Length: > 0 } path)
                {
                    continue;
                }

                entries.Add(new RepoTreeEntry(
                    path,
                    ReadString(item, "sha"),
                    item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                        ? size.GetInt64()
                        : null));
            }

            var truncated = document.RootElement.TryGetProperty("truncated", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            return new RepoTree(entries, truncated);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    public async Task<string?> GetRawBlobAsync(string owner, string repo, string blobSha, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/git/blobs/{blobSha}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    /// <summary>
    /// GitHub reports an asset checksum as <c>sha256:&lt;hex&gt;</c> (null for
    /// assets uploaded before the field existed). Normalize to bare lowercase
    /// hex so it compares directly with <c>AppDownload.Sha256</c>; any other
    /// algorithm is ignored.
    /// </summary>
    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 0 ? null : hex.ToLowerInvariant();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> ReadBodySafe(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 300 ? body[..300] + "…" : body;
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetDto> Assets { get; set; } = [];
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("content_type")]
        public string? ContentType { get; set; }

        [JsonPropertyName("download_count")]
        public long DownloadCount { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
