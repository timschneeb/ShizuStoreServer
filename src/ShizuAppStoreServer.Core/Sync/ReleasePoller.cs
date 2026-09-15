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
    // SmartspacerPlugins spreads its apps across many releases, so one
    // latest-release compare cannot see them; it stays on the due window.
    private const string InstafelListOwner = "mamiiblt";
    private const string InstafelListRepo = "instafel";
    private const string InstafelUpdaterOwner = "instafel";
    private const string InstafelUpdaterRepo = "u-rel";
    private const string HlbmergeOwner = "molihuan";
    private const string HlbmergeRepo = "hlbmerge_flutter";
    private const string SmartspacerOwner = "KieronQuinn";
    private const string SmartspacerRepo = "SmartspacerPlugins";

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
            .Where(a => a.Availability != Availability.Excluded && a.RootAppId == null)
            .Select(a => new Candidate(a.Id, a.Url, a.SourceUrl, a.EnrichEtag))
            .ToListAsync(ct);
        if (apps.Count == 0)
        {
            return new HashSet<long>();
        }

        var downloads = await db.Downloads.AsNoTracking().ToListAsync(ct);
        var byApp = downloads.GroupBy(d => d.AppId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Extra packages of a multi-app repo are polled through their root:
        // every latest-release asset must already be recorded somewhere in the
        // group, so a new app or a new build flips the root as changed.
        var variantsByRoot = (await db.Apps.AsNoTracking()
                .Where(a => a.RootAppId != null)
                .Select(a => new { a.Id, RootId = a.RootAppId!.Value })
                .ToListAsync(ct))
            .GroupBy(v => v.RootId)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Id).ToList());

        var groupDownloads = new Dictionary<long, List<AppDownload>>();
        foreach (var app in apps)
        {
            var rows = byApp.TryGetValue(app.Id, out var rootRows)
                ? new List<AppDownload>(rootRows)
                : [];
            if (variantsByRoot.TryGetValue(app.Id, out var variantIds))
            {
                foreach (var variantId in variantIds)
                {
                    if (byApp.TryGetValue(variantId, out var variantRows))
                    {
                        rows.AddRange(variantRows);
                    }
                }
            }

            groupDownloads[app.Id] = rows;
        }

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
            forge,
            (t, c) => PollForgeAsync(t, groupDownloads, variantsByRoot.ContainsKey(t.App.Id), c),
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

            // SmartspacerPlugins publishes one app per release; a single
            // latest-release compare would keep flagging the other apps.
            if (owner == SmartspacerOwner && repo == SmartspacerRepo)
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
        ForgeTarget target,
        Dictionary<long, List<AppDownload>> groupDownloads,
        bool hasVariants,
        CancellationToken ct)
    {
        try
        {
            SourceRelease? release;
            if (target.IsGitHub)
            {
                release = await github.GetLatestReleaseAsync(
                    new SourceTarget(SourceKind.GitHub, $"{target.OwnerOrProject}/{target.Repo}"),
                    target.App.Etag, ct);
            }
            else
            {
                release = await gitlab.GetLatestReleaseAsync(
                    new SourceTarget(SourceKind.GitLab, target.OwnerOrProject), target.App.Etag, ct);
            }

            if (release is null)
            {
                return false;
            }

            var rows = groupDownloads.TryGetValue(target.App.Id, out var group) ? group : [];

            // A multi-app repo ships several packages per release: the poll is
            // stale as soon as any APK asset is not recorded on the group.
            if (hasVariants)
            {
                var latest = release.Assets
                    .Where(IsApkAsset)
                    .Select(a => a.Url)
                    .ToHashSet(StringComparer.Ordinal);
                if (latest.Count == 0)
                {
                    return false;
                }

                var recorded = rows.Select(d => d.ApkUrl).ToHashSet(StringComparer.Ordinal);
                return !latest.IsSubsetOf(recorded);
            }

            var latestUrl = ApkAssetSelector.PickApk(release.Assets)?.Url
                ?? ApkAssetSelector.PickZip(release.Assets)?.Url;

            // No installable asset (or a feed we cannot map): due-only covers it.
            if (latestUrl is null)
            {
                return false;
            }

            var primary = rows.FirstOrDefault(d => d.IsPrimary);
            return primary is null || primary.ApkUrl != latestUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Release poll failed for app {AppId}.", target.App.Id);
            return false;
        }
    }

    private static bool IsApkAsset(SourceAsset asset) =>
        asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
        || asset.Url.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

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
