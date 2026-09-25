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
    /// Exodus category tags of the detected trackers as <c>tracker:tag</c>
    /// pairs (one entry per tag), so each tag stays associated with the
    /// tracker it belongs to.
    /// </summary>
    public List<string> TrackerTags { get; set; } = [];

    /// <summary>
    /// True once the recorded build was scanned for analysis signals (declared
    /// Dhizuku permission, Exodus tracker code signatures). Rows analyzed
    /// before the signals existed stay false so the next pass re-analyzes them
    /// once; the flag is never downgraded.
    /// </summary>
    public bool Inspected { get; set; }

    /// <summary>
    /// Shizuku-family managers the build can drive, canonical order:
    /// <c>shizuku</c>, <c>dhizuku</c>, <c>sui</c>, <c>root</c>. Empty until the
    /// usage pass ran or when no manager evidence was found.
    /// </summary>
    public List<string> Managers { get; set; } = [];

    /// <summary>
    /// Shizuku API form the build uses: <c>user_service</c>, <c>new_process</c>
    /// or <c>permission</c>; null until usage evidence exists.
    /// </summary>
    public string? ApiForm { get; set; }

    /// <summary>
    /// Capabilities the shell command strings suggest, canonical order:
    /// install, uninstall, freeze, appops, system_settings, process,
    /// diagnostics, reboot, wireless_adb, compile. Evidence of capability, not
    /// proof of use.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// True when a fallback path (package installer, install intent) suggests
    /// Shizuku is optional rather than required. Only meaningful with managers.
    /// </summary>
    public bool UsageOptional { get; set; }

    /// <summary>
    /// True once a source scan reached the repo tree, so a row without evidence
    /// is genuinely usage-free rather than unscanned. A failed fetch stays
    /// false so the next pass retries.
    /// </summary>
    public bool UsageSourceScanned { get; set; }

    /// <summary>
    /// Usage extraction generation (<c>0</c> before versioning). Rows older
    /// than <c>AppEnricher.CurrentUsageVersion</c> are re-scanned once.
    /// </summary>
    public int UsageVersion { get; set; }

    /// <summary>
    /// Raw usage evidence as <c>kind|value|source|confidence</c> entries, so a
    /// detection can be audited and replayed without a second scan.
    /// </summary>
    public List<string> UsageEvidence { get; set; } = [];

    /// <summary>Evidence-bound usage summary shown on the details screen; null until generated.</summary>
    public string? UsageSummary { get; set; }

    /// <summary>Generator that produced <see cref="UsageSummary"/>: a model id or <c>template-v1</c>.</summary>
    public string? UsageSummaryModel { get; set; }

    /// <summary>Hash of the evidence bundle the summary was generated from.</summary>
    public string? UsageSummaryHash { get; set; }

    /// <summary>
    /// Summary generation version. A stored summary older than
    /// <c>AppEnricher.CurrentUsageSummaryVersion</c> is regenerated once.
    /// </summary>
    public int UsageSummaryVersion { get; set; }

    /// <summary>
    /// Native ABI the build targets, parsed from the APK's <c>native-code</c>
    /// line; null means a fat/universal build that runs on any device.
    /// </summary>
    public string? Abi { get; set; }

    /// <summary>
    /// Every native ABI of the build from badging, empty until analyzed. The
    /// single-valued <see cref="Abi"/> stays the row identity key; this holds
    /// the full set, so clients can tell a per-arch build from a universal one.
    /// </summary>
    public List<string> Abis { get; set; } = [];

    /// <summary>
    /// Localized app names from badging as <c>locale=label</c> entries, empty
    /// until analyzed. Labels come from <c>application-label-&lt;locale&gt;</c>.
    /// </summary>
    public List<string> LocalizedLabels { get; set; } = [];

    /// <summary>Signer certificate DN(s), newline-joined when key rotation yields several.</summary>
    public string? SignerDn { get; set; }

    /// <summary>Signature schemes that verified, e.g. <c>v1+v2+v3</c>.</summary>
    public string? SignerScheme { get; set; }

    /// <summary>Signer key algorithm with size, e.g. <c>RSA 2048</c>.</summary>
    public string? SignerKeyAlgorithm { get; set; }

    /// <summary>
    /// Extraction generation of the last analysis (<c>0</c> for rows analyzed
    /// before versioning). Rows older than
    /// <c>AppEnricher.CurrentAnalysisVersion</c> are re-analyzed once so newly
    /// added fields get backfilled.
    /// </summary>
    public int AnalysisVersion { get; set; }

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
