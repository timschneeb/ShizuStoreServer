using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api;

/// <summary>Entity → DTO projections shared by the apps and changes controllers.</summary>
public static class AppMapper
{
    public static AppSummaryDto ToSummary(App a) => new(
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
        a.VersionCode,
        a.VersionName,
        a.MinSdk,
        a.IconHash,
        a.IconAdaptive,
        a.Category?.Slug ?? string.Empty,
        a.UpdatedAt,
        a.SigSha256,
        a.SigMd5);

    /// <summary>
    /// Detail projection. <paramref name="path"/> is the root→leaf category
    /// chain (built by the caller, which already holds the category rows).
    /// </summary>
    public static AppDetailDto ToDetail(App a, IReadOnlyList<CategoryPathDto> path) => new(
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
        a.VersionCode,
        a.VersionName,
        a.MinSdk,
        a.IconHash,
        a.IconAdaptive,
        a.Category?.Slug ?? string.Empty,
        a.UpdatedAt,
        a.Url,
        a.SourceUrl,
        ApiEnums.ToApiString(a.SourceKind),
        a.ApkUrl,
        a.ApkSize,
        a.ApkSha256,
        a.ApkArchiveEntry,
        a.StoreUrl,
        a.ExcludedReason,
        path,
        a.Parent?.Slug,
        a.AddedAt,
        a.LastCheckedAt,
        a.SigSha256,
        a.SigMd5,
        ToVariant(a));

    private static FdroidVariantDto? ToVariant(App a) =>
        a.FdroidApkUrl is null
            ? null
            : new FdroidVariantDto(
                a.FdroidApkUrl,
                a.FdroidVersionCode,
                a.FdroidVersionName,
                a.FdroidApkSize,
                a.FdroidApkSha256,
                a.FdroidSigSha256,
                a.FdroidSigMd5);

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
