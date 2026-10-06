using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// Global operator blocklist for screenshot URLs (typically false detections
/// inside source repos). Matching ignores the commit segment of the pinned raw
/// URLs the repo resolver produces, so a block survives a repository re-pin;
/// every other URL is matched exactly. The list is filtered when screenshots
/// are resolved and again on read, so a block takes effect immediately; the
/// sync pass also purges stored hits so the column converges.
/// </summary>
public static class ScreenshotBlocklist
{
    /// <summary>Active blocked URLs, normalized for matching.</summary>
    public static async Task<HashSet<string>> LoadAsync(ShizuDbContext db, CancellationToken ct = default)
    {
        var urls = await db.BlockedScreenshotUrls.AsNoTracking()
            .Where(b => b.DeletedAt == null)
            .Select(b => b.Url)
            .ToListAsync(ct);
        return new HashSet<string>(urls.Select(Normalize), StringComparer.Ordinal);
    }

    /// <summary>Drops blocked URLs, preserving order; a null or empty block is a no-op.</summary>
    public static List<string> Filter(IEnumerable<string> urls, IReadOnlySet<string>? blocked) =>
        blocked is null || blocked.Count == 0
            ? [.. urls]
            : urls.Where(url => !IsBlocked(url, blocked)).ToList();

    /// <summary>True when the URL matches the (normalized) blocklist.</summary>
    public static bool IsBlocked(string url, IReadOnlySet<string> blocked) =>
        blocked.Count > 0 && blocked.Contains(Normalize(url));

    /// <summary>
    /// Canonical form for matching: drops the commit segment (40-hex SHA-1 or
    /// 64-hex SHA-256) from the pinned raw URL shapes the repo resolver emits,
    /// so a block keeps matching after the repository head moves. Any other URL
    /// is returned unchanged.
    /// </summary>
    public static string Normalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // {owner}/{repo}/{sha}/{path}
        if (host == "raw.githubusercontent.com" && segments.Length >= 4 && IsCommit(segments[2]))
        {
            return Canonical(uri, host, segments, drop: 2);
        }

        if (host == "gitlab.com")
        {
            // {group...}/{project}/-/raw/{sha}/{path}
            for (var i = 0; i + 2 < segments.Length; i++)
            {
                if (segments[i] == "-" && segments[i + 1] == "raw" && IsCommit(segments[i + 2]))
                {
                    return Canonical(uri, host, segments, drop: i + 2);
                }
            }
        }

        return url;
    }

    private static bool IsCommit(string segment) =>
        segment.Length is 40 or 64 && segment.All(Uri.IsHexDigit);

    private static string Canonical(Uri uri, string host, string[] segments, int drop)
    {
        var kept = segments.Where((_, index) => index != drop);
        return $"{uri.Scheme}://{host}/{string.Join('/', kept)}{uri.Query}{uri.Fragment}";
    }
}
