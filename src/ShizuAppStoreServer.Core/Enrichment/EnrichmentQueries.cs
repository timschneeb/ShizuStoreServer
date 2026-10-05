using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Enrichment;

internal static class EnrichmentQueries
{
    internal static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    internal static async Task<List<App>> LoadVariantGroupAsync(
        ShizuDbContext db, App root, CancellationToken ct)
    {
        // The query still sees a variant marked for removal (the DELETE has not
        // been flushed), so filter deleted rows out of the group.
        var rows = (await db.Apps.Where(a => a.RootAppId == root.Id).ToListAsync(ct))
            .Where(a => db.Entry(a).State != EntityState.Deleted)
            .ToList();
        foreach (var local in db.Apps.Local)
        {
            if (local.RootAppId == root.Id
                && db.Entry(local).State != EntityState.Deleted
                && !rows.Contains(local))
            {
                rows.Add(local);
            }
        }

        return rows;
    }

    /// <summary>
    /// Compares two artifact checksums, tolerating a <c>algo:</c> prefix and
    /// case so a source digest and the recorded bare hex compare equal.
    /// </summary>
    internal static bool HashMatches(string? left, string? right)
    {
        var a = NormalizeHash(left);
        var b = NormalizeHash(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var value = hash.Trim();
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            value = value[(colon + 1)..].Trim();
        }

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }
}
