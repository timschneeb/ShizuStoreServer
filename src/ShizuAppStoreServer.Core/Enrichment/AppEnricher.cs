using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Per-app enrichment. Resolution is forge-first: GitHub, GitLab and GitCode
/// releases resolve to an APK asset (temp download, <c>aapt2 dump badging</c>,
/// <c>apksigner</c> fingerprints, best-density icon, fill the APK columns,
/// delete the APK), because F-Droid builds are delayed and (unless
/// reproducible) signed with a different key. F-Droid/Izzy packages resolve
/// via the repo <c>index-v2.json</c>, downloading the APK on version change
/// for full analysis. Candidates resolve symmetrically: a forge primary is
/// also looked up in the F-Droid main index and recorded as an alternate, and
/// a forge with no usable artifact falls back to the main index by matching
/// the entry's source URL. There is no source lock; candidates never alter
/// the primary except through the primary election. Multi-package releases
/// are grouped by APK label into root and variant rows. Special-case release
/// homes and mirror feeds are resolved before dispatch. Play-sole-source apps
/// become redirects to their Play listing instead of being hidden. Failures
/// record <c>last_error</c> and keep previous good values. Does not call
/// <c>SaveChanges</c>, the caller batches (fast loop, tests).
/// </summary>
public sealed class AppEnricher(
    IGitHubReleaseClient github,
    IGitLabReleaseClient gitlab,
    FdroidIndexProvider fdroid,
    IAapt2Runner aapt2,
    IApkSignerRunner signer,
    ILauncherIconService launcherIcons,
    HttpClient downloads,
    EnrichmentOptions options,
    ShizuDbContext db,
    IGitCodeReleaseClient? gitcode = null,
    IPlayStoreClient? play = null,
    IzzyStatsProvider? izzyStats = null,
    ILogger<AppEnricher>? log = null,
    IRepoScreenshotResolver? repoScreenshots = null,
    ITrackerCatalog? trackers = null,
    IUsageAnalysisQueue? usageQueue = null,
    IAppSourceOverrides? sourceOverrides = null)
{
    // Hand-built callers (tests) fall back to the in-code special cases;
    // production registers the singleton instance.
    private readonly IAppSourceOverrides sourceOverrides =
        sourceOverrides ?? StaticAppSourceOverrides.Instance;

    private readonly StarHistoryService starHistory = new(github, gitlab, db, log);
    private readonly ScreenshotService screenshots = new(fdroid, repoScreenshots, options, db, log);
    private readonly IconPipeline icons = new(launcherIcons, downloads, options, play, log, db);
    private ArtifactAnalyzer? artifactsBacking;
    private ArtifactAnalyzer artifacts =>
        artifactsBacking ??= new ArtifactAnalyzer(aapt2, signer, downloads, options, trackers, icons, log);

    private readonly DownloadStore downloadStore = new(db, usageQueue, trackers, izzyStats);
    private VariantGrouper? variantsBacking;
    private VariantGrouper variants =>
        variantsBacking ??= new VariantGrouper(db, downloadStore, icons, log);

    // READMEs are unbounded; the full-description screen only needs a sane
    // excerpt, so cap what we persist and send.
    private const int MaxFullDescriptionChars = 200_000;

    // Release notes and F-Droid long descriptions are unbounded too; the
    // changelog screen only needs a sane excerpt.
    private const int MaxChangelogChars = 100_000;

    // A row that has never captured release notes fetches unconditionally
    // once, so rows enriched before this field existed heal; afterwards the
    // stored ETag throttles the fetch again. An empty (not null) changelog
    // marks "fetched, this source publishes none".
    private static string? ChangelogEtag(App app) => app.Changelog is null ? null : app.EnrichEtag;

    /// <summary>
    /// Times an upstream feed fetch into the job event stream; without these
    /// lines a stalled API call is indistinguishable from a slow download.
    /// </summary>
    private async Task<T> TimedAsync<T>(string label, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await action();
            JobContext.Current?.Release($"{label} {(result is null ? "304" : "ok")} in {EnrichmentQueries.Elapsed(started)}ms");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobContext.Current?.Release(
                $"{label} failed after {EnrichmentQueries.Elapsed(started)}ms: {ex.Message}", level: JobEventLevel.Warning);
            throw;
        }
    }

    private static string NormalizeChangelog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxChangelogChars ? trimmed[..MaxChangelogChars] : trimmed;
    }

    /// <summary>
    /// Raw markdown, not rendered HTML: the client renders markdown. When the
    /// list entry links a markdown README directly (localized README_EN.md on
    /// projects whose landing README is non-English), that file wins over the
    /// repo default. Both are refetched every pass so the stored snapshot and
    /// the client's live refetch URL stay current; a failed fetch keeps the
    /// previous text.
    /// </summary>
    private async Task RefreshFullDescriptionAsync(
        App app, Func<Task<ReadmeDocument?>> fetchLinked, Func<Task<ReadmeDocument?>> fetchDefault)
    {
        var document = ReadmeLink.IsReadme(app.Url)
            ? await fetchLinked() ?? await fetchDefault()
            : await fetchDefault();

        if (document is { Markdown.Length: > 0 })
        {
            SetFullDescription(app, document.Markdown);
            app.ReadmeUrl = document.RawUrl;
        }
    }

    private static void SetFullDescription(App app, string value) =>
        app.FullDescription = value.Length > MaxFullDescriptionChars
            ? value[..MaxFullDescriptionChars]
            : value;

    public async Task<EnrichResult> EnrichAsync(
        App app, DateTimeOffset now, CancellationToken ct = default, bool force = false)
    {
        // A pre-fix recompute bug left some row sets without a primary; the
        // unchanged-asset short-circuits would never revisit them, so repair
        // from the stored rows before anything else.
        await downloadStore.HealMissingPrimaryAsync(app, ct);
        await downloadStore.HealTrackerTagsAsync(app, ct);

        if (!force
            && app.LastCheckedAt is { } checkedAt
            && checkedAt + (app.LastError is null ? options.SuccessRecheckInterval : options.FailedRecheckInterval) > now)
        {
            return new EnrichResult(EnrichOutcome.SkippedFresh, null) { Detail = "within recheck window" };
        }

        // Per-call cache: every ABI variant of a release resolves the same
        // launcher icon, and a resolve may be a full Gradle render; one per
        // package (and one per call, so a reused instance cannot go stale).
        icons.BeginPass();

        try
        {
            var availabilityBefore = app.Availability;
            var storeUrlBefore = app.StoreUrl;

            var result = await DispatchAsync(app, now, ct);
            await screenshots.ApplyScreenshotsAsync(app, now, ct);

            // External-only Play apps never get an APK; give them the real
            // listing icon instead of a generated avatar and stop counting
            // them as failures on every pass.
            if (result.Outcome == EnrichOutcome.Failed
                && !await downloadStore.HasDownloadsAsync(app, ct)
                && app.IconHash is null
                && await icons.TryPlayIconAsync(app, now, ct))
            {
                app.LastError = null;
                result = new EnrichResult(EnrichOutcome.AvatarFallback, null);
            }

            if (result.Outcome != EnrichOutcome.Failed)
            {
                if (app.PublishedAt is null)
                {
                    // Unpublished rows are hidden from every read path. By here
                    // a successful check has given the row an icon and an
                    // availability, so publish it; the UpdatedAt stamp guarantees
                    // the changes feed ships it even when no summary-visible
                    // column moved.
                    app.PublishedAt = now;
                    app.UpdatedAt = now;
                }

                await StampVariantGroupAsync(app, now, ct);
            }

            if (app.Availability != availabilityBefore || app.StoreUrl != storeUrlBefore)
            {
                // The changes feed only ships rows whose updated_at moved, so an
                // enrich that flips what the row serves must stamp the commit
                // time or incremental clients never learn about the flip
                // (live 2026-09-27: healed Play-only rows stayed invisible).
                app.UpdatedAt = now;
            }

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (an inner HttpClient/linked-cts fired while the outer
            // token lives): a failed upstream, not a shutdown. Record it so
            // backoff applies and the cause stays visible, an unrecorded
            // Failed once hid a whole pass (live 2026-09-12). Genuine
            // cancellation still propagates to abort the pass.
            return Fail(app, now, "Upstream timed out.");
        }
        catch (HttpRequestException ex)
        {
            // Transport failure (DNS, TLS, reset): same treatment, with the
            // cause recorded instead of a silent Failed.
            return Fail(app, now, $"Upstream error: {ex.Message}");
        }
    }

    /// <summary>
    /// Variants are enriched through their root's pass and never selected
    /// directly, so a completed root check must refresh them too. The skip
    /// paths that stamp only the root (unchanged release, 304, all-assets-known)
    /// would otherwise freeze variant timestamps until their own artifact is
    /// re-analyzed, and the health snapshot flags them stale after two windows.
    /// </summary>
    private async Task StampVariantGroupAsync(App root, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var variant in await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct))
        {
            variant.LastCheckedAt = now;
            variant.LastError = null;
            // Variants share the root's serve surface: one created while the
            // root was still unpublished (or by code older than the publish
            // gate) must not stay hidden after the root publishes.
            if (root.PublishedAt is { } publishedAt && variant.PublishedAt is null)
            {
                variant.PublishedAt = publishedAt;
            }

            // Variants never run their own EnrichAsync, so they only inherit
            // the root's README snapshot and raw URL at creation. Mirror them
            // each pass or rows created before the route existed keep serving
            // the stale snapshot without a live refetch URL.
            variant.FullDescription = root.FullDescription;
            variant.ReadmeUrl = root.ReadmeUrl;
            // Variants never run their own EnrichAsync, so the per-app primary
            // heal cannot reach them; repair here, or a flag lost to an
            // interrupted recompute would stay lost (live 2026-09-25).
            await downloadStore.HealMissingPrimaryAsync(variant, ct);
            await downloadStore.HealTrackerTagsAsync(variant, ct);
        }
    }

    /// <summary>
    /// Screenshots-only maintenance refresh (the admin trigger): re-resolves
    /// F-Droid/Izzy and re-runs the repo fallback even when repo-sourced URLs
    /// are already stored, so dead ones are cleared and fresh ones replace
    /// them. No APK work. Returns <c>Enriched</c> only when the URL list
    /// changed.
    /// </summary>
    public Task<EnrichResult> RefreshScreenshotsAsync(
        App app, DateTimeOffset now, CancellationToken ct = default) =>
        screenshots.RefreshScreenshotsAsync(app, now, ct);

    private async Task<EnrichResult> DispatchAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        var githubPrimary = SourceClassifier.TryParseGitHubRepo(app.Url, out var owner, out var repo);
        if (githubPrimary || SourceClassifier.TryParseGitHubRepo(app.SourceUrl, out owner, out repo))
        {
            app.SourceKind = SourceKind.GitHub;
            KeepPlayStoreUrl(app, githubPrimary);

            // Instafel and LinkSheet publish from separate release repos; the
            // list target stays the analysis source for usage and screenshots.
            (owner, repo) = sourceOverrides.RemapReleaseHome(owner, repo);

            EnrichResult result;
            var mirror = sourceOverrides.GitCodeMirrorFor(owner, repo);
            if (gitcode is not null && mirror is not null)
            {
                result = await EnrichFromGitCodeAsync(app, mirror, now, ct);
            }
            else
            {
                result = await EnrichFromGitHubAsync(app, owner, repo, now, ct);
            }

            if (result.Outcome is EnrichOutcome.Enriched or EnrichOutcome.UpToDate
                && app.SourceKind is not (SourceKind.FDroid or SourceKind.Izzy))
            {
                await ResolveFdroidCandidateAsync(app, now, ct);
            }

            return result;
        }

        var gitlabPrimary = SourceClassifier.TryParseGitLabRepo(app.Url, out var project);
        if (gitlabPrimary || SourceClassifier.TryParseGitLabRepo(app.SourceUrl, out project))
        {
            app.SourceKind = SourceKind.GitLab;
            KeepPlayStoreUrl(app, gitlabPrimary);
            var result = await EnrichFromGitLabAsync(app, project, now, ct);
            if (result.Outcome is EnrichOutcome.Enriched or EnrichOutcome.UpToDate
                && app.SourceKind is not (SourceKind.FDroid or SourceKind.Izzy))
            {
                await ResolveFdroidCandidateAsync(app, now, ct);
            }

            return result;
        }

        if (TryParseFdroid(app.Url, app.SourceUrl, out var repoBase, out var packageId, out var fdroidPrimary))
        {
            var kind = repoBase == FdroidRepos.IzzyBase ? SourceKind.Izzy : SourceKind.FDroid;
            app.SourceKind = kind;
            KeepPlayStoreUrl(app, fdroidPrimary);
            return await EnrichFromFdroidAsync(app, repoBase, packageId, now, ct);
        }

        return await EnrichFallbackAsync(app, now, ct);
    }

    private static bool TryParseFdroid(string? primary, string? secondary,
        out string repoBase, out string packageId, out bool fromPrimary)
    {
        repoBase = string.Empty;
        if (SourceClassifier.TryParseFdroidPackage(primary, out packageId))
        {
            repoBase = FdroidRepos.BaseFor(SourceClassifier.Classify(primary));
            fromPrimary = true;
            return true;
        }

        fromPrimary = false;
        if (SourceClassifier.TryParseFdroidPackage(secondary, out packageId))
        {
            repoBase = FdroidRepos.BaseFor(SourceClassifier.Classify(secondary));
            return true;
        }

        return false;
    }

    // Play + forge combos: the forge wins for the APK, Play stays as store_url.
    private static void KeepPlayStoreUrl(App app, bool forgeFromPrimary)
    {
        var otherUrl = forgeFromPrimary ? app.SourceUrl : app.Url;
        if (SourceClassifier.Classify(otherUrl) == SourceKind.Play)
        {
            app.StoreUrl = otherUrl;
        }
    }

    private async Task<EnrichResult> EnrichFromGitHubAsync(
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
            latest = await TimedAsync(
                $"github release list {owner}/{repo}",
                () => github.GetLatestReleaseAsync(target, ChangelogEtag(app), ct));
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

            await RefreshFullDescriptionAsync(
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
                        latest = await TimedAsync(
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
            app.Changelog = NormalizeChangelog(latest.Changelog);
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

        if (await EnrichFromReleaseAsync(app, SourceKind.GitHub, release, urlIdentifiesVersion: true, now, ct) is { } enriched)
        {
            return enriched;
        }

        if (await TryFdroidFallbackAsync(app, now, ct) is { } fdroidFallback)
        {
            return fdroidFallback;
        }

        if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } playFallback)
        {
            return playFallback;
        }

        return Fail(app, now, $"GitHub release {release.TagName} of {owner}/{repo} has no .apk asset.");
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
        var result = await TryFdroidFallbackAsync(app, now, ct);
        if (result is null || result.Outcome == EnrichOutcome.Failed)
        {
            return null;
        }

        // The forge release notes are the app's changelog; the index
        // description only stands when the forge publishes none.
        if (release.Changelog is not null)
        {
            app.Changelog = NormalizeChangelog(release.Changelog);
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
            && await TryFdroidFallbackAsync(app, now, ct) is { } rescued)
        {
            return rescued;
        }

        if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
        {
            return play;
        }

        return Fail(app, now, $"GitHub: {ex.Message}");
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

        await RefreshFullDescriptionAsync(
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
        app.Changelog = NormalizeChangelog(releases[0].Changelog);
        app.ChangelogUrl = releases[0].WebUrl;
        var assets = releases.SelectMany(r => r.Assets).ToList();
        if (await EnrichFromAssetsAsync(
                app, SourceKind.GitHub, assets, null, releaseReleasedAt: null, releaseTag: null,
                urlIdentifiesVersion: false, alwaysAnalyzePrimary: false, isPrerelease: false, now, ct) is { } enriched)
        {
            return enriched;
        }

        if (await TryFdroidFallbackAsync(app, now, ct) is { } fdroidFallback)
        {
            return fdroidFallback;
        }

        if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } playFallback)
        {
            return playFallback;
        }

        return Fail(app, now, $"GitHub repo {owner}/{repo} has no .apk asset.");
    }

    /// <summary>
    /// hlbmerge_flutter's APK builds exist only on the GitCode mirror. The
    /// asset URL embeds the tag, so an unchanged URL plus a recorded version
    /// code skips the download.
    /// </summary>
    private async Task<EnrichResult> EnrichFromGitCodeAsync(
        App app, GitCodeMirror mirror, DateTimeOffset now, CancellationToken ct)
    {
        // The APK lives on the mirror but the list links the GitHub repo, so
        // the README still comes from GitHub.
        await RefreshFullDescriptionAsync(
            app,
            () => github.GetLinkedMarkdownAsync(app.Url, ct),
            () => github.GetReadmeMarkdownAsync(mirror.ReadmeOwner, mirror.ReadmeRepo, ct));

        SourceRelease release;
        try
        {
            var target = new SourceTarget(
                SourceKind.Other, $"{mirror.Owner}/{mirror.Repo}");
            var latest = await TimedAsync(
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
            return Fail(app, now, $"GitCode: {ex.Message}");
        }

        if (await EnrichFromReleaseAsync(app, SourceKind.Other, release, urlIdentifiesVersion: true, now, ct) is { } enriched)
        {
            return enriched;
        }

        return Fail(app, now, $"GitCode release {release.TagName} of "
            + $"{mirror.Owner}/{mirror.Repo} has no .apk asset.");
    }

    private async Task<EnrichResult> EnrichFromGitLabAsync(
        App app, string projectPath, DateTimeOffset now, CancellationToken ct)
    {
        starHistory.ApplyGitLabAuthor(app, projectPath);

        var target = new SourceTarget(SourceKind.GitLab, projectPath);

        SourceRelease? latest;
        try
        {
            latest = await TimedAsync(
                $"gitlab release list {projectPath}",
                () => gitlab.GetLatestReleaseAsync(target, ChangelogEtag(app), ct));
        }
        catch (GitLabApiException ex)
        {
            await starHistory.RefreshGitLabStatsAsync(app, projectPath, ct);
            if (ex.Status == HttpStatusCode.NotFound
                && await TryFdroidFallbackAsync(app, now, ct) is { } rescued)
            {
                return rescued;
            }

            if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
            {
                return play;
            }

            return Fail(app, now, $"GitLab: {ex.Message}");
        }

        SourceRelease release;
        try
        {
            await starHistory.RefreshGitLabStatsAsync(app, projectPath, ct);

            await RefreshFullDescriptionAsync(
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
                        latest = await TimedAsync(
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
            app.Changelog = NormalizeChangelog(release.Changelog);
            app.ChangelogUrl = release.WebUrl;
        }
        catch (GitLabApiException ex)
        {
            if (ex.Status == HttpStatusCode.NotFound
                && await TryFdroidFallbackAsync(app, now, ct) is { } rescued)
            {
                return rescued;
            }

            if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
            {
                return play;
            }

            return Fail(app, now, $"GitLab: {ex.Message}");
        }

        if (await EnrichFromReleaseAsync(app, SourceKind.GitLab, release, urlIdentifiesVersion: true, now, ct) is { } enriched)
        {
            return enriched;
        }

        if (await TryFdroidFallbackAsync(app, now, ct) is { } fdroidFallback)
        {
            return fdroidFallback;
        }

        if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } playFallback)
        {
            return playFallback;
        }

        return Fail(app, now, $"GitLab release {release.TagName} of {projectPath} has no .apk asset link.");
    }

    /// <summary>
    /// Shared release pipeline for every forge source: analyze the release's
    /// primary asset plus every sibling APK, group the results by Android
    /// package (one repo can ship several distinct apps) and apply each
    /// analysis to the row that owns its package. Returns null when the
    /// release carries no usable .apk or .zip so the caller can run fallbacks.
    /// </summary>
    private async Task<EnrichResult?> EnrichFromReleaseAsync(
        App app, SourceKind kind, SourceRelease release, bool urlIdentifiesVersion,
        DateTimeOffset now, CancellationToken ct) =>
        await EnrichFromAssetsAsync(
            app, kind, release.Assets, release.Etag, release.ReleasedAt, release.TagName, urlIdentifiesVersion,
            alwaysAnalyzePrimary: true, isPrerelease: release.IsPrerelease, now, ct);

    /// <summary>
    /// Shared forge/index asset pipeline. Artifacts whose checksum matches the
    /// recorded one are skipped before the expensive tools run: the source
    /// digest avoids the download too, and when the source exposes none the
    /// file is hashed straight after download. A newer release, or a row whose
    /// package/icon is missing (or whose icon file vanished), always analyzes
    /// once so a version bump or a partial record still heals.
    /// </summary>
    private async Task<EnrichResult?> EnrichFromAssetsAsync(
        App app, SourceKind kind, IReadOnlyList<SourceAsset> assets, string? etag,
        DateTimeOffset? releaseReleasedAt, string? releaseTag, bool urlIdentifiesVersion, bool alwaysAnalyzePrimary,
        bool isPrerelease, DateTimeOffset now, CancellationToken ct)
    {
        var downloads = await downloadStore.LoadGroupDownloadsAsync(app, ct);
        var ownerById = new Dictionary<long, App> { [app.Id] = app };
        foreach (var variant in await EnrichmentQueries.LoadVariantGroupAsync(db, app, ct))
        {
            ownerById[variant.Id] = variant;
        }

        // Record every artifact already analyzed for this group, including twins
        // remembered on the row's current version, so repeats never re-download.
        var recordedByUrl = new Dictionary<string, AppDownload>(StringComparer.Ordinal);
        var recordedShaByUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var download in downloads)
        {
            recordedByUrl[download.ApkUrl] = download;
            if (download.Sha256 is { Length: > 0 } storedSha)
            {
                recordedShaByUrl[download.ApkUrl] = storedSha;
            }

            foreach (var entry in download.AnalyzedArtifacts)
            {
                var parsed = RecordedArtifacts.Parse(entry);
                if (parsed is null)
                {
                    continue;
                }

                recordedByUrl.TryAdd(parsed.Value.Url, download);
                recordedShaByUrl.TryAdd(parsed.Value.Url, parsed.Value.Sha256);
            }
        }

        var known = recordedByUrl.Keys.ToHashSet(StringComparer.Ordinal);

        // The row that owns a recorded artifact, so a skipped file stamps the
        // right row (the list row or one of its variants).
        App? OwnerOf(AppDownload download) =>
            download.App
            ?? (download.AppId != 0 && ownerById.TryGetValue(download.AppId, out var owner) ? owner : null);

        // Complete = the recorded derived data is all present and the recorded
        // release is not older than the one being scanned, so nothing would be
        // recomputed by analyzing identical bytes.
        bool Complete(App row, AppDownload download, DateTimeOffset? releasedAt) =>
            download.Sha256 is not null
            && row.PackageName is not null
            && row.IconHash is not null
            && icons.IconFileExists(row.IconHash)
            && !DownloadStore.NeedsPermissionHeal(row, download)
            && !DownloadStore.NeedsSignalHeal(download)
            && !DownloadStore.NeedsAnalysisHeal(download)
            && !DownloadStore.ReleasedNewerThan(releasedAt, row);

        void Stamp(App row)
        {
            row.LastCheckedAt = now;
            row.LastError = null;
            if (etag is not null)
            {
                app.EnrichEtag = etag;
            }
        }

        // Source-declared digest match: skip the download entirely.
        bool TrySkipByChecksum(SourceAsset candidate, DateTimeOffset? releasedAt)
        {
            if (candidate.Sha256 is not { Length: > 0 } declared
                || !recordedByUrl.TryGetValue(candidate.Url, out var download)
                || OwnerOf(download) is not { } owner
                || !Complete(owner, download, releasedAt)
                || !recordedShaByUrl.TryGetValue(candidate.Url, out var recordedSha)
                || !EnrichmentQueries.HashMatches(recordedSha, declared))
            {
                return false;
            }

            Stamp(owner);
            return true;
        }

        string? ExpectedFor(SourceAsset candidate, DateTimeOffset? releasedAt) =>
            recordedByUrl.TryGetValue(candidate.Url, out var download)
            && OwnerOf(download) is { } owner
            && Complete(owner, download, releasedAt)
            && recordedShaByUrl.TryGetValue(candidate.Url, out var recordedSha)
                ? recordedSha
                : null;

        void StampUrl(string url, DateTimeOffset? releasedAt)
        {
            if (recordedByUrl.TryGetValue(url, out var download) && OwnerOf(download) is { } owner)
            {
                Stamp(owner);
            }
            else if (etag is not null)
            {
                app.EnrichEtag = etag;
            }
        }

        var asset = assets.FirstOrDefault(a => a.Primary) ?? ApkAssetSelector.PickApk(assets);
        if (asset is null)
        {
            // Some projects attach only a zip with the APK inside.
            var zip = ApkAssetSelector.PickZip(assets);
            if (zip is null)
            {
                return null;
            }

            var zipReleasedAt = zip.ReleasedAt ?? releaseReleasedAt;
            if (TrySkipByChecksum(zip, zipReleasedAt))
            {
                return new EnrichResult(EnrichOutcome.UpToDate, null)
                {
                    Detail = "APK unchanged, analysis skipped",
                };
            }

            // Best effort: a broken or APK-less archive must not fail the app
            // (null lets the caller run its fallbacks), matching the forge
            // sibling handling below.
            var zipResult = await artifacts.DownloadAndAnalyzeZipAsync(
                zip.Url, etag, kind, zipReleasedAt, ExpectedFor(zip, zipReleasedAt), ct);
            if (zipResult.Unchanged)
            {
                StampUrl(zip.Url, zipReleasedAt);
                return new EnrichResult(EnrichOutcome.UpToDate, null)
                {
                    Detail = "APK unchanged, analysis skipped",
                };
            }

            return zipResult.Analysis is null
                ? null
                : await ApplyAnalysesAsync(
                    app, kind, [zipResult.Analysis with { ReleaseTag = releaseTag }], etag, null,
                    false, now, ct, isPrerelease, releaseTag);
        }

        // Sources whose asset URLs embed the version (GitHub tags, GitLab,
        // GitCode) treat the recorded URL as the change signal, but only when
        // every sibling is already recorded (a newly added architecture must
        // not be skipped) and the recorded row is complete. GitHub allows
        // replacing an asset under the same tag, so verify the digest whenever
        // the source declares one; older uploads without a digest accept the
        // rare staleness in exchange for skipping the transfer.
        if (urlIdentifiesVersion)
        {
            var current = await downloadStore.PrimaryDownloadAsync(app, ct);
            var releasedAt = asset.ReleasedAt ?? releaseReleasedAt;
            if (current is not null && asset.Url == current.ApkUrl
                && current.VersionCode is not null
                && Complete(app, current, releasedAt)
                && (asset.Sha256 is not { Length: > 0 } || EnrichmentQueries.HashMatches(current.Sha256, asset.Sha256))
                && assets.All(a => known.Contains(a.Url) || !DownloadStore.IsApkAsset(a)))
            {
                Stamp(app);
                // The finalizer still runs: the scanned asset set drives
                // variant pruning and display names even on a skipped pass.
                await ApplyAnalysesAsync(app, kind, [], etag, assets, permitRemoval: true, now, ct, isPrerelease, releaseTag);
                return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "asset URL unchanged" };
            }
        }

        var analyses = new List<ArtifactAnalysis>();
        var failed = 0;

        // A pass whose artifacts were all unchanged is reported as a skip in
        // the run log; otherwise the outcome stays Enriched.
        var unchanged = false;

        if (alwaysAnalyzePrimary || !known.Contains(asset.Url))
        {
            var releasedAt = asset.ReleasedAt ?? releaseReleasedAt;
            if (TrySkipByChecksum(asset, releasedAt))
            {
                unchanged = true;
            }
            else
            {
                var expected = ExpectedFor(asset, releasedAt);
                if (expected is null && await downloadStore.PrimaryDownloadAsync(app, ct) is { } primary
                    && Complete(app, primary, releasedAt))
                {
                    // Moved URL (GitHub tag re-upload): verify the transfer
                    // against the recorded primary hash instead of re-analyzing
                    // bytes we have already seen.
                    expected = primary.Sha256;
                }

                var result = await artifacts.DownloadAndAnalyzeApkAsync(
                    asset.Url, etag, kind, releasedAt, expected, ct);
                if (result.Unchanged)
                {
                    StampUrl(asset.Url, releasedAt);
                    unchanged = true;
                }
                else if (result.Analysis is null)
                {
                    return Fail(app, now, result.Error ?? "APK analysis failed.");
                }
                else
                {
                    analyses.Add(result.Analysis with { ReleaseTag = releaseTag });
                }
            }
        }

        // Some releases ship one APK per architecture and no universal build.
        // Record every sibling as its own candidate so the client can pick the
        // device's ABI; recorded URLs are skipped unless their declared
        // checksum changed, so repeat passes stay cheap.
        foreach (var extra in assets)
        {
            if (extra.Primary || !DownloadStore.IsApkAsset(extra) || extra.Url == asset.Url)
            {
                continue;
            }

            var releasedAt = extra.ReleasedAt ?? releaseReleasedAt;
            if (known.Contains(extra.Url))
            {
                var reuploaded = extra.Sha256 is { Length: > 0 } declared
                    && recordedShaByUrl.TryGetValue(extra.Url, out var recorded)
                    && !EnrichmentQueries.HashMatches(recorded, declared);
                if (!reuploaded)
                {
                    continue;
                }
            }

            if (!extra.Analyze)
            {
                await downloadStore.UpsertIndexAssetAsync(app, kind, extra, app.PackageName, now, ct);
                continue;
            }

            if (TrySkipByChecksum(extra, releasedAt))
            {
                unchanged = true;
                continue;
            }

            var result = await artifacts.DownloadAndAnalyzeApkAsync(
                extra.Url, null, kind, releasedAt, ExpectedFor(extra, releasedAt), ct);
            if (result.Unchanged)
            {
                StampUrl(extra.Url, releasedAt);
                unchanged = true;
                continue;
            }

            if (result.Analysis is null)
            {
                failed++;
                continue;
            }

            analyses.Add(result.Analysis with { ReleaseTag = releaseTag });
        }

        // Run the finalizer even when nothing was analyzed: the scanned asset
        // set still drives variant pruning and display names, so a release
        // that dropped a package prunes its row on the checksum-skip path too.
        var applied = await ApplyAnalysesAsync(
            app, kind, analyses, etag, assets, failed == 0, now, ct, isPrerelease, releaseTag);
        app.LastCheckedAt = now;
        app.LastError = null;
        if (unchanged && applied.Outcome == EnrichOutcome.UpToDate)
        {
            return applied with { Detail = "APK unchanged, analysis skipped" };
        }

        return applied;
    }

    /// <summary>
    /// Applies one analyzed artifact to a target row: records the signature
    /// keyed download, optionally recomputes the primary, then writes the
    /// shared presentation fields only when this analysis still represents
    /// the target's primary (same artifact or same signing identity/version)
    /// so an older sibling can never overwrite a newer primary.
    /// </summary>
    private async Task<EnrichResult> ApplyAnalysisAsync(
        App target, ArtifactAnalysis analysis, bool asRepresentative, bool recomputePrimary, bool setEtag,
        DateTimeOffset now, CancellationToken ct, bool recordDownload = true, string? preferredPackage = null,
        bool isPrerelease = false)
    {
        // A metadata heal re-analyzes a recorded primary to refill presentation
        // fields; recording it again could rewrite the row's version and flip
        // the primary on a later recompute, so the heal leaves rows untouched.
        // The APK's badging name can be shell output from broken release
        // tooling; the release tag is the best label then.
        var versionName = VersionNames.Resolve(analysis.Badging.VersionName, analysis.ReleaseTag);
        if (recordDownload)
        {
            await downloadStore.UpsertDownloadAsync(target, new DownloadCandidate(
                analysis.LockSource, null, analysis.ArtifactUrl, analysis.ArchiveEntry,
                analysis.Badging.VersionCode, versionName,
                analysis.FileSize, analysis.FileSha256, analysis.SigSha256, analysis.SigMd5,
                analysis.Badging.MinSdk, analysis.Badging.Abi,
                TargetSdk: analysis.Badging.TargetSdk,
                CompileSdk: analysis.Badging.CompileSdk,
                Locales: analysis.Badging.Locales,
                Abis: analysis.Badging.Abis,
                LocalizedLabels: DownloadStore.LocalizedLabelEntries(analysis.Badging.LocalizedLabels),
                SignerDn: analysis.Signers.Dn,
                SignerScheme: analysis.Signers.Scheme,
                SignerKeyAlgorithm: analysis.Signers.KeyAlgorithm,
                AnalysisVersion: CurrentAnalysisVersion,
                Inspection: analysis.Inspection,
                ReleaseTag: analysis.ReleaseTag,
                PackageName: analysis.Badging.PackageName, Analyzed: true), now, ct);
        }

        if (recomputePrimary)
        {
            await downloadStore.RecomputePrimaryAsync(target, ct, preferredPackage, analysis.ReleaseTag);
        }

        // A completed analysis stamps the check even when it changes nothing
        // user-visible: otherwise an app whose analyzed build never becomes
        // primary stays due forever and is re-downloaded every pass.
        target.LastCheckedAt = now;
        target.LastError = null;

        if (!asRepresentative)
        {
            return new EnrichResult(EnrichOutcome.Enriched, null);
        }

        var primary = await downloadStore.PrimaryDownloadAsync(target, ct);
        var sameArtifact = primary is not null && primary.ApkUrl == analysis.ArtifactUrl;
        var sameVariant = primary is not null
            && primary.SigKey == DownloadStore.ComputeSigKey(analysis.SigSha256, analysis.SigMd5, analysis.ArtifactUrl)
            && analysis.Badging.VersionCode is not null
            && primary.VersionCode == analysis.Badging.VersionCode;
        if (!sameArtifact && !sameVariant)
        {
            return new EnrichResult(EnrichOutcome.UpToDate, null);
        }

        var oldIcon = target.IconHash;
        target.PackageName = analysis.Badging.PackageName;
        target.ApkLabel = analysis.Badging.ApplicationLabel;
        target.Permissions = analysis.Badging.Permissions.ToList();
        // During a deferred pass the inline analysis only knows rasters
        // (XML renders wait for the batch phase), so adopting one here would
        // downgrade an existing adaptive icon. The batch phase owns icon
        // writes then.
        var adoptIcon = analysis.Icon is not null && !options.DeferXmlIconRenders && !options.SkipIconRenders;
        if (adoptIcon)
        {
            await icons.WriteIconFileAsync(analysis.Icon!, ct);
            target.IconHash = analysis.Icon!.Sha256;
            target.IconAdaptive = analysis.Icon!.Adaptive;
        }

        target.Availability = Availability.DirectApk;
        if (setEtag && analysis.Etag is not null)
        {
            target.EnrichEtag = analysis.Etag;
        }

        target.ExcludedReason = null;

        if (recordDownload)
        {
            await downloadStore.AddVersionRowAsync(
                target, analysis.Badging.VersionCode, versionName, analysis.ArtifactUrl,
                isPrerelease, now, ct);
        }

        // Only a real release date moves the app up "recently updated";
        // metadata refreshes never touch this, and sources without dates stay
        // unknown.
        if (analysis.ReleasedAt is not null &&
            (target.VersionUpdatedAt is null || analysis.ReleasedAt > target.VersionUpdatedAt))
        {
            target.VersionUpdatedAt = analysis.ReleasedAt;
        }

        if (adoptIcon)
        {
            await icons.DeleteIconIfOrphanedAsync(target, oldIcon, ct);
        }

        return new EnrichResult(EnrichOutcome.Enriched, null);
    }

    /// <summary>
    /// Groups the release's artifacts by the app name they declare and applies
    /// each group to its row: the group matching the list entry goes on the
    /// root, every other distinct name becomes a variant row. Same-name
    /// packages are flavors of one app (FOSS vs Play, debug vs release), so
    /// they become candidates of one row (distinguished by package) instead of
    /// separate entries. A package no longer present in the scanned release is
    /// dropped, but only when every asset analyzed cleanly.
    /// </summary>
    private async Task<EnrichResult> ApplyAnalysesAsync(
        App root, SourceKind kind, IReadOnlyList<ArtifactAnalysis> analyses, string? etag,
        IReadOnlyList<SourceAsset>? scannedAssets, bool permitRemoval, DateTimeOffset now, CancellationToken ct,
        bool isPrerelease = false, string? releaseTag = null)
    {
        var result = new EnrichResult(EnrichOutcome.UpToDate, null);
        var presented = new List<App>();
        var listPackage = VariantGrouper.ListEndpointPackage(root);

        var scanned = analyses;
        analyses = await variants.ApplyDownloadExclusionsAsync(root, analyses, ct);
        // Operator-excluded artifacts are never served, but they must still be
        // remembered as recorded: the fast-path poll compares the release
        // against the stored URLs, and an unrecorded exclusion forces this app
        // on every pass even though enrichment will never serve it.
        var excludedArtifacts = scanned
            .Where(a => !analyses.Any(kept => ReferenceEquals(kept, a)))
            .ToList();

        // A repo can ship the same package for phone, TV and watch (for example
        // universal-installer's app/tv/wearos release APKs); the phone build is
        // the one a phone store should offer, so a package that also ships a
        // phone build drops its TV and watch flavors.
        var groups = VariantGrouper.GroupByLabel(VariantGrouper.PreferPhoneAnalyses(analyses));
        var rootGroup = VariantGrouper.SelectRootGroup(root, groups, listPackage);

        // Fold variant rows that predate flavor grouping: their package belongs
        // to the root's group now, and a row plus a candidate for the same
        // package must not both exist.
        await variants.MergeSameLabelVariantsAsync(root, rootGroup?.Label, listPackage, now, ct);

        // Two list entries can point at one source repo and scan the same
        // release; each package must stay on the entry that owns it instead of
        // being mirrored as a variant by its sibling. Claims are resolved once
        // per pass so the prune and the create path agree.
        var siblingClaims = await variants.LoadSiblingPackageClaimsAsync(root, ct);
        await variants.PruneSiblingOwnedVariantsAsync(root, siblingClaims, ct);

        foreach (var group in groups)
        {
            var canonical = VariantGrouper.ResolveCanonicalPackage(root, group.Label, VariantGrouper.GroupPackages(group), listPackage);
            App? target;
            if (ReferenceEquals(group, rootGroup))
            {
                target = root;
            }
            else
            {
                target = await variants.EnsureVariantAsync(root, kind, canonical, siblingClaims, now, ct);
                if (target is null)
                {
                    continue;
                }
            }

            foreach (var analysis in group.Items)
            {
                // Only the canonical package presents the row; flavor siblings
                // record candidates (their own package) only.
                var representative = canonical is not null
                    && string.Equals(analysis.Badging.PackageName, canonical, StringComparison.OrdinalIgnoreCase);
                var applied = await ApplyAnalysisAsync(
                    target, analysis, asRepresentative: representative, recomputePrimary: true,
                    setEtag: representative, now, ct, preferredPackage: canonical, isPrerelease: isPrerelease);
                if (applied.Outcome == EnrichOutcome.Enriched)
                {
                    result = applied;
                    presented.Add(target);
                }
            }
        }

        if (excludedArtifacts.Count > 0)
        {
            // A row marked for deletion is still visible to the query until the
            // DELETE flushes, so never anchor the memory on it.
            var alive = (await downloadStore.LoadGroupDownloadsAsync(root, ct))
                .Where(d => db.Entry(d).State != EntityState.Deleted)
                .ToList();
            var host = alive.FirstOrDefault(d => d.IsPrimary) ?? alive.FirstOrDefault();
            if (host is not null)
            {
                foreach (var analysis in excludedArtifacts)
                {
                    DownloadStore.RememberArtifact(host, analysis.FileSha256, analysis.ArtifactUrl);
                }
            }
        }

        // Removal runs first so display names reflect the packages that survive
        // this pass (a repo that drops its second app goes back to a bare label).
        var liveVariants = await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct);
        if (permitRemoval && scannedAssets is { Count: > 0 })
        {
            await variants.RemoveVanishedVariantsAsync(root, liveVariants, scannedAssets, ct);
            liveVariants = await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct);
        }

        var members = new List<App> { root };
        members.AddRange(liveVariants);
        // Skipped artifacts (unchanged URL or declared checksum) apply no
        // analysis, so the tag preference never ran; reassert it here so a
        // stable switch still takes the primary from older prerelease rows.
        if (releaseTag is not null)
        {
            foreach (var member in members)
            {
                await downloadStore.RecomputePrimaryAsync(member, ct, member.PackageName, releaseTag);
            }
        }

        await HealRootPresentationAsync(root, kind, analyses, now, ct);
        var multi = members.Count > 1;
        foreach (var member in members)
        {
            member.DisplayName = VariantGrouper.BuildDisplayName(member, root, multi);
        }

        // Letter-avatars need the final display name, so they are generated
        // after the names settle rather than during the analysis.
        foreach (var target in presented.Distinct())
        {
            if (target.IconHash is null)
            {
                var avatar = LetterAvatarGenerator.Generate(target.DisplayName ?? target.Name);
                await icons.WriteIconFileAsync(avatar, ct);
                target.IconHash = avatar.Sha256;
                target.IconAdaptive = false;
            }
        }

        return result;
    }

    /// <summary>
    /// Fills the list row's presentation fields when the scanned release no
    /// longer carries its primary artifact. The primary guard deliberately
    /// skips those fields for non-representative assets, which otherwise
    /// leaves a row whose package moved to an older release without a label
    /// or permissions. The recorded primary is analyzed once; after that the
    /// row is complete and the heal is a no-op.
    /// </summary>
    private async Task HealRootPresentationAsync(
        App root, SourceKind kind, IReadOnlyList<ArtifactAnalysis> analyses,
        DateTimeOffset now, CancellationToken ct)
    {
        if (root.ApkLabel is not null)
        {
            return;
        }

        var primary = await downloadStore.PrimaryDownloadAsync(root, ct);
        if (primary is null || analyses.Any(a => a.ArtifactUrl == primary.ApkUrl))
        {
            return;
        }

        var result = await artifacts.DownloadAndAnalyzeApkAsync(primary.ApkUrl, null, kind, null, null, ct);
        if (result.Analysis is null)
        {
            return;
        }

        // recomputePrimary false and recordDownload false: a metadata heal must
        // never reorder or rewrite candidates.
        await ApplyAnalysisAsync(
            root, result.Analysis, asRepresentative: true, recomputePrimary: false, setEtag: false,
            now, ct, recordDownload: false);
    }

    /// <summary>
    /// Extraction generation of the current binary. Bump it whenever the
    /// analysis gains a persisted field: rows older than this are re-analyzed
    /// once so the new field backfills (see <see cref="NeedsAnalysisHeal"/>).
    /// </summary>
    public const int CurrentAnalysisVersion = 2;

    /// <summary>
    /// Rescue path for apps whose forge published no APK: package id from an
    /// F-Droid/Izzy URL, else an F-Droid index lookup by the repo's forge
    /// URL. A hit becomes the app's primary download (the only candidate),
    /// recorded in the signature-keyed downloads list like any other source.
    /// Returns null when the app is not published on F-Droid (the forge
    /// failure then stands). Best-effort: index trouble never replaces the
    /// forge error.
    /// </summary>
    private async Task<EnrichResult?> TryFdroidFallbackAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        if (TryParseFdroid(app.Url, app.SourceUrl, out var repoBase, out var packageId, out _))
        {
            return await EnrichFromFdroidAsync(app, repoBase, packageId, now, ct);
        }

        var repoKey = SourceClassifier.RepoKey(app.Url) ?? SourceClassifier.RepoKey(app.SourceUrl);
        if (repoKey is null)
        {
            return null;
        }

        FdroidPackageInfo? package;
        try
        {
            package = await fdroid.FindPackageBySourceAsync(FdroidRepos.FDroidBase, repoKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return package is null
            ? null
            : await EnrichFromFdroidAsync(app, FdroidRepos.FDroidBase, package.PackageName, now, ct);
    }

    private async Task<EnrichResult> EnrichFromFdroidAsync(
        App app, string repoBase, string packageId, DateTimeOffset now, CancellationToken ct)
    {
        (IReadOnlyList<FdroidPackageInfo> Packages, string? IndexEtag)? fetched;
        try
        {
            fetched = await fdroid.GetPackagesAsync(repoBase, packageId, ChangelogEtag(app), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            return Fail(app, now, $"F-Droid: {ex.Message}");
        }

        if (fetched is null)
        {
            // A 304 would keep pre-fix rows permission-less forever (see
            // NeedsPermissionHeal), and legacy index-only rows would stay
            // without a SHA-256 identity; refetch the index once and
            // re-analyze below. The provider memoizes the forced fetch, so a
            // rerun cannot loop on it. A failing refetch is not an upstream
            // change, so stay up-to-date instead of failing the pass.
            var primary = await downloadStore.PrimaryDownloadAsync(app, ct);
            if (DownloadStore.NeedsPermissionHeal(app, primary)
                || DownloadStore.NeedsSignalHeal(primary)
                || DownloadStore.NeedsAnalysisHeal(primary)
                || (primary is not null && !primary.Analyzed && primary.SigSha256 is null))
            {
                try
                {
                    fetched = await fdroid.GetPackagesAsync(repoBase, packageId, null, ct, force: true);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
                {
                    fetched = null;
                }
            }

            if (fetched is null)
            {
                app.LastCheckedAt = now;
                return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "index not modified" };
            }
        }

        var (packages, indexEtag) = fetched.Value;
        var package = packages.FirstOrDefault();
        if (package is null)
        {
            return Fail(app, now, $"F-Droid: package {packageId} not in the {repoBase} index.");
        }

        // The index <desc> is the only changelog text these repos publish.
        app.Changelog = NormalizeChangelog(package.LongDescription);

        var kind = repoBase == FdroidRepos.IzzyBase ? SourceKind.Izzy : SourceKind.FDroid;
        app.SourceKind = kind;

        // f-droid.org publishes no download counts; only the Izzy repo does.
        if (kind == SourceKind.Izzy)
        {
            await downloadStore.ApplyIzzyDownloadsAsync(app, package.PackageName, ct);
        }

        var apkUrl = $"{repoBase.TrimEnd('/')}/{package.ApkName}";
        var siblings = DownloadStore.FdroidSiblings(packages, package);
        var recorded = await downloadStore.LoadDownloadsAsync(app, ct);
        var siblingsRecorded = siblings.All(s => recorded.Any(d =>
            d.Source == kind
            && d.SourceRef == packageId
            && d.Abi == s.Abi
            && d.ApkUrl.EndsWith('/' + s.ApkName, StringComparison.Ordinal)));
        var current = await downloadStore.PrimaryDownloadAsync(app, ct);
        if (current is not null
            && current.Source == kind
            && current.ApkUrl == apkUrl
            && current.VersionCode == package.VersionCode
            && current.Sha256 is not null
            && (package.Sha256 is null || EnrichmentQueries.HashMatches(current.Sha256, package.Sha256))
            && app.PackageName is not null
            && app.IconHash is not null && icons.IconFileExists(app.IconHash)
            && !DownloadStore.NeedsPermissionHeal(app, current)
            && !DownloadStore.NeedsSignerBackfill(current, package)
            && !DownloadStore.NeedsSignalHeal(current)
            && !DownloadStore.NeedsAnalysisHeal(current)
            && siblingsRecorded)
        {
            // Heals the release date on rows enriched before the index date
            // was parsed; everything else about the row is already current.
            DownloadStore.StampFdroidDate(app, package, now);
            app.EnrichEtag = indexEtag;
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "index unchanged" };
        }

        // Changed version: analyze the actual APK (same quality bar as forge
        // builds). Anything wrong with the file falls back to the index-only
        // record, except a package-name mismatch, which means the index row
        // and the file disagree, so the old values are kept.
        var oldIcon = app.IconHash;
        using var analyzed = await artifacts.TryAnalyzeDownloadAsync(apkUrl, ct);
        if (analyzed is not null && analyzed.Badging.PackageName != package.PackageName)
        {
            return Fail(app, now, $"F-Droid: {apkUrl} contains {analyzed.Badging.PackageName}, expected {package.PackageName}.");
        }

        var icon = analyzed is not null
            && await launcherIcons.ResolveAsync(analyzed.ApkPath, analyzed.Badging, ct) is { } apkIcon
            ? apkIcon
            : await icons.MirrorIconAsync(package.IconFile, repoBase, app.Name, ct);
        await icons.WriteIconFileAsync(icon, ct);

        var versionCode = analyzed?.Badging.VersionCode ?? package.VersionCode;
        var versionName = analyzed?.Badging.VersionName ?? package.VersionName;
        var minSdk = analyzed?.Badging.MinSdk ?? package.MinSdk;
        // Index signers come from index-v2 manifest.signer.sha256; an
        // analyzed APK wins because it reflects the actual file.
        var sigSha256 = analyzed is null
            ? package.SigSha256
            : CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Sha256));
        var sigMd5 = analyzed is null
            ? null
            : CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Md5));

        await downloadStore.UpsertDownloadAsync(app, new DownloadCandidate(
            kind,
            packageId,
            apkUrl,
            null,
            versionCode,
            versionName,
            analyzed?.FileSize ?? package.Size,
            analyzed?.FileSha256 ?? package.Sha256,
            sigSha256,
            sigMd5,
            minSdk,
            analyzed?.Badging.Abi ?? package.Abi,
            TargetSdk: analyzed?.Badging.TargetSdk,
            CompileSdk: analyzed?.Badging.CompileSdk,
            Locales: analyzed?.Badging.Locales,
            Abis: analyzed?.Badging.Abis,
            LocalizedLabels: analyzed is null ? null : DownloadStore.LocalizedLabelEntries(analyzed.Badging.LocalizedLabels),
            SignerDn: analyzed?.Signers.Dn,
            SignerScheme: analyzed?.Signers.Scheme,
            SignerKeyAlgorithm: analyzed?.Signers.KeyAlgorithm,
            AnalysisVersion: analyzed is null ? 0 : CurrentAnalysisVersion,
            Inspection: analyzed?.Inspection,
            PackageName: package.PackageName, Analyzed: analyzed is not null,
            ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? [])), now, ct);
        await downloadStore.RecomputePrimaryAsync(app, ct, package.PackageName);

        app.PackageName = package.PackageName;
        if (analyzed is not null)
        {
            // The analyzed APK's permission list belongs to the served
            // build regardless of source; index-only rows keep prior values.
            app.Permissions = analyzed.Badging.Permissions.ToList();
        }

        app.IconHash = icon.Sha256;
        app.IconAdaptive = icon.Adaptive;
        app.Availability = Availability.DirectApk;
        app.EnrichEtag = indexEtag;
        app.ExcludedReason = null;
        app.LastCheckedAt = now;
        app.LastError = null;

        // The index lists one package per architecture; record the siblings as
        // index-only rows so the client can pick the device's ABI.
        await downloadStore.UpsertFdroidSiblingsAsync(app, kind, repoBase, packageId, packages, package, now, ct);
        await downloadStore.RecomputePrimaryAsync(app, ct, package.PackageName);

        await downloadStore.AddVersionRowAsync(app, versionCode, versionName, apkUrl, false, now, ct);

        DownloadStore.StampFdroidDate(app, package, now);

        await icons.DeleteIconIfOrphanedAsync(app, oldIcon, ct);
        await ResolveForgeCandidateFromSourceAsync(app, package.SourceUrl, now, ct);
        if (kind == SourceKind.Izzy)
        {
            // Izzy builds come from the developers, F-Droid rebuilds are a
            // distinct source; record the f-droid.org candidate as well.
            await ResolveFdroidCandidateAsync(app, now, ct);
        }

        return new EnrichResult(EnrichOutcome.Enriched, null);
    }

    /// <summary>
    /// F-Droid candidate for forge-primary apps: when the same package is
    /// published on F-Droid, record its build next to the primary forge
    /// build. Only runs after a fresh primary enrich (never on
    /// <c>SkippedFresh</c>); candidate problems are best-effort and never
    /// change the primary outcome.
    /// </summary>
    private async Task ResolveFdroidCandidateAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.PackageName))
        {
            return;
        }

        FdroidPackageInfo? package;
        IReadOnlyList<FdroidPackageInfo> packages = [];
        try
        {
            var fetched = await fdroid.GetPackagesAsync(FdroidRepos.FDroidBase, app.PackageName, seedEtag: null, ct);
            if (fetched is null)
            {
                return; // 304 with nothing cached: no new information.
            }

            packages = fetched.Value.Packages;
            package = packages.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // Best-effort candidate: index trouble never fails the primary.
        }

        if (package is null)
        {
            await downloadStore.RemoveDownloadAsync(app, SourceKind.FDroid, ct);
            await RecomputePrimaryAfterCandidateAsync(app, ct);
            return;
        }

        var apkUrl = $"{FdroidRepos.FDroidBase.TrimEnd('/')}/{package.ApkName}";
        await downloadStore.UpsertFdroidSiblingsAsync(
            app, SourceKind.FDroid, FdroidRepos.FDroidBase, package.PackageName, packages, package, now, ct);
        var existing = (await downloadStore.LoadDownloadsAsync(app, ct))
            .FirstOrDefault(d => d.Source == SourceKind.FDroid
                && d.ApkUrl == apkUrl
                && d.VersionCode == package.VersionCode
                && d.Sha256 is not null);
        if (existing is not null)
        {
            // Backfill the SHA-256 identity from the index when the first
            // discovery predates the signer map.
            if (existing.SigSha256 is null && package.SigSha256 is not null)
            {
                existing.SigSha256 = package.SigSha256;
                existing.ResolvedAt = now;
            }

            // The index knows the declared permissions even for builds the
            // analyzer never downloaded; use it as a second witness.
            if (!existing.ShizukuDeclared && ShizukuPermission.IsDeclared(package.Permissions ?? []))
            {
                existing.ShizukuDeclared = true;
                existing.ResolvedAt = now;
            }

            await RecomputePrimaryAfterCandidateAsync(app, ct);
            return;
        }

        using var analyzed = await artifacts.TryAnalyzeDownloadAsync(apkUrl, ct);
        var candidate = analyzed is not null && analyzed.Badging.PackageName == package.PackageName
            ? new DownloadCandidate(
                SourceKind.FDroid,
                package.PackageName,
                apkUrl,
                null,
                analyzed.Badging.VersionCode,
                analyzed.Badging.VersionName,
                analyzed.FileSize,
                analyzed.FileSha256,
                CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Sha256)),
                CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Md5)),
                analyzed.Badging.MinSdk,
                analyzed.Badging.Abi,
                TargetSdk: analyzed.Badging.TargetSdk,
                CompileSdk: analyzed.Badging.CompileSdk,
                Locales: analyzed.Badging.Locales,
                Abis: analyzed.Badging.Abis,
                LocalizedLabels: DownloadStore.LocalizedLabelEntries(analyzed.Badging.LocalizedLabels),
                SignerDn: analyzed.Signers.Dn,
                SignerScheme: analyzed.Signers.Scheme,
                SignerKeyAlgorithm: analyzed.Signers.KeyAlgorithm,
                AnalysisVersion: CurrentAnalysisVersion,
                Inspection: analyzed.Inspection,
                PackageName: package.PackageName, Analyzed: true,
                ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? []))
            : new DownloadCandidate(
                SourceKind.FDroid,
                package.PackageName,
                apkUrl,
                null,
                package.VersionCode,
                package.VersionName,
                package.Size,
                package.Sha256,
                package.SigSha256,
                null,
                package.MinSdk,
                package.Abi,
                PackageName: package.PackageName,
                ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? []));
        await downloadStore.UpsertDownloadAsync(app, candidate, now, ct);
        await RecomputePrimaryAfterCandidateAsync(app, ct);
    }

    /// <summary>
    /// Recomputes the primary after an F-Droid candidate changed the row set.
    /// The forge pass just settled the primary with release-tag precedence, so
    /// the candidate must not undo it: without the tag a higher-coded
    /// prerelease row would outrank the freshly served stable build.
    /// </summary>
    private async Task RecomputePrimaryAfterCandidateAsync(App app, CancellationToken ct)
    {
        var primary = await downloadStore.PrimaryDownloadAsync(app, ct);
        await downloadStore.RecomputePrimaryAsync(app, ct, app.PackageName, primary?.ReleaseTag);
    }

    /// <summary>
    /// Exposes the forge build alongside an F-Droid-primary app when the index
    /// names a forge repo (<c>&lt;application&gt;&lt;source&gt;</c>). Candidate-only:
    /// presentation stays with the F-Droid build and every failure is swallowed.
    /// </summary>
    private async Task ResolveForgeCandidateFromSourceAsync(App app, string? sourceUrl, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        try
        {
            if (SourceClassifier.TryParseGitHubRepo(sourceUrl, out var owner, out var repo))
            {
                var target = new SourceTarget(SourceKind.GitHub, $"{owner}/{repo}");
                var release = await github.GetLatestReleaseAsync(target, null, ct);
                if (release is null)
                {
                    return;
                }

                var asset = ApkAssetSelector.PickApk(release.Assets);
                if (asset is not null)
                {
                    await ResolveCandidateApkAsync(app, asset.Url, release.Etag, SourceKind.GitHub, now, ct);
                }
                else if (ApkAssetSelector.PickZip(release.Assets) is { } zip)
                {
                    await ResolveCandidateZipAsync(app, zip.Url, release.Etag, SourceKind.GitHub, now, ct);
                }

                return;
            }

            if (SourceClassifier.TryParseGitLabRepo(sourceUrl, out var projectPath))
            {
                var release = await gitlab.GetLatestReleaseAsync(
                    new SourceTarget(SourceKind.GitLab, projectPath), null, ct);
                if (release is null)
                {
                    return;
                }

                var link = ApkAssetSelector.PickApk(release.Assets);
                if (link is not null)
                {
                    await ResolveCandidateApkAsync(app, link.Url, release.Etag, SourceKind.GitLab, now, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Forge candidate resolution failed for {Slug}.", app.Slug);
        }
    }

    /// <summary>
    /// Records an extra source for an app without letting the candidate
    /// disturb the primary: download, upsert the signature keyed row and
    /// stamp the check, nothing else.
    /// </summary>
    private async Task ResolveCandidateApkAsync(
        App app, string url, string? etag, SourceKind kind, DateTimeOffset now, CancellationToken ct)
    {
        var result = await artifacts.DownloadAndAnalyzeApkAsync(
            url, etag, kind, null, await ExpectedCandidateHashAsync(app, url, ct), ct);
        await ApplyCandidateAsync(app, result, now, ct);
    }

    private async Task ResolveCandidateZipAsync(
        App app, string url, string? etag, SourceKind kind, DateTimeOffset now, CancellationToken ct)
    {
        var result = await artifacts.DownloadAndAnalyzeZipAsync(
            url, etag, kind, null, await ExpectedCandidateHashAsync(app, url, ct), ct);
        await ApplyCandidateAsync(app, result, now, ct);
    }

    /// <summary>
    /// Stored checksum of a candidate URL when the app row already looks
    /// complete, so re-resolving an unchanged alternate-source build skips the
    /// download's expensive analysis.
    /// </summary>
    private async Task<string?> ExpectedCandidateHashAsync(App app, string url, CancellationToken ct)
    {
        var row = (await downloadStore.LoadDownloadsAsync(app, ct)).FirstOrDefault(d => d.ApkUrl == url);
        return row is not null
            && row.Sha256 is not null
            && app.PackageName is not null
            && app.IconHash is not null
            && icons.IconFileExists(app.IconHash)
            && !DownloadStore.NeedsPermissionHeal(app, row)
            && !DownloadStore.NeedsAnalysisHeal(row)
                ? row.Sha256
                : null;
    }

    private async Task ApplyCandidateAsync(App app, ArtifactResult result, DateTimeOffset now, CancellationToken ct)
    {
        if (result.Unchanged)
        {
            app.LastCheckedAt = now;
            app.LastError = null;
            return;
        }

        if (result.Analysis is not null)
        {
            await ApplyAnalysisAsync(
                app, result.Analysis, asRepresentative: false, recomputePrimary: true, setEtag: false, now, ct);
        }
    }

    /// <summary>
    /// Batched icon refresh (phase A): downloads +
    /// analyzes the recorded APK and stages its XML icon (if any) into the
    /// shared batch dir. Raster icons resolve immediately and are written
    /// like a normal refresh; XML icons come back as
    /// <see cref="PrepareIconResult.Pending"/> for one shared Gradle render
    /// (phase B) plus <see cref="CommitIconRefreshAsync"/> (phase C).
    /// Icon-only: version/signature/check timestamps are left alone.
    /// </summary>
    public async Task<PrepareIconResult> PrepareIconRefreshAsync(
        App app, string batchWorkDir, string prefix, CancellationToken ct = default, bool force = false)
    {
        var primary = await downloadStore.PrimaryDownloadAsync(app, ct);
        if (app.Availability != Availability.DirectApk
            || primary is null
            || string.IsNullOrWhiteSpace(primary.ApkUrl))
        {
            // Store-only listings carry letter-avatars (see
            // EnrichFallbackAsync); heal a deleted avatar file when the
            // bytes still match. Anything else is left alone: unknown
            // provenance, never touch.
            if ((app.Availability is Availability.LinkOnly or Availability.PlayRedirect)
                && app.IconHash is not null)
            {
                try
                {
                    var storeAvatar = LetterAvatarGenerator.Generate(app.DisplayName ?? app.Name);
                    if (storeAvatar.Sha256 == app.IconHash)
                    {
                        app.IconAdaptive = false; // avatars are never adaptive
                        if (!icons.IconFileExists(storeAvatar.Sha256))
                        {
                            await icons.WriteIconFileAsync(storeAvatar, ct);
                            return new PrepareIconResult(EnrichOutcome.Enriched, null, null);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new PrepareIconResult(EnrichOutcome.Failed, $"Icon refresh: {ex.Message}", null);
                }
            }

            return new PrepareIconResult(EnrichOutcome.UpToDate, null, null);
        }

        try
        {
            using var analyzed = await artifacts.TryAnalyzeDownloadAsync(primary.ApkUrl, ct);
            if (analyzed is null)
            {
                return new PrepareIconResult(EnrichOutcome.Failed,
                    "Icon refresh: recorded APK no longer analyzable; icon kept.", null);
            }

            PendingBatchIcon? pending;
            using (var zip = ZipFile.OpenRead(analyzed.ApkPath))
            {
                pending = launcherIcons.PrepareBatchRender(
                    zip, ZipEntryReader.Read(zip, "resources.arsc"),
                    analyzed.Badging, batchWorkDir, prefix);
            }

            if (pending is null)
            {
                // No XML path: resolve immediately. A null resolve means
                // the APK declares no icon at all; enrich records an
                // avatar for those, so refresh must too, or a deleted
                // avatar file 404s forever while the pass reports
                // UpToDate.
                var icon = await launcherIcons.ResolveAsync(analyzed.ApkPath, analyzed.Badging, ct)
                    ?? LetterAvatarGenerator.Generate(app.DisplayName ?? app.Name);
                // Provenance always syncs, even when the bytes match: one
                // pass heals stale flags without any icon churn.
                app.IconAdaptive = icon.Adaptive;

                // Write first: a missing file must heal even when the
                // bytes still match the recorded icon (write is a no-op
                // otherwise).
                var healed = !icons.IconFileExists(icon.Sha256);
                await icons.WriteIconFileAsync(icon, ct);
                if (icon.Sha256 == app.IconHash)
                {
                    // Force re-renders everything (swap detection: a
                    // self-consistent wrong file only surfaces when fresh
                    // bytes are compared), so equal bytes still count.
                    return force || healed
                        ? new PrepareIconResult(EnrichOutcome.Enriched, null, null)
                        : new PrepareIconResult(EnrichOutcome.UpToDate, null, null);
                }

                var oldIcon = app.IconHash;
                app.IconHash = icon.Sha256;
                await icons.DeleteIconIfOrphanedAsync(app, oldIcon, ct);
                return new PrepareIconResult(EnrichOutcome.Enriched, null, null);
            }

            return new PrepareIconResult(EnrichOutcome.UpToDate, null, pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PrepareIconResult(EnrichOutcome.Failed, $"Icon refresh: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Batched icon refresh (phase C): normalizes
    /// one batch-rendered PNG and adopts it when it differs. Failures keep
    /// the current icon without touching the row.
    /// </summary>
    public Task<EnrichResult> CommitIconRefreshAsync(
        App app, byte[]? png, CancellationToken ct = default, bool force = false, bool isAdaptive = false) =>
        icons.CommitAsync(app, png, ct, force, isAdaptive);
    private async Task<EnrichResult> EnrichFallbackAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        var kind = SourceClassifier.Classify(app.Url);
        app.SourceKind = kind;

        // Play listings carry the only metadata external-only apps have.
        var playDetails = kind == SourceKind.Play
            ? await FetchPlayDetailsAsync(app, ct)
            : null;
        ApplyPlayDetails(app, playDetails);

        // The real Play listing icon beats a generated avatar when linked.
        var hasIcon = playDetails?.IconUrl is { Length: > 0 } remoteIcon
            ? await icons.TryAdoptPlayIconAsync(app, remoteIcon, ct)
            : await icons.TryPlayIconAsync(app, now, ct);
        if (!hasIcon)
        {
            var avatar = LetterAvatarGenerator.Generate(app.DisplayName ?? app.Name);
            await icons.WriteIconFileAsync(avatar, ct);
            app.IconHash = avatar.Sha256;
            app.IconAdaptive = false;
        }

        if (kind == SourceKind.Play)
        {
            app.Availability = Availability.PlayRedirect;
            app.StoreUrl = app.Url;
            app.ExcludedReason = null;
        }
        else
        {
            app.Availability = Availability.LinkOnly;
            app.ExcludedReason = null;
        }

        app.LastCheckedAt = now;
        app.LastError = null;
        return new EnrichResult(EnrichOutcome.AvatarFallback, null);
    }

    /// <summary>
    /// Resolves the linked Play listing and returns its scraped details.
    /// Best effort: a missing page or an unparseable package id leaves the
    /// app untouched, so a Play redirect can still fall back to the avatar.
    /// </summary>
    private async Task<PlayAppDetails?> FetchPlayDetailsAsync(App app, CancellationToken ct)
    {
        if (play is null)
        {
            return null;
        }

        var packageId = string.Empty;
        if (!SourceClassifier.TryParsePlayPackage(app.Url, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.SourceUrl, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.StoreUrl, out packageId))
        {
            return null;
        }

        // The listing URL is the authoritative package identity for Play apps.
        app.PackageName = packageId;

        try
        {
            return await play.GetAppDetailsAsync(packageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Play details fetch failed for {Slug}.", app.Slug);
            return null;
        }
    }

    private static void ApplyPlayDetails(App app, PlayAppDetails? details)
    {
        if (details is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(details.DeveloperName))
        {
            app.AuthorName = details.DeveloperName;
            app.AuthorUrl = details.DeveloperUrl;
            app.AuthorKey = string.IsNullOrWhiteSpace(details.DeveloperId)
                ? $"play:{details.DeveloperName.ToLowerInvariant()}"
                : $"play:{details.DeveloperId}";
        }

        if (!string.IsNullOrWhiteSpace(details.VersionName))
        {
            app.VersionName = details.VersionName;
        }

        // Play publishes a real last-update date, unlike a detection time.
        if (details.UpdatedAt is { } updated
            && (app.VersionUpdatedAt is null || updated > app.VersionUpdatedAt))
        {
            app.VersionUpdatedAt = updated;
        }

        if (!string.IsNullOrWhiteSpace(details.FullDescription))
        {
            app.FullDescription = details.FullDescription.Length > MaxFullDescriptionChars
                ? details.FullDescription[..MaxFullDescriptionChars]
                : details.FullDescription;
            // Play text is not markdown and has no raw route; drop a stale
            // README URL so clients never refetch the wrong document.
            app.ReadmeUrl = null;
        }
    }

    /// <summary>
    /// A source repo without an APK is not a dead end when the list entry
    /// points at a Play listing: run the normal source fallback so the app
    /// becomes a Play redirect instead of a bare link.
    /// </summary>
    private async Task<EnrichResult?> TryPlayRedirectFallbackAsync(
        App app, DateTimeOffset now, CancellationToken ct)
    {
        if (SourceClassifier.Classify(app.Url) != SourceKind.Play)
        {
            return null;
        }

        return await EnrichFallbackAsync(app, now, ct);
    }

    private static EnrichResult Fail(App app, DateTimeOffset now, string message)
    {
        app.LastCheckedAt = now;
        app.LastError = message.Length > 500 ? message[..500] + "…" : message;
        return new EnrichResult(EnrichOutcome.Failed, app.LastError);
    }
}
