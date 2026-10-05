using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Shared pipeline surface the per-source enrichers call back into. Keeps the
/// source classes independent of each other: cross-source fallbacks (F-Droid,
/// Play), the shared release/asset pipeline and the stamping helpers all route
/// through the facade.
/// </summary>
internal interface IEnrichmentPipeline
{
    int MaxFullDescriptionChars { get; }

    int CurrentAnalysisVersion { get; }

    Task<T> TimedAsync<T>(string label, Func<Task<T>> action);

    string NormalizeChangelog(string? value);

    string? ChangelogEtag(App app);

    Task RefreshFullDescriptionAsync(
        App app, Func<Task<ReadmeDocument?>> fetchLinked, Func<Task<ReadmeDocument?>> fetchDefault);

    Task<EnrichResult?> EnrichFromReleaseAsync(
        App app, SourceKind kind, SourceRelease release, bool urlIdentifiesVersion,
        DateTimeOffset now, CancellationToken ct);

    Task<EnrichResult?> EnrichFromAssetsAsync(
        App app, SourceKind kind, IReadOnlyList<SourceAsset> assets, string? etag,
        DateTimeOffset? releaseReleasedAt, string? releaseTag, bool urlIdentifiesVersion, bool alwaysAnalyzePrimary,
        bool isPrerelease, DateTimeOffset now, CancellationToken ct);

    Task<EnrichResult?> TryFdroidFallbackAsync(App app, DateTimeOffset now, CancellationToken ct);

    Task<EnrichResult?> TryPlayRedirectFallbackAsync(App app, DateTimeOffset now, CancellationToken ct);

    Task ResolveCandidateApkAsync(
        App app, string url, string? etag, SourceKind kind, DateTimeOffset now, CancellationToken ct);

    Task ResolveCandidateZipAsync(
        App app, string url, string? etag, SourceKind kind, DateTimeOffset now, CancellationToken ct);

    bool TryParseFdroid(string? primary, string? secondary,
        out string repoBase, out string packageId, out bool fromPrimary);

    EnrichResult Fail(App app, DateTimeOffset now, string message);
}
