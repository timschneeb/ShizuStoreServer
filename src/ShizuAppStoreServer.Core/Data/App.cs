namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One awesome-list entry (table <c>apps</c>). Enrichment columns
/// (<c>PackageName</c>, <c>ApkUrl</c>, …) are filled by the resolvers (M3/M4);
/// <c>AddedAt</c>/<c>UpdatedAt</c> come from the git-history backfill (M2).
/// </summary>
public sealed class App
{
    public long Id { get; set; }

    /// <summary>Globally unique URL slug (stable across renames).</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Normalized license tag (<c>Propietary</c> typo fixed by the parser).</summary>
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

    /// <summary>Primary link from the list entry.</summary>
    public required string Url { get; set; }

    public string? SourceUrl { get; set; }
    public SourceKind SourceKind { get; set; } = SourceKind.Other;
    public Availability Availability { get; set; } = Availability.LinkOnly;
    public string? ExcludedReason { get; set; }

    /// <summary>
    /// Operator override: keep a Play-sole-source app as
    /// <c>PlayRedirect</c> instead of excluding it. Never touched by the
    /// upserter — set via admin tooling (M5/M6).
    /// </summary>
    public bool ExcludeOverride { get; set; }

    public string? PackageName { get; set; }
    public long? VersionCode { get; set; }
    public string? VersionName { get; set; }
    public string? ApkUrl { get; set; }
    public long? ApkSize { get; set; }
    public string? ApkSha256 { get; set; }

    /// <summary>
    /// Entry path of the APK inside <see cref="ApkUrl"/> when clients
    /// download a release archive (zip) instead of a bare APK; null for
    /// plain APK URLs. <see cref="ApkSize"/> and <see cref="ApkSha256"/>
    /// describe the downloaded archive in that case.
    /// </summary>
    public string? ApkArchiveEntry { get; set; }
    public int? MinSdk { get; set; }
    /// <summary>
    /// Source that supplied the currently served APK, locked once clients
    /// have seen it. F-Droid rebuilds carry a different signing key (unless
    /// reproducible), so enrichment must never switch the APK download
    /// between a forge and F-Droid/Izzy (update would fail the signature
    /// check). Null until the first APK is recorded.
    /// </summary>
    public SourceKind? ApkSource { get; set; }

    /// <summary>
    /// Identity inside <see cref="ApkSource"/> for deterministic re-resolution:
    /// the F-Droid/Izzy package id; null for forge locks (repo re-derived from
    /// the entry URLs).
    /// </summary>
    public string? ApkSourceRef { get; set; }

    public string? StoreUrl { get; set; }
    public string? IconHash { get; set; }

    /// <summary>
    /// True when the served icon file was rendered from an XML drawable
    /// (adaptive-icon or plain vector) through Paparazzi: full-bleed and
    /// mask-safe, so clients frame it as a rounded square. False for
    /// density rasters, mirrored F-Droid icons and letter-avatars, which
    /// carry their own shape and go into a squircle box instead.
    /// </summary>
    public bool IconAdaptive { get; set; }

    /// <summary>
    /// Primary APK signing-cert fingerprints: SHA-256 (+ MD5) from
    /// <c>apksigner</c>, or the index <c>&lt;sig&gt;</c> MD5 for
    /// index-only F-Droid/Izzy primaries. Space-joined sets when the APK
    /// carries rotated keys; null while unanalyzed. Clients match the
    /// locally installed cert against these + the F-Droid variant below.
    /// </summary>
    public string? SigSha256 { get; set; }
    public string? SigMd5 { get; set; }

    /// <summary>
    /// F-Droid alternate variant: recorded when the primary APK comes from a
    /// forge (GitHub/GitLab) but the same package is also published on
    /// F-Droid (different signer, delayed builds). The forge build stays
    /// primary; clients offer this URL when the installed cert matches the
    /// variant fingerprints instead.
    /// </summary>
    public string? FdroidApkUrl { get; set; }
    public long? FdroidVersionCode { get; set; }
    public string? FdroidVersionName { get; set; }
    public long? FdroidApkSize { get; set; }
    public string? FdroidApkSha256 { get; set; }
    public string? FdroidSigSha256 { get; set; }
    public string? FdroidSigMd5 { get; set; }

    public long CategoryId { get; set; }
    public Category? Category { get; set; }

    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// ETag from the last release-feed fetch, for conditional requests
    /// (GitHub <c>If-None-Match</c>; reused by the M4 resolvers).
    /// </summary>
    public string? EnrichEtag { get; set; }

    public List<AppVersion> Versions { get; } = [];
}
