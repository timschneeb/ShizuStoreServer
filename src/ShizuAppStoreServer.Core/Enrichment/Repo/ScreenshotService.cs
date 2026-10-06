using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Overrides;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment.Repo;

internal sealed class ScreenshotService(
    FdroidIndexProvider fdroid,
    IRepoScreenshotResolver? repoScreenshots,
    EnrichmentOptions options,
    ShizuDbContext db,
    ILogger? log)
{
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
    internal async Task<EnrichResult> RefreshScreenshotsAsync(
        App app, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = app.Screenshots.ToArray();
        await ApplyScreenshotsAsync(app, now, ct, force: true);
        return app.Screenshots.SequenceEqual(before)
            ? new EnrichResult(EnrichOutcome.UpToDate, null)
            : new EnrichResult(EnrichOutcome.Enriched, null);
    }

    internal async Task ApplyScreenshotsAsync(App app, DateTimeOffset now, CancellationToken ct, bool force = false)
    {
        try
        {
            var blocked = await ScreenshotBlocklist.LoadAsync(db, ct);

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
                SetScreenshots(app, indexUrls, blocked);
                return;
            }

            // Only repo-sourced URLs can be stored now. They stay until the
            // recheck window elapses; a forced refresh re-runs the lookup every
            // time. The repository itself decides their fate: a listed tree
            // replaces or clears them, a failed clone keeps them.
            var storedRepo = app.Screenshots.Where(url => !IsFdroidSourced(url)).ToList();
            if (!ShouldTryRepoScreenshots(app, now, force))
            {
                SetScreenshots(app, storedRepo, blocked);
                return;
            }

            app.ScreenshotsCheckedAt = now;
            var resolved = await repoScreenshots!.ResolveAsync(app.Url, app.SourceUrl, ct);
            SetScreenshots(app, resolved.Reached ? resolved.Urls : storedRepo, blocked);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Screenshot lookup failed for {Slug}.", app.Slug);
        }
    }

    /// <summary>Stores a fresh list, blocklist-filtered and capped; identical content stays untouched.</summary>
    private static void SetScreenshots(App app, IReadOnlyList<string> urls, IReadOnlySet<string> blocked)
    {
        var filtered = ScreenshotBlocklist.Filter(urls, blocked);
        var capped = filtered.Count > MaxScreenshots ? filtered.Take(MaxScreenshots).ToList() : filtered;
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
}
