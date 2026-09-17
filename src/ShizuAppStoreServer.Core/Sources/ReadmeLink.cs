namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// List entries occasionally link straight at a markdown README
/// (<c>README_EN.md</c> on projects whose landing README is non-English).
/// Recognizes those links and resolves the forge web route to the raw file,
/// so enrichment can use the linked README instead of the repo default.
/// </summary>
public static class ReadmeLink
{
    /// <summary>True for http(s) URLs whose last path segment is a README markdown file.</summary>
    public static bool IsReadme(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var name = uri.Segments.LastOrDefault()?.Trim('/');
        return name is not null
            && name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            && (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Web README link to raw markdown: GitHub <c>/blob/</c> and <c>/raw/</c>
    /// routes become raw.githubusercontent.com, GitLab <c>/-/blob/</c> becomes
    /// <c>/-/raw/</c>. Other hosts pass through unchanged (the file may already
    /// be raw); null when a forge link does not match a raw route, so a stray
    /// HTML page is never stored as the full description.
    /// </summary>
    public static string? ToRawUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (IsHost(uri.Host, "github.com") || IsHost(uri.Host, "www.github.com"))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4 || (segments[2] != "blob" && segments[2] != "raw"))
            {
                return null;
            }

            return $"https://raw.githubusercontent.com/{segments[0]}/{segments[1]}/{string.Join('/', segments[3..])}";
        }

        if (IsHost(uri.Host, "gitlab.com") || IsHost(uri.Host, "www.gitlab.com"))
        {
            const string marker = "/-/blob/";
            var path = uri.AbsolutePath;
            var index = path.IndexOf(marker, StringComparison.Ordinal);
            return index < 0
                ? null
                : $"https://gitlab.com{path[..index]}/-/raw/{path[(index + marker.Length)..]}";
        }

        return uri.ToString();
    }

    /// <summary>
    /// Fetches the markdown behind a linked README. Null on any failure, the
    /// same fail-soft contract as the forge clients' README fetches.
    /// </summary>
    public static async Task<string?> FetchAsync(HttpClient http, string? url, CancellationToken ct = default)
    {
        var rawUrl = ToRawUrl(url);
        if (rawUrl is null)
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(rawUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private static bool IsHost(string host, string expected) =>
        host.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
