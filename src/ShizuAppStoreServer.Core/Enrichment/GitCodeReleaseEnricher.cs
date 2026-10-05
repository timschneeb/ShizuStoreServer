using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

internal sealed class GitCodeReleaseEnricher(
    IEnrichmentPipeline pipeline,
    IGitCodeReleaseClient? gitcode,
    IGitHubReleaseClient github)
{
    /// <summary>
    /// hlbmerge_flutter's APK builds exist only on the GitCode mirror. The
    /// asset URL embeds the tag, so an unchanged URL plus a recorded version
    /// code skips the download.
    /// </summary>
    internal async Task<EnrichResult> EnrichFromGitCodeAsync(
        App app, GitCodeMirror mirror, DateTimeOffset now, CancellationToken ct)
    {
        // The APK lives on the mirror but the list links the GitHub repo, so
        // the README still comes from GitHub.
        await pipeline.RefreshFullDescriptionAsync(
            app,
            () => github.GetLinkedMarkdownAsync(app.Url, ct),
            () => github.GetReadmeMarkdownAsync(mirror.ReadmeOwner, mirror.ReadmeRepo, ct));

        SourceRelease release;
        try
        {
            var target = new SourceTarget(
                SourceKind.Other, $"{mirror.Owner}/{mirror.Repo}");
            var latest = await pipeline.TimedAsync(
                $"gitcode release list {mirror.Owner}/{mirror.Repo}",
                () => gitcode!.GetLatestReleaseAsync(target, app.EnrichEtag, ct));
            if (latest is null)
            {
                app.LastCheckedAt = now;
                return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "release feed not modified" };
            }

            release = latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return pipeline.Fail(app, now, $"GitCode: {ex.Message}");
        }

        if (await pipeline.EnrichFromReleaseAsync(app, SourceKind.Other, release, urlIdentifiesVersion: true, now, ct) is { } enriched)
        {
            return enriched;
        }

        return pipeline.Fail(app, now, $"GitCode release {release.TagName} of "
            + $"{mirror.Owner}/{mirror.Repo} has no .apk asset.");
    }
}
