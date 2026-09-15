using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>One data quality finding for the health snapshot (not yet persisted).</summary>
public sealed record QualityIssue(string Rule, long AppId, string Slug, string Message);

/// <summary>
/// Heuristic data quality checks over the catalog. Excluded rows are hidden
/// by design and never checked. Runs read only, after upsert and enrich.
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

    public static async Task<List<QualityIssue>> CheckAsync(
        ShizuDbContext db, DateTimeOffset now, TimeSpan successWindow, CancellationToken ct = default)
    {
        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded)
            .Include(a => a.Downloads)
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
        }

        return issues;
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
