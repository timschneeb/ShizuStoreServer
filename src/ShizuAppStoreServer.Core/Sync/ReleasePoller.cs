using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>
/// Fast-path release check: cheap metadata-only signal (no APK download,
/// no aapt2, no DB writes) for whether a fast-loop pass should enrich an
/// app that is still inside its re-check window.
/// </summary>
public interface IReleasePoller
{
    /// <returns>Ids of non-excluded apps whose upstream release looks newer
    /// than the recorded primary download. Empty when the poll is disabled,
    /// unauthenticated, or everything is unchanged. Never throws on
    /// upstream trouble (fail-open to due-only enrichment), except on
    /// cancellation or an unwritable DB.</returns>
    Task<IReadOnlySet<long>> FindChangedAsync(CancellationToken ct = default);
}

/// <summary>
/// Cheap per-source new-release signal, read-only (the F-Droid index
/// provider cache is the only state touched). GitHub compares the picked
/// APK/zip asset URL against the primary download (the stored ETag rides
/// along, so unchanged feeds answer 304); GitLab does the same URL
/// compare (its API largely ignores ETags); F-Droid/Izzy fetch each repo
/// index once and compare version codes in memory. Play, link-only,
/// Codeberg and the GitCode special case have no cheap signal and stay
/// on the due window. Poll failures are soft (unchanged), so a
/// flapping upstream never marks rows failed.
/// </summary>
public sealed class ReleasePoller(
    ShizuDbContext db,
    IGitHubReleaseClient github,
    IGitLabReleaseClient gitlab,
    FdroidIndexProvider fdroid,
    EnrichmentOptions enrichment,
    SyncOptions sync,
    ILogger<ReleasePoller>? log = null) : IReleasePoller
{
    // Same special cases as AppEnricher: instafel's list URL points at the
    // source monorepo while the updater releases live in u-rel, so poll that
    // feed; hlbmerge rebuilds only on GitCode, so skip the GitHub poll.
    private const string InstafelListOwner = "mamiiblt";
    private const string InstafelListRepo = "instafel";
    private const string InstafelUpdaterOwner = "instafel";
    private const string InstafelUpdaterRepo = "u-rel";
    private const string HlbmergeOwner = "molihuan";
    private const string HlbmergeRepo = "hlbmerge_flutter";

    private sealed record Candidate(long Id, string Url, string? SourceUrl, string? Etag);
    private sealed record ForgeTarget(Candidate App, string OwnerOrProject, string Repo, bool IsGitHub);
    private sealed record FdroidTarget(Candidate App, string RepoBase, string PackageId, SourceKind Kind);

    public async Task<IReadOnlySet<long>> FindChangedAsync(CancellationToken ct = default)
    {
        // The poll is one release-feed call per app per pass; anonymous
        // GitHub allows 60/hr, so without a PAT the poll stays off and
        // fast passes enrich due-only apps (Program.cs warns at startup).
        if (!sync.PollEnabled || string.IsNullOrEmpty(enrichment.GitHubToken))
        {
            return new HashSet<long>();
        }

        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded)
            .Select(a => new Candidate(a.Id, a.Url, a.SourceUrl, a.EnrichEtag))
            .ToListAsync(ct);
        if (apps.Count == 0)
        {
            return new HashSet<long>();
        }

        var downloads = await db.Downloads.AsNoTracking().ToListAsync(ct);
        var byApp = downloads.GroupBy(d => d.AppId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var forge = new List<ForgeTarget>();
        var fdroid = new Dictionary<string, List<FdroidTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (TryForgeTarget(app) is { } target)
            {
                forge.Add(target);
            }
            else if (TryFdroid(app, out var repoBase, out var packageId, out var kind))
            {
                if (!fdroid.TryGetValue(repoBase, out var members))
                {
                    members = [];
                    fdroid[repoBase] = members;
                }

                members.Add(new FdroidTarget(app, repoBase, packageId, kind));
            }
        }

        var changed = new HashSet<long>();
        foreach (var (app, isChanged) in await BulkEnricher.EnrichManyAsync<ForgeTarget, bool>(
            forge, (t, c) => PollForgeAsync(t, byApp, c),
            Math.Max(1, sync.PollParallelism), _ => false, ct))
        {
            if (isChanged)
            {
                changed.Add(app.App.Id);
            }
        }

        foreach (var (repoBase, members) in fdroid)
        {
            await PollFdroidRepoAsync(repoBase, members, byApp, changed, ct);
        }

        return changed;
    }

    private static ForgeTarget? TryForgeTarget(Candidate app)
    {
        if (SourceClassifier.TryParseGitHubRepo(app.Url, out var owner, out var repo)
            || SourceClassifier.TryParseGitHubRepo(app.SourceUrl, out owner, out repo))
        {
            // hlbmerge rebuilds only on GitCode, so a GitHub poll would
            // compare the wrong feed.
            if (owner == HlbmergeOwner && repo == HlbmergeRepo)
            {
                return null;
            }

            // instafel's list URL points at the source monorepo; the updater
            // releases live in u-rel, so poll that feed instead.
            if (owner == InstafelListOwner && repo == InstafelListRepo)
            {
                owner = InstafelUpdaterOwner;
                repo = InstafelUpdaterRepo;
            }

            return new ForgeTarget(app, owner, repo, true);
        }

        if (SourceClassifier.TryParseGitLabRepo(app.Url, out var project)
            || SourceClassifier.TryParseGitLabRepo(app.SourceUrl, out project))
        {
            return new ForgeTarget(app, project, string.Empty, false);
        }

        return null;
    }

    private static bool TryFdroid(
        Candidate app, out string repoBase, out string packageId, out SourceKind kind)
    {
        string? parsedUrl = null;
        if (SourceClassifier.TryParseFdroidPackage(app.Url, out packageId))
        {
            parsedUrl = app.Url;
        }
        else if (SourceClassifier.TryParseFdroidPackage(app.SourceUrl, out packageId))
        {
            parsedUrl = app.SourceUrl;
        }

        if (parsedUrl is null)
        {
            repoBase = string.Empty;
            kind = SourceKind.Other;
            return false;
        }

        repoBase = FdroidRepos.BaseFor(SourceClassifier.Classify(parsedUrl));
        kind = repoBase == FdroidRepos.IzzyBase ? SourceKind.Izzy : SourceKind.FDroid;
        return true;
    }

    private async Task<bool> PollForgeAsync(
        ForgeTarget target, Dictionary<long, List<AppDownload>> byApp, CancellationToken ct)
    {
        try
        {
            string? latestUrl;
            if (target.IsGitHub)
            {
                var release = await github.GetLatestReleaseAsync(
                    target.OwnerOrProject, target.Repo, target.App.Etag, ct);
                if (release is null)
                {
                    return false;
                }

                latestUrl = ApkAssetSelector.PickApk(release.Assets)?.BrowserDownloadUrl
                    ?? ApkAssetSelector.PickZip(release.Assets, a => a.Name, a => a.Size)?.BrowserDownloadUrl;
            }
            else
            {
                var release = await gitlab.GetLatestReleaseAsync(target.OwnerOrProject, target.App.Etag, ct);
                if (release is null)
                {
                    return false;
                }

                latestUrl = ApkAssetSelector.PickApk(
                    release.Assets, l => l.Name, _ => 0L, l => l.Url)?.Url;
            }

            // No installable asset (or a feed we cannot map): due-only covers it.
            if (latestUrl is null)
            {
                return false;
            }

            var primary = byApp.TryGetValue(target.App.Id, out var rows)
                ? rows.FirstOrDefault(d => d.IsPrimary)
                : null;
            return primary is null || primary.ApkUrl != latestUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Release poll failed for app {AppId}.", target.App.Id);
            return false;
        }
    }

    private async Task PollFdroidRepoAsync(
        string repoBase,
        List<FdroidTarget> members,
        Dictionary<long, List<AppDownload>> byApp,
        HashSet<long> changed,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, FdroidPackageInfo>? index;
        try
        {
            // One conditional fetch per repo per pass; the provider cache
            // makes this a 304 once warm.
            index = await fdroid.GetAllPackagesAsync(repoBase, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Release poll: F-Droid index fetch failed for {Repo}.", repoBase);
            return;
        }

        if (index is null)
        {
            return;
        }

        foreach (var member in members)
        {
            if (!index.TryGetValue(member.PackageId, out var package))
            {
                continue;
            }

            var apkUrl = $"{repoBase.TrimEnd('/')}/{package.ApkName}";
            var row = byApp.TryGetValue(member.App.Id, out var rows)
                ? rows.FirstOrDefault(d => d.Source == member.Kind)
                : null;
            if (row is null || row.VersionCode != package.VersionCode || row.ApkUrl != apkUrl)
            {
                changed.Add(member.App.Id);
            }
        }
    }
}
