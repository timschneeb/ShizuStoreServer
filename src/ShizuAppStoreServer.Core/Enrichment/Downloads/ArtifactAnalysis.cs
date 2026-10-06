using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment.Apk;
using ShizuAppStoreServer.Core.Enrichment.Icons;

namespace ShizuAppStoreServer.Core.Enrichment.Downloads;

internal sealed record ArtifactAnalysis(
    string ArtifactUrl,
    string? ArchiveEntry,
    SourceKind LockSource,
    string? Etag,
    BadgingInfo Badging,
    string FileSha256,
    long FileSize,
    string? SigSha256,
    string? SigMd5,
    DateTimeOffset? ReleasedAt,
    ProcessedIcon? Icon,
    ApkInspection Inspection,
    ApkSignerInfo Signers,
    string? ReleaseTag = null);
