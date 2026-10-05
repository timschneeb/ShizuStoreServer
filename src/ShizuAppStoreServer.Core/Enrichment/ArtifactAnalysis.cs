using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Enrichment;

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
