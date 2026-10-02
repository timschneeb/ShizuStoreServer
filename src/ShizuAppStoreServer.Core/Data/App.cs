namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One awesome-list entry (table <c>apps</c>). Installable download
/// candidates live in <see cref="Downloads"/>; enrichment keeps identity
/// (<c>PackageName</c>), presentation (<c>IconHash</c>) and link state
/// (<c>Availability</c>, <c>StoreUrl</c>) on the row itself.
/// <c>AddedAt</c>/<c>UpdatedAt</c> come from the git-history backfill.
/// </summary>
public sealed class App
{
    public long Id { get; set; }

    /// <summary>Globally unique URL slug (stable across renames).</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Name clients should show, derived from the served APK label when known.
    /// Falls back to <see cref="Name"/> (the awesome-list name). Set on the
    /// root row even when the row has no variants.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Raw <c>application-label</c> from the served APK, kept separately so
    /// the variant suffix can be recomputed when the group size changes.
    /// </summary>
    public string? ApkLabel { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? License { get; set; }

    public Listing Listing { get; set; }
    public AppType Type { get; set; }

    public bool IsRecommended { get; set; }
    public bool HasPaid { get; set; }
    public bool HasIap { get; set; }
    public bool HasAds { get; set; }
    public int? TrialDays { get; set; }
    public bool RequiresRoot { get; set; }

    /// <summary>Nested list entries (e.g. "aShell You" under "aShell").</summary>
    public long? ParentId { get; set; }
    public App? Parent { get; set; }
    public List<App> Children { get; } = [];

    /// <summary>
    /// Set on rows that represent an extra Android package published by the
    /// same source repo as the list entry. Points at the list row, which is
    /// the only row the catalog upserter matches and stale-checks.
    /// </summary>
    public long? RootAppId { get; set; }
    public App? Root { get; set; }
    public List<App> Variants { get; } = [];

    /// <summary>Primary link from the list entry.</summary>
    public required string Url { get; set; }

    public string? SourceUrl { get; set; }
    public SourceKind SourceKind { get; set; } = SourceKind.Other;
    public Availability Availability { get; set; } = Availability.LinkOnly;
    public string? ExcludedReason { get; set; }

    /// <summary>
    /// When the row first passed an enrichment check without failing. Null
    /// while the entry still awaits its first successful check; every public
    /// read path hides unpublished rows so clients never see an entry
    /// without icon, description and availability.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Operator override for the Shizuku permission gate: keep an app whose
    /// served APK does not declare the Shizuku permission. Never touched by
    /// the upserter, set via admin tooling.
    /// </summary>
    public bool ExcludeOverride { get; set; }

    /// <summary>Android package id (identity for F-Droid and Play lookups).</summary>
    public string? PackageName { get; set; }

    /// <summary>
    /// Permissions declared by the served APK (aapt2 badging), in declaration
    /// order. Empty when unknown; extracted from the APK only, never from
    /// F-Droid metadata.
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// Display name of the source owner (GitHub login, GitLab namespace, or
    /// F-Droid author). Shown under the app title and in the developer section.
    /// </summary>
    public string? AuthorName { get; set; }

    /// <summary>Profile URL of the source owner, when known.</summary>
    public string? AuthorUrl { get; set; }

    /// <summary>
    /// Stable grouping key for "more from this dev" (<c>github:owner</c>,
    /// <c>gitlab:group</c>). Null when the owner cannot be identified
    /// confidently; clients only show the row when it is set.
    /// </summary>
    public string? AuthorKey { get; set; }

    /// <summary>
    /// README markdown (or scraped plain-text Play description) for the
    /// full-description subscreen. Server-only: sent on the detail endpoint,
    /// never in summaries or the change feed.
    /// </summary>
    public string? FullDescription { get; set; }

    /// <summary>
    /// Direct raw markdown URL of <see cref="FullDescription"/> (GitHub
    /// raw.githubusercontent.com or GitLab <c>/-/raw/</c> route), so clients
    /// can refetch the current README instead of rendering the stored
    /// snapshot. Null for Play descriptions and sources without a raw route.
    /// Server-only: sent on the detail endpoint, never in summaries or the
    /// change feed.
    /// </summary>
    public string? ReadmeUrl { get; set; }

    /// <summary>
    /// Latest release notes: the release markdown body for GitHub/GitLab, or
    /// the F-Droid/Izzy index long description when the source publishes no
    /// release notes. Server-only: sent on the detail endpoint, never in
    /// summaries or the change feed.
    /// </summary>
    public string? Changelog { get; set; }

    /// <summary>
    /// Browser page the changelog text was read from (the GitHub/GitLab
    /// release page). Null for index-sourced changelogs, which have no
    /// per-release page. Server-only: sent on the detail endpoint, never in
    /// summaries or the change feed.
    /// </summary>
    public string? ChangelogUrl { get; set; }

    /// <summary>
    /// Screenshot image URLs harvested from the F-Droid/Izzy <c>index-v2.json</c>
    /// for any of the app's package names, or lifted from the app's own repo
    /// tree when those indexes carry none. Server-only: sent on the detail
    /// endpoint, never in summaries or the change feed.
    /// </summary>
    public List<string> Screenshots { get; set; } = [];

    /// <summary>
    /// Last time the repo fallback looked for screenshots. Throttles re-cloning
    /// a repo that yielded none; null means never tried.
    /// </summary>
    public DateTimeOffset? ScreenshotsCheckedAt { get; set; }

    public string? StoreUrl { get; set; }

    /// <summary>
    /// Version name of an external-only listing (for example a Play build),
    /// used when the app has no APK download to read a version from.
    /// </summary>
    public string? VersionName { get; set; }

    public string? IconHash { get; set; }

    /// <summary>
    /// True when the served icon file was rendered from an XML drawable
    /// (adaptive-icon or plain vector) through Paparazzi: full-bleed and
    /// mask-safe, so clients frame it as a rounded square. False for
    /// density rasters, mirrored F-Droid icons and letter-avatars, which
    /// carry their own shape and go into a squircle box instead.
    /// </summary>
    public bool IconAdaptive { get; set; }

    /// <summary>GitHub stargazers of the source repo, for popularity sorting. Null when not GitHub.</summary>
    public int? Stars { get; set; }

    /// <summary>
    /// Total <c>download_count</c> over the source's release assets (GitHub).
    /// Null when unknown; refreshed whenever the release feed changes.
    /// </summary>
    public long? DownloadTotal { get; set; }

    /// <summary>
    /// Successful installs reported by Shizu clients
    /// (<c>POST /v1/apps/{slug}/installs</c>). Monotonic counter, never
    /// bumps <c>UpdatedAt</c> so the changes feed does not churn.
    /// </summary>
    public long InstallCount { get; set; }

    /// <summary>
    /// When <c>InstallCount</c> was last incremented. Drives the
    /// <c>installsUpdated</c> delta map on <c>/v1/changes</c>; null until
    /// the first reported install.
    /// </summary>
    public DateTimeOffset? InstallCountUpdatedAt { get; set; }

    public long CategoryId { get; set; }
    public Category? Category { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    /// When the awesome-list entry was last added or edited (git history,
    /// <c>[silent]</c> commits excluded). Mirrors the published changelog and
    /// drives "recently added" ordering; never touched by enrichment.
    /// </summary>
    public DateTimeOffset? ListUpdatedAt { get; set; }

    /// <summary>
    /// Change clock: bumped by any summary-visible change (icon, popularity,
    /// list metadata). Drives <c>/v1/changes</c>, not "recently updated".
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Release date of the currently served APK version (detection time when
    /// the source publishes none). Drives "recently updated" ordering and is
    /// never touched by metadata refreshes.
    /// </summary>
    public DateTimeOffset? VersionUpdatedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// ETag from the last release-feed fetch, for conditional requests
    /// (GitHub <c>If-None-Match</c>; reused by the M4 resolvers).
    /// </summary>
    public string? EnrichEtag { get; set; }

    /// <summary>
    /// True when the forge's newest release in the served channel ships no
    /// installable artifact, so an alternative source with a newer build is
    /// preferred instead of the older forge APK. Cleared when the forge ships
    /// assets again.
    /// </summary>
    public bool ForgeAssetsStale { get; set; }

    public List<AppVersion> Versions { get; } = [];

    /// <summary>Installable build candidates: one row per signing identity.</summary>
    public List<AppDownload> Downloads { get; } = [];

    /// <summary>
    /// One-line AI summary of what the app does with Shizuku, shown in the
    /// details usage row. Null when the app has no analyzable repo or the
    /// analysis has not succeeded yet. Server-only: sent on the detail
    /// endpoint, never in summaries or the change feed.
    /// </summary>
    public string? UsageShort { get; set; }

    /// <summary>
    /// Full AI usage report as GitHub-flavored markdown, shown on the usage
    /// subscreen. Null under the same conditions as <see cref="UsageShort"/>.
    /// Server-only: sent on the detail endpoint, never in summaries or the
    /// change feed.
    /// </summary>
    public string? UsageMarkdown { get; set; }

    /// <summary>
    /// Capability section of the report on its own: intro sentence plus the
    /// bold-labelled capability bullets. Stored so the sections can be
    /// rendered separately; <see cref="UsageMarkdown"/> is the composed text
    /// with the server-owned headings.
    /// </summary>
    public string? UsageMarkdownUsage { get; set; }

    /// <summary>API list section of the report on its own, without a heading; null when no Shizuku usage was found.</summary>
    public string? UsageMarkdownApiUsage { get; set; }

    /// <summary>Notable details section of the report on its own, without a heading; null when there is nothing notable.</summary>
    public string? UsageMarkdownNotableDetails { get; set; }

    /// <summary>When the stored analysis completed; null means never analyzed.</summary>
    public DateTimeOffset? UsageAnalyzedAt { get; set; }

    /// <summary>Model id that produced the stored analysis.</summary>
    public string? UsageModel { get; set; }

    /// <summary>Commit the stored analysis was generated from.</summary>
    public string? UsageCommit { get; set; }

    /// <summary>Release tag the commit came from, when one was resolved.</summary>
    public string? UsageReleaseRef { get; set; }

    /// <summary>
    /// Prompt contract generation of the stored analysis; older than
    /// <c>UsageAnalysisOptions.PromptVersion</c> marks it stale for backfill.
    /// </summary>
    public int UsagePromptVersion { get; set; }

    /// <summary>
    /// Pipeline generation (context, tools, validation) of the stored analysis;
    /// older than <c>UsageAnalysisOptions.AnalysisVersion</c> marks it stale.
    /// </summary>
    public int UsageAnalysisVersion { get; set; }
}
