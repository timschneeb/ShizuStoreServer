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
    long InstallCount,
    DateTimeOffset? VersionUpdatedAt,
    DateTimeOffset? ListUpdatedAt,
    string? AuthorKey,
    string? AuthorName,
    string SourceName,
    int? TargetSdk = null,
    int? CompileSdk = null,
    int? LocaleCount = null,
    IReadOnlyList<string>? Abis = null,
    IReadOnlyDictionary<string, string>? LocalizedLabels = null,
    bool DhizukuDeclared = false,
    IReadOnlyList<string>? Trackers = null,
    IReadOnlyList<TrackerTagDto>? TrackerTags = null);

/// <summary>
/// One installable candidate of an app, keyed by signing identity. Clients
/// compare the installed signing cert against the fingerprints, then compare
/// that candidate's <c>VersionCode</c>; the <c>Primary</c> entry is the
/// default offer for fresh installs (non-F-Droid builds preferred).
/// <c>PackageName</c> is the package this build installs, so flavor variants
/// of one entry are distinguishable.
/// </summary>
public sealed record DownloadDto(
    string Source,
    string? PackageName,
    string ApkUrl,
    string? ArchiveEntry,
    long? VersionCode,
    string? VersionName,
    long? Size,
    string? Sha256,
    string? SigSha256,
    string? SigMd5,
    int? MinSdk,
    string? Abi,
    bool Primary,
    int? TargetSdk = null,
    int? CompileSdk = null,
    IReadOnlyList<string>? Locales = null,
    IReadOnlyList<string>? Abis = null,
    IReadOnlyDictionary<string, string>? LocalizedLabels = null,
    string? SignerDn = null,
    string? SignerScheme = null,
    string? SignerKeyAlgorithm = null,
    bool DhizukuDeclared = false,
    IReadOnlyList<string>? Trackers = null,
    IReadOnlyList<TrackerTagDto>? TrackerTags = null);

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
    long InstallCount,
    DateTimeOffset? VersionUpdatedAt,
    DateTimeOffset? ListUpdatedAt,
    string? AuthorName,
    string? AuthorUrl,
    IReadOnlyList<string> Permissions,
    string? FullDescription,
    string? ReadmeUrl,
    string? Changelog,
    string? ChangelogUrl,
    IReadOnlyList<string> Screenshots,
    string SourceName,
    int? TargetSdk = null,
    int? CompileSdk = null,
    int? LocaleCount = null,
    IReadOnlyList<string>? Locales = null,
    IReadOnlyList<string>? Abis = null,
    string? UsageShort = null,
    string? UsageMarkdown = null,
    DateTimeOffset? UsageAnalyzedAt = null,
    bool DhizukuDeclared = false,
    IReadOnlyList<string>? Trackers = null,
    IReadOnlyList<TrackerTagDto>? TrackerTags = null);

public sealed record CategoryPathDto(string Slug, string Name);

/// <summary>One detected Exodus tracker with its category tags.</summary>
public sealed record TrackerTagDto(string Name, IReadOnlyList<string> Tags);

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
    IReadOnlyList<RemovedAppDto> Removed,
    IReadOnlyDictionary<string, long> InstallsUpdated,
    DateTimeOffset? CatalogPurgeRequestedAt,
    DateTimeOffset GeneratedAt);

public sealed record CountsDto(int Apps, int Categories);

/// <summary>One entry of the catalog health snapshot.</summary>
public sealed record IssueDto(
    string Kind,
    string Rule,
    string? Slug,
    string Message,
    string? Location);

public sealed record IssueSummaryDto(int Parse, int Enrich, int Quality, int Catalog, int Total);

/// <summary>Current health snapshot: latest completed run plus its issues.</summary>
public sealed record IssuesDto(
    long? RunId,
    string? HeadCommit,
    DateTimeOffset GeneratedAt,
    IssueSummaryDto Summary,
    IReadOnlyList<IssueDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record MetaDto(
    DateTimeOffset GeneratedAt,
    string? ListCommit,
    CountsDto Counts,
    bool UseInstallCountsForPopularity);

public sealed record HealthDto(string Status);

public sealed record SyncAcceptedDto(bool Queued);

/// <summary>Optional POST body for <c>POST /v1/admin/refresh-icons</c>.</summary>
public sealed record IconRefreshRequestDto(bool Force = false);

/// <summary>Operator view of the in-process icon refresh.</summary>
public sealed record IconRefreshStatusDto(
    string State,
    bool Force,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Checked,
    int Refreshed,
    int AlreadyCurrent,
    int Failed,
    IReadOnlyList<string> Errors,
    string? Error);

/// <summary>Operator view of the in-process screenshots refresh.</summary>
public sealed record ScreenshotRefreshStatusDto(
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Checked,
    int Updated,
    int Current,
    int Failed,
    IReadOnlyList<string> Errors,
    string? Error);

/// <summary>Result of <c>POST /v1/apps/{slug}/installs</c>: the new total.</summary>
public sealed record InstallRecordedDto(string Slug, long InstallCount);

/// <summary>One app's window totals in <c>GET /v1/trending</c>.</summary>
public sealed record TrendingItemDto(
    string Slug,
    long Installs,
    long PreviousInstalls,
    long Delta);

/// <summary>
/// Ranked install windows for the client's home trending row. <c>Items</c>
/// orders by <c>Installs</c> (default) or <c>Delta</c> (fastest growing).
/// </summary>
public sealed record TrendingDto(
    DateTimeOffset GeneratedAt,
    int WindowDays,
    string Sort,
    IReadOnlyList<TrendingItemDto> Items);

/// <summary>One zero-filled install bucket from <c>GET /v1/apps/{slug}/history</c>.</summary>
public sealed record InstallDayDto(string Day, long Count);

/// <summary>One carry-forward star snapshot from <c>GET /v1/apps/{slug}/history</c>.</summary>
public sealed record StarDayDto(string Day, int Stars);

/// <summary>
/// Daily install counts and star snapshots for the detail sparkline; install
/// days cover every day of the window, star days only days with a known value.
/// </summary>
public sealed record AppHistoryDto(
    string Slug,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<InstallDayDto> Installs,
    IReadOnlyList<StarDayDto> Stars);

/// <summary>Optional body for <c>POST /v1/admin/usage-analysis/queue</c>.</summary>
public sealed record UsageAnalysisQueueRequestDto(
    bool OnlyMissing = true,
    bool Stale = false,
    bool Force = false,
    string? Slug = null,
    int? Limit = null);

public sealed record UsageAnalysisQueueDto(int Queued);

/// <summary>Operator view of the AI usage-analysis queue and budget.</summary>
public sealed record UsageAnalysisStatusDto(
    bool Enabled,
    string Model,
    int Pending,
    int Running,
    int Succeeded,
    int Failed,
    int StartedToday,
    int MaxRunsPerDay,
    decimal SpentThisMonthUsd,
    decimal MonthlyBudgetUsd,
    IReadOnlyList<UsageAnalysisRunDto> RecentFailures);

public sealed record UsageAnalysisRunDto(
    long Id,
    string? Slug,
    string Model,
    int Attempts,
    string? RepoCommit,
    string? Error,
    string? LogFile,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal CostUsd,
    int ToolCalls,
    DateTimeOffset? FinishedAt);

/// <summary>Aggregated token and cost stats for the analyzer.</summary>
public sealed record UsageAnalysisStatsDto(
    int Runs,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal CostUsd,
    IReadOnlyList<UsageAnalysisDayDto> Days,
    IReadOnlyList<UsageAnalysisModelDto> Models);

public sealed record UsageAnalysisDayDto(
    string Day,
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

public sealed record UsageAnalysisModelDto(
    string Model,
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);
