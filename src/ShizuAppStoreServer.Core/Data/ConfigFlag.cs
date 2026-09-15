using Microsoft.EntityFrameworkCore;

namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One operator-controlled config flag (table <c>config_flags</c>). Flags
/// are read live from the database on every use, so flipping a row takes
/// effect without a restart. A missing row reads as its documented
/// default; <c>GET</c> endpoints never insert rows.
/// </summary>
public sealed class ConfigFlag
{
    /// <summary>Flag key, e.g. <c>use_install_counts_for_popularity</c>.</summary>
    public required string Key { get; set; }

    /// <summary>Raw value; booleans are <c>true</c>/<c>false</c> (case-insensitive).</summary>
    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Well-known <see cref="ConfigFlag"/> keys and typed readers.</summary>
public static class ConfigFlags
{
    /// <summary>
    /// When <c>true</c>, clients sort the popularity view by
    /// client-reported <c>install_count</c> instead of upstream
    /// <c>download_total</c>, and show the install count in list subtitles.
    /// Missing row reads as false. Surfaced on <c>GET /v1/meta</c>.
    /// </summary>
    public const string UseInstallCountsForPopularity = "use_install_counts_for_popularity";

    /// <summary>Reads a boolean flag; missing or unparsable rows yield <paramref name="defaultValue"/>.</summary>
    public static async Task<bool> GetBoolAsync(
        ShizuDbContext db, string key, bool defaultValue = false, CancellationToken ct = default)
    {
        var raw = await db.ConfigFlags.AsNoTracking()
            .Where(f => f.Key == key)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);
        return raw is not null && bool.TryParse(raw.Trim(), out var value) ? value : defaultValue;
    }
}
