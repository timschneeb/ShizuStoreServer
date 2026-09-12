using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>One release asset from the GitHub Releases API.</summary>
public sealed record GitHubAsset(string Name, string BrowserDownloadUrl, long Size, string? ContentType);

/// <summary>
/// Latest release of a repo. <c>Etag</c> is the response ETag, stored
/// on the app row for conditional requests on the next pass.
/// </summary>
public sealed record GitHubRelease(
    string TagName,
    DateTimeOffset? PublishedAt,
    string? Etag,
    IReadOnlyList<GitHubAsset> Assets);

/// <summary>Non-success response from the GitHub API (4xx/5xx, e.g. 404 unknown repo, 403 rate limit).</summary>
public sealed class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public interface IGitHubReleaseClient
{
    /// <returns>
    /// Newest non-draft release, or null when the server answered
    /// <c>304 Not Modified</c> for <paramref name="etag"/>.
    /// Prereleases count: many Shizuku apps ship only prereleases.
    /// </returns>
    /// <exception cref="GitHubApiException">Repo has no published release yet (404 on the list endpoint never happens — empty list), unknown repo, rate limit, …</exception>
    Task<GitHubRelease?> GetLatestReleaseAsync(
        string owner, string repo, string? etag, CancellationToken ct = default);
}

/// <summary>
/// Minimal GitHub Releases client over <see cref="HttpClient"/> +
/// <c>System.Text.Json</c> — deliberately <i>not</i> Octokit: the plan
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

    public async Task<GitHubRelease?> GetLatestReleaseAsync(
        string owner, string repo, string? etag, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (etag is not null)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        }

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
        return new GitHubRelease(
            release.TagName,
            release.PublishedAt,
            responseEtag,
            release.Assets.Select(a => new GitHubAsset(a.Name, a.BrowserDownloadUrl, a.Size, a.ContentType)).ToList());
    }

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
    }
}
