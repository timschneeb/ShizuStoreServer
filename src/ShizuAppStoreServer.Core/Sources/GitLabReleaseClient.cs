using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>Non-success response from the GitLab API (404 unknown project, 403 releases disabled, …).</summary>
public sealed class GitLabApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Subset of <c>GET /projects/{urlencoded-path}</c> used by enrichment:
/// popularity (star count).
/// </summary>
public sealed record GitLabProjectStats(int? Stars);

public interface IGitLabReleaseClient : IAppSource
{
    /// <summary>
    /// Project metadata via <c>GET /projects/{urlencoded-path}</c>. Null on
    /// any failure; popularity is best-effort and never fails an enrich.
    /// Default impl keeps test doubles that only care about releases simple.
    /// </summary>
    Task<GitLabProjectStats?> GetProjectStatsAsync(string projectPath, CancellationToken ct = default) =>
        Task.FromResult<GitLabProjectStats?>(null);

    /// <summary>
    /// Raw README markdown: the project's <c>readme_url</c> resolves to a
    /// repository file fetched through <c>/repository/files/…/raw</c>. Null
    /// on any failure (including no README). Default impl keeps test
    /// doubles simple.
    /// </summary>
    Task<string?> GetReadmeMarkdownAsync(string projectPath, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Minimal GitLab Releases client over <see cref="HttpClient"/> +
/// <c>System.Text.Json</c>, same hand-rolled shape as
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

    // Markdown link: [name](url), release descriptions commonly carry the
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

    public async Task<SourceRelease?> GetLatestReleaseAsync(
        SourceTarget target, string? etag, CancellationToken ct = default)
    {
        var projectPath = target.Key;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(projectPath)}/releases?per_page=100");
        request.ApplyIfNoneMatch(etag);

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
        var assets = new List<SourceAsset>();
        foreach (var link in latest.Assets.Links)
        {
            var url = link.DirectAssetUrl ?? link.Url;
            if (!string.IsNullOrWhiteSpace(url))
            {
                assets.Add(new SourceAsset(link.Name, url));
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
                assets.Add(new SourceAsset(
                    string.IsNullOrWhiteSpace(name) ? FileNameOf(resolved) : name,
                    resolved));
            }
        }

        return new SourceRelease(
            latest.TagName,
            latest.ReleasedAt,
            responseEtag,
            ApkAssetSelector.MarkPrimary(assets));
    }

    public async Task<GitLabProjectStats?> GetProjectStatsAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(projectPath)}",
                HttpCompletionOption.ResponseHeadersRead, ct);
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

            int? stars = root.TryGetProperty("star_count", out var starsElement)
                && starsElement.TryGetInt32(out var starsValue)
                    ? starsValue
                    : null;
            return new GitLabProjectStats(stars);
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

    public async Task<string?> GetReadmeMarkdownAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            var project = Uri.EscapeDataString(projectPath);
            using var meta = await _http.GetAsync(
                $"https://gitlab.com/api/v4/projects/{project}",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!meta.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await meta.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryReadString(document.RootElement, "readme_url", out var readmeUrl)
                || string.IsNullOrEmpty(readmeUrl)
                || !TryReadString(document.RootElement, "default_branch", out var branch)
                || string.IsNullOrEmpty(branch))
            {
                return null;
            }

            // readme_url is .../-/blob/{branch}/{path}; the default branch
            // disambiguates slash-bearing branch names. Absent without a
            // match (renamed branch), not worth guessing.
            var prefix = $"/-/blob/{branch}/";
            var slash = readmeUrl.IndexOf(prefix, StringComparison.Ordinal);
            if (slash < 0)
            {
                return null;
            }

            var filePath = readmeUrl[(slash + prefix.Length)..];
            if (filePath.Length == 0)
            {
                return null;
            }

            using var raw = await _http.GetAsync(
                $"https://gitlab.com/api/v4/projects/{project}/repository/files/{Uri.EscapeDataString(filePath)}/raw?ref={Uri.EscapeDataString(branch)}",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!raw.IsSuccessStatusCode)
            {
                return null;
            }

            return await raw.Content.ReadAsStringAsync(ct);
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

    private static bool TryReadString(JsonElement element, string name, out string? value)
    {
        value = element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        return value is not null;
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
