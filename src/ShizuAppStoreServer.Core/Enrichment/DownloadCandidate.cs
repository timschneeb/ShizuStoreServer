using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Enrichment;

internal sealed record DownloadCandidate(
    SourceKind Source,
    string? SourceRef,
    string ApkUrl,
    string? ArchiveEntry,
    long? VersionCode,
    string? VersionName,
    long? SizeBytes,
    string? Sha256,
    string? SigSha256,
    string? SigMd5,
    int? MinSdk,
    string? Abi = null,
    string? PackageName = null,
    bool Analyzed = false,
    int? TargetSdk = null,
    int? CompileSdk = null,
    IReadOnlyList<string>? Locales = null,
    IReadOnlyList<string>? Abis = null,
    IReadOnlyList<string>? LocalizedLabels = null,
    string? SignerDn = null,
    string? SignerScheme = null,
    string? SignerKeyAlgorithm = null,
    int AnalysisVersion = 0,
    ApkInspection? Inspection = null,
    string? ReleaseTag = null,
    bool ShizukuDeclared = false);
