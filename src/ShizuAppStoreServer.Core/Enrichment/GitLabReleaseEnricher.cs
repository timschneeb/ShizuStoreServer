using System.Net;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

internal sealed class GitLabReleaseEnricher(
    IEnrichmentPipeline pipeline,
    IGitLabReleaseClient gitlab,
    StarHistoryService starHistory,
    DownloadStore downloadStore)
{
    internal async Task<EnrichResult> EnrichFromGitLabAsync(
        App app, string projectPath, DateTimeOffset now, CancellationToken ct)
    {
        starHistory.ApplyGitLabAuthor(app, projectPath);

        var target = new SourceTarget(SourceKind.GitLab, projectPath);

        SourceRelease? latest;
        try
        {
            latest = await pipeline.TimedAsync(
                $"gitlab release list {projectPath}",
                () => gitlab.GetLatestReleaseAsync(target, pipeline.ChangelogEtag(app), ct));
        }
        catch (GitLabApiException ex)
        {
            await starHistory.RefreshGitLabStatsAsync(app, projectPath, ct);
            if (ex.Status == HttpStatusCode.NotFound
                && await pipeline.TryFdroidFallbackAsync(app, now, ct) is { } rescued)
            {
                return rescued;
            }

            if (await pipeline.TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
            {
                return play;
            }

            return pipeline.Fail(app, now, $"GitLab: {ex.Message}");
        }

        SourceRelease release;
        try
        {
            await starHistory.RefreshGitLabStatsAsync(app, projectPath, ct);

            await pipeline.RefreshFullDescriptionAsync(
                app,
                () => gitlab.GetLinkedMarkdownAsync(app.Url, ct),
                () => gitlab.GetReadmeMarkdownAsync(projectPath, ct));

            if (latest is null)
            {
                // A 304 would keep pre-fix rows permission-less forever (see
                // NeedsPermissionHeal), and pre-signal rows would stay without
                // their analysis signals; refetch the list once and re-analyze
                // below. A failing refetch is not an upstream change, so stay
                // up-to-date instead of failing the pass.
                var current = await downloadStore.PrimaryDownloadAsync(app, ct);
                if (DownloadStore.NeedsPermissionHeal(app, current) || DownloadStore.NeedsSignalHeal(current) || DownloadStore.NeedsAnalysisHeal(current))
                {
                    try
                    {
                        latest = await pipeline.TimedAsync(
                            $"gitlab release list {projectPath} recheck",
                            () => gitlab.GetLatestReleaseAsync(target, null, ct));
                    }
                    catch (GitLabApiException)
                    {
                        latest = null;
                    }
                }

                if (latest is null)
                {
                    app.LastCheckedAt = now;
                    return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "release feed not modified" };
                }
            }

            release = latest;
            app.Changelog = pipeline.NormalizeChangelog(release.Changelog);
            app.ChangelogUrl = release.WebUrl;
        }
        catch (GitLabApiException ex)
        {
            if (ex.Status == HttpStatusCode.NotFound
                && await pipeline.TryFdroidFallbackAsync(app, now, ct) is { } rescued)
            {
                return rescued;
            }

            if (await pipeline.TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
            {
                return play;
            }

            return pipeline.Fail(app, now, $"GitLab: {ex.Message}");
        }

        if (await pipeline.EnrichFromReleaseAsync(app, SourceKind.GitLab, release, urlIdentifiesVersion: true, now, ct) is { } enriched)
        {
            return enriched;
        }

        if (await pipeline.TryFdroidFallbackAsync(app, now, ct) is { } fdroidFallback)
        {
            return fdroidFallback;
        }

        if (await pipeline.TryPlayRedirectFallbackAsync(app, now, ct) is { } playFallback)
        {
            return playFallback;
        }

        return pipeline.Fail(app, now, $"GitLab release {release.TagName} of {projectPath} has no .apk asset link.");
    }
}
