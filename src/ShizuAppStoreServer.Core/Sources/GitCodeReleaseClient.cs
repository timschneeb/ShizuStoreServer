using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>One release asset from the GitCode API.</summary>
public sealed record GitCodeAsset(string Name, string Url);

/// <summary>Latest release of a GitCode mirror (GitHub-shaped payload).</summary>
public sealed record GitCodeRelease(
    string TagName,
    string? Etag,
    IReadOnlyList<GitCodeAsset> Assets);

/// <summary>Non-success response from the GitCode API.</summary>
public sealed class GitCodeApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public interface IGitCodeReleaseClient
{
    /// <returns>Newest release (releases arrive newest-first), or null on 304 Not Modified.</returns>
    /// <exception cref="GitCodeApiException">Non-success response or an empty release list.</exception>
    Task<GitCodeRelease?> GetLatestReleaseAsync(
        string owner, string repo, string? etag = null, CancellationToken ct = default);
}

/// <summary>
/// Minimal GitCode releases client for mirrors that host a project's APK
/// builds (hlbmerge_flutter publishes only under
/// gitcode.com/bigmolihuan/hlbmerge_flutter). The v5 API is GitHub-shaped
/// (<c>tag_name</c>, <c>assets[].browser_download_url</c>) but assets carry
/// no sizes, so selection relies on input order. Download URLs (GET, after a
/// WAF redirect) serve anonymously; HEAD is rejected.
/// </summary>
public sealed class GitCodeReleaseClient : IGitCodeReleaseClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;

    public GitCodeReleaseClient(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShizuAppStoreServer", "1.0"));
        }
    }

    public async Task<GitCodeRelease?> GetLatestReleaseAsync(
        string owner, string repo, string? etag = null, CancellationToken ct = default)
    {
        var url = $"https://api.gitcode.com/api/v5/repos/{Uri.EscapeDataString(owner)}"
            + $"/{Uri.EscapeDataString(repo)}/releases?per_page=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.ApplyIfNoneMatch(etag);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitCodeApiException(response.StatusCode,
                $"GitCode API {(int)response.StatusCode} for {owner}/{repo}.");
        }

        var releases = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(
            await response.Content.ReadAsStreamAsync(ct), Json, ct)
            ?? throw new GitCodeApiException(response.StatusCode,
                $"GitCode API returned no JSON for {owner}/{repo}.");

        var latest = releases.FirstOrDefault();
        if (latest is null)
        {
            throw new GitCodeApiException(HttpStatusCode.NotFound, $"No release found for {owner}/{repo}.");
        }

        var assets = latest.Assets
            .Where(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .Select(a => new GitCodeAsset(a.Name, a.BrowserDownloadUrl))
            .ToList();

        return new GitCodeRelease(latest.TagName, response.Headers.ETag?.ToString(), assets);
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<AssetDto> Assets { get; set; } = [];
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
