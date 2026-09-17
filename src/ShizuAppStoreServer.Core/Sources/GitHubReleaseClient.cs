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
    /// Raw README markdown via <c>GET /repos/{owner}/{repo}/readme</c>
    /// (<c>application/vnd.github.raw</c>). The client renders markdown, so the
    /// server ships the source, not GitHub's rendered HTML. Null on any failure.
    /// Default impl keeps test doubles simple.
    /// </summary>
    Task<string?> GetReadmeMarkdownAsync(string owner, string repo, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Markdown of a README the list entry links directly (localized
    /// <c>README_EN.md</c> on projects whose landing README is non-English).
    /// Null on any failure. Default impl keeps test doubles simple.
    /// </summary>
    Task<string?> GetLinkedMarkdownAsync(string url, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Every non-draft release, newest first, each with its own assets. Used
    /// only for repos that ship several distinct apps, one release per app.
    /// Empty by default so test doubles that only serve the latest release
    /// keep compiling.
    /// </summary>
    Task<IReadOnlyList<SourceRelease>> GetAllReleasesAsync(SourceTarget target, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceRelease>>([]);
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

        // GitHub returns releases newest-first; drafts are unpublished, so the
        // first non-draft entry is the newest usable release.
        var release = releases.FirstOrDefault(r => !r.Draft);
        if (release is null)
        {
            throw new GitHubApiException(HttpStatusCode.NotFound, $"No release found for {owner}/{repo}.");
        }

        var responseEtag = response.Headers.ETag?.ToString();
        var totalDownloads = releases
            .Where(r => !r.Draft)
            .Sum(r => r.Assets.Sum(a => a.DownloadCount));
        var assets = release.Assets
            .Select(a => new SourceAsset(
                a.Name, a.BrowserDownloadUrl, Size: a.Size,
                Sha256: NormalizeDigest(a.Digest), ReleasedAt: release.PublishedAt))
            .ToList();
        return new SourceRelease(
            release.TagName,
            release.PublishedAt,
            responseEtag,
            ApkAssetSelector.MarkPrimary(assets),
            totalDownloads,
            release.Body,
            release.HtmlUrl);
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
                result.Add(new SourceRelease(release.TagName, release.PublishedAt, null, assets, Changelog: release.Body, WebUrl: release.HtmlUrl));
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

    public async Task<string?> GetReadmeMarkdownAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/readme");
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

    public Task<string?> GetLinkedMarkdownAsync(string url, CancellationToken ct = default) =>
        ReadmeLink.FetchAsync(_http, url, ct);

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
