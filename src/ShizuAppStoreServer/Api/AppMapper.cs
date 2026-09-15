using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api;

/// <summary>Entity → DTO projections shared by the apps and changes controllers.</summary>
public static class AppMapper
{
    /// <summary>Default candidate for clients without an installed-signature match.</summary>
    public static AppDownload? Primary(App a) => a.Downloads.FirstOrDefault(d => d.IsPrimary);

    public static AppSummaryDto ToSummary(App a)
    {
        var primary = Primary(a);
        return new(
            a.Slug,
            a.Name,
            a.Description,
            a.License,
            ApiEnums.ToApiString(a.Listing),
            ApiEnums.ToApiString(a.Type),
            a.IsRecommended,
            a.HasPaid,
            a.HasIap,
            a.HasAds,
            a.TrialDays,
            a.RequiresRoot,
            ApiEnums.ToApiString(a.Availability),
            a.PackageName,
            primary?.VersionCode,
            primary?.VersionName ?? a.VersionName,
            primary?.MinSdk,
            primary?.SizeBytes,
            a.IconHash,
            a.IconAdaptive,
            a.Category?.Slug ?? string.Empty,
            a.UpdatedAt,
            primary?.SigSha256,
            primary?.SigMd5,
            a.Stars,
            a.DownloadTotal,
            a.InstallCount,
            a.VersionUpdatedAt,
            a.ListUpdatedAt,
            a.AuthorKey,
            a.AuthorName,
            SourceName(a));
    }

    /// <summary>
    /// Detail projection. <paramref name="path"/> is the root→leaf category
    /// chain (built by the caller, which already holds the category rows).
    /// </summary>
    public static AppDetailDto ToDetail(App a, IReadOnlyList<CategoryPathDto> path)
    {
        var primary = Primary(a);
        return new(
            a.Slug,
            a.Name,
            a.Description,
            a.License,
            ApiEnums.ToApiString(a.Listing),
            ApiEnums.ToApiString(a.Type),
            a.IsRecommended,
            a.HasPaid,
            a.HasIap,
            a.HasAds,
            a.TrialDays,
            a.RequiresRoot,
            ApiEnums.ToApiString(a.Availability),
            a.PackageName,
            primary?.VersionCode,
            primary?.VersionName ?? a.VersionName,
            primary?.MinSdk,
            a.IconHash,
            a.IconAdaptive,
            a.Category?.Slug ?? string.Empty,
            a.UpdatedAt,
            a.Url,
            a.SourceUrl,
            ApiEnums.ToApiString(a.SourceKind),
            a.Downloads
                .OrderByDescending(d => d.IsPrimary)
                .ThenByDescending(d => d.VersionCode ?? -1)
                .Select(ToDownload)
                .ToList(),
            a.StoreUrl,
            a.ExcludedReason,
            path,
            a.Parent?.Slug,
            a.AddedAt,
            a.LastCheckedAt,
            a.Stars,
            a.DownloadTotal,
            a.InstallCount,
            a.VersionUpdatedAt,
            a.ListUpdatedAt,
            a.AuthorName,
            a.AuthorUrl,
            a.Permissions,
            a.FullDescription,
            SourceName(a));
    }

    /// <summary>
    /// Human-readable origin for the detail subtitle. Play redirects report the
    /// store even when the list entry points at a forge repository.
    /// </summary>
    private static string SourceName(App a) => a.Availability == Availability.PlayRedirect
        ? "Play Store"
        : a.SourceKind.GetDescription();

    private static DownloadDto ToDownload(AppDownload d) => new(
        ApiEnums.ToApiString(d.Source),
        d.ApkUrl,
        d.ArchiveEntry,
        d.VersionCode,
        d.VersionName,
        d.SizeBytes,
        d.Sha256,
        d.SigSha256,
        d.SigMd5,
        d.MinSdk,
        d.IsPrimary);

    /// <summary>Root→leaf <c>(slug, name)</c> chain for a category (max depth 2 in real data).</summary>
    public static IReadOnlyList<CategoryPathDto> CategoryPath(Category? category)
    {
        if (category is null)
        {
            return [];
        }

        var chain = new List<CategoryPathDto>();
        for (var c = category; c is not null; c = c.Parent)
        {
            chain.Add(new CategoryPathDto(c.Slug, c.Name));
        }

        chain.Reverse();
        return chain;
    }

    public static RemovedAppDto ToRemoved(RemovedApp t) => new(t.Slug, t.Name, t.RemovedAt);
}
