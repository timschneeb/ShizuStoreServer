using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

public enum EnrichOutcome
{
    /// <summary>APK downloaded + aapt2 parsed, row updated (possibly with letter-avatar icon).</summary>
    Enriched,
    /// <summary>Release feed / repo index answered 304 Not Modified (or the same asset is already recorded); only <c>last_checked_at</c> touched.</summary>
    UpToDate,
    /// <summary>No forge source: letter-avatar icon, source kind set, no APK fields.</summary>
    AvatarFallback,
    /// <summary>Something failed; <c>last_error</c> set, backoff applies. Previous good values kept.</summary>
    Failed,
    /// <summary>Checked recently (success or backoff window); no work done.</summary>
    SkippedFresh,
}

public sealed record EnrichResult(EnrichOutcome Outcome, string? Error)
{
    /// <summary>
    /// Optional human-readable reason for a non-action outcome, e.g. why a run
    /// skipped an app or only skipped APK analysis. Written to the run log.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Phase-A outcome of a batched icon refresh: either final already
/// (raster written, up-to-date, failed) or a staged <c>Pending</c> entry
/// awaiting the shared render plus a commit call.
/// </summary>
public sealed record PrepareIconResult(
    EnrichOutcome Outcome, string? Error, PendingBatchIcon? Pending);

/// <summary>
/// Per-app enrichment. Resolution is forge-first: GitHub and GitLab releases
/// resolve to an APK asset (temp download → <c>aapt2 dump badging</c> →
/// <c>apksigner</c> fingerprints → best-density icon → fill the APK columns →
/// delete the APK), because F-Droid builds are delayed and (unless
/// reproducible) signed with a different key. F-Droid/Izzy packages resolve
/// via the repo <c>index.xml</c>, downloading the APK on version change for
/// full analysis. Whenever the primary APK comes from a forge, the same
/// package is additionally looked up in the F-Droid main index and recorded
/// as the alternate variant (<c>Fdroid*</c> columns), so clients can match a
/// locally installed F-Droid build by its signing cert and offer the right
/// download URL. When a forge has no APK at all (no releases or no `.apk`
/// asset), the app falls back to the F-Droid main index by matching the
/// application's `<source>` URL against the entry's forge URL. The source
/// that supplied an APK is recorded and locked (<c>ApkSource</c>): once a
/// build was served, enrichment never switches between forge and F-Droid
/// (different signing keys would break updates). Two entries release
/// outside the repo the list points at and are special-cased: instafel (the
/// updater APK ships from github.com/instafel/u-rel while the list links the
/// source monorepo mamiiblt/instafel) and hlbmerge_flutter (APK builds only
/// on the GitCode mirror gitcode.com/bigmolihuan/hlbmerge_flutter). Play-sole-source apps become
/// redirects to their Play listing instead of being hidden. Failures record
/// <c>last_error</c> and keep previous good values. Does not call
/// <c>SaveChanges</c>, the caller batches (fast loop in M6, tests).
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
    IUsageAnalysisQueue? usageQueue = null)
{
    // Special-case release homes (instafel, LinkSheet) live in
    // ForgeReleaseHomes so enrichment and the release poll cannot drift.
    // hlbmerge rebuilds only on GitCode, so its releases are fetched there.
    private const string HlbmergeGitHubOwner = "molihuan";
    private const string HlbmergeGitHubRepo = "hlbmerge_flutter";
    private const string HlbmergeGitCodeOwner = "bigmolihuan";
    private const string HlbmergeGitCodeRepo = "hlbmerge_flutter";

    // One repo, several distinct apps, each in its own GitHub release; the
    // newest release only carries one of them, so scan them all.
    private const string SmartspacerOwner = "KieronQuinn";
    private const string SmartspacerRepo = "SmartspacerPlugins";

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

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

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
            JobContext.Current?.Release($"{label} {(result is null ? "304" : "ok")} in {Elapsed(started)}ms");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobContext.Current?.Release(
                $"{label} failed after {Elapsed(started)}ms: {ex.Message}", level: JobEventLevel.Warning);
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

    // GitHub's old rendered README HTML is recognizable by its wrapper tags; a
    // refetch replaces it with markdown. The markers are rendered-only, so raw
    // markdown (which may embed <p align="...">) never matches and is not
    // refetched on every pass. Play descriptions arrive as plain text and never
    // match either.
    private static bool NeedsReadmeRefresh(string? value) =>
        string.IsNullOrEmpty(value)
        || value.Contains("id=\"readme\"", StringComparison.Ordinal)
        || value.Contains("data-path=", StringComparison.Ordinal)
        || value.Contains("class=\"markdown-body\"", StringComparison.Ordinal)
        || value.Contains("class=\"markdown-heading\"", StringComparison.Ordinal)
        || value.Contains("class=\"highlight", StringComparison.Ordinal);

    /// <summary>
    /// Raw markdown, not rendered HTML: the client renders markdown. When the
    /// list entry links a markdown README directly (localized README_EN.md on
    /// projects whose landing README is non-English), that file wins over the
    /// repo default and is refetched every pass, so rows enriched before this
    /// pick existed heal. The default fetch keeps its legacy-HTML gate.
    /// </summary>
    private async Task RefreshFullDescriptionAsync(
        App app, Func<Task<string?>> fetchLinked, Func<Task<string?>> fetchDefault)
    {
        if (ReadmeLink.IsReadme(app.Url)
            && await fetchLinked() is { Length: > 0 } linked)
        {
            SetFullDescription(app, linked);
            return;
        }

        if (NeedsReadmeRefresh(app.FullDescription)
            && await fetchDefault() is { Length: > 0 } readme)
        {
            SetFullDescription(app, readme);
        }
    }

    private static void SetFullDescription(App app, string value) =>
        app.FullDescription = value.Length > MaxFullDescriptionChars
            ? value[..MaxFullDescriptionChars]
            : value;

    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessedIcon?>>> _iconByPackage =
        new(StringComparer.Ordinal);

    public async Task<EnrichResult> EnrichAsync(
        App app, DateTimeOffset now, CancellationToken ct = default, bool force = false)
    {
        // A pre-fix recompute bug left some row sets without a primary; the
        // unchanged-asset short-circuits would never revisit them, so repair
        // from the stored rows before anything else.
        await HealMissingPrimaryAsync(app, ct);
        await HealTrackerTagsAsync(app, ct);

        if (!force
            && app.LastCheckedAt is { } checkedAt
            && checkedAt + (app.LastError is null ? options.SuccessRecheckInterval : options.FailedRecheckInterval) > now)
        {
            return new EnrichResult(EnrichOutcome.SkippedFresh, null) { Detail = "within recheck window" };
        }

        // Per-call cache: every ABI variant of a release resolves the same
        // launcher icon, and a resolve may be a full Gradle render; one per
        // package (and one per call, so a reused instance cannot go stale).
        _iconByPackage.Clear();

        try
        {
            var availabilityBefore = app.Availability;
            var storeUrlBefore = app.StoreUrl;

            var result = await DispatchAsync(app, now, ct);
            await ApplyScreenshotsAsync(app, now, ct);

            // External-only Play apps never get an APK; give them the real
            // listing icon instead of a generated avatar and stop counting
            // them as failures on every pass.
            if (result.Outcome == EnrichOutcome.Failed
                && !await HasDownloadsAsync(app, ct)
                && app.IconHash is null
                && await TryPlayIconAsync(app, now, ct))
            {
                app.LastError = null;
                result = new EnrichResult(EnrichOutcome.AvatarFallback, null);
            }

            if (result.Outcome != EnrichOutcome.Failed)
            {
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
        foreach (var variant in await LoadVariantGroupAsync(root, ct))
        {
            variant.LastCheckedAt = now;
            variant.LastError = null;
            // Variants never run their own EnrichAsync, so the per-app primary
            // heal cannot reach them; repair here, or a flag lost to an
            // interrupted recompute would stay lost (live 2026-09-25).
            await HealMissingPrimaryAsync(variant, ct);
            await HealTrackerTagsAsync(variant, ct);
        }
    }

    // Screenshots come from the large index-v2.json, which the legacy
    // index.xml path never touches. Best-effort: a missing or broken
    // index-v2 must never fail an otherwise good enrichment.
    private const int MaxScreenshots = 12;

    // The two repos fail independently. A 2026-09-16 Izzy outage aborted the
    // whole lookup and silently dropped F-Droid screenshots for every app after
    // the first failure, so a repo that cannot be reached now keeps the URLs it
    // contributed earlier instead of taking the other repo's hits down with it.
    // A repo that answers replaces its URLs wholesale, so dead shots are
    // cleared on rescan. When F-Droid/Izzy end up with nothing, the app's own
    // repo tree is the last resort (see RepoScreenshotResolver).
    /// <summary>
    /// Screenshots-only maintenance refresh (the admin trigger): re-resolves
    /// F-Droid/Izzy and re-runs the repo fallback even when repo-sourced URLs
    /// are already stored, so dead ones are cleared and fresh ones replace
    /// them. No APK work. Returns <c>Enriched</c> only when the URL list
    /// changed.
    /// </summary>
    public async Task<EnrichResult> RefreshScreenshotsAsync(
        App app, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = app.Screenshots.ToArray();
        await ApplyScreenshotsAsync(app, now, ct, force: true);
        return app.Screenshots.SequenceEqual(before)
            ? new EnrichResult(EnrichOutcome.UpToDate, null)
            : new EnrichResult(EnrichOutcome.Enriched, null);
    }

    private async Task ApplyScreenshotsAsync(App app, DateTimeOffset now, CancellationToken ct, bool force = false)
    {
        try
        {
            // Rebuild instead of append: an index that answers replaces its
            // URLs entirely (an empty answer clears them), an unreachable one
            // keeps the URLs it contributed earlier.
            var (reached, freshIndex) = await ResolveFdroidScreenshotsAsync(app, ct);
            var indexUrls = reached
                ? freshIndex
                : app.Screenshots.Where(IsFdroidSourced).ToList();

            // Index shots win; repo-sourced leftovers are dropped and the repo
            // is never cloned while the index supplies shots.
            if (indexUrls.Count > 0)
            {
                SetScreenshots(app, indexUrls);
                return;
            }

            // Only repo-sourced URLs can be stored now. They stay until the
            // recheck window elapses; a forced refresh re-runs the lookup every
            // time. The repository itself decides their fate: a listed tree
            // replaces or clears them, a failed clone keeps them.
            var storedRepo = app.Screenshots.Where(url => !IsFdroidSourced(url)).ToList();
            if (!ShouldTryRepoScreenshots(app, now, force))
            {
                SetScreenshots(app, storedRepo);
                return;
            }

            app.ScreenshotsCheckedAt = now;
            var resolved = await repoScreenshots!.ResolveAsync(app.Url, app.SourceUrl, ct);
            SetScreenshots(app, resolved.Reached ? resolved.Urls : storedRepo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Screenshot lookup failed for {Slug}.", app.Slug);
        }
    }

    /// <summary>Stores a fresh list, capped, and leaves identical content untouched.</summary>
    private static void SetScreenshots(App app, IReadOnlyList<string> urls)
    {
        var capped = urls.Count > MaxScreenshots ? urls.Take(MaxScreenshots).ToList() : [.. urls];
        if (!capped.SequenceEqual(app.Screenshots))
        {
            app.Screenshots = capped;
        }
    }

    /// <summary>
    /// F-Droid/Izzy half of the screenshot lookup. <c>Reached</c> is true when
    /// at least one index answered, which lets the caller distinguish "no
    /// screenshots" (clear) from "index unreachable" (keep existing).
    /// </summary>
    private async Task<(bool Reached, List<string> Urls)> ResolveFdroidScreenshotsAsync(App app, CancellationToken ct)
    {
        var packageIds = await CollectPackageIdsAsync(app, ct);
        if (packageIds.Count == 0)
        {
            return (false, []);
        }

        string[] repos = [FdroidRepos.FDroidBase, FdroidRepos.IzzyBase];
        var fresh = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var repoBase in repos)
        {
            try
            {
                var map = await fdroid.GetScreenshotsAsync(repoBase, ct);
                if (map is not null)
                {
                    fresh[repoBase] = CollectScreenshotNames(map, packageIds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogDebug(ex, "Screenshot lookup failed for {Repo}.", repoBase);
            }
        }

        if (fresh.Count == 0)
        {
            return (false, []);
        }

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repoBase in repos)
        {
            var repo = repoBase.TrimEnd('/');
            var candidates = fresh.TryGetValue(repoBase, out var found)
                ? found.Select(name => (Name: name, Url: $"{repo}/{name.TrimStart('/')}"))
                : app.Screenshots
                    .Where(url => url.StartsWith($"{repo}/", StringComparison.Ordinal))
                    .Select(url => (Name: url[(repo.Length + 1)..], Url: url));
            foreach (var (name, url) in candidates)
            {
                if (seen.Add(name))
                {
                    urls.Add(url);
                    if (urls.Count >= MaxScreenshots)
                    {
                        break;
                    }
                }
            }

            if (urls.Count >= MaxScreenshots)
            {
                break;
            }
        }

        return (true, urls);
    }

    private static bool IsFdroidSourced(string url) =>
        url.StartsWith(FdroidRepos.FDroidBase.TrimEnd('/') + "/", StringComparison.Ordinal)
        || url.StartsWith(FdroidRepos.IzzyBase.TrimEnd('/') + "/", StringComparison.Ordinal);

    private bool ShouldTryRepoScreenshots(App app, DateTimeOffset now, bool force) =>
        repoScreenshots is not null
        && options.RepoScreenshotsEnabled
        && (force
            || app.ScreenshotsCheckedAt is null
            || app.ScreenshotsCheckedAt + options.RepoScreenshotsRecheckInterval <= now);

    private static List<string> CollectScreenshotNames(
        IReadOnlyDictionary<string, IReadOnlyList<string>> map, List<string> packageIds)
    {
        var names = new List<string>();
        foreach (var id in packageIds)
        {
            if (!map.TryGetValue(id, out var found))
            {
                continue;
            }

            foreach (var name in found)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                names.Add(name);
                if (names.Count >= MaxScreenshots)
                {
                    return names;
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Every package name this app can be known by: the primary plus each
    /// variant download row. Variant rows added earlier in this same pass are
    /// still only in the change tracker, so both are read.
    /// </summary>
    private async Task<List<string>> CollectPackageIdsAsync(App app, CancellationToken ct)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        Add(app.PackageName);
        foreach (var id in await db.Downloads.AsNoTracking()
            .Where(d => d.AppId == app.Id)
            .Select(d => d.PackageName)
            .ToListAsync(ct))
        {
            Add(id);
        }

        foreach (var entry in db.ChangeTracker.Entries<AppDownload>())
        {
            if (entry.Entity.AppId == app.Id)
            {
                Add(entry.Entity.PackageName);
            }
        }

        return ids;
    }

    private async Task<EnrichResult> DispatchAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        var githubPrimary = SourceClassifier.TryParseGitHubRepo(app.Url, out var owner, out var repo);
        if (githubPrimary || SourceClassifier.TryParseGitHubRepo(app.SourceUrl, out owner, out repo))
        {
            app.SourceKind = SourceKind.GitHub;
            KeepPlayStoreUrl(app, githubPrimary);

            // Instafel and LinkSheet publish from separate release repos; the
            // list target stays the analysis source for usage and screenshots.
            (owner, repo) = ForgeReleaseHomes.Remap(owner, repo);

            EnrichResult result;
            if (gitcode is not null && owner == HlbmergeGitHubOwner && repo == HlbmergeGitHubRepo)
            {
                result = await EnrichFromGitCodeAsync(app, now, ct);
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

    /// <summary>
    /// Best-effort repo metadata: stars plus the repo owner as developer
    /// identity. Popularity and developer details are refreshed even on a 304,
    /// so they stay current without a new release. Never throws (except on
    /// cancellation): a failing stats call leaves previous values alone and
    /// the parsed owner stays the developer fallback.
    /// </summary>
    private async Task RefreshGitHubStatsAsync(App app, string owner, string repo, CancellationToken ct)
    {
        GitHubRepoStats? stats = null;
        try
        {
            stats = await github.GetRepoStatsAsync(owner, repo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Repo stats fetch failed for {Owner}/{Repo}.", owner, repo);
        }

        if (stats?.Stars is { } stars)
        {
            app.Stars = stars;
        }

        // The repo owner is a stable developer identity; fall back to the
        // parsed owner when the stats call failed.
        var ownerLogin = stats?.OwnerLogin ?? owner;
        app.AuthorName = ownerLogin;
        app.AuthorUrl = stats?.OwnerUrl ?? $"https://github.com/{ownerLogin}";
        app.AuthorKey = $"github:{ownerLogin.ToLowerInvariant()}";
    }

    private async Task<EnrichResult> EnrichFromGitHubAsync(
        App app, string owner, string repo, DateTimeOffset now, CancellationToken ct)
    {
        var target = new SourceTarget(SourceKind.GitHub, $"{owner}/{repo}");

        if (owner == SmartspacerOwner && repo == SmartspacerRepo)
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
            await RefreshGitHubStatsAsync(app, owner, repo, ct);
            return await HandleGitHubFailureAsync(app, ex, now, ct);
        }

        SourceRelease release;
        try
        {
            await RefreshGitHubStatsAsync(app, owner, repo, ct);

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
                var current = await PrimaryDownloadAsync(app, ct);
                if (NeedsPermissionHeal(app, current) || NeedsSignalHeal(current) || NeedsAnalysisHeal(current))
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
        var previous = await PrimaryDownloadAsync(app, ct);
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
        var served = await PrimaryDownloadAsync(app, ct);
        if (usageQueue is not null
            && app.UsageAnalyzedAt is not null
            && previous is not null
            && served is not null
            && previous.ApkUrl != served.ApkUrl)
        {
            await usageQueue.EnqueueAsync(app, artifactChanged: true, firstAnalysis: false, ct);
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
        await RefreshGitHubStatsAsync(app, owner, repo, ct);

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
        App app, DateTimeOffset now, CancellationToken ct)
    {
        SourceRelease release;
        try
        {
            var target = new SourceTarget(
                SourceKind.Other, $"{HlbmergeGitCodeOwner}/{HlbmergeGitCodeRepo}");
            var latest = await TimedAsync(
                $"gitcode release list {HlbmergeGitCodeOwner}/{HlbmergeGitCodeRepo}",
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
            + $"{HlbmergeGitCodeOwner}/{HlbmergeGitCodeRepo} has no .apk asset.");
    }

    private async Task<EnrichResult> EnrichFromGitLabAsync(
        App app, string projectPath, DateTimeOffset now, CancellationToken ct)
    {
        ApplyGitLabAuthor(app, projectPath);

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
            await RefreshGitLabStatsAsync(app, projectPath, ct);
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
            await RefreshGitLabStatsAsync(app, projectPath, ct);

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
                var current = await PrimaryDownloadAsync(app, ct);
                if (NeedsPermissionHeal(app, current) || NeedsSignalHeal(current) || NeedsAnalysisHeal(current))
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
        var downloads = await LoadGroupDownloadsAsync(app, ct);
        var ownerById = new Dictionary<long, App> { [app.Id] = app };
        foreach (var variant in await LoadVariantGroupAsync(app, ct))
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
            && IconFileExists(row.IconHash)
            && !NeedsPermissionHeal(row, download)
            && !NeedsSignalHeal(download)
            && !NeedsAnalysisHeal(download)
            && !ReleasedNewerThan(releasedAt, row);

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
                || !HashMatches(recordedSha, declared))
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
            var zipResult = await DownloadAndAnalyzeZipAsync(
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
            var current = await PrimaryDownloadAsync(app, ct);
            var releasedAt = asset.ReleasedAt ?? releaseReleasedAt;
            if (current is not null && asset.Url == current.ApkUrl
                && current.VersionCode is not null
                && Complete(app, current, releasedAt)
                && (asset.Sha256 is not { Length: > 0 } || HashMatches(current.Sha256, asset.Sha256))
                && assets.All(a => known.Contains(a.Url) || !IsApkAsset(a)))
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
                if (expected is null && await PrimaryDownloadAsync(app, ct) is { } primary
                    && Complete(app, primary, releasedAt))
                {
                    // Moved URL (GitHub tag re-upload): verify the transfer
                    // against the recorded primary hash instead of re-analyzing
                    // bytes we have already seen.
                    expected = primary.Sha256;
                }

                var result = await DownloadAndAnalyzeApkAsync(
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
            if (extra.Primary || !IsApkAsset(extra) || extra.Url == asset.Url)
            {
                continue;
            }

            var releasedAt = extra.ReleasedAt ?? releaseReleasedAt;
            if (known.Contains(extra.Url))
            {
                var reuploaded = extra.Sha256 is { Length: > 0 } declared
                    && recordedShaByUrl.TryGetValue(extra.Url, out var recorded)
                    && !HashMatches(recorded, declared);
                if (!reuploaded)
                {
                    continue;
                }
            }

            if (!extra.Analyze)
            {
                await UpsertIndexAssetAsync(app, kind, extra, app.PackageName, now, ct);
                continue;
            }

            if (TrySkipByChecksum(extra, releasedAt))
            {
                unchanged = true;
                continue;
            }

            var result = await DownloadAndAnalyzeApkAsync(
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
        if (recordDownload)
        {
            await UpsertDownloadAsync(target, new DownloadCandidate(
                analysis.LockSource, null, analysis.ArtifactUrl, analysis.ArchiveEntry,
                analysis.Badging.VersionCode, analysis.Badging.VersionName,
                analysis.FileSize, analysis.FileSha256, analysis.SigSha256, analysis.SigMd5,
                analysis.Badging.MinSdk, analysis.Badging.Abi,
                TargetSdk: analysis.Badging.TargetSdk,
                CompileSdk: analysis.Badging.CompileSdk,
                Locales: analysis.Badging.Locales,
                Abis: analysis.Badging.Abis,
                LocalizedLabels: LocalizedLabelEntries(analysis.Badging.LocalizedLabels),
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
            await RecomputePrimaryAsync(target, ct, preferredPackage, analysis.ReleaseTag);
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

        var primary = await PrimaryDownloadAsync(target, ct);
        var sameArtifact = primary is not null && primary.ApkUrl == analysis.ArtifactUrl;
        var sameVariant = primary is not null
            && primary.SigKey == ComputeSigKey(analysis.SigSha256, analysis.SigMd5, analysis.ArtifactUrl)
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
            await WriteIconFileAsync(analysis.Icon!, ct);
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
            await AddVersionRowAsync(
                target, analysis.Badging.VersionCode, analysis.Badging.VersionName, analysis.ArtifactUrl,
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
            await DeleteIconIfOrphanedAsync(target, oldIcon, ct);
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
        var listPackage = ListEndpointPackage(root);

        var scanned = analyses;
        analyses = await ApplyDownloadExclusionsAsync(root, analyses, ct);
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
        var groups = GroupByLabel(PreferPhoneAnalyses(analyses));
        var rootGroup = SelectRootGroup(root, groups, listPackage);

        // Fold variant rows that predate flavor grouping: their package belongs
        // to the root's group now, and a row plus a candidate for the same
        // package must not both exist.
        await MergeSameLabelVariantsAsync(root, rootGroup?.Label, listPackage, now, ct);

        // Two list entries can point at one source repo and scan the same
        // release; each package must stay on the entry that owns it instead of
        // being mirrored as a variant by its sibling. Claims are resolved once
        // per pass so the prune and the create path agree.
        var siblingClaims = await LoadSiblingPackageClaimsAsync(root, ct);
        await PruneSiblingOwnedVariantsAsync(root, siblingClaims, ct);

        foreach (var group in groups)
        {
            var canonical = ResolveCanonicalPackage(root, group.Label, GroupPackages(group), listPackage);
            App? target;
            if (ReferenceEquals(group, rootGroup))
            {
                target = root;
            }
            else
            {
                target = await EnsureVariantAsync(root, kind, canonical, siblingClaims, now, ct);
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
            var alive = (await LoadGroupDownloadsAsync(root, ct))
                .Where(d => db.Entry(d).State != EntityState.Deleted)
                .ToList();
            var host = alive.FirstOrDefault(d => d.IsPrimary) ?? alive.FirstOrDefault();
            if (host is not null)
            {
                foreach (var analysis in excludedArtifacts)
                {
                    RememberArtifact(host, analysis.FileSha256, analysis.ArtifactUrl);
                }
            }
        }

        // Removal runs first so display names reflect the packages that survive
        // this pass (a repo that drops its second app goes back to a bare label).
        var variants = await LoadVariantGroupAsync(root, ct);
        if (permitRemoval && scannedAssets is { Count: > 0 })
        {
            await RemoveVanishedVariantsAsync(root, variants, scannedAssets, ct);
            variants = await LoadVariantGroupAsync(root, ct);
        }

        var members = new List<App> { root };
        members.AddRange(variants);
        // Skipped artifacts (unchanged URL or declared checksum) apply no
        // analysis, so the tag preference never ran; reassert it here so a
        // stable switch still takes the primary from older prerelease rows.
        if (releaseTag is not null)
        {
            foreach (var member in members)
            {
                await RecomputePrimaryAsync(member, ct, member.PackageName, releaseTag);
            }
        }

        await HealRootPresentationAsync(root, kind, analyses, now, ct);
        var multi = members.Count > 1;
        foreach (var member in members)
        {
            member.DisplayName = BuildDisplayName(member, root, multi);
        }

        // Letter-avatars need the final display name, so they are generated
        // after the names settle rather than during the analysis.
        foreach (var target in presented.Distinct())
        {
            if (target.IconHash is null)
            {
                var avatar = LetterAvatarGenerator.Generate(target.DisplayName ?? target.Name);
                await WriteIconFileAsync(avatar, ct);
                target.IconHash = avatar.Sha256;
                target.IconAdaptive = false;
            }
        }

        return result;
    }

    /// <summary>
    /// Drops operator-excluded packages (table <c>app_download_exclusions</c>)
    /// from the release before it is grouped, and deletes the stale candidates
    /// already stored for them. A root whose stored identity is excluded is
    /// cleared so the surviving group becomes the entry instead of a variant.
    /// When everything would be excluded the release is kept unchanged: an
    /// operator typo must never empty a row.
    /// </summary>
    private async Task<IReadOnlyList<ArtifactAnalysis>> ApplyDownloadExclusionsAsync(
        App root, IReadOnlyList<ArtifactAnalysis> analyses, CancellationToken ct)
    {
        var excluded = await LoadExcludedPackagesAsync(root.Slug, ct);
        if (excluded.Count == 0)
        {
            return analyses;
        }

        var kept = analyses.Where(a => !IsExcluded(excluded, a.Badging.PackageName)).ToList();
        if (kept.Count == 0)
        {
            // A partial pass can re-scan only the excluded artifact while its
            // surviving sibling stays recorded and Complete, so re-applying the
            // excluded analysis would silently undo the operator's exclusion.
            // Keep the stored state in that case. Only when no non-excluded
            // candidate is recorded is the release genuinely all-excluded
            // (operator typo), and then it stays applied unchanged so the row
            // never goes empty.
            var stored = await LoadGroupDownloadsAsync(root, ct);
            return stored.Any(d => !IsExcluded(excluded, d.PackageName)) ? [] : analyses;
        }

        foreach (var download in await LoadGroupDownloadsAsync(root, ct))
        {
            if (IsExcluded(excluded, download.PackageName))
            {
                db.Downloads.Remove(download);
            }
        }

        // The stored package is the root-group selector: pointing at an
        // excluded package would keep the removed group alive, so clear it and
        // let SelectRootGroup fall back to the group that survives.
        if (IsExcluded(excluded, root.PackageName))
        {
            root.PackageName = null;
            root.ApkLabel = null;
        }

        // A variant whose only package was excluded must not linger as an
        // empty entry; drop and tombstone it like a vanished variant.
        foreach (var variant in await LoadVariantGroupAsync(root, ct))
        {
            if (!IsExcluded(excluded, variant.PackageName)
                || (await LoadDownloadsAsync(variant, ct)).Count > 0)
            {
                continue;
            }

            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
        }

        return kept;
    }

    private async Task<HashSet<string>> LoadExcludedPackagesAsync(string slug, CancellationToken ct)
    {
        var packages = await db.AppDownloadExclusions.AsNoTracking()
            .Where(x => x.AppSlug == slug)
            .Select(x => x.PackageName)
            .ToListAsync(ct);
        return new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(HashSet<string> excluded, string? packageName) =>
        !string.IsNullOrEmpty(packageName) && excluded.Contains(packageName);

    /// <summary>
    /// One app name's artifacts within a release. Same label means the same
    /// user-facing app; a blank label cannot group and stays one group per
    /// package.
    /// </summary>
    private sealed record LabelGroup(string? Label, List<ArtifactAnalysis> Items);

    private static List<LabelGroup> GroupByLabel(IReadOnlyList<ArtifactAnalysis> analyses)
    {
        var groups = new List<LabelGroup>();
        foreach (var analysis in analyses)
        {
            var label = NormalizeLabel(analysis.Badging.ApplicationLabel);
            var group = label is null
                ? null
                : groups.FirstOrDefault(g => string.Equals(g.Label, label, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new LabelGroup(label, []);
                groups.Add(group);
            }

            group.Items.Add(analysis);
        }

        return groups;
    }

    private static string? NormalizeLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static List<string> GroupPackages(LabelGroup group) =>
        group.Items.Select(a => a.Badging.PackageName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The group that owns the list entry: the stored APK label, else the group
    /// holding the list URL's package, else the root package, else the group
    /// carrying the broadest (base) package. The list entry is normally the
    /// base app; flavors hang off it.
    /// </summary>
    private static LabelGroup? SelectRootGroup(App root, List<LabelGroup> groups, string? listPackage)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var current = NormalizeLabel(root.ApkLabel);
        var match = current is null
            ? null
            : groups.FirstOrDefault(g => string.Equals(g.Label, current, StringComparison.OrdinalIgnoreCase));
        match ??= listPackage is null ? null : GroupWithPackage(groups, listPackage);

        // A stored package is the root's identity: when the release no longer
        // carries it, every group is a different app, so the root stays
        // unclaimed until HealRootPresentationAsync refills it from its primary.
        if (!string.IsNullOrEmpty(root.PackageName))
        {
            return match ?? GroupWithPackage(groups, root.PackageName);
        }

        // First enrich: nothing stored yet, so the broadest group is the list app.
        var basePackage = FindBasePackage(groups.SelectMany(g => GroupPackages(g)).ToList());
        match ??= basePackage is null ? null : GroupWithPackage(groups, basePackage);
        return match ?? groups[0];
    }

    private static LabelGroup? GroupWithPackage(List<LabelGroup> groups, string packageName) =>
        groups.FirstOrDefault(g => g.Items.Any(a =>
            string.Equals(a.Badging.PackageName, packageName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The package that presents a group: the list URL's package when present,
    /// else the dot-prefix base (the flavor parent), else the package whose id
    /// names the app (label/repo token, e.g. <c>com.dergoogler.mmrl</c> over an
    /// obfuscated spoof build), else the first.
    /// </summary>
    private static string? ResolveCanonicalPackage(
        App root, string? label, IReadOnlyList<string> packages, string? listPackage)
    {
        if (packages.Count <= 1)
        {
            return packages.FirstOrDefault();
        }

        if (listPackage is not null)
        {
            var listed = packages.FirstOrDefault(p => string.Equals(p, listPackage, StringComparison.OrdinalIgnoreCase));
            if (listed is not null)
            {
                return listed;
            }
        }

        var basePackage = FindBasePackage(packages);
        if (basePackage is not null)
        {
            return basePackage;
        }

        foreach (var token in IdentityTokens(root, label))
        {
            var tokenMatch = packages.FirstOrDefault(p => p.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (tokenMatch is not null)
            {
                return tokenMatch;
            }
        }

        return packages[0];
    }

    /// <summary>
    /// The shortest package that prefixes every other with a dot (the flavor
    /// parent: <c>app.mihon</c> for <c>app.mihon.foss</c>). Candidates must be
    /// members, so an unrelated short id cannot win.
    /// </summary>
    private static string? FindBasePackage(IReadOnlyList<string> packages) =>
        packages
            .Where(p => packages.All(q => string.Equals(p, q, StringComparison.OrdinalIgnoreCase)
                || q.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();

    private static IEnumerable<string> IdentityTokens(App root, string? label)
    {
        foreach (var raw in new[] { label, RepoName(root.Url), RepoName(root.SourceUrl) })
        {
            var token = NormalizeToken(raw);
            if (token is { Length: >= 3 })
            {
                yield return token;
            }
        }
    }

    private static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? RepoName(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// Package id a list entry's own URL targets (F-Droid or Play), the
    /// strongest signal for which package the entry means.
    /// </summary>
    private static string? ListEndpointPackage(App root)
    {
        if (SourceClassifier.TryParseFdroidPackage(root.Url, out var fdroid)
            || SourceClassifier.TryParseFdroidPackage(root.SourceUrl, out fdroid))
        {
            return fdroid;
        }

        if (SourceClassifier.TryParsePlayPackage(root.Url, out var play)
            || SourceClassifier.TryParsePlayPackage(root.SourceUrl, out play))
        {
            return play;
        }

        return null;
    }

    /// <summary>
    /// Folds pre-flavor-grouping variant rows back into the root. A variant
    /// whose stored APK label matches the root's is a flavor of the same app:
    /// its candidates move onto the root (each keeping its package), the row is
    /// deleted and tombstoned so cached clients drop it. Root <c>UpdatedAt</c>
    /// bumps so those clients also refetch the merged entry.
    /// </summary>
    private async Task MergeSameLabelVariantsAsync(
        App root, string? rootLabel, string? listPackage, DateTimeOffset now, CancellationToken ct)
    {
        var label = rootLabel ?? NormalizeLabel(root.ApkLabel);
        if (label is null)
        {
            return;
        }

        var variants = await LoadVariantGroupAsync(root, ct);
        if (variants.Count == 0)
        {
            return;
        }

        var packages = new List<string>();
        foreach (var variant in variants)
        {
            if (!string.IsNullOrEmpty(variant.PackageName))
            {
                packages.Add(variant.PackageName);
            }
        }

        foreach (var download in await LoadDownloadsAsync(root, ct))
        {
            if (!string.IsNullOrEmpty(download.PackageName))
            {
                packages.Add(download.PackageName);
            }
        }

        if (!string.IsNullOrEmpty(root.PackageName))
        {
            packages.Add(root.PackageName);
        }

        var canonical = ResolveCanonicalPackage(root, label, packages.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), listPackage);

        var folded = false;
        foreach (var variant in variants)
        {
            if (!string.Equals(NormalizeLabel(variant.ApkLabel), label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var download in await LoadDownloadsAsync(variant, ct))
            {
                download.App = root;
                download.AppId = root.Id;
                download.PackageName ??= variant.PackageName;
            }

            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
            folded = true;
        }

        if (!folded)
        {
            return;
        }

        if (canonical is not null && !string.Equals(root.PackageName, canonical, StringComparison.OrdinalIgnoreCase))
        {
            root.PackageName = canonical;
        }

        // Folding changes the entry's candidates; refresh cached clients.
        root.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task WriteRemovedTombstoneAsync(App app, CancellationToken ct)
    {
        var existing = db.RemovedApps.Local.FirstOrDefault(t => t.Slug == app.Slug)
            ?? await db.RemovedApps.FirstOrDefaultAsync(t => t.Slug == app.Slug, ct);
        if (existing is null)
        {
            existing = new RemovedApp { Slug = app.Slug };
            db.RemovedApps.Add(existing);
        }

        existing.Name = app.Name;
        existing.Listing = app.Listing;
        // Commit time, not the pass start: a client that synced mid-pass must
        // still see the removal, the same reason the Shizuku gate stamps so.
        existing.RemovedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Drops TV and Wear OS artifacts when the same package also ships a phone
    /// build. A package that only ships a TV or watch build is kept: then that
    /// form factor is the app. Grouped by package so a multi-app repo's phone
    /// variant never suppresses another package's watch-only variant.
    /// </summary>
    private static List<ArtifactAnalysis> PreferPhoneAnalyses(IReadOnlyList<ArtifactAnalysis> analyses)
    {
        var kept = new List<ArtifactAnalysis>(analyses.Count);
        foreach (var group in analyses.GroupBy(a => a.Badging.PackageName ?? string.Empty, StringComparer.Ordinal))
        {
            var hasPhone = group.Any(a => !a.Badging.IsTvFormFactor && !a.Badging.IsWearFormFactor);
            foreach (var analysis in group)
            {
                if (hasPhone && (analysis.Badging.IsTvFormFactor || analysis.Badging.IsWearFormFactor))
                {
                    continue;
                }

                kept.Add(analysis);
            }
        }

        return kept;
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

        var primary = await PrimaryDownloadAsync(root, ct);
        if (primary is null || analyses.Any(a => a.ArtifactUrl == primary.ApkUrl))
        {
            return;
        }

        var result = await DownloadAndAnalyzeApkAsync(primary.ApkUrl, null, kind, null, null, ct);
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

    private static string BuildDisplayName(App member, App root, bool multi)
    {
        var label = string.IsNullOrWhiteSpace(member.ApkLabel) ? member.Name : member.ApkLabel;
        // The parenthetical only disambiguates; drop it when the APK label is
        // already the list name (naming a row "DroidOS (DroidOS)" helps nobody).
        return multi && !string.Equals(label, root.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{label} ({root.Name})"
            : label;
    }

    /// <summary>
    /// The row for a non-root app name (one repo, several distinct apps). A
    /// new name becomes a variant row that mirrors the list entry's metadata
    /// and points back at it; its package identifies it across passes.
    /// </summary>
    private async Task<App?> EnsureVariantAsync(
        App root, SourceKind kind, string? packageName,
        IReadOnlyDictionary<string, SiblingClaim> siblingClaims, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(packageName))
        {
            return root;
        }

        var variants = await LoadVariantGroupAsync(root, ct);
        var existing = variants.FirstOrDefault(v =>
            string.Equals(v.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        if (siblingClaims.TryGetValue(packageName, out var owner))
        {
            // A live entry sharing this source repo already serves the
            // package; a second listing would overwrite it on install.
            log?.LogInformation(
                "Skipping variant {Package} for {Root}: already served by {Owner}.",
                packageName, root.Slug, owner.Owner.Slug);
            return null;
        }

        var variant = new App
        {
            Slug = await UniqueVariantSlugAsync(root, packageName, ct),
            Name = root.Name,
            DisplayName = root.Name,
            Url = root.Url,
            Description = root.Description,
            License = root.License,
            Listing = root.Listing,
            Type = root.Type,
            SourceUrl = root.SourceUrl,
            SourceKind = kind,
            CategoryId = root.CategoryId,
            ParentId = root.ParentId,
            AuthorName = root.AuthorName,
            AuthorUrl = root.AuthorUrl,
            AuthorKey = root.AuthorKey,
            Stars = root.Stars,
            DownloadTotal = root.DownloadTotal,
            FullDescription = root.FullDescription,
            Changelog = root.Changelog,
            ChangelogUrl = root.ChangelogUrl,
            AddedAt = now,
            UpdatedAt = now,
            Root = root,
            PackageName = packageName,
        };

        // Add before wiring downloads: EF assigns a distinct temporary key so
        // each new variant's downloads stay separated in the change tracker.
        db.Apps.Add(variant);
        return variant;
    }

    private sealed record SiblingClaim(App Owner, bool RootPackage);

    /// <summary>
    /// Packages already served by live list entries that share this root's
    /// source repo, mapped to the owning entry. A sibling's own row always
    /// owns its package; its variants count too, because the sibling pass
    /// would otherwise mirror the package back.
    /// </summary>
    private async Task<Dictionary<string, SiblingClaim>> LoadSiblingPackageClaimsAsync(
        App root, CancellationToken ct)
    {
        var claims = new Dictionary<string, SiblingClaim>(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in await LoadSiblingRootsAsync(root, ct))
        {
            if (!string.IsNullOrEmpty(sibling.PackageName))
            {
                claims.TryAdd(sibling.PackageName, new SiblingClaim(sibling, RootPackage: true));
            }

            foreach (var variant in await LoadVariantGroupAsync(sibling, ct))
            {
                if (!string.IsNullOrEmpty(variant.PackageName))
                {
                    claims.TryAdd(variant.PackageName, new SiblingClaim(sibling, RootPackage: false));
                }
            }
        }

        return claims;
    }

    /// <summary>
    /// Live (non-excluded) roots whose list entry targets the same source repo
    /// as this one, tracked or just added in the current pass. One repo can
    /// ship several apps, so sibling entries are legitimate; they only must
    /// not mirror each other's packages.
    /// </summary>
    private async Task<List<App>> LoadSiblingRootsAsync(App root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root.SourceUrl))
        {
            return [];
        }

        var source = NormalizeSourceUrl(root.SourceUrl);
        var siblings = new List<App>();
        var rows = await db.Apps.AsNoTracking()
            .Where(a => a.Id != root.Id && a.RootAppId == null && a.SourceUrl != null)
            .ToListAsync(ct);
        foreach (var app in rows)
        {
            if (app.Availability != Availability.Excluded
                && string.Equals(NormalizeSourceUrl(app.SourceUrl!), source, StringComparison.OrdinalIgnoreCase))
            {
                siblings.Add(app);
            }
        }

        foreach (var local in db.Apps.Local)
        {
            if (local.Id != root.Id
                && local.RootAppId == null
                && local.SourceUrl is not null
                && local.Availability != Availability.Excluded
                && db.Entry(local).State != EntityState.Deleted
                && string.Equals(NormalizeSourceUrl(local.SourceUrl), source, StringComparison.OrdinalIgnoreCase)
                && !siblings.Contains(local))
            {
                siblings.Add(local);
            }
        }

        return siblings;
    }

    private static string NormalizeSourceUrl(string sourceUrl) => sourceUrl.TrimEnd('/');

    /// <summary>
    /// Drops variants whose package a sibling entry sharing this source repo
    /// already serves. A list entry always keeps its own package; when two
    /// siblings both mirrored one unlisted package, the lower root id wins so
    /// both passes agree on the owner.
    /// </summary>
    private async Task PruneSiblingOwnedVariantsAsync(
        App root, IReadOnlyDictionary<string, SiblingClaim> siblingClaims, CancellationToken ct)
    {
        foreach (var variant in await LoadVariantGroupAsync(root, ct))
        {
            if (string.IsNullOrEmpty(variant.PackageName)
                || !siblingClaims.TryGetValue(variant.PackageName, out var claim)
                || (!claim.RootPackage && claim.Owner.Id > root.Id))
            {
                continue;
            }

            log?.LogInformation(
                "Removing variant {Slug}: package {Package} is already served by {Owner}.",
                variant.Slug, variant.PackageName, claim.Owner.Slug);
            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
        }
    }

    private async Task<List<App>> LoadVariantGroupAsync(App root, CancellationToken ct)
    {
        // The query still sees a variant marked for removal (the DELETE has not
        // been flushed), so filter deleted rows out of the group.
        var rows = (await db.Apps.Where(a => a.RootAppId == root.Id).ToListAsync(ct))
            .Where(a => db.Entry(a).State != EntityState.Deleted)
            .ToList();
        foreach (var local in db.Apps.Local)
        {
            if (local.RootAppId == root.Id
                && db.Entry(local).State != EntityState.Deleted
                && !rows.Contains(local))
            {
                rows.Add(local);
            }
        }

        return rows;
    }

    private async Task<List<AppDownload>> LoadGroupDownloadsAsync(App root, CancellationToken ct)
    {
        var rows = await LoadDownloadsAsync(root, ct);
        foreach (var variant in await LoadVariantGroupAsync(root, ct))
        {
            rows.AddRange(await LoadDownloadsAsync(variant, ct));
        }

        return rows;
    }

    private async Task<string> UniqueVariantSlugAsync(App root, string packageName, CancellationToken ct)
    {
        var baseSlug = Slug.Slugify(packageName);
        var used = new HashSet<string>(await db.Apps.Select(a => a.Slug).ToListAsync(ct), StringComparer.Ordinal);
        foreach (var local in db.Apps.Local)
        {
            used.Add(local.Slug);
        }

        var candidate = baseSlug;
        var i = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseSlug}-{i++}";
        }

        return candidate.Length > 200 ? candidate[..200] : candidate;
    }

    private async Task RemoveVanishedVariantsAsync(
        App root, IReadOnlyList<App> variants, IReadOnlyList<SourceAsset> scannedAssets, CancellationToken ct)
    {
        var scannedUrls = scannedAssets
            .Where(IsApkAsset)
            .Select(a => a.Url)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var downloads = await LoadDownloadsAsync(variant, ct);
            if (downloads.Count == 0 || downloads.Any(d => scannedUrls.Contains(d.ApkUrl)))
            {
                continue;
            }

            log?.LogInformation(
                "Removing variant {Slug}: package {Package} is no longer published by {Root}.",
                variant.Slug, variant.PackageName, root.Slug);
            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
        }
    }

    private static bool IsApkAsset(SourceAsset asset) =>
        asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
        || asset.Url.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records an index-only (not downloaded) candidate, used for the
    /// per-architecture siblings of an F-Droid package.
    /// </summary>
    private async Task UpsertIndexAssetAsync(
        App app, SourceKind kind, SourceAsset asset, string? packageName, DateTimeOffset now, CancellationToken ct)
    {
        await UpsertDownloadAsync(app, new DownloadCandidate(
            kind,
            asset.Name,
            asset.Url,
            ArchiveEntry: null,
            VersionCode: asset.VersionCode,
            VersionName: asset.VersionName,
            SizeBytes: asset.Size,
            Sha256: asset.Sha256,
            SigSha256: null,
            SigMd5: asset.SigMd5,
            MinSdk: null,
            Abi: asset.Abi,
            PackageName: packageName), now, ct);
    }

    /// <summary>
    /// Best-effort GitLab popularity: star count from the project metadata.
    /// Developer identity stays path-derived (<see cref="ApplyGitLabAuthor"/>);
    /// stats refresh even when the release list fails, so they stay current
    /// without a new release. Never throws (except on cancellation).
    /// </summary>
    private async Task RefreshGitLabStatsAsync(App app, string projectPath, CancellationToken ct)
    {
        GitLabProjectStats? stats = null;
        try
        {
            stats = await gitlab.GetProjectStatsAsync(projectPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Project stats fetch failed for {Project}.", projectPath);
        }

        if (stats?.Stars is { } stars)
        {
            app.Stars = stars;
        }
    }

    /// <summary>
    /// Extraction generation of the current binary. Bump it whenever the
    /// analysis gains a persisted field: rows older than this are re-analyzed
    /// once so the new field backfills (see <see cref="NeedsAnalysisHeal"/>).
    /// </summary>
    public const int CurrentAnalysisVersion = 2;

    /// <summary>
    /// Pre-versioning rows recorded a fully analyzed build but predate one or
    /// more current extraction fields (full ABI set, localized labels, signer
    /// details); the same-asset short-circuits would keep them empty forever,
    /// so re-analyze once. Index-only rows never ran an analysis and stay
    /// untouched.
    /// </summary>
    private static bool NeedsAnalysisHeal(AppDownload? primary) =>
        primary is not null
        && primary.Analyzed
        && primary.AnalysisVersion < CurrentAnalysisVersion;

    /// <summary>
    /// Pre-fix rows recorded a fully analyzed build but never persisted its
    /// permissions (the F-Droid path never wrote them); the same-asset
    /// short-circuits would keep them blank forever, so re-analyze once. Only
    /// analyzed rows heal: index-only rows never went through a badging pass,
    /// so their empty permission list is not an error. A genuinely
    /// permission-less analyzed build re-verifies each pass; such builds are
    /// all but nonexistent.
    /// </summary>
    private static bool NeedsPermissionHeal(App app, AppDownload? primary) =>
        primary is not null
        && primary.Analyzed
        && app.Permissions is not { Count: > 0 };

    /// <summary>
    /// True when an index-only row predates the index-v2 signer map and the
    /// fresh index entry now carries a certificate SHA-256 to backfill it.
    /// </summary>
    private static bool NeedsSignerBackfill(AppDownload? primary, FdroidPackageInfo package) =>
        primary is not null
        && !primary.Analyzed
        && primary.SigSha256 is null
        && package.SigSha256 is not null;

    /// <summary>
    /// Pre-signals rows recorded a fully analyzed build but never scanned it
    /// for analysis signals (Dhizuku declaration, Exodus tracker code
    /// signatures); the same-asset short-circuits would keep them empty
    /// forever, so re-analyze once. The inspected flag is set by the first
    /// signal scan and never downgraded; index-only rows never ran a badging
    /// pass and stay untouched.
    /// </summary>
    private static bool NeedsSignalHeal(AppDownload? primary) =>
        primary is not null
        && primary.Analyzed
        && !primary.Inspected;

    /// <summary>
    /// The top-level GitLab namespace (group or user) is a stable developer
    /// identity, so it can be read from the project path without a call.
    /// </summary>
    private static void ApplyGitLabAuthor(App app, string projectPath)
    {
        var group = projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(group))
        {
            return;
        }

        app.AuthorName = group;
        app.AuthorUrl = $"https://gitlab.com/{group}";
        app.AuthorKey = $"gitlab:{group.ToLowerInvariant()}";
    }

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

    /// <summary>
    /// Best-effort icon source for apps that stay external-only (no APK
    /// anywhere): scrapes the linked Play Store listing. Never touches
    /// apk_url; returns true only when an icon file was adopted.
    /// </summary>
    private async Task<bool> TryPlayIconAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        if (play is null)
        {
            return false;
        }

        var packageId = string.Empty;
        if (!SourceClassifier.TryParsePlayPackage(app.Url, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.SourceUrl, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.StoreUrl, out packageId))
        {
            return false;
        }

        try
        {
            var iconUrl = await play.GetIconUrlAsync(packageId, ct);
            return iconUrl is not null && await TryAdoptPlayIconAsync(app, iconUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Play icon fetch failed for {Slug}.", app.Slug);
            return false;
        }
    }

    /// <summary>
    /// Downloads and adopts an already resolved Play icon URL; shared by the
    /// plain icon lookup and the richer details scrape so the page is fetched
    /// only once per enrich pass.
    /// </summary>
    private async Task<bool> TryAdoptPlayIconAsync(App app, string iconUrl, CancellationToken ct)
    {
        try
        {
            using var response = await downloads.GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var icon = IconProcessor.ProcessRawImage(bytes);
            if (icon is null)
            {
                return false;
            }

            await WriteIconFileAsync(icon, ct);
            var oldIcon = app.IconHash;
            app.IconHash = icon.Sha256;
            app.IconAdaptive = false;
            await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Play icon fetch failed for {Slug}.", app.Slug);
            return false;
        }
    }

    private sealed record DownloadCandidate(
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

    /// <summary>
    /// Signing identity of a candidate: the first SHA-256 token, else the
    /// first MD5 token, else a URL-derived fallback for fingerprintless rows.
    /// The downloads snapshot keeps one row per identity (newest build only).
    /// </summary>
    private static string ComputeSigKey(string? sigSha256, string? sigMd5, string apkUrl)
    {
        var key = FirstFingerprint(sigSha256) ?? FirstFingerprint(sigMd5);
        return key ?? "url:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apkUrl)));
    }

    private static string? FirstFingerprint(string? value) =>
        value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();

    /// <summary>
    /// Encodes localized labels as <c>locale=label</c> entries for the
    /// newline-joined column, preserving dictionary order.
    /// </summary>
    private static List<string> LocalizedLabelEntries(IReadOnlyDictionary<string, string> labels) =>
        labels.Select(kv => $"{kv.Key}={kv.Value}").ToList();

    /// <summary>
    /// Compares two artifact checksums, tolerating a <c>algo:</c> prefix and
    /// case so a source digest and the recorded bare hex compare equal.
    /// </summary>
    private static bool HashMatches(string? left, string? right)
    {
        var a = NormalizeHash(left);
        var b = NormalizeHash(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var value = hash.Trim();
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            value = value[(colon + 1)..].Trim();
        }

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    /// <summary>
    /// Records an analyzed artifact on the row so flavor twins of the same
    /// version are skipped without another download or AI analysis. Entries are
    /// <c>sha256 url</c>; a repeated URL replaces its checksum.
    /// </summary>
    private static void RememberArtifact(AppDownload row, DownloadCandidate candidate) =>
        RememberArtifact(row, candidate.Sha256, candidate.ApkUrl);

    private static void RememberArtifact(AppDownload row, string? sha, string url)
    {
        if (sha is not { Length: > 0 })
        {
            return;
        }

        row.AnalyzedArtifacts.RemoveAll(entry =>
            RecordedArtifacts.Parse(entry) is { } parsed && string.Equals(parsed.Url, url, StringComparison.Ordinal));
        row.AnalyzedArtifacts.Add($"{sha} {url}");
    }

    /// <summary>
    /// Rank for same-version flavor twins: stable release builds beat debug,
    /// test, beta and terminal builds; equal rank keeps the incumbent.
    /// </summary>
    private static int ArtifactRank(string url)
    {
        var name = url[(url.LastIndexOf('/') + 1)..];
        var rank = 0;
        if (name.Contains("release", StringComparison.OrdinalIgnoreCase))
        {
            rank += 2;
        }

        if (name.Contains("debug", StringComparison.OrdinalIgnoreCase)
            || name.Contains("test", StringComparison.OrdinalIgnoreCase)
            || name.Contains("beta", StringComparison.OrdinalIgnoreCase)
            || name.Contains("terminal", StringComparison.OrdinalIgnoreCase))
        {
            rank -= 2;
        }

        return rank;
    }

    /// <summary>
    /// A release newer than the one already recorded means a version bump may
    /// still be pending even when the artifact bytes match, so the checksum
    /// short-circuits must not fire.
    /// </summary>
    private static bool ReleasedNewerThan(DateTimeOffset? releasedAt, App row) =>
        releasedAt is not null && (row.VersionUpdatedAt is null || releasedAt > row.VersionUpdatedAt);

    // IzzyOnDroid hosts the developers' own upstream builds (same signing key
    // as forge releases); only f-droid.org rebuilds use a different key.
    private static bool IsForgeSource(SourceKind kind) => kind != SourceKind.FDroid;

    private static int SourceOrder(SourceKind kind) => kind switch
    {
        SourceKind.GitHub => 0,
        SourceKind.GitLab => 1,
        SourceKind.Izzy => 2,
        SourceKind.Codeberg => 3,
        SourceKind.Other => 4,
        SourceKind.FDroid => 5,
        _ => 6,
    };

    private async Task<List<AppDownload>> LoadDownloadsAsync(App app, CancellationToken ct)
    {
        // A variant created in this pass has no database key yet, so its
        // pending rows are matched by navigation rather than by AppId.
        var rows = app.Id == 0
            ? []
            : await db.Downloads.Where(d => d.AppId == app.Id).ToListAsync(ct);
        foreach (var local in db.Downloads.Local)
        {
            if (!rows.Contains(local) && IsForApp(local, app))
            {
                rows.Add(local);
            }
        }

        // A row marked for removal must not count as a candidate: the primary
        // recompute would otherwise pick it and persist no live primary.
        return rows.Where(r => db.Entry(r).State != EntityState.Deleted).ToList();
    }

    private static bool IsForApp(AppDownload row, App app) =>
        app.Id != 0 ? row.AppId == app.Id : ReferenceEquals(row.App, app);

    private async Task<AppDownload?> PrimaryDownloadAsync(App app, CancellationToken ct) =>
        (await LoadDownloadsAsync(app, ct)).FirstOrDefault(d => d.IsPrimary);

    private async Task<bool> HasDownloadsAsync(App app, CancellationToken ct) =>
        await db.Downloads.AnyAsync(d => d.AppId == app.Id, ct);

    /// <summary>
    /// Upserts the row for the candidate's signing identity. Newer versions
    /// replace older ones; on a version tie the forge source's URL wins. A
    /// row is matched by signing key, then by MD5, then by the APK's SHA-256,
    /// so an index-only twin without a SHA-256 identity merges with its
    /// analyzed counterpart instead of duplicating it.
    /// </summary>
    private async Task UpsertDownloadAsync(App app, DownloadCandidate candidate, DateTimeOffset now, CancellationToken ct)
    {
        var sigKey = ComputeSigKey(candidate.SigSha256, candidate.SigMd5, candidate.ApkUrl);
        var md5 = FirstFingerprint(candidate.SigMd5);
        var rows = await LoadDownloadsAsync(app, ct);
        // Package, signing identity and ABI are the key: one release can ship
        // several per-arch APKs that share a signing identity, and one app row
        // can carry several flavor packages (FOSS vs Play, debug vs release).
        // An unknown package (index-only asset) matches any; a null-package row
        // is legacy data that adopts the package instead of gaining a twin.
        bool SamePackage(string? value) =>
            candidate.PackageName is null || string.Equals(value, candidate.PackageName, StringComparison.OrdinalIgnoreCase);
        var row = rows.FirstOrDefault(d => SamePackage(d.PackageName) && d.SigKey == sigKey && d.Abi == candidate.Abi)
            ?? (md5 is null ? null : rows.FirstOrDefault(d => SamePackage(d.PackageName) && FirstFingerprint(d.SigMd5) == md5 && d.Abi == candidate.Abi))
            ?? (candidate.Sha256 is null ? null : rows.FirstOrDefault(d => SamePackage(d.PackageName) && d.Abi == candidate.Abi && d.Sha256 is not null && HashMatches(d.Sha256, candidate.Sha256)));
        if (row is null)
        {
            row = new AppDownload { App = app, SigKey = sigKey, ApkUrl = candidate.ApkUrl, Abi = candidate.Abi };
            db.Downloads.Add(row);
        }
        else
        {
            row.SigKey = sigKey;
            var newer = row.VersionCode is null || candidate.VersionCode is null || candidate.VersionCode > row.VersionCode;
            var forgeWinsTie = candidate.VersionCode == row.VersionCode
                && IsForgeSource(candidate.Source) && !IsForgeSource(row.Source);
            if (!newer && !forgeWinsTie && candidate.Source != row.Source)
            {
                return;
            }

            // Same source and version: flavor twins (debug vs release, terminal
            // vs full, fdroid vs plain) share the identity row. Keep the row
            // holding the higher-ranked artifact and only remember the twin, so
            // later passes skip it without re-downloading or re-queueing AI.
            if (candidate.Source == row.Source
                && candidate.VersionCode is not null
                && candidate.VersionCode == row.VersionCode
                && !string.Equals(candidate.ApkUrl, row.ApkUrl, StringComparison.Ordinal)
                && ArtifactRank(candidate.ApkUrl) <= ArtifactRank(row.ApkUrl))
            {
                RememberArtifact(row, candidate);
                return;
            }
        }

        // A lower offered version means the app moved from a prerelease track
        // to an older stable one. History rows above the newly served code came
        // from that track, so flag them and keep the version anomaly check
        // focused on skipped releases and failed analyses.
        if (row.VersionCode is long incumbent && candidate.VersionCode is long incoming && incoming < incumbent)
        {
            await FlagSupersededPrereleaseVersionsAsync(app, incoming, ct);
        }

        // A different version invalidates the twin memory of the old release.
        if (candidate.VersionCode is not null && row.VersionCode is not null
            && candidate.VersionCode != row.VersionCode)
        {
            row.AnalyzedArtifacts.Clear();
        }

        row.Source = candidate.Source;
        row.SourceRef = candidate.SourceRef;
        // The release tag is forge metadata; a path that does not know it
        // (index-only updates, local analyses) must not clear a recorded one.
        row.ReleaseTag = candidate.ReleaseTag ?? row.ReleaseTag;
        if (candidate.PackageName is not null)
        {
            row.PackageName = candidate.PackageName;
        }

        // Keep the incumbent artifact in the memory so a heal (debug replaced
        // by release) does not make the old twin look unanalyzed next pass.
        RememberArtifact(row, row.Sha256, row.ApkUrl);
        row.ApkUrl = candidate.ApkUrl;
        row.ArchiveEntry = candidate.ArchiveEntry;
        row.VersionCode = candidate.VersionCode;
        row.VersionName = candidate.VersionName;
        row.SizeBytes = candidate.SizeBytes;
        // A new artifact means a new release; the first checksum ever recorded
        // for a row means this build was never viewed by the AI analyzer. Both
        // queue exactly one source analysis (see the queue call below).
        var apkChanged = candidate.Sha256 is not null
            && row.Sha256 is not null
            && !HashMatches(row.Sha256, candidate.Sha256);
        var firstAnalysis = candidate.Sha256 is not null && row.Sha256 is null;
        row.Sha256 = candidate.Sha256;
        // Index-only candidates carry no fingerprints; never clear what a
        // previous analysis established.
        row.SigSha256 = candidate.SigSha256 ?? row.SigSha256;
        row.SigMd5 = candidate.SigMd5 ?? row.SigMd5;
        // Once a build has been inspected, a later index-only update must not
        // make the row forget that its data came from a real analysis.
        row.Analyzed = row.Analyzed || candidate.Analyzed;
        row.MinSdk = candidate.MinSdk;
        // SDK levels and locales only come from a real analysis; index-only
        // updates must not clear what a previous analysis established.
        row.TargetSdk = candidate.TargetSdk ?? row.TargetSdk;
        row.CompileSdk = candidate.CompileSdk ?? row.CompileSdk;
        if (candidate.Locales is { Count: > 0 })
        {
            row.Locales = candidate.Locales.ToList();
        }

        if (candidate.Abis is { Count: > 0 })
        {
            row.Abis = candidate.Abis.ToList();
        }

        if (candidate.LocalizedLabels is { Count: > 0 })
        {
            row.LocalizedLabels = candidate.LocalizedLabels.ToList();
        }

        // Signer detail and the extraction generation only come from a real
        // analysis; index-only updates keep the last recorded values.
        row.SignerDn = candidate.SignerDn ?? row.SignerDn;
        row.SignerScheme = candidate.SignerScheme ?? row.SignerScheme;
        row.SignerKeyAlgorithm = candidate.SignerKeyAlgorithm ?? row.SignerKeyAlgorithm;
        row.AnalysisVersion = Math.Max(row.AnalysisVersion, candidate.AnalysisVersion);

        // Signals come from a real inspection only; an index-only update
        // keeps whatever the last analysis recorded. The inspected flag marks
        // that a signal scan ran and is never downgraded.
        if (candidate.Inspection is not null)
        {
            row.Inspected = true;
            row.DhizukuDeclared = candidate.Inspection.Signals.DhizukuDeclared;
            row.ShizukuDeclared = row.ShizukuDeclared || candidate.Inspection.Signals.ShizukuDeclared;
            row.Trackers = candidate.Inspection.Trackers
                .Select(t => t.Name).Distinct(StringComparer.Ordinal).ToList();
            row.TrackerSignatures = candidate.Inspection.Trackers
                .Select(t => t.Signature).Distinct(StringComparer.Ordinal).ToList();
            row.TrackerTags = candidate.Inspection.Trackers
                .SelectMany(t => t.Tags.Select(tag => $"{t.Name}:{tag}"))
                .Distinct(StringComparer.Ordinal).ToList();
        }

        // An index-only update can carry the signal parsed from the F-Droid
        // index; an analyzed row keeps it true once any recorded build
        // declared Shizuku (the gate accepts the app on a single witness).
        row.ShizukuDeclared = row.ShizukuDeclared || candidate.ShizukuDeclared;

        row.Abi = candidate.Abi;
        row.ResolvedAt = now;
        RememberArtifact(row, candidate);

        if (usageQueue is not null && (apkChanged || (firstAnalysis && app.UsageAnalyzedAt is null)))
        {
            await usageQueue.EnqueueAsync(app, apkChanged, firstAnalysis, ct);
        }
    }

    /// <summary>
    /// Flags history rows above the newly served version as pre-releases. An
    /// intentional downgrade only happens when a stable release replaces the
    /// prerelease that was served before, so those rows are prerelease
    /// history, not the skipped release the anomaly check looks for.
    /// </summary>
    private async Task FlagSupersededPrereleaseVersionsAsync(App app, long ceiling, CancellationToken ct)
    {
        foreach (var version in app.Versions)
        {
            if (version.VersionCode > ceiling)
            {
                version.IsPrerelease = true;
            }
        }

        if (app.Id == 0)
        {
            return;
        }

        var superseded = await db.AppVersions
            .Where(v => v.AppId == app.Id && v.VersionCode > ceiling && !v.IsPrerelease)
            .ToListAsync(ct);
        foreach (var version in superseded)
        {
            version.IsPrerelease = true;
        }
    }

    /// <summary>
    /// Default candidate for fresh installs: forge sources beat F-Droid/Izzy,
    /// then the newest version, then a fixed source order; when the forge no
    /// longer ships binaries (see <see cref="App.ForgeAssetsStale"/>) the
    /// alternatives compete on version code alone. Exactly one row is primary
    /// per app (invariant enforced here, not by a DB constraint).
    /// </summary>
    private async Task RecomputePrimaryAsync(
        App app, CancellationToken ct, string? preferredPackage = null, string? preferredReleaseTag = null)
    {
        var rows = await LoadDownloadsAsync(app, ct);
        // Flavor packages share one row; the default offer must stay the
        // canonical package (the list URL's package), so a flavor can never
        // become the fresh install. Unknown packages keep the old ordering.
        var candidates = preferredPackage is null
            ? rows
            : rows.Where(r => r.PackageName is null || string.Equals(r.PackageName, preferredPackage, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = rows;
        }

        AppDownload? best = null;
        foreach (var row in candidates)
        {
            best = best is null ? row : PreferDownload(row, best, preferredReleaseTag, preferForge: !app.ForgeAssetsStale);
        }

        // The partial unique index on (app_id) where is_primary is not
        // deferrable, and a single SaveChanges may send the promotion before
        // the demotion. Persist the demotion first (raw, so pending inserts
        // stay untouched), then let the caller's save promote the winner.
        if (rows.Any(r => r.IsPrimary))
        {
            await db.Downloads
                .Where(d => d.AppId == app.Id && d.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPrimary, false), ct);

            // The raw update bypasses the tracker, so the tracked rows still
            // carry their old flag as the original value; re-promoting the
            // same row would then look like a no-op and never be saved.
            foreach (var row in rows)
            {
                var entry = db.Entry(row);
                if (entry.State != EntityState.Detached)
                {
                    entry.Property(x => x.IsPrimary).OriginalValue = false;
                }
            }
        }

        foreach (var row in rows)
        {
            row.IsPrimary = ReferenceEquals(row, best);
        }
    }

    /// <summary>
    /// Rebuilds the primary flag for an app whose stored rows lost it (a
    /// pre-fix recompute bug). Cheap: one existence check, and a recompute
    /// over rows already in the database, never a download.
    /// </summary>
    private async Task HealMissingPrimaryAsync(App app, CancellationToken ct)
    {
        if (app.Id == 0)
        {
            return;
        }

        if (await db.Downloads.AnyAsync(d => d.AppId == app.Id && d.IsPrimary, ct))
        {
            return;
        }

        var rows = await LoadDownloadsAsync(app, ct);
        if (rows.Count == 0)
        {
            return;
        }

        await RecomputePrimaryAsync(app, ct, app.PackageName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rows analyzed before the category tags were stored have tracker names
    /// but no tags. The tags are derivable from the stored names via the
    /// cached Exodus catalog, so backfill them without re-downloading; rows
    /// whose trackers all lack categories retry harmlessly through the cached
    /// catalog. Cheap: two stored-row checks plus an in-memory lookup.
    /// </summary>
    private async Task HealTrackerTagsAsync(App app, CancellationToken ct)
    {
        if (trackers is null)
        {
            return;
        }

        var primary = await PrimaryDownloadAsync(app, ct);
        if (primary is null || primary.Trackers.Count == 0 || primary.TrackerTags.Count > 0)
        {
            return;
        }

        var catalog = await trackers.GetAsync(ct);
        if (catalog.Count == 0)
        {
            return;
        }

        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tracker in catalog)
        {
            if (!byName.TryGetValue(tracker.Name, out var categories))
            {
                byName[tracker.Name] = categories = [];
            }

            categories.AddRange(tracker.Categories);
        }

        var tags = primary.Trackers
            .SelectMany(name => byName.TryGetValue(name, out var categories)
                ? categories.Select(tag => $"{name}:{tag}")
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tags.Count == 0)
        {
            return;
        }

        primary.TrackerTags = tags;
        await db.SaveChangesAsync(ct);
    }

    private static AppDownload PreferDownload(
        AppDownload a, AppDownload b, string? preferredReleaseTag = null, bool preferForge = true)
    {
        // When the forge stopped shipping binaries, its rows compete on
        // version code only: the alternative source's newer build leads.
        if (preferForge)
        {
            var aForge = IsForgeSource(a.Source);
            var bForge = IsForgeSource(b.Source);
            if (aForge != bForge)
            {
                return aForge ? a : b;
            }

            // The release just scanned owns its tag's rows: when a repo switches
            // from prereleases to stable, the freshly recorded stable build must
            // outrank older prerelease rows kept for ABI or signature history.
            if (preferredReleaseTag is not null)
            {
                var aTag = string.Equals(a.ReleaseTag, preferredReleaseTag, StringComparison.Ordinal);
                var bTag = string.Equals(b.ReleaseTag, preferredReleaseTag, StringComparison.Ordinal);
                if (aTag != bTag)
                {
                    return aTag ? a : b;
                }
            }
        }

        var av = a.VersionCode ?? -1;
        var bv = b.VersionCode ?? -1;
        if (av != bv)
        {
            return av > bv ? a : b;
        }

        var aAbi = AbiOrder(a.Abi);
        var bAbi = AbiOrder(b.Abi);
        if (aAbi != bAbi)
        {
            return aAbi < bAbi ? a : b;
        }

        // An analyzed row carries the real SHA-256 identity; prefer it over a
        // twin that only has index metadata when everything else ties.
        if ((a.SigSha256 is null) != (b.SigSha256 is null))
        {
            return a.SigSha256 is not null ? a : b;
        }

        return SourceOrder(a.Source) <= SourceOrder(b.Source) ? a : b;
    }

    // Universal builds run anywhere, so they lead the fresh-install default;
    // arm64-v8a covers most devices when the release ships only splits.
    private static int AbiOrder(string? abi) => abi?.ToLowerInvariant() switch
    {
        null => 0,
        "arm64-v8a" => 1,
        "armeabi-v7a" => 2,
        "x86_64" => 3,
        "x86" => 4,
        _ => 5,
    };

    private async Task RemoveDownloadAsync(App app, SourceKind source, CancellationToken ct)
    {
        var rows = await LoadDownloadsAsync(app, ct);
        foreach (var row in rows.Where(r => r.Source == source))
        {
            db.Downloads.Remove(row);
        }
    }

    /// <summary>
    /// F-Droid index siblings of the primary package: same version name, a
    /// distinct non-null ABI and not a newer version code, so an index-only
    /// row can never outrank the analyzed primary.
    /// </summary>
    private static List<FdroidPackageInfo> FdroidSiblings(
        IReadOnlyList<FdroidPackageInfo> packages, FdroidPackageInfo primary)
    {
        var siblings = new List<FdroidPackageInfo>();
        if (primary.VersionName is null)
        {
            return siblings;
        }

        foreach (var candidate in packages)
        {
            if (ReferenceEquals(candidate, primary)
                || candidate.Abi is null
                || string.Equals(candidate.Abi, primary.Abi, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.VersionName, primary.VersionName, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.VersionCode is { } code
                && primary.VersionCode is { } primaryCode
                && code > primaryCode)
            {
                continue;
            }

            siblings.Add(candidate);
        }

        return siblings;
    }

    /// <summary>
    /// Records every per-architecture sibling of an F-Droid primary as an
    /// index-only download row and drops arch rows that vanished from the
    /// index for that package.
    /// </summary>
    private async Task UpsertFdroidSiblingsAsync(
        App app, SourceKind kind, string repoBase, string packageId,
        IReadOnlyList<FdroidPackageInfo> packages, FdroidPackageInfo primary,
        DateTimeOffset now, CancellationToken ct)
    {
        var siblings = FdroidSiblings(packages, primary);
        foreach (var sibling in siblings)
        {
            await UpsertDownloadAsync(app, new DownloadCandidate(
                kind,
                packageId,
                $"{repoBase.TrimEnd('/')}/{sibling.ApkName}",
                null,
                sibling.VersionCode,
                sibling.VersionName,
                sibling.Size,
                sibling.Sha256,
                sibling.SigSha256,
                null,
                sibling.MinSdk,
                sibling.Abi,
                PackageName: packageId,
                ShizukuDeclared: ShizukuPermission.IsDeclared(sibling.Permissions ?? [])), now, ct);
        }

        var keep = siblings.Select(s => s.ApkName).Append(primary.ApkName).ToHashSet(StringComparer.Ordinal);
        foreach (var row in await LoadDownloadsAsync(app, ct))
        {
            if (row.Source == kind
                && row.SourceRef == packageId
                && row.Abi is not null
                && row.ApkUrl.LastIndexOf('/') is var slash
                && slash >= 0
                && !keep.Contains(row.ApkUrl[(slash + 1)..]))
            {
                db.Downloads.Remove(row);
            }
        }
    }

    /// <summary>
    /// Best-effort IzzyOnDroid rolling download count for an Izzy-primary app
    /// (the only F-Droid-compatible repo that publishes counts). Never fails
    /// an enrich: stats trouble leaves the previous value untouched.
    /// </summary>
    private async Task ApplyIzzyDownloadsAsync(App app, string packageName, CancellationToken ct)
    {
        if (izzyStats is null)
        {
            return;
        }

        try
        {
            if (await izzyStats.GetDownloadsAsync(packageName, ct) is { } downloads)
            {
                app.DownloadTotal = downloads;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Popularity is best-effort; the enrich outcome must not depend on it.
        }
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
            var primary = await PrimaryDownloadAsync(app, ct);
            if (NeedsPermissionHeal(app, primary)
                || NeedsSignalHeal(primary)
                || NeedsAnalysisHeal(primary)
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
            await ApplyIzzyDownloadsAsync(app, package.PackageName, ct);
        }

        var apkUrl = $"{repoBase.TrimEnd('/')}/{package.ApkName}";
        var siblings = FdroidSiblings(packages, package);
        var recorded = await LoadDownloadsAsync(app, ct);
        var siblingsRecorded = siblings.All(s => recorded.Any(d =>
            d.Source == kind
            && d.SourceRef == packageId
            && d.Abi == s.Abi
            && d.ApkUrl.EndsWith('/' + s.ApkName, StringComparison.Ordinal)));
        var current = await PrimaryDownloadAsync(app, ct);
        if (current is not null
            && current.Source == kind
            && current.ApkUrl == apkUrl
            && current.VersionCode == package.VersionCode
            && current.Sha256 is not null
            && (package.Sha256 is null || HashMatches(current.Sha256, package.Sha256))
            && app.PackageName is not null
            && app.IconHash is not null && IconFileExists(app.IconHash)
            && !NeedsPermissionHeal(app, current)
            && !NeedsSignerBackfill(current, package)
            && !NeedsSignalHeal(current)
            && !NeedsAnalysisHeal(current)
            && siblingsRecorded)
        {
            app.EnrichEtag = indexEtag;
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "index unchanged" };
        }

        // Changed version: analyze the actual APK (same quality bar as forge
        // builds). Anything wrong with the file falls back to the index-only
        // record, except a package-name mismatch, which means the index row
        // and the file disagree, so the old values are kept.
        var oldIcon = app.IconHash;
        using var analyzed = await TryAnalyzeDownloadAsync(apkUrl, ct);
        if (analyzed is not null && analyzed.Badging.PackageName != package.PackageName)
        {
            return Fail(app, now, $"F-Droid: {apkUrl} contains {analyzed.Badging.PackageName}, expected {package.PackageName}.");
        }

        var icon = analyzed is not null
            && await launcherIcons.ResolveAsync(analyzed.ApkPath, analyzed.Badging, ct) is { } apkIcon
            ? apkIcon
            : await MirrorIconAsync(package.IconFile, repoBase, app.Name, ct);
        await WriteIconFileAsync(icon, ct);

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

        await UpsertDownloadAsync(app, new DownloadCandidate(
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
            LocalizedLabels: analyzed is null ? null : LocalizedLabelEntries(analyzed.Badging.LocalizedLabels),
            SignerDn: analyzed?.Signers.Dn,
            SignerScheme: analyzed?.Signers.Scheme,
            SignerKeyAlgorithm: analyzed?.Signers.KeyAlgorithm,
            AnalysisVersion: analyzed is null ? 0 : CurrentAnalysisVersion,
            Inspection: analyzed?.Inspection,
            PackageName: package.PackageName, Analyzed: analyzed is not null,
            ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? [])), now, ct);
        await RecomputePrimaryAsync(app, ct, package.PackageName);

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
        await UpsertFdroidSiblingsAsync(app, kind, repoBase, packageId, packages, package, now, ct);
        await RecomputePrimaryAsync(app, ct, package.PackageName);

        await AddVersionRowAsync(app, versionCode, versionName, apkUrl, false, now, ct);

        // F-Droid publishes no release dates, so VersionUpdatedAt stays unknown
        // and the app sorts last under "recently updated".

        await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
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
            await RemoveDownloadAsync(app, SourceKind.FDroid, ct);
            await RecomputePrimaryAfterCandidateAsync(app, ct);
            return;
        }

        var apkUrl = $"{FdroidRepos.FDroidBase.TrimEnd('/')}/{package.ApkName}";
        await UpsertFdroidSiblingsAsync(
            app, SourceKind.FDroid, FdroidRepos.FDroidBase, package.PackageName, packages, package, now, ct);
        var existing = (await LoadDownloadsAsync(app, ct))
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

        using var analyzed = await TryAnalyzeDownloadAsync(apkUrl, ct);
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
                LocalizedLabels: LocalizedLabelEntries(analyzed.Badging.LocalizedLabels),
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
        await UpsertDownloadAsync(app, candidate, now, ct);
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
        var primary = await PrimaryDownloadAsync(app, ct);
        await RecomputePrimaryAsync(app, ct, app.PackageName, primary?.ReleaseTag);
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
    /// One analyzed APK download: badging + file hash/size + (best-effort)
    /// signer certs. Owns the temp file; dispose when done (icon extraction
    /// via <c>IconProcessor</c> happens first, on <see cref="ApkPath"/>).
    /// </summary>
    private sealed record AnalyzedApk(
        string ApkPath,
        BadgingInfo Badging,
        string FileSha256,
        long FileSize,
        ApkSignerInfo Signers,
        ApkInspection Inspection) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(ApkPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Static signals derived from one analyzed APK: manifest-level Shizuku
    /// facts plus Exodus tracker code-signature matches. Candidates carry
    /// null when no analysis ran (index-only rows), so those never overwrite
    /// what an earlier analysis recorded.
    /// </summary>
    private sealed record ApkInspection(ApkSignals Signals, IReadOnlyList<TrackerHit> Trackers);

    private sealed record ArtifactAnalysis(
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

    /// <summary>
    /// Outcome of downloading and analyzing one artifact. <c>Unchanged</c> is
    /// true when the computed checksum matched the recorded one, in which case
    /// no badging, signer or icon work ran and <c>Analysis</c> is null.
    /// </summary>
    private sealed record ArtifactResult(ArtifactAnalysis? Analysis, bool Unchanged, string? Error);

    private async Task<ArtifactResult> DownloadAndAnalyzeApkAsync(
        string url, string? etag, SourceKind lockSource, DateTimeOffset? releasedAt,
        string? expectedSha256, CancellationToken ct)
    {
        // Metadata-only operator pass: report Unchanged so the row is stamped
        // and the release metadata still lands, but spend no bandwidth or CPU
        // on the APK (see EnrichmentOptions.SkipApkAnalysis).
        if (options.SkipApkAnalysis)
        {
            return new ArtifactResult(null, true, null);
        }

        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        try
        {
            await DownloadAsync(url, tempApk, ct);
            return await AnalyzeArtifactAsync(url, null, tempApk, tempApk, etag, lockSource, releasedAt, expectedSha256, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ArtifactResult(null, false, $"Download: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempApk); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Some releases attach only a zip with the APK inside (e.g.
    /// AppControl-X v3.0.0). Downloads the archive, picks the best
    /// <c>.apk</c> entry and analyzes it like a direct download; the
    /// recorded URL/size/hash stay the archive's and
    /// <see cref="App.ApkArchiveEntry"/> names the APK inside.
    /// </summary>
    private async Task<ArtifactResult> DownloadAndAnalyzeZipAsync(
        string zipUrl, string? etag, SourceKind lockSource, DateTimeOffset? releasedAt,
        string? expectedSha256, CancellationToken ct)
    {
        // See DownloadAndAnalyzeApkAsync: same metadata-only skip.
        if (options.SkipApkAnalysis)
        {
            return new ArtifactResult(null, true, null);
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.zip");
        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        try
        {
            await DownloadAsync(zipUrl, tempZip, ct);
            using var zip = ZipFile.OpenRead(tempZip);
            var entry = ApkAssetSelector.PickApk(zip.Entries, e => e.FullName, e => e.Length);
            if (entry is null)
            {
                return new ArtifactResult(null, false, null);
            }

            entry.ExtractToFile(tempApk, overwrite: true);
            return await AnalyzeArtifactAsync(zipUrl, entry.FullName, tempZip, tempApk, etag, lockSource, releasedAt, expectedSha256, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a broken or APK-less candidate zip must not stop
            // the F-Droid fallback (the caller reports no .apk asset if all fail).
            return new ArtifactResult(null, false, ex.Message);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best effort */ }
            try { File.Delete(tempApk); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Shared analysis: artifact hash/size first (returning <c>Unchanged</c>
    /// when it matches <paramref name="expectedSha256"/>), then badging and
    /// (best-effort) signer certs and launcher icon. <paramref name="apkPath"/>
    /// is the APK that gets analyzed, <paramref name="artifactPath"/> the file
    /// clients download (same file unless the APK came out of an archive). Does
    /// not mutate the app row; the caller routes the analysis to the row that
    /// owns its package.
    /// </summary>
    private async Task<ArtifactResult> AnalyzeArtifactAsync(
        string artifactUrl, string? archiveEntry, string artifactPath, string apkPath,
        string? etag, SourceKind lockSource, DateTimeOffset? releasedAt,
        string? expectedSha256, CancellationToken ct)
    {
        // Hash first: when the file is byte-identical to what is already
        // recorded, every derived field (badging, signers, icon) is unchanged
        // too, so the expensive tools can be skipped entirely.
        var started = Stopwatch.GetTimestamp();
        var label = Path.GetFileName(apkPath);
        string artifactSha256;
        long artifactSize;
        try
        {
            using var sha = SHA256.Create();
            await using (var stream = File.OpenRead(artifactPath))
            {
                artifactSize = stream.Length;
                artifactSha256 = Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, ct));
            }
        }
        catch (IOException ex)
        {
            return new ArtifactResult(null, false, $"APK unreadable: {ex.Message}");
        }

        if (expectedSha256 is not null && HashMatches(artifactSha256, expectedSha256))
        {
            JobContext.Current?.Analyze($"analyze {label} unchanged, hashed {artifactSize}B in {Elapsed(started)}ms");
            return new ArtifactResult(null, true, null);
        }

        var badgingStarted = Stopwatch.GetTimestamp();
        BadgingInfo badging;
        try
        {
            badging = BadgingParser.Parse(await aapt2.DumpBadgingAsync(apkPath, ct));
        }
        catch (Exception ex) when (ex is Aapt2Exception or BadgingParseException)
        {
            return new ArtifactResult(null, false, $"aapt2: {ex.Message}");
        }

        var signerStarted = Stopwatch.GetTimestamp();
        var signers = await TryExtractSignersAsync(apkPath, ct);
        var sigSha256 = CertFingerprint.Join(signers.Signers.Select(s => s.Sha256));
        var sigMd5 = CertFingerprint.Join(signers.Signers.Select(s => s.Md5));
        var signalsStarted = Stopwatch.GetTimestamp();
        var inspection = await InspectApkAsync(apkPath, badging, ct);
        var iconStarted = Stopwatch.GetTimestamp();
        // An icons:false admin pass renders nothing: skip the resolve so no
        // raster decode or Gradle XML render runs and no icon gets adopted.
        var icon = options.SkipIconRenders ? null : await ResolveIconAsync(badging, apkPath, ct);
        JobContext.Current?.Analyze($"analyze {label} badging {Elapsed(badgingStarted)}ms, "
            + $"signers {Elapsed(signerStarted)}ms, signals {Elapsed(signalsStarted)}ms, "
            + $"icon {Elapsed(iconStarted)}ms, total {Elapsed(started)}ms");

        return new ArtifactResult(new ArtifactAnalysis(
            artifactUrl, archiveEntry, lockSource, etag, badging, artifactSha256, artifactSize,
            sigSha256, sigMd5, releasedAt, icon, inspection, signers), false, null);
    }

    /// <summary>
    /// Static APK inspection: declared Shizuku/Dhizuku permissions plus
    /// Exodus tracker matches in the DEX code. Both are best-effort garnish;
    /// a failed scan must not fail enrichment, and the DEX read only happens
    /// when a tracker catalog is injected, so test runs skip it.
    /// </summary>
    private async Task<ApkInspection> InspectApkAsync(string apkPath, BadgingInfo badging, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var signals = ShizukuSignalScanner.Scan(badging.Permissions);
        var dexText = TrackerScanner.ReadDexText(apkPath);

        IReadOnlyList<TrackerHit> hits = [];
        if (trackers is not null)
        {
            try
            {
                var catalog = await trackers.GetAsync(ct);
                hits = TrackerScanner.ScanText(dexText, catalog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogDebug(ex, "Tracker scan failed for {ApkPath}.", apkPath);
            }
        }

        JobContext.Current?.Analyze($"inspect {Path.GetFileName(apkPath)} dhizuku={signals.DhizukuDeclared} "
            + $"shizuku={signals.ShizukuDeclared} trackers={hits.Count} in {Elapsed(started)}ms");
        return new ApkInspection(signals, hits);
    }

    /// <summary>
    /// One launcher-icon resolve per package per app pass (see
    /// <c>_iconByPackage</c>). A failed resolve is evicted so the next
    /// artifact can try again instead of replaying the fault.
    /// </summary>
    private async Task<ProcessedIcon?> ResolveIconAsync(
        BadgingInfo badging, string apkPath, CancellationToken ct)
    {
        var key = badging.PackageName is { Length: > 0 } packageName ? packageName : apkPath;
        var entry = _iconByPackage.GetOrAdd(
            key,
            _ => new Lazy<Task<ProcessedIcon?>>(() => launcherIcons.ResolveAsync(
                apkPath, badging, ct, allowXmlRender: !options.DeferXmlIconRenders)));
        try
        {
            return await entry.Value;
        }
        catch
        {
            _iconByPackage.TryRemove(key, out _);
            throw;
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
        var result = await DownloadAndAnalyzeApkAsync(
            url, etag, kind, null, await ExpectedCandidateHashAsync(app, url, ct), ct);
        await ApplyCandidateAsync(app, result, now, ct);
    }

    private async Task ResolveCandidateZipAsync(
        App app, string url, string? etag, SourceKind kind, DateTimeOffset now, CancellationToken ct)
    {
        var result = await DownloadAndAnalyzeZipAsync(
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
        var row = (await LoadDownloadsAsync(app, ct)).FirstOrDefault(d => d.ApkUrl == url);
        return row is not null
            && row.Sha256 is not null
            && app.PackageName is not null
            && app.IconHash is not null
            && IconFileExists(app.IconHash)
            && !NeedsPermissionHeal(app, row)
            && !NeedsAnalysisHeal(row)
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
        var primary = await PrimaryDownloadAsync(app, ct);
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
                        if (!IconFileExists(storeAvatar.Sha256))
                        {
                            await WriteIconFileAsync(storeAvatar, ct);
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
            using var analyzed = await TryAnalyzeDownloadAsync(primary.ApkUrl, ct);
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
                var healed = !IconFileExists(icon.Sha256);
                await WriteIconFileAsync(icon, ct);
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
                await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
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
    public async Task<EnrichResult> CommitIconRefreshAsync(
        App app, byte[]? png, CancellationToken ct = default, bool force = false, bool isAdaptive = false)
    {
        try
        {
            var icon = png is null ? null : LauncherIconService.NormalizeRender(png, isAdaptive);
            if (icon is null)
            {
                return new EnrichResult(EnrichOutcome.Failed, "Icon refresh: batch render produced no usable icon; kept.");
            }

            // Sync even on equal bytes to heal stale flags; the flag is
            // the caller's true-adaptive signal (SyncService forwards the
            // staged root kind), not a blanket XML marker.
            app.IconAdaptive = icon.Adaptive;

            // Write first: a missing file must heal even when the render
            // still matches the recorded icon (write is a no-op otherwise).
            var healed = !IconFileExists(icon.Sha256);
            await WriteIconFileAsync(icon, ct);
            if (icon.Sha256 == app.IconHash)
            {
                // Enriched (not UpToDate) when a file was restored, so the
                // pass tally proves the healing happened; force counts
                // every rewrite (swap detection needs fresh bytes).
                return force || healed
                    ? new EnrichResult(EnrichOutcome.Enriched, null)
                    : new EnrichResult(EnrichOutcome.UpToDate, null);
            }

            await WriteIconFileAsync(icon, ct);
            var oldIcon = app.IconHash;
            app.IconHash = icon.Sha256;
            await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
            return new EnrichResult(EnrichOutcome.Enriched, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EnrichResult(EnrichOutcome.Failed, $"Icon refresh: {ex.Message}");
        }
    }
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
            ? await TryAdoptPlayIconAsync(app, remoteIcon, ct)
            : await TryPlayIconAsync(app, now, ct);
        if (!hasIcon)
        {
            var avatar = LetterAvatarGenerator.Generate(app.DisplayName ?? app.Name);
            await WriteIconFileAsync(avatar, ct);
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

    private async Task<ProcessedIcon> MirrorIconAsync(
        string? iconFile, string repoBase, string appName, CancellationToken ct)
    {
        if (iconFile is not null)
        {
            foreach (var iconUrl in FdroidRepos.IconUrls(repoBase, iconFile))
            {
                try
                {
                    using var response = await downloads.GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (IconProcessor.ProcessRawImage(bytes) is { } icon)
                    {
                        return icon;
                    }

                    break; // Got bytes but undecodable; the legacy size won't decode either.
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    break;
                }
            }
        }

        return LetterAvatarGenerator.Generate(appName);
    }

    private async Task AddVersionRowAsync(
        App app, long? versionCode, string? versionName, string apkUrl, bool isPrerelease,
        DateTimeOffset now, CancellationToken ct)
    {
        // Identity is (app, code), mirroring IX_app_versions_app_id_version_code:
        // upstreams re-tag the same build (vFlow's v1.5.3-pr1 reuses 1.5.2's
        // code), and matching on the name too inserted a duplicate row whose
        // unique violation rolled back the whole enrich (stars, permissions).
        // Pending rows are invisible to AnyAsync, so a multi-ABI release (same
        // versionCode per ABI, same signing key) would insert the row twice.
        var exists = app.Versions.Any(v => v.VersionCode == versionCode)
            || db.AppVersions.Local.Any(v =>
                (ReferenceEquals(v.App, app) || (app.Id != 0 && v.AppId == app.Id))
                && v.VersionCode == versionCode)
            || await db.AppVersions.AnyAsync(v =>
                v.AppId == app.Id && v.VersionCode == versionCode, ct);
        if (exists)
        {
            return;
        }

        app.Versions.Add(new AppVersion
        {
            VersionCode = versionCode,
            VersionName = versionName,
            ApkUrl = apkUrl,
            IsPrerelease = isPrerelease,
            DetectedAt = now,
        });
    }

    private async Task DownloadAsync(string url, string tempApk, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        JobContext.Current?.Download($"download start {url}", new { url });
        using var response = await downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            JobContext.Current?.Download(
                $"download failed HTTP {(int)response.StatusCode} after {Elapsed(started)}ms",
                new { url, status = (int)response.StatusCode }, JobEventLevel.Warning);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}.");
        }

        await using var file = File.Create(tempApk);
        await response.Content.CopyToAsync(file, ct);
        JobContext.Current?.Download(
            $"download done {file.Length}B in {Elapsed(started)}ms", new { url, bytes = file.Length });
    }

    /// <summary>
    /// Download → aapt2 → file hash, with best-effort signer certs. Returns
    /// null when the file can't be fetched or parsed (callers fall back to
    /// index metadata); never throws except on cancellation.
    /// </summary>
    private async Task<AnalyzedApk?> TryAnalyzeDownloadAsync(string apkUrl, CancellationToken ct)
    {
        // Metadata-only operator pass: callers fall back to index metadata.
        if (options.SkipApkAnalysis)
        {
            return null;
        }

        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        AnalyzedApk? result = null;
        try
        {
            await DownloadAsync(apkUrl, tempApk, ct);

            BadgingInfo badging;
            try
            {
                badging = BadgingParser.Parse(await aapt2.DumpBadgingAsync(tempApk, ct));
            }
            catch (Exception ex) when (ex is Aapt2Exception or BadgingParseException)
            {
                return null;
            }

            string fileSha256;
            long fileSize;
            try
            {
                using var sha = SHA256.Create();
                await using (var stream = File.OpenRead(tempApk))
                {
                    fileSize = stream.Length;
                    fileSha256 = Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, ct));
                }
            }
            catch (IOException)
            {
                return null;
            }

            result = new AnalyzedApk(
                tempApk,
                badging,
                fileSha256,
                fileSize,
                await TryExtractSignersAsync(tempApk, ct),
                await InspectApkAsync(tempApk, badging, ct));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // The success path owns the file via AnalyzedApk disposal.
            if (result is null)
            {
                try { File.Delete(tempApk); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Best-effort signer facts (none when apksigner is missing, Java is
    /// absent, or the APK is unsigned). Never fails enrichment.
    /// </summary>
    private async Task<ApkSignerInfo> TryExtractSignersAsync(string apkPath, CancellationToken ct)
    {
        try
        {
            return ApkSignerParser.Parse(await signer.PrintCertsAsync(apkPath, ct));
        }
        catch (Exception ex) when (ex is ApkSignerException or ApkSignerParseException)
        {
            return ApkSignerInfo.None;
        }
    }

    private async Task WriteIconFileAsync(ProcessedIcon icon, CancellationToken ct)
    {
        var path = Path.Combine(options.IconStorePath, $"{icon.Sha256}.png");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(options.IconStorePath);
            await File.WriteAllBytesAsync(path, icon.Png, ct);
        }
    }

    private bool IconFileExists(string sha256) =>
        File.Exists(Path.Combine(options.IconStorePath, $"{sha256}.png"));

    private async Task DeleteIconIfOrphanedAsync(App app, string? oldIcon, CancellationToken ct)
    {
        if (oldIcon is null || oldIcon == app.IconHash)
        {
            return;
        }

        // Local first: fast loop enriches a batch before saving, so
        // other apps may reference the hash only in the change tracker.
        var stillUsedLocal = db.Apps.Local.Any(a =>
            a.Id != app.Id && a.IconHash == oldIcon && db.Entry(a).State != EntityState.Deleted);
        var stillUsed = stillUsedLocal
            || await db.Apps.AnyAsync(a => a.Id != app.Id && a.IconHash == oldIcon, ct);
        if (!stillUsed)
        {
            // Warning on purpose: mass disappearance of icon files once went
            // unnoticed because deletes are silent; the log is the audit trail.
            log?.LogWarning("Deleting orphaned icon file {IconHash}", oldIcon);
            try { File.Delete(Path.Combine(options.IconStorePath, $"{oldIcon}.png")); }
            catch { /* stale file is harmless; next deploy wipes nothing, icons are content-addressed */ }
        }
    }

    private static EnrichResult Fail(App app, DateTimeOffset now, string message)
    {
        app.LastCheckedAt = now;
        app.LastError = message.Length > 500 ? message[..500] + "…" : message;
        return new EnrichResult(EnrichOutcome.Failed, app.LastError);
    }
}
