using System.Net;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Enrichment;

internal sealed class GitHubReleaseEnricher(
    IEnrichmentPipeline pipeline,
    IGitHubReleaseClient github,
    IAppSourceOverrides sourceOverrides,
    DownloadStore downloadStore,
    StarHistoryService starHistory,
    IUsageAnalysisQueue? usageQueue = null)
{
    internal async Task<EnrichResult> EnrichFromGitHubAsync(
        App app, string owner, string repo, DateTimeOffset now, CancellationToken ct)
    {
        var target = new SourceTarget(SourceKind.GitHub, $"{owner}/{repo}");

        if (sourceOverrides.ScansAllReleases(owner, repo))
        {
            return await EnrichFromAllReleasesAsync(app, owner, repo, target, now, ct);
        }

        // A failing release list must not gate repo metadata: rate limits or
        // missing releases would otherwise also blank stars and developer.
        SourceRelease? latest;
        try
        {
            latest = await pipeline.TimedAsync(
                $"github release list {owner}/{repo}",
                () => github.GetLatestReleaseAsync(target, pipeline.ChangelogEtag(app), ct));
        }
        catch (GitHubApiException ex)
        {
            await starHistory.RefreshGitHubStatsAsync(app, owner, repo, now, ct);
            return await HandleGitHubFailureAsync(app, ex, now, ct);
        }

        SourceRelease release;
        try
        {
            await starHistory.RefreshGitHubStatsAsync(app, owner, repo, now, ct);

            await pipeline.RefreshFullDescriptionAsync(
                app,
                () => github.GetLinkedMarkdownAsync(app.Url, ct),
                () => github.GetReadmeMarkdownAsync(owner, repo, ct));

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
                            $"github release list {owner}/{repo} recheck",
                            () => github.GetLatestReleaseAsync(target, null, ct));
                    }
                    catch (GitHubApiException)
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

            app.DownloadTotal = latest.TotalDownloads;
            app.Changelog = pipeline.NormalizeChangelog(latest.Changelog);
            app.ChangelogUrl = latest.WebUrl;
            release = latest;
        }
        catch (GitHubApiException ex)
        {
            return await HandleGitHubFailureAsync(app, ex, now, ct);
        }

        // A forge whose newest release ships no binary is no longer the
        // download channel; remember that so later passes and primary
        // recomputes keep preferring an alternative source.
        app.ForgeAssetsStale = release.IsOlderFallback;
        if (release.IsOlderFallback && await TryAlternativeSourceAsync(app, release, now, ct) is { } alternative)
        {
            return alternative;
        }

        if (await pipeline.EnrichFromReleaseAsync(app, SourceKind.GitHub, release, urlIdentifiesVersion: true, now, ct) is { } enriched)
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

        return pipeline.Fail(app, now, $"GitHub release {release.TagName} of {owner}/{repo} has no .apk asset.");
    }

    /// <summary>
    /// The forge's newest release ships no installable artifact, so an
    /// F-Droid build is the better served version when one exists. A missing
    /// or failing alternative returns null and the older forge APK is used,
    /// so index trouble never costs the download.
    /// </summary>
    private async Task<EnrichResult?> TryAlternativeSourceAsync(
        App app, SourceRelease release, DateTimeOffset now, CancellationToken ct)
    {
        var previous = await downloadStore.PrimaryDownloadAsync(app, ct);
        var result = await pipeline.TryFdroidFallbackAsync(app, now, ct);
        if (result is null || result.Outcome == EnrichOutcome.Failed)
        {
            return null;
        }

        // The forge release notes are the app's changelog; the index
        // description only stands when the forge publishes none.
        if (release.Changelog is not null)
        {
            app.Changelog = pipeline.NormalizeChangelog(release.Changelog);
            app.ChangelogUrl = release.WebUrl;
        }

        // The stored report describes the old forge build now; re-run it
        // against the served alternative build.
        var served = await downloadStore.PrimaryDownloadAsync(app, ct);
        if (usageQueue is not null
            && app.UsageAnalyzedAt is not null
            && previous is not null
            && served is not null
            && previous.ApkUrl != served.ApkUrl)
        {
            await usageQueue.EnqueueAsync(
                app, artifactChanged: true, firstAnalysis: false,
                releaseRef: served.ReleaseTag ?? ReleaseTagParser.FromArtifactUrl(served.ApkUrl), ct);
        }

        return result;
    }

    /// <summary>
    /// A release-list failure must not lock the source: an app with no
    /// releases may still be F-Droid-only, and a transient API error (rate
    /// limit, 5xx) is not a content change.
    /// </summary>
    private async Task<EnrichResult> HandleGitHubFailureAsync(
        App app, GitHubApiException ex, DateTimeOffset now, CancellationToken ct)
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

        return pipeline.Fail(app, now, $"GitHub: {ex.Message}");
    }

    /// <summary>
    /// SmartspacerPlugins publishes each plugin as its own GitHub release, so
    /// the newest release only carries one of the packages. Scan every release
    /// and let the shared pipeline group the assets by package.
    /// </summary>
    private async Task<EnrichResult> EnrichFromAllReleasesAsync(
        App app, string owner, string repo, SourceTarget target, DateTimeOffset now, CancellationToken ct)
    {
        await starHistory.RefreshGitHubStatsAsync(app, owner, repo, now, ct);

        await pipeline.RefreshFullDescriptionAsync(
            app,
            () => github.GetLinkedMarkdownAsync(app.Url, ct),
            () => github.GetReadmeMarkdownAsync(owner, repo, ct));

        IReadOnlyList<SourceRelease> releases;
        try
        {
            releases = await github.GetAllReleasesAsync(target, ct);
        }
        catch (GitHubApiException ex)
        {
            return await HandleGitHubFailureAsync(app, ex, now, ct);
        }

        if (releases.Count == 0)
        {
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "no releases" };
        }

        app.DownloadTotal = releases.Sum(r => r.TotalDownloads);
        app.Changelog = pipeline.NormalizeChangelog(releases[0].Changelog);
        app.ChangelogUrl = releases[0].WebUrl;
        var assets = releases.SelectMany(r => r.Assets).ToList();
        if (await pipeline.EnrichFromAssetsAsync(
                app, SourceKind.GitHub, assets, null, releaseReleasedAt: null, releaseTag: null,
                urlIdentifiesVersion: false, alwaysAnalyzePrimary: false, isPrerelease: false, now, ct) is { } enriched)
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

        return pipeline.Fail(app, now, $"GitHub repo {owner}/{repo} has no .apk asset.");
    }
}
