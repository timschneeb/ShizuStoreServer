using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// One downloadable artifact exposed by a source. <c>Primary</c> marks the
/// build the source recommends (newest release asset, or the newest index
/// package); <c>Analyze</c> is false for index-only siblings that carry
/// metadata but are not downloaded (F-Droid per-architecture packages).
/// <c>Abi</c> is the native ABI when the artifact is architecture-specific
/// (null for universal builds). <c>Sha256</c> is the source-declared checksum
/// of the artifact file (bare lowercase hex) when the source exposes one, which
/// lets enrichment skip re-downloading an unchanged build.
/// </summary>
public sealed record SourceAsset(
    string Name,
    string Url,
    bool Primary = false,
    bool Analyze = true,
    long Size = 0,
    long? VersionCode = null,
    string? VersionName = null,
    string? Sha256 = null,
    string? SigMd5 = null,
    string? Abi = null,
    DateTimeOffset? ReleasedAt = null);

/// <summary>
/// Latest version of an app from a source. <c>Etag</c> is the response
/// validator stored on the app row for conditional requests; a null
/// resolution means <c>304 Not Modified</c>. <c>Assets</c> holds the primary
/// build plus any sibling builds (for example one APK per ABI).
/// <c>Changelog</c> is the release markdown body when the source publishes one;
/// <c>WebUrl</c> is the browser page of that release (or null when the source
/// is an index without release pages).
/// </summary>
public sealed record SourceRelease(
    string TagName,
    DateTimeOffset? ReleasedAt,
    string? Etag,
    IReadOnlyList<SourceAsset> Assets,
    long TotalDownloads = 0,
    string? Changelog = null,
    string? WebUrl = null);

/// <summary>
/// Source-scoped app identity. <c>Key</c> is the source's own locator:
/// <c>owner/repo</c> for GitHub/GitCode, a GitLab project path, or an F-Droid
/// package id.
/// </summary>
public sealed record SourceTarget(SourceKind Kind, string Key)
{
    /// <summary>Splits an <c>owner/repo</c> key (GitHub, GitCode).</summary>
    public (string Owner, string Repo) SplitRepoKey()
    {
        var slash = Key.IndexOf('/');
        return slash < 0 ? (Key, string.Empty) : (Key[..slash], Key[(slash + 1)..]);
    }
}

/// <summary>
/// Common contract for every app source. Implementations fetch the newest
/// release (forge) or index entry (F-Droid) and return its artifacts with the
/// primary marked, so the enrichment pipeline is shared across sources.
/// </summary>
public interface IAppSource
{
    /// <returns>
    /// Newest release, or null when the server answered <c>304 Not Modified</c>
    /// for <paramref name="etag"/>.
    /// </returns>
    Task<SourceRelease?> GetLatestReleaseAsync(SourceTarget target, string? etag, CancellationToken ct = default);
}
