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
    long? Size,
    string? IconHash,
    bool IconAdaptive,
    string CategorySlug,
    DateTimeOffset UpdatedAt,
    string? SigSha256,
    string? SigMd5,
    int? Stars,
    long? DownloadTotal,
    DateTimeOffset? VersionUpdatedAt,
    DateTimeOffset? ListUpdatedAt,
    string? AuthorKey,
    string? AuthorName,
    string SourceName);

/// <summary>
/// One installable candidate of an app, keyed by signing identity. Clients
/// compare the installed signing cert against the fingerprints, then compare
/// that candidate's <c>VersionCode</c>; the <c>Primary</c> entry is the
/// default offer for fresh installs (non-F-Droid builds preferred).
/// </summary>
public sealed record DownloadDto(
    string Source,
    string ApkUrl,
    string? ArchiveEntry,
    long? VersionCode,
    string? VersionName,
    long? Size,
    string? Sha256,
    string? SigSha256,
    string? SigMd5,
    int? MinSdk,
    bool Primary);

/// <summary>
/// Full app detail: summary fields flattened plus URLs, relations and every
/// installable candidate (<c>Downloads</c>, primary first).
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
    IReadOnlyList<DownloadDto> Downloads,
    string? StoreUrl,
    string? ExcludedReason,
    IReadOnlyList<CategoryPathDto> CategoryPath,
    string? ParentSlug,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastCheckedAt,
    int? Stars,
    long? DownloadTotal,
    DateTimeOffset? VersionUpdatedAt,
    DateTimeOffset? ListUpdatedAt,
    string? AuthorName,
    string? AuthorUrl,
    IReadOnlyList<string> Permissions,
    string? FullDescription,
    string SourceName);

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
