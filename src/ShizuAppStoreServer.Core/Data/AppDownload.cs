namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One installable build candidate of an app, keyed by package, signing
/// identity and ABI: at most one row per (package, signature, ABI), always
/// that triple's newest version. Clients pick the row whose fingerprint
/// matches the locally installed build and whose ABI the device supports;
/// <see cref="IsPrimary"/> marks the default candidate for fresh installs
/// (forge builds preferred over F-Droid rebuilds).
/// </summary>
public sealed class AppDownload
{
    public long Id { get; set; }

    public long AppId { get; set; }
    public App? App { get; set; }

    /// <summary>
    /// Android package this build installs. Flavor variants of one app (FOSS
    /// vs Play, debug vs release) share a row but differ here, so the client
    /// can show and install the right one.
    /// </summary>
    public string? PackageName { get; set; }

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

    /// <summary>
    /// True once this row's recorded build was downloaded and inspected
    /// (badging plus signer extraction). Index-only rows stay false until an
    /// analysis runs, and the flag is never downgraded.
    /// </summary>
    public bool Analyzed { get; set; }

    public int? MinSdk { get; set; }

    /// <summary>
    /// Declared <c>targetSdkVersion</c> from badging; null until the build has
    /// been analyzed.
    /// </summary>
    public int? TargetSdk { get; set; }

    /// <summary>
    /// Declared <c>compileSdkVersion</c> from the package line; null until the
    /// build has been analyzed.
    /// </summary>
    public int? CompileSdk { get; set; }

    /// <summary>
    /// Resource locales from badging, empty until analyzed. The
    /// <c>--_--</c> pseudo-locale is not a translation and is dropped.
    /// </summary>
    public List<string> Locales { get; set; } = [];

    /// <summary>
    /// True when the build declares a <c>com.rosan.dhizuku.permission.*</c>
    /// permission. The Shizuku permission itself is not tracked: almost every
    /// app in the catalog declares it, so it carries no signal.
    /// </summary>
    public bool DhizukuDeclared { get; set; }

    /// <summary>Names of the Exodus trackers detected in the DEX code.</summary>
    public List<string> Trackers { get; set; } = [];

    /// <summary>Matched Exodus code signatures, one per tracker, for auditing.</summary>
    public List<string> TrackerSignatures { get; set; } = [];

    /// <summary>
    /// Native ABI the build targets, parsed from the APK's <c>native-code</c>
    /// line; null means a fat/universal build that runs on any device.
    /// </summary>
    public string? Abi { get; set; }

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
