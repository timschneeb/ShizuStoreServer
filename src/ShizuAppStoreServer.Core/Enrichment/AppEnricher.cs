using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

public enum EnrichOutcome
{
    /// <summary>APK downloaded + aapt2 parsed, row updated (possibly with letter-avatar icon).</summary>
    Enriched,
    /// <summary>Release feed / repo index answered 304 Not Modified (or the same asset is already recorded); only <c>last_checked_at</c> touched.</summary>
    UpToDate,
    /// <summary>No forge source: letter-avatar icon, source kind set, no APK fields.</summary>
    AvatarFallback,
    /// <summary>Play is the only source and no override is set: never sent to clients.</summary>
    Excluded,
    /// <summary>Something failed; <c>last_error</c> set, backoff applies. Previous good values kept.</summary>
    Failed,
    /// <summary>Checked recently (success or backoff window); no work done.</summary>
    SkippedFresh,
}

public sealed record EnrichResult(EnrichOutcome Outcome, string? Error);

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
/// outside their GitHub project and are special-cased: instafel (maintainer
/// API at api.mamii.dev) and hlbmerge_flutter (APK builds only on the GitCode
/// mirror gitcode.com/bigmolihuan/hlbmerge_flutter). Play-sole-source apps are
/// excluded unless the operator override flag is set. Failures record
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
    IInstafelReleaseClient? instafel = null,
    IGitCodeReleaseClient? gitcode = null,
    IPlayStoreClient? play = null,
    IzzyStatsProvider? izzyStats = null,
    ILogger<AppEnricher>? log = null)
{
    // Special-case release homes (user calls): the GitHub projects below
    // publish no usable release assets on GitHub itself.
    private const string InstafelOwner = "mamiiblt";
    private const string InstafelRepo = "instafel";
    private const string HlbmergeGitHubOwner = "molihuan";
    private const string HlbmergeGitHubRepo = "hlbmerge_flutter";
    private const string HlbmergeGitCodeOwner = "bigmolihuan";
    private const string HlbmergeGitCodeRepo = "hlbmerge_flutter";

    // READMEs are unbounded; the full-description screen only needs a sane
    // excerpt, so cap what we persist and send.
    private const int MaxFullDescriptionChars = 200_000;

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

    public async Task<EnrichResult> EnrichAsync(
        App app, DateTimeOffset now, CancellationToken ct = default, bool force = false)
    {
        if (!force
            && app.LastCheckedAt is { } checkedAt
            && checkedAt + (app.LastError is null ? options.SuccessRecheckInterval : options.FailedRecheckInterval) > now)
        {
            return new EnrichResult(EnrichOutcome.SkippedFresh, null);
        }

        try
        {
            var result = await DispatchAsync(app, now, ct);

            // External-only Play apps never get an APK; give them the real
            // listing icon instead of a generated avatar and stop counting
            // them as failures on every pass.
            if (result.Outcome == EnrichOutcome.Failed
                && !await HasDownloadsAsync(app, ct)
                && app.IconHash is null
                && await TryPlayIconAsync(app, now, ct))
            {
                app.LastError = null;
                return new EnrichResult(EnrichOutcome.AvatarFallback, null);
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

    private async Task<EnrichResult> DispatchAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        var githubPrimary = SourceClassifier.TryParseGitHubRepo(app.Url, out var owner, out var repo);
        if (githubPrimary || SourceClassifier.TryParseGitHubRepo(app.SourceUrl, out owner, out repo))
        {
            app.SourceKind = SourceKind.GitHub;
            KeepPlayStoreUrl(app, githubPrimary);
            EnrichResult result;
            if (instafel is not null && owner == InstafelOwner && repo == InstafelRepo)
            {
                result = await EnrichFromInstafelAsync(app, now, ct);
            }
            else if (gitcode is not null && owner == HlbmergeGitHubOwner && repo == HlbmergeGitHubRepo)
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
        // A failing release list must not gate repo metadata: rate limits or
        // missing releases would otherwise also blank stars and developer.
        GitHubRelease? latest;
        try
        {
            latest = await github.GetLatestReleaseAsync(owner, repo, app.EnrichEtag, ct);
        }
        catch (GitHubApiException ex)
        {
            await RefreshGitHubStatsAsync(app, owner, repo, ct);
            return await HandleGitHubFailureAsync(ex);
        }

        GitHubRelease release;
        try
        {
            await RefreshGitHubStatsAsync(app, owner, repo, ct);

            // Raw markdown, not rendered HTML: the client renders markdown.
            if (NeedsReadmeRefresh(app.FullDescription)
                && await github.GetReadmeMarkdownAsync(owner, repo, ct) is { Length: > 0 } readme)
            {
                app.FullDescription = readme.Length > MaxFullDescriptionChars
                    ? readme[..MaxFullDescriptionChars]
                    : readme;
            }

            if (latest is null)
            {
                // A 304 would keep pre-fix rows permission-less forever (see
                // NeedsPermissionHeal): refetch the list once and re-analyze
                // below. A failing refetch is not an upstream change, so stay
                // up-to-date instead of failing the pass.
                if (NeedsPermissionHeal(app, await PrimaryDownloadAsync(app, ct)))
                {
                    try
                    {
                        latest = await github.GetLatestReleaseAsync(owner, repo, null, ct);
                    }
                    catch (GitHubApiException)
                    {
                        latest = null;
                    }
                }

                if (latest is null)
                {
                    app.LastCheckedAt = now;
                    return new EnrichResult(EnrichOutcome.UpToDate, null);
                }
            }

            app.DownloadTotal = latest.TotalDownloads;
            release = latest;
        }
        catch (GitHubApiException ex)
        {
            return await HandleGitHubFailureAsync(ex);
        }

        async Task<EnrichResult> HandleGitHubFailureAsync(GitHubApiException ex)
        {
            // No releases / unknown repo: the app may be F-Droid-only. A
            // transient API error (rate limit, 5xx) must not lock the source.
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

        var asset = ApkAssetSelector.PickApk(release.Assets);
        if (asset is null)
        {
            // Some projects attach only a zip with the APK inside.
            var zip = ApkAssetSelector.PickZip(release.Assets, a => a.Name, a => a.Size);
            if (zip is not null
                && await TryEnrichFromZipAsync(app, zip.BrowserDownloadUrl, release.Etag, SourceKind.GitHub, now, ct, releasedAt: release.PublishedAt) is { } zipped)
            {
                return zipped;
            }

            if (await TryFdroidFallbackAsync(app, now, ct) is { } rescued)
            {
                return rescued;
            }

            if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
            {
                return play;
            }

            return Fail(app, now, $"GitHub release {release.TagName} of {owner}/{repo} has no .apk asset.");
        }

        return await EnrichFromApkAsync(app, asset.BrowserDownloadUrl, release.Etag, SourceKind.GitHub, now, ct, release.PublishedAt);
    }

    /// <summary>
    /// Instafel publishes through api.mamii.dev, not GitHub. The payload's
    /// file hash is the change signal (recorded as the etag): an unchanged
    /// hash plus unchanged URL skips the download entirely.
    /// </summary>
    private async Task<EnrichResult> EnrichFromInstafelAsync(
        App app, DateTimeOffset now, CancellationToken ct)
    {
        InstafelRelease release;
        try
        {
            release = await instafel!.GetLatestAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(app, now, $"Instafel API: {ex.Message}");
        }

        var current = await PrimaryDownloadAsync(app, ct);
        if (current is not null
            && release.ApkUrl == current.ApkUrl
            && release.FileHash == app.EnrichEtag
            && current.VersionCode is not null
            && !NeedsPermissionHeal(app, current))
        {
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null);
        }

        return await EnrichFromApkAsync(app, release.ApkUrl, release.FileHash, SourceKind.GitHub, now, ct);
    }

    /// <summary>
    /// hlbmerge_flutter's APK builds exist only on the GitCode mirror. The
    /// asset URL embeds the tag, so an unchanged URL plus a recorded version
    /// code skips the download.
    /// </summary>
    private async Task<EnrichResult> EnrichFromGitCodeAsync(
        App app, DateTimeOffset now, CancellationToken ct)
    {
        GitCodeRelease release;
        try
        {
            var latest = await gitcode!.GetLatestReleaseAsync(
                HlbmergeGitCodeOwner, HlbmergeGitCodeRepo, ct: ct);
            if (latest is null)
            {
                app.LastCheckedAt = now;
                return new EnrichResult(EnrichOutcome.UpToDate, null);
            }

            release = latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(app, now, $"GitCode: {ex.Message}");
        }

        var asset = ApkAssetSelector.PickApk(release.Assets, a => a.Name, _ => 0L);
        if (asset is null)
        {
            return Fail(app, now, $"GitCode release {release.TagName} of "
                + $"{HlbmergeGitCodeOwner}/{HlbmergeGitCodeRepo} has no .apk asset.");
        }

        var current = await PrimaryDownloadAsync(app, ct);
        if (current is not null && asset.Url == current.ApkUrl && current.VersionCode is not null
            && !NeedsPermissionHeal(app, current))
        {
            app.EnrichEtag = release.Etag;
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null);
        }

        return await EnrichFromApkAsync(app, asset.Url, release.Etag, SourceKind.Other, now, ct);
    }

    private async Task<EnrichResult> EnrichFromGitLabAsync(
        App app, string projectPath, DateTimeOffset now, CancellationToken ct)
    {
        ApplyGitLabAuthor(app, projectPath);

        GitLabRelease? latest;
        try
        {
            latest = await gitlab.GetLatestReleaseAsync(projectPath, app.EnrichEtag, ct);
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

        GitLabRelease release;
        try
        {
            await RefreshGitLabStatsAsync(app, projectPath, ct);

            // Raw markdown, not rendered HTML: the client renders markdown.
            if (NeedsReadmeRefresh(app.FullDescription)
                && await gitlab.GetReadmeMarkdownAsync(projectPath, ct) is { Length: > 0 } readme)
            {
                app.FullDescription = readme.Length > MaxFullDescriptionChars
                    ? readme[..MaxFullDescriptionChars]
                    : readme;
            }

            if (latest is null)
            {
                app.LastCheckedAt = now;
                return new EnrichResult(EnrichOutcome.UpToDate, null);
            }

            release = latest;
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

        var link = ApkAssetSelector.PickApk(release.Assets, l => l.Name, _ => 0L, l => l.Url);
        if (link is null)
        {
            if (await TryFdroidFallbackAsync(app, now, ct) is { } rescued)
            {
                return rescued;
            }

            if (await TryPlayRedirectFallbackAsync(app, now, ct) is { } play)
            {
                return play;
            }

            return Fail(app, now, $"GitLab release {release.TagName} of {projectPath} has no .apk asset link.");
        }

        // GitLab largely ignores If-None-Match: same recorded asset URL means nothing new.
        var gitlabCurrent = await PrimaryDownloadAsync(app, ct);
        if (gitlabCurrent is not null && link.Url == gitlabCurrent.ApkUrl && gitlabCurrent.VersionCode is not null
            && !NeedsPermissionHeal(app, gitlabCurrent))
        {
            app.EnrichEtag = release.Etag;
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null);
        }

        return await EnrichFromApkAsync(app, link.Url, release.Etag, SourceKind.GitLab, now, ct, release.ReleasedAt);
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
    /// Pre-fix rows recorded a fully analyzed build but never persisted its
    /// permissions (the F-Droid path never wrote them); the same-asset
    /// short-circuits would keep them blank forever, so re-analyze once. A
    /// SHA-256 identity proves a full analysis ran before: index-only rows
    /// (MD5 at most) never had one and stay up-to-date. A genuinely
    /// permission-less build re-verifies each pass; such builds are all but
    /// nonexistent.
    /// </summary>
    private static bool NeedsPermissionHeal(App app, AppDownload? primary) =>
        app.Permissions is not { Count: > 0 } && primary?.SigSha256 is not null;

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
        int? MinSdk);

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
        var rows = await db.Downloads.Where(d => d.AppId == app.Id).ToListAsync(ct);
        foreach (var local in db.Downloads.Local)
        {
            if (local.AppId == app.Id && !rows.Contains(local))
            {
                rows.Add(local);
            }
        }

        return rows;
    }

    private async Task<AppDownload?> PrimaryDownloadAsync(App app, CancellationToken ct) =>
        (await LoadDownloadsAsync(app, ct)).FirstOrDefault(d => d.IsPrimary);

    private async Task<bool> HasDownloadsAsync(App app, CancellationToken ct) =>
        await db.Downloads.AnyAsync(d => d.AppId == app.Id, ct);

    /// <summary>
    /// Upserts the row for the candidate's signing identity. Newer versions
    /// replace older ones; on a version tie the forge source's URL wins.
    /// An index-only row (MD5 fingerprint) is upgraded in place when the
    /// analyzed build reveals the SHA-256 identity.
    /// </summary>
    private async Task UpsertDownloadAsync(App app, DownloadCandidate candidate, DateTimeOffset now, CancellationToken ct)
    {
        var sigKey = ComputeSigKey(candidate.SigSha256, candidate.SigMd5, candidate.ApkUrl);
        var md5 = FirstFingerprint(candidate.SigMd5);
        var rows = await LoadDownloadsAsync(app, ct);
        var row = rows.FirstOrDefault(d => d.SigKey == sigKey)
            ?? (md5 is null ? null : rows.FirstOrDefault(d => FirstFingerprint(d.SigMd5) == md5));
        if (row is null)
        {
            row = new AppDownload { AppId = app.Id, SigKey = sigKey, ApkUrl = candidate.ApkUrl };
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
        }

        row.Source = candidate.Source;
        row.SourceRef = candidate.SourceRef;
        row.ApkUrl = candidate.ApkUrl;
        row.ArchiveEntry = candidate.ArchiveEntry;
        row.VersionCode = candidate.VersionCode;
        row.VersionName = candidate.VersionName;
        row.SizeBytes = candidate.SizeBytes;
        row.Sha256 = candidate.Sha256;
        row.SigSha256 = candidate.SigSha256;
        row.SigMd5 = candidate.SigMd5;
        row.MinSdk = candidate.MinSdk;
        row.ResolvedAt = now;
    }

    /// <summary>
    /// Default candidate for fresh installs: forge sources beat F-Droid/Izzy,
    /// then the newest version, then a fixed source order. Exactly one row is
    /// primary per app (invariant enforced here, not by a DB constraint).
    /// </summary>
    private async Task RecomputePrimaryAsync(App app, CancellationToken ct)
    {
        var rows = await LoadDownloadsAsync(app, ct);
        AppDownload? best = null;
        foreach (var row in rows)
        {
            best = best is null ? row : PreferDownload(row, best);
        }

        foreach (var row in rows)
        {
            row.IsPrimary = ReferenceEquals(row, best);
        }
    }

    private static AppDownload PreferDownload(AppDownload a, AppDownload b)
    {
        var aForge = IsForgeSource(a.Source);
        var bForge = IsForgeSource(b.Source);
        if (aForge != bForge)
        {
            return aForge ? a : b;
        }

        var av = a.VersionCode ?? -1;
        var bv = b.VersionCode ?? -1;
        if (av != bv)
        {
            return av > bv ? a : b;
        }

        return SourceOrder(a.Source) <= SourceOrder(b.Source) ? a : b;
    }

    private async Task RemoveDownloadAsync(App app, SourceKind source, CancellationToken ct)
    {
        var rows = await LoadDownloadsAsync(app, ct);
        foreach (var row in rows.Where(r => r.Source == source))
        {
            db.Downloads.Remove(row);
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
        (FdroidPackageInfo? Package, string? IndexEtag)? fetched;
        try
        {
            fetched = await fdroid.GetPackageAsync(repoBase, packageId, app.EnrichEtag, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or InvalidDataException)
        {
            return Fail(app, now, $"F-Droid: {ex.Message}");
        }

        if (fetched is null)
        {
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null);
        }

        var (package, indexEtag) = fetched.Value;
        if (package is null)
        {
            return Fail(app, now, $"F-Droid: package {packageId} not in the {repoBase} index.");
        }

        var kind = repoBase == FdroidRepos.IzzyBase ? SourceKind.Izzy : SourceKind.FDroid;
        app.SourceKind = kind;

        // f-droid.org publishes no download counts; only the Izzy repo does.
        if (kind == SourceKind.Izzy)
        {
            await ApplyIzzyDownloadsAsync(app, package.PackageName, ct);
        }

        var apkUrl = $"{repoBase.TrimEnd('/')}/{package.ApkName}";
        var current = await PrimaryDownloadAsync(app, ct);
        if (current is not null
            && current.Source == kind
            && current.ApkUrl == apkUrl
            && current.VersionCode == package.VersionCode
            && current.Sha256 is not null
            && !NeedsPermissionHeal(app, current))
        {
            app.EnrichEtag = indexEtag;
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null);
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
        var sigSha256 = analyzed is null ? null : CertFingerprint.Join(analyzed.Signers.Select(s => s.Sha256));
        var sigMd5 = analyzed is null
            ? package.SigMd5
            : CertFingerprint.Join(analyzed.Signers.Select(s => s.Md5)) ?? package.SigMd5;

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
            minSdk), now, ct);
        await RecomputePrimaryAsync(app, ct);

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

        await AddVersionRowAsync(app, versionCode, versionName, apkUrl, now, ct);

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
        try
        {
            var fetched = await fdroid.GetPackageAsync(FdroidRepos.FDroidBase, app.PackageName, seedEtag: null, ct);
            if (fetched is null)
            {
                return; // 304 with nothing cached: no new information.
            }

            package = fetched.Value.Package;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // Best-effort candidate: index trouble never fails the primary.
        }

        if (package is null)
        {
            await RemoveDownloadAsync(app, SourceKind.FDroid, ct);
            await RecomputePrimaryAsync(app, ct);
            return;
        }

        var apkUrl = $"{FdroidRepos.FDroidBase.TrimEnd('/')}/{package.ApkName}";
        var existing = (await LoadDownloadsAsync(app, ct))
            .FirstOrDefault(d => d.Source == SourceKind.FDroid
                && d.ApkUrl == apkUrl
                && d.VersionCode == package.VersionCode
                && d.Sha256 is not null);
        if (existing is not null)
        {
            if (package.SigMd5 is not null)
            {
                existing.SigMd5 = package.SigMd5;
                existing.ResolvedAt = now;
            }

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
                CertFingerprint.Join(analyzed.Signers.Select(s => s.Sha256)),
                CertFingerprint.Join(analyzed.Signers.Select(s => s.Md5)) ?? package.SigMd5,
                analyzed.Badging.MinSdk)
            : new DownloadCandidate(
                SourceKind.FDroid,
                package.PackageName,
                apkUrl,
                null,
                package.VersionCode,
                package.VersionName,
                package.Size,
                package.Sha256,
                null,
                package.SigMd5,
                package.MinSdk);
        await UpsertDownloadAsync(app, candidate, now, ct);
        await RecomputePrimaryAsync(app, ct);
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
                var release = await github.GetLatestReleaseAsync(owner, repo, null, ct);
                if (release is null)
                {
                    return;
                }

                var asset = ApkAssetSelector.PickApk(release.Assets);
                if (asset is not null)
                {
                    await RunArtifactAsync(app, asset.BrowserDownloadUrl, null, release.Etag, SourceKind.GitHub, now, ct, asPrimary: false);
                }
                else if (ApkAssetSelector.PickZip(release.Assets, a => a.Name, a => a.Size) is { } zip)
                {
                    await TryEnrichFromZipAsync(app, zip.BrowserDownloadUrl, release.Etag, SourceKind.GitHub, now, ct, asPrimary: false);
                }

                return;
            }

            if (SourceClassifier.TryParseGitLabRepo(sourceUrl, out var projectPath))
            {
                var release = await gitlab.GetLatestReleaseAsync(projectPath, null, ct);
                if (release is null)
                {
                    return;
                }

                var link = ApkAssetSelector.PickApk(release.Assets, l => l.Name, _ => 0L, l => l.Url);
                if (link is not null)
                {
                    await RunArtifactAsync(app, link.Url, null, release.Etag, SourceKind.GitLab, now, ct, asPrimary: false);
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
        IReadOnlyList<SignerCertificates> Signers) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(ApkPath); } catch { /* best effort */ }
        }
    }

    private Task<EnrichResult> EnrichFromApkAsync(
        App app, string apkUrl, string? etag, SourceKind lockSource, DateTimeOffset now, CancellationToken ct,
        DateTimeOffset? releasedAt = null) =>
        RunArtifactAsync(app, apkUrl, archiveEntry: null, etag, lockSource, now, ct, releasedAt: releasedAt);

    /// <summary>
    /// Some releases attach only a zip with the APK inside (e.g.
    /// AppControl-X v3.0.0). Downloads the archive, picks the best
    /// <c>.apk</c> entry and analyzes it like a direct download; the
    /// recorded URL/size/hash stay the archive's and
    /// <see cref="App.ApkArchiveEntry"/> names the APK inside. Null when
    /// the archive is unusable or carries no APK (caller falls through
    /// to the next source).
    /// </summary>
    private async Task<EnrichResult?> TryEnrichFromZipAsync(
        App app, string zipUrl, string? etag, SourceKind lockSource, DateTimeOffset now, CancellationToken ct,
        bool asPrimary = true, DateTimeOffset? releasedAt = null)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.zip");
        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        try
        {
            await DownloadAsync(zipUrl, tempZip, ct);
            using var zip = ZipFile.OpenRead(tempZip);
            var entry = ApkAssetSelector.PickApk(zip.Entries, e => e.FullName, e => e.Length);
            if (entry is null)
            {
                return null;
            }

            entry.ExtractToFile(tempApk, overwrite: true);
            return await AnalyzeTempApkAsync(app, zipUrl, entry.FullName, tempZip, tempApk, etag, lockSource, now, ct, asPrimary, releasedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a broken or APK-less candidate zip must not stop
            // the F-Droid fallback (the caller reports no .apk asset if all fail).
            return null;
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best effort */ }
            try { File.Delete(tempApk); } catch { /* best effort */ }
        }
    }

    private async Task<EnrichResult> RunArtifactAsync(
        App app, string url, string? archiveEntry, string? etag, SourceKind lockSource, DateTimeOffset now, CancellationToken ct,
        bool asPrimary = true, DateTimeOffset? releasedAt = null)
    {
        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        try
        {
            await DownloadAsync(url, tempApk, ct);
            return await AnalyzeTempApkAsync(app, url, archiveEntry, tempApk, tempApk, etag, lockSource, now, ct, asPrimary, releasedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Candidate-only resolutions must not mark the app failed.
            var message = $"Download: {ex.Message}";
            return asPrimary
                ? Fail(app, now, message)
                : new EnrichResult(EnrichOutcome.Failed, message);
        }
        finally
        {
            try { File.Delete(tempApk); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Shared analysis tail: badging, artifact hash/size, icon, signatures
    /// and the recorded fields. <paramref name="apkPath"/> is the APK that
    /// gets analyzed, <paramref name="artifactPath"/> the file clients
    /// download (same file unless the APK came out of an archive). The build
    /// is always upserted as a signature-keyed download candidate; app-level
    /// presentation is only written when <paramref name="asPrimary"/> is set.
    /// </summary>
    private async Task<EnrichResult> AnalyzeTempApkAsync(
        App app, string artifactUrl, string? archiveEntry, string artifactPath, string apkPath,
        string? etag, SourceKind lockSource, DateTimeOffset now, CancellationToken ct, bool asPrimary = true,
        DateTimeOffset? releasedAt = null)
    {
        BadgingInfo badging;
        try
        {
            badging = BadgingParser.Parse(await aapt2.DumpBadgingAsync(apkPath, ct));
        }
        catch (Exception ex) when (ex is Aapt2Exception or BadgingParseException)
        {
            var message = $"aapt2: {ex.Message}";
            return asPrimary ? Fail(app, now, message) : new EnrichResult(EnrichOutcome.Failed, message);
        }

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
            var message = $"APK unreadable: {ex.Message}";
            return asPrimary ? Fail(app, now, message) : new EnrichResult(EnrichOutcome.Failed, message);
        }

        var signers = await TryExtractSignersAsync(apkPath, ct);
        var sigSha256 = CertFingerprint.Join(signers.Select(s => s.Sha256));
        var sigMd5 = CertFingerprint.Join(signers.Select(s => s.Md5));
        await UpsertDownloadAsync(app, new DownloadCandidate(
            lockSource, null, artifactUrl, archiveEntry, badging.VersionCode, badging.VersionName,
            artifactSize, artifactSha256, sigSha256, sigMd5, badging.MinSdk), now, ct);
        await RecomputePrimaryAsync(app, ct);

        // A completed analysis stamps the check even when it changes nothing
        // user-visible: without this, an app whose analyzed build never
        // becomes primary stays due forever and is re-downloaded every pass
        // (seen live: two rows pinned at null LastCheckedAt that the new
        // never_checked report surfaced). The ETag is deliberately left
        // alone here; it belongs to the primary source's conditional requests.
        if (!asPrimary)
        {
            app.LastCheckedAt = now;
            app.LastError = null;
            return new EnrichResult(EnrichOutcome.Enriched, null);
        }

        // An older analysis must not overwrite a newer primary's presentation.
        var primary = await PrimaryDownloadAsync(app, ct);
        if (primary is null
            || primary.ApkUrl != artifactUrl
            || primary.SigKey != ComputeSigKey(sigSha256, sigMd5, artifactUrl))
        {
            app.LastCheckedAt = now;
            app.LastError = null;
            return new EnrichResult(EnrichOutcome.UpToDate, null);
        }

        var icon = await launcherIcons.ResolveAsync(apkPath, badging, ct)
            ?? LetterAvatarGenerator.Generate(app.Name);
        await WriteIconFileAsync(icon, ct);

        var oldIcon = app.IconHash;
        app.PackageName = badging.PackageName;
        app.Permissions = badging.Permissions.ToList();
        app.IconHash = icon.Sha256;
        app.IconAdaptive = icon.Adaptive;
        app.Availability = Availability.DirectApk;
        app.EnrichEtag = etag;
        app.ExcludedReason = null;
        app.LastCheckedAt = now;
        app.LastError = null;

        await AddVersionRowAsync(app, badging.VersionCode, badging.VersionName, artifactUrl, now, ct);

        // Only a real release date moves the app up "recently updated"; metadata
        // refreshes never touch this, and sources without dates stay unknown.
        if (releasedAt is not null &&
            (app.VersionUpdatedAt is null || releasedAt > app.VersionUpdatedAt))
        {
            app.VersionUpdatedAt = releasedAt;
        }

        await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
        return new EnrichResult(EnrichOutcome.Enriched, null);
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
                    var storeAvatar = LetterAvatarGenerator.Generate(app.Name);
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
                    ?? LetterAvatarGenerator.Generate(app.Name);
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
            var avatar = LetterAvatarGenerator.Generate(app.Name);
            await WriteIconFileAsync(avatar, ct);
            app.IconHash = avatar.Sha256;
            app.IconAdaptive = false;
        }

        if (kind == SourceKind.Play && !HasAltSource(app) && !app.ExcludeOverride)
        {
            app.Availability = Availability.Excluded;
            app.ExcludedReason = "Play Store is the only source; no APK available, no source code link";
            app.LastCheckedAt = now;
            app.LastError = null;
            return new EnrichResult(EnrichOutcome.Excluded, null);
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

    private static bool HasAltSource(App app) =>
        !string.IsNullOrWhiteSpace(app.SourceUrl)
        && SourceClassifier.Classify(app.SourceUrl) != SourceKind.Play;

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
        App app, long? versionCode, string? versionName, string apkUrl, DateTimeOffset now, CancellationToken ct)
    {
        // Identity is (app, code), mirroring IX_app_versions_app_id_version_code:
        // upstreams re-tag the same build (vFlow's v1.5.3-pr1 reuses 1.5.2's
        // code), and matching on the name too inserted a duplicate row whose
        // unique violation rolled back the whole enrich (stars, permissions).
        if (!await db.AppVersions.AnyAsync(v =>
            v.AppId == app.Id && v.VersionCode == versionCode, ct))
        {
            app.Versions.Add(new AppVersion
            {
                VersionCode = versionCode,
                VersionName = versionName,
                ApkUrl = apkUrl,
                DetectedAt = now,
            });
        }
    }

    private async Task DownloadAsync(string url, string tempApk, CancellationToken ct)
    {
        using var response = await downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}.");
        }

        await using var file = File.Create(tempApk);
        await response.Content.CopyToAsync(file, ct);
    }

    /// <summary>
    /// Download → aapt2 → file hash, with best-effort signer certs. Returns
    /// null when the file can't be fetched or parsed (callers fall back to
    /// index metadata); never throws except on cancellation.
    /// </summary>
    private async Task<AnalyzedApk?> TryAnalyzeDownloadAsync(string apkUrl, CancellationToken ct)
    {
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
                await TryExtractSignersAsync(tempApk, ct));
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
    /// Best-effort signer fingerprints (empty when apksigner is missing, Java
    /// is absent, or the APK is unsigned). Never fails enrichment.
    /// </summary>
    private async Task<IReadOnlyList<SignerCertificates>> TryExtractSignersAsync(string apkPath, CancellationToken ct)
    {
        try
        {
            return ApkSignerParser.Parse(await signer.PrintCertsAsync(apkPath, ct));
        }
        catch (Exception ex) when (ex is ApkSignerException or ApkSignerParseException)
        {
            return [];
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
