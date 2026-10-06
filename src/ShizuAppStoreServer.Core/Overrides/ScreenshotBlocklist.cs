using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// Global operator blocklist for screenshot URLs (typically false detections
/// inside source repos). The list is filtered when screenshots are resolved
/// and again on read, so a block takes effect immediately; the sync pass also
/// purges stored hits so the column converges.
/// </summary>
public static class ScreenshotBlocklist
{
    /// <summary>Active blocked URLs; soft-deleted rows are ignored.</summary>
    public static async Task<HashSet<string>> LoadAsync(ShizuDbContext db, CancellationToken ct = default)
    {
        var urls = await db.BlockedScreenshotUrls.AsNoTracking()
            .Where(b => b.DeletedAt == null)
            .Select(b => b.Url)
            .ToListAsync(ct);
        return new HashSet<string>(urls, StringComparer.Ordinal);
    }

    /// <summary>Drops blocked URLs, preserving order; a null or empty block is a no-op.</summary>
    public static List<string> Filter(IEnumerable<string> urls, IReadOnlySet<string>? blocked) =>
        blocked is null || blocked.Count == 0
            ? [.. urls]
            : urls.Where(url => !blocked.Contains(url)).ToList();
}
