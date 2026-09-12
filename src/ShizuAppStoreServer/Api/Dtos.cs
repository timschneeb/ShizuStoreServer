namespace ShizuAppStoreServer.Api;

/// <summary>Compact app shape for list items and change-feed entries.</summary>
public sealed record AppSummaryDto(
    string Slug,
    string Name,
    string Description,
    string? License,
    string Listing,
    string Type,
    bool IsRecommended,
    bool HasPaid,
    bool HasIap,
    bool HasAds,
    int? TrialDays,
    bool RequiresRoot,
    string Availability,
    string? PackageName,
    long? VersionCode,
    string? VersionName,
    int? MinSdk,
    string? IconHash,
    bool IconAdaptive,
    string CategorySlug,
    DateTimeOffset UpdatedAt,
    string? SigSha256,
    string? SigMd5);

/// <summary>
/// F-Droid alternate build of a forge-primary app (null when the package is
/// not on F-Droid). Clients compare the installed signing cert against the
/// fingerprints and pick this URL when the F-Droid build is installed.
/// </summary>
public sealed record FdroidVariantDto(
    string ApkUrl,
    long? VersionCode,
    string? VersionName,
    long? ApkSize,
    string? ApkSha256,
    string? SigSha256,
    string? SigMd5);

/// <summary>
/// Full app detail: summary fields flattened plus URLs and relations.
/// <c>ApkArchiveEntry</c> is set when <c>ApkUrl</c> is a zip archive and
/// names the APK entry inside it (case-insensitive match).
/// </summary>
public sealed record AppDetailDto(
    string Slug,
    string Name,
    string Description,
    string? License,
    string Listing,
    string Type,
    bool IsRecommended,
    bool HasPaid,
    bool HasIap,
    bool HasAds,
    int? TrialDays,
    bool RequiresRoot,
    string Availability,
    string? PackageName,
    long? VersionCode,
    string? VersionName,
    int? MinSdk,
    string? IconHash,
    bool IconAdaptive,
    string CategorySlug,
    DateTimeOffset UpdatedAt,
    string Url,
    string? SourceUrl,
    string SourceKind,
    string? ApkUrl,
    long? ApkSize,
    string? ApkSha256,
    string? ApkArchiveEntry,
    string? StoreUrl,
    string? ExcludedReason,
    IReadOnlyList<CategoryPathDto> CategoryPath,
    string? ParentSlug,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastCheckedAt,
    string? SigSha256,
    string? SigMd5,
    FdroidVariantDto? FdroidVariant);

public sealed record CategoryPathDto(string Slug, string Name);

public sealed record CategoryNodeDto(
    string Slug,
    string Name,
    string Section,
    int AppCount,
    IReadOnlyList<CategoryNodeDto> Children);

public sealed record PagedAppsDto(
    IReadOnlyList<AppSummaryDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record RemovedAppDto(
    string Slug,
    string? Name,
    DateTimeOffset RemovedAt);

public sealed record ChangesDto(
    IReadOnlyList<AppSummaryDto> Added,
    IReadOnlyList<AppSummaryDto> Updated,
    IReadOnlyList<RemovedAppDto> Removed);

public sealed record CountsDto(int Apps, int Categories);

public sealed record MetaDto(
    DateTimeOffset GeneratedAt,
    string? ListCommit,
    CountsDto Counts);

public sealed record HealthDto(string Status);

public sealed record SyncAcceptedDto(bool Queued);
