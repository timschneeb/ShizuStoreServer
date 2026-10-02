using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>One data quality finding for the health snapshot (not yet persisted).</summary>
public sealed record QualityIssue(string Rule, long AppId, string Slug, string Message);

/// <summary>
/// Heuristic data quality checks over the catalog. Hidden rows (excluded and
/// not yet published) are invisible by design and never checked. Runs read
/// only, after upsert and enrich.
/// </summary>
public static class CatalogHealthCheck
{
    public const string MissingLicense = "missing_license";
    public const string MissingDescription = "missing_description";
    public const string MissingIcon = "missing_icon";
    public const string MissingPackage = "missing_package";
    public const string DirectApkWithoutPrimary = "direct_apk_without_primary";
    public const string NeverChecked = "never_checked";
    public const string StaleCheck = "stale_check";
    public const string BadUrl = "bad_url";
    public const string DuplicatePackage = "duplicate_package";
    public const string VersionAnomaly = "version_anomaly";

    public static async Task<List<QualityIssue>> CheckAsync(
        ShizuDbContext db, DateTimeOffset now, TimeSpan successWindow, CancellationToken ct = default)
    {
        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && a.PublishedAt != null)
            .Include(a => a.Downloads)
            .Include(a => a.Versions)
            .ToListAsync(ct);

        var issues = new List<QualityIssue>();
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.License))
            {
                issues.Add(new QualityIssue(MissingLicense, app.Id, app.Slug, "Entry has no license tag."));
            }

            if (string.IsNullOrWhiteSpace(app.Description))
            {
                issues.Add(new QualityIssue(MissingDescription, app.Id, app.Slug, "Entry has no description."));
            }

            if (string.IsNullOrWhiteSpace(app.IconHash))
            {
                issues.Add(new QualityIssue(MissingIcon, app.Id, app.Slug, "App has no icon file."));
            }

            if (!IsHttpUrl(app.Url))
            {
                issues.Add(new QualityIssue(BadUrl, app.Id, app.Slug, $"Entry URL '{app.Url}' is not an absolute http(s) URL."));
            }
            else if (app.SourceUrl is not null && !IsHttpUrl(app.SourceUrl))
            {
                issues.Add(new QualityIssue(BadUrl, app.Id, app.Slug, $"Source URL '{app.SourceUrl}' is not an absolute http(s) URL."));
            }

            if (app.Availability == Availability.DirectApk)
            {
                if (string.IsNullOrWhiteSpace(app.PackageName))
                {
                    issues.Add(new QualityIssue(MissingPackage, app.Id, app.Slug, "Direct APK app has no package name."));
                }

                if (!app.Downloads.Any(d => d.IsPrimary))
                {
                    issues.Add(new QualityIssue(DirectApkWithoutPrimary, app.Id, app.Slug, "Direct APK app has no primary download."));
                }
            }

            if (app.LastCheckedAt is null)
            {
                issues.Add(new QualityIssue(NeverChecked, app.Id, app.Slug, "App was never enrichment checked."));
            }
            else if (app.LastCheckedAt + successWindow + successWindow <= now)
            {
                // Twice the success window: the row missed two re-checks, so
                // the loop is likely wedged for it rather than just due.
                issues.Add(new QualityIssue(StaleCheck, app.Id, app.Slug, "App missed two enrichment windows."));
            }

            var primary = app.Downloads.FirstOrDefault(d => d.IsPrimary);
            if (primary?.VersionCode is long primaryCode)
            {
                // Pre-release rows are channel history, not a skipped stable
                // release; only stable history can outrank the served build.
                // Same version names are one release: multi-ABI artifacts
                // encode the ABI in the version code (obtainium's 2356
                // universal vs 23563 arm64; seen so far only in some Flutter
                // apps), so those rows are flavors of the served build, not
                // a skipped release.
                var newest = app.Versions
                    .Where(v => !v.IsPrerelease && !SharesVersionName(v.VersionName, primary.VersionName))
                    .Max(v => v.VersionCode);
                if (newest > primaryCode)
                {
                    issues.Add(new QualityIssue(VersionAnomaly, app.Id, app.Slug,
                        $"Primary download version {primaryCode} is older than the newest recorded version {newest}; " +
                        "check for a skipped release or failed APK analysis."));
                }
            }
        }

        AddDuplicatePackageIssues(apps, issues);

        return issues;
    }

    /// <summary>
    /// Version names identify a release across its per-ABI artifacts. Both
    /// missing names compare equal: an unlabeled history row cannot prove a
    /// skipped release against an unlabeled served build.
    /// </summary>
    private static bool SharesVersionName(string? left, string? right)
    {
        var a = left?.Trim() ?? string.Empty;
        var b = right?.Trim() ?? string.Empty;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two live listings sharing a canonical package overwrite each other on
    /// install, so every member of a duplicate group is reported.
    /// </summary>
    private static void AddDuplicatePackageIssues(List<App> apps, List<QualityIssue> issues)
    {
        var groups = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
            .GroupBy(a => a.PackageName!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(a => a.Slug).Distinct(StringComparer.Ordinal).Count() > 1)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var members = group.OrderBy(a => a.Slug, StringComparer.Ordinal).ToList();
            foreach (var app in members)
            {
                var others = string.Join(", ", members
                    .Where(m => m.Slug != app.Slug)
                    .Select(m => $"'{m.Slug}'"));
                issues.Add(new QualityIssue(DuplicatePackage, app.Id, app.Slug,
                    $"Package '{group.Key}' is also listed as {others}."));
            }
        }
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
