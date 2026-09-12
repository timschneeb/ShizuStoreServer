using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>One <c>assets.links[]</c> entry from the GitLab Releases API.</summary>
public sealed record GitLabAssetLink(string Name, string Url);

/// <summary>
/// Latest non-upcoming release of a GitLab project. <c>Assets</c> combines
/// <c>assets.links</c> with APK links embedded in the release description
/// (some projects, e.g. AuroraStore, publish only markdown links); relative
/// <c>/uploads/…</c> paths resolve against the project. <c>Etag</c> is the
/// response ETag, stored on the app row for conditional requests (GitLab
/// largely ignores <c>If-None-Match</c>, so the enricher additionally
/// short-circuits on an unchanged asset URL).
/// </summary>
public sealed record GitLabRelease(
    string TagName,
    DateTimeOffset? ReleasedAt,
    string? Etag,
    IReadOnlyList<GitLabAssetLink> Assets);

/// <summary>Non-success response from the GitLab API (404 unknown project, 403 releases disabled, …).</summary>
public sealed class GitLabApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public interface IGitLabReleaseClient
{
    /// <returns>
    /// Newest non-upcoming release, or null when the server answered
    /// <c>304 Not Modified</c> for <paramref name="etag"/>.
    /// </returns>
    /// <exception cref="GitLabApiException">Unknown project, no usable release, …</exception>
    Task<GitLabRelease?> GetLatestReleaseAsync(
        string projectPath, string? etag, CancellationToken ct = default);
}

/// <summary>
/// Minimal GitLab Releases client over <see cref="HttpClient"/> +
/// <c>System.Text.Json</c> — same hand-rolled shape as
/// <see cref="GitHubReleaseClient"/> (stubbed-<c>HttpClient</c> tests, no
/// extra deps). Token (optional, raises rate limits) comes from
/// <c>SHIZU_GITLAB_TOKEN</c> via <c>PRIVATE-TOKEN</c>.
/// </summary>
public sealed class GitLabReleaseClient : IGitLabReleaseClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    // Markdown link: [name](url) — release descriptions commonly carry the
    // APK download links as markdown, not as assets.links entries.
    private static readonly Regex MarkdownLink =
        new(@"\[(?<name>[^\]]*)\]\((?<url>[^)\s]+)\)", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public GitLabReleaseClient(HttpClient http, string? token = null)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShizuAppStoreServer", "1.0"));
        }

        if (token is not null && !_http.DefaultRequestHeaders.Contains("PRIVATE-TOKEN"))
        {
            _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        }
    }

    public async Task<GitLabRelease?> GetLatestReleaseAsync(
        string projectPath, string? etag, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(projectPath)}/releases?per_page=100");
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
            throw new GitLabApiException(response.StatusCode,
                $"GitLab API {(int)response.StatusCode} for {projectPath}: {body}");
        }

        var releases = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(
            await response.Content.ReadAsStreamAsync(ct), Json, ct)
            ?? throw new GitLabApiException(response.StatusCode, $"GitLab API returned no JSON for {projectPath}.");

        var latest = releases.FirstOrDefault(r => !r.UpcomingRelease);
        if (latest is null)
        {
            throw new GitLabApiException(HttpStatusCode.NotFound, $"No release found for {projectPath}.");
        }

        var responseEtag = response.Headers.ETag?.ToString();
        var assets = new List<GitLabAssetLink>();
        foreach (var link in latest.Assets.Links)
        {
            var url = link.DirectAssetUrl ?? link.Url;
            if (!string.IsNullOrWhiteSpace(url))
            {
                assets.Add(new GitLabAssetLink(link.Name, url));
            }
        }

        // Description links append after explicit assets so API links keep
        // tie priority (PickApk keeps input order for equal-size assets).
        if (!string.IsNullOrWhiteSpace(latest.Description))
        {
            var seen = assets.Select(a => a.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in MarkdownLink.Matches(latest.Description))
            {
                var raw = match.Groups["url"].Value;
                if (!raw.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolved = ResolveAssetUrl(projectPath, raw);
                if (resolved is null || !seen.Add(resolved))
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                assets.Add(new GitLabAssetLink(
                    string.IsNullOrWhiteSpace(name) ? FileNameOf(resolved) : name,
                    resolved));
            }
        }

        return new GitLabRelease(
            latest.TagName,
            latest.ReleasedAt,
            responseEtag,
            assets);
    }

    /// <summary>
    /// Expands a description-embedded link into a fetchable URL. GitLab
    /// markdown uses project-relative <c>/uploads/…</c> paths; the web-UI
    /// routes for those either 404 (<c>/{project}</c> form) or redirect
    /// anonymous users to sign-in (<c>/-/project/…</c>), while the API
    /// uploads route serves them anonymously with the URL-encoded project
    /// path (verified against AuroraStore release uploads).
    /// </summary>
    private static string? ResolveAssetUrl(string projectPath, string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var project = Uri.EscapeDataString(projectPath);
        return url.StartsWith('/')
            ? $"https://gitlab.com/api/v4/projects/{project}{url}"
            : $"https://gitlab.com/api/v4/projects/{project}/{url}";
    }

    private static string FileNameOf(string url)
    {
        var path = url.Split('?', '#')[0];
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? Uri.UnescapeDataString(path[(slash + 1)..]) : path;
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

        [JsonPropertyName("upcoming_release")]
        public bool UpcomingRelease { get; set; }

        [JsonPropertyName("released_at")]
        public DateTimeOffset? ReleasedAt { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("assets")]
        public AssetsDto Assets { get; set; } = new();
    }

    private sealed class AssetsDto
    {
        [JsonPropertyName("links")]
        public List<LinkDto> Links { get; set; } = [];
    }

    private sealed class LinkDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("direct_asset_url")]
        public string? DirectAssetUrl { get; set; }
    }
}
