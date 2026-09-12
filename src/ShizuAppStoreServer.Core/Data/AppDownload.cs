namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One installable build candidate of an app, keyed by signing identity:
/// at most one row per signature, always the per-signature newest version.
/// Clients pick the row whose fingerprint matches the locally installed
/// build; <see cref="IsPrimary"/> marks the default candidate for fresh
/// installs (forge builds preferred over F-Droid rebuilds).
/// </summary>
public sealed class AppDownload
{
    public long Id { get; set; }

    public long AppId { get; set; }
    public App? App { get; set; }

    /// <summary>Where this build was resolved from.</summary>
    public SourceKind Source { get; set; }

    /// <summary>Repo key or F-Droid package id used to re-resolve the source.</summary>
    public string? SourceRef { get; set; }

    public required string ApkUrl { get; set; }

    /// <summary>APK entry inside <see cref="ApkUrl"/> when that is a zip archive.</summary>
    public string? ArchiveEntry { get; set; }

    public long? VersionCode { get; set; }
    public string? VersionName { get; set; }

    /// <summary>Bytes of the downloaded artifact (archive size for zip URLs).</summary>
    public long? SizeBytes { get; set; }

    public string? Sha256 { get; set; }

    public string? SigSha256 { get; set; }
    public string? SigMd5 { get; set; }

    public int? MinSdk { get; set; }

    /// <summary>
    /// Dedupe key of the signing identity: lowercased first SHA-256
    /// fingerprint token, else lowercased first MD5 token, else
    /// <c>url:&lt;apkUrl&gt;</c> when the build carries no fingerprint.
    /// </summary>
    public required string SigKey { get; set; }

    /// <summary>Default candidate for fresh installs; exactly one per app.</summary>
    public bool IsPrimary { get; set; }

    public DateTimeOffset ResolvedAt { get; set; }
}
