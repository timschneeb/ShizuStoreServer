using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Enrichment.Apk;
using ShizuAppStoreServer.Core.Enrichment.Icons;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.Overrides;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>Outcome of one sync pass (the <c>job_runs</c> row carries the subset that has columns).</summary>
public sealed record SyncPassResult(
    string Trigger,
    string? HeadCommit,
    int Added,
    int Updated,
    int Removed,
    int Enriched,
    int UpToDate,
    int Failed,
    int DrainedRequests,
    int ParseWarnings,
    int ArchivedChanged,
    int UnlistedChanged,
    bool Skipped,
    string? Error)
{
    /// <summary>
    /// Per-app failure messages (enrichment crashes with their causes).
    /// Intentionally not a column: the pass log (SyncPassRunner warnings)
    /// is their home; the row keeps counts only.
    /// </summary>
    public IReadOnlyList<string> FailedMessages { get; init; } = [];
}

/// <summary>
/// Outcome of an icon refresh. The caller owns the job run row: the admin
/// coordinator and the CLI open one, a full sync pass logs into its own run.
/// </summary>
public sealed record IconRefreshResult(
    int Checked,
    int Refreshed,
    int AlreadyCurrent,
    int Failed)
{
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Outcome of the screenshots-only admin refresh (the coordinator owns the job run row).</summary>
public sealed record ScreenshotRefreshResult(
    int Checked,
    int Updated,
    int Current,
    int Failed)
{
    public IReadOnlyList<string> Errors { get; init; } = [];
}
/// <summary>
/// One list-sync pass. Scoped: resolved fresh
/// per pass by the workers, sharing one <see cref="ShizuDbContext"/> with
/// the injected <see cref="CatalogUpserter"/>.
/// </summary>
/// <remarks>
/// Pass shape:
/// <list type="number">
/// <item>Drain unprocessed <c>sync_requests</c> (any rows → trigger becomes
/// <c>webhook</c>); they are marked processed only on success, so a failed
/// pass retries next loop.</item>
/// <item>Best-effort <c>git fetch</c> (offline/timeout → continue off local
/// clone state; enrichment failures will still surface).</item>
/// <item>HEAD unchanged + no requests + not a full re-check → enrich due
/// apps plus poll-changed apps (force), or record a skipped run when neither
/// has anything.</item>
/// <item>Otherwise parse README plus CLOSED_SOURCE, merge git history,
/// upsert, apply ARCHIVED.md exclusions, then enrich.</item>
/// </list>
/// Invariant: this scope makes no app mutations after the upserter saves, so
/// the final bookkeeping <c>SaveChanges</c> only writes request flags + the
/// issue snapshot (enrichment runs in per-app scopes via
/// <see cref="IEnrichmentRunner"/>); the job run row is owned by the job log
/// sink. Never throws except on <c>OperationCanceledException</c> or an
/// unwritable DB, failures become failed run rows.
/// </remarks>
public sealed class SyncService(
    ShizuDbContext db,
    CatalogUpserter upserter,
    GitHistoryService git,
    IEnrichmentRunner enrich,
    IReleasePoller poll,
    IPaparazziRenderer renderer,
    SyncOptions options,
    EnrichmentOptions enrichment,
    IJobLog? jobLog = null,
    ILogger<SyncService>? log = null,
    FdroidIndexProvider? fdroid = null,
    AppOverrideApplier? appOverrides = null)
{
    private readonly IJobLog _jobs = jobLog ?? NullJobLog.Instance;
    private readonly ILogger<SyncService>? _log = log;
    private readonly FdroidIndexProvider? _fdroid = fdroid;
    private readonly AppOverrideApplier? _appOverrides = appOverrides;

    /// <summary>Exclusion reason for apps listed in <c>pages/ARCHIVED.md</c>.</summary>
    public const string ArchivedReason = "Archived in the upstream list.";

    /// <summary>Exclusion reason for apps unlisted through <c>app_unlist_overrides</c>.</summary>
    public const string UnlistedReason = "Unlisted by the operator.";

    private const string ReadmePath = "README.md";
    private const string ClosedSourcePath = "pages/CLOSED_SOURCE.md";
    private const string ArchivedPath = "pages/ARCHIVED.md";

    public async Task<SyncPassResult> RunAsync(
        string trigger, bool fullRecheck, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(trigger, fullRecheck, now, ct);
        }
        // A request-level timeout surfaces as OperationCanceledException while
        // the passed token is still live; that is a failed pass, not a host
        // shutdown, and must not escape to stop the host.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Reached only when the pass crashed before opening a run session
            // (request drain, git fetch); record a failed run so the failure
            // stays visible next to the passes that got further.
            _log?.LogError(ex, "Sync pass failed before it opened a run session.");
            db.ChangeTracker.Clear();
            var error = $"Sync pass failed: {ex.Message}";
            await using (var session = _jobs.Begin(new JobStart(JobKind.Sync, ParseTrigger(trigger), now)))
            {
                await session.FinishAsync(
                    new JobFinish(JobStatus.Failed, Summary: "pass failed", Error: error),
                    CancellationToken.None);
            }

            return new SyncPassResult(trigger, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, error);
        }
    }

    private async Task<SyncPassResult> RunCoreAsync(
        string trigger, bool fullRecheck, DateTimeOffset now, CancellationToken ct)
    {
        // One index fetch per repo per pass: the provider memoizes the
        // revalidation, so hundreds of apps do not turn into hundreds of
        // conditional GETs against the same repo.
        _fdroid?.BeginRun();
        var pending = await db.SyncRequests
            .Where(r => !r.Processed)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        var pendingIds = pending.Select(r => r.Id).ToList();

        // A full-flagged admin request upgrades this pass: the row must not be
        // drained without the full-catalog re-check the operator asked for.
        fullRecheck |= pending.Exists(r => r.Full);

        // An icons:false request force-disables icon rendering for the whole
        // drained pass; with several pending rows any veto wins.
        var skipIcons = pending.Exists(r => !r.Icons);

        var effectiveTrigger = pending.Count > 0 ? "webhook" : trigger;
        var start = new JobStart(
            JobKind.Sync, ParseTrigger(effectiveTrigger), now, Metadata: new { fullRecheck, skipIcons });

        // Best-effort fetch with its own timeout: a stuck network must not
        // wedge the loop (the git process itself may linger; it exits alone).
        using (var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            fetchCts.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await git.FetchAsync(options.ListPath, fetchCts.Token);
            }
            catch (InvalidOperationException)
            {
                // No remote / offline, continue off local clone state.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Fetch timed out, same fallback.
            }
        }

        var head = await git.GetHeadCommitAsync(options.ListPath, ct);
        var lastHead = await db.JobRuns
            .Where(r => r.Kind == JobKind.Sync)
            .OrderByDescending(r => r.Id)
            .Select(r => r.Reference)
            .FirstOrDefaultAsync(ct);

        if (!fullRecheck && pending.Count == 0 && head is not null && head == lastHead)
        {
            var dueIds = await SelectDueAppIdsAsync(now, ct);
            var freshIds = (await PollChangedAsync(ct)).Except(dueIds).ToList();
            if (dueIds.Count == 0 && freshIds.Count == 0)
            {
                // Operator edits must materialize even when no app is due: the
                // override pickup latency is the fast-pass cadence itself.
                await ApplyOperatorOverridesAsync(now, ct);
                // Keep the observed head on the row: the next tick compares
                // against it, and /v1/meta and /v1/issues read it.
                _jobs.Skipped(start with { Reference = head }, "nothing due");
                return new SyncPassResult(
                    effectiveTrigger, head, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, null);
            }

            await using var dueSession = _jobs.Begin(start with { ItemCount = dueIds.Count + freshIds.Count });
            dueSession.Phase("enrich", $"enriching {dueIds.Count + freshIds.Count} apps",
                new { due = dueIds.Count, polled = freshIds.Count, fullRecheck });
            try
            {
                enrichment.SkipIconRenders = skipIcons;
                var (enriched, upToDate, failed, failedMessages) =
                    await EnrichWithFreshAsync(dueIds, false, freshIds, now, ct);
                await ApplyOperatorOverridesAsync(now, ct);
                await ApplyShizukuFilterAsync(ct);
                return await FinishRunAsync(dueSession, effectiveTrigger, head,
                    0, 0, 0, enriched, upToDate, failed,
                    [], [], false, 0, 0, now, ct, failedMessages);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return await FailRunAsync(dueSession, effectiveTrigger, ex);
            }
            finally
            {
                enrichment.SkipIconRenders = false;
            }
        }

        await using var session = _jobs.Begin(start);
        try
        {
            session.Phase("list", "parsing upstream list",
                new { head = ShortCommit(head), previous = ShortCommit(lastHead) });

            var readme = await File.ReadAllTextAsync(Path.Combine(options.ListPath, ReadmePath), ct);
            var parser = new AwesomeListParser();
            var mainDoc = parser.Parse(readme, "main");

            // The closed list is optional: a mirror without it serves main only.
            var closedSourcePath = Path.Combine(options.ListPath, ClosedSourcePath);
            var closedSource = File.Exists(closedSourcePath)
                ? await File.ReadAllTextAsync(closedSourcePath, ct)
                : string.Empty;
            var closedDoc = parser.Parse(closedSource, "closed-source");

            var history = await git.GetHistoryAsync(options.ListPath, ReadmePath, ct);
            if (File.Exists(closedSourcePath))
            {
                foreach (var (url, entry) in await git.GetHistoryAsync(options.ListPath, ClosedSourcePath, ct))
                {
                    // The main list is the primary source of list-change dates; a URL
                    // that appears in both lists keeps its main-list history.
                    history.TryAdd(url, entry);
                }
            }

            var counts = await upserter.UpsertAsync([mainDoc, closedDoc], history, now, ct);
            var (archivedChanged, archivedWarnings, archivedUrls) = await ApplyArchivedAsync(ct);
            var unlistedChanged = await ApplyUnlistedAsync(archivedUrls, ct);
            var parseWarnings = mainDoc.Warnings
                .Concat(closedDoc.Warnings)
                .Concat(archivedWarnings)
                .ToList();

            var ids = fullRecheck
                ? await db.Apps.AsNoTracking()
                    // Rows excluded by the Shizuku gate stay selected so a later
                    // APK that declares the permission auto-heals them.
                    .Where(a => (a.Availability != Availability.Excluded
                            || a.ExcludedReason == ShizukuPermission.Reason)
                        && a.RootAppId == null)
                    .Select(a => a.Id)
                    .ToListAsync(ct)
                : await SelectDueAppIdsAsync(now, ct);
            // The nightly force-enriches everything already; polling would only
            // re-list what the pass enriches anyway.
            var extraIds = fullRecheck ? [] : (await PollChangedAsync(ct)).Except(ids).ToList();
            session.SetItemCount(ids.Count + extraIds.Count);
            session.Phase("enrich", $"enriching {ids.Count + extraIds.Count} apps",
                new { asserted = ids.Count, polled = extraIds.Count, fullRecheck });

            var batchIcons = fullRecheck && enrichment.BatchIconsOnFullPass && !skipIcons;
            if (batchIcons)
            {
                // Full passes batch their icon renders after enrichment (see
                // RefreshIconsAsync); during enrichment only rasters resolve, so
                // no per-icon Gradle invocation can stall the pass.
                enrichment.DeferXmlIconRenders = true;
            }

            (int Enriched, int UpToDate, int Failed, List<string> FailedMessages) full;
            try
            {
                enrichment.SkipIconRenders = skipIcons;
                full = await EnrichWithFreshAsync(ids, fullRecheck, extraIds, now, ct);
            }
            finally
            {
                enrichment.SkipIconRenders = false;
                enrichment.DeferXmlIconRenders = false;
            }

            await ApplyOperatorOverridesAsync(now, ct);
            await ApplyShizukuFilterAsync(ct);

            if (batchIcons)
            {
                await BatchRenderIconsAsync(ct);
            }

            return await FinishRunAsync(session, effectiveTrigger, head,
                counts.Added, counts.Updated, counts.Removed,
                full.Enriched, full.UpToDate, full.Failed,
                pendingIds, parseWarnings, true, archivedChanged, unlistedChanged, now, ct, full.FailedMessages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return await FailRunAsync(session, effectiveTrigger, ex);
        }
    }

    /// <summary>Records a crashed pass on its run row and returns the error result.</summary>
    private async Task<SyncPassResult> FailRunAsync(JobSession session, string trigger, Exception ex)
    {
        // The message goes on the run row; the journal keeps the stack.
        _log?.LogError(ex, "Sync pass failed; recording the failed job run.");
        // The upserter may already have saved; discard any half-tracked state
        // so the failure bookkeeping below is the only thing written.
        db.ChangeTracker.Clear();
        var error = $"Sync pass failed: {ex.Message}";
        await session.FinishAsync(
            new JobFinish(JobStatus.Failed, Summary: "pass failed", Error: error),
            CancellationToken.None);
        return new SyncPassResult(trigger, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, error);
    }

    private static JobTrigger ParseTrigger(string trigger) => trigger.ToLowerInvariant() switch
    {
        "startup" => JobTrigger.Startup,
        "scheduled" => JobTrigger.Scheduled,
        "nightly" => JobTrigger.Nightly,
        "webhook" => JobTrigger.Webhook,
        "cli" => JobTrigger.Cli,
        "backfill" => JobTrigger.Backfill,
        "auto" => JobTrigger.Auto,
        _ => JobTrigger.Manual,
    };

    private static string? ShortCommit(string? commit) =>
        string.IsNullOrEmpty(commit) ? null : commit[..Math.Min(8, commit.Length)];

    /// <summary>IDs due for enrichment (never-checked, or outside the success/failure window).</summary>
    /// <remarks>
    /// Extra packages of a multi-app repo are enriched through their root's
    /// pass, never selected directly. Filtered client-side: the SQLite provider
    /// can't do <c>DateTimeOffset</c> arithmetic in SQL (see M5), and the
    /// catalog is tiny.
    /// </remarks>
    private async Task<List<long>> SelectDueAppIdsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Apps.AsNoTracking()
            .Where(a => a.RootAppId == null)
            .Select(a => new { a.Id, a.LastCheckedAt, a.LastError, a.ExcludedReason })
            .ToListAsync(ct);
        return rows
            // Operator unlists and override exclusions are hidden on purpose:
            // the recheck window would otherwise re-enrich and re-publish them.
            .Where(r => r.ExcludedReason != UnlistedReason
                && r.ExcludedReason != AppOverrideApplier.ExcludedReason)
            .Where(r => r.LastCheckedAt is null
                || r.LastCheckedAt + (r.LastError is null
                    ? enrichment.SuccessRecheckInterval
                    : enrichment.FailedRecheckInterval) <= now)
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// Materializes operator overrides after enrichment and before the Shizuku
    /// gate; a null applier (hand-built tests) disables the stage.
    /// </summary>
    private async Task ApplyOperatorOverridesAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_appOverrides is null)
        {
            return;
        }
        var changed = await _appOverrides.ApplyAsync(now, ct);
        if (changed > 0)
        {
            _log?.LogInformation("Materialized operator overrides on {Count} apps.", changed);
        }
    }

    /// <summary>Poll-changed ids (best-effort: any failure means no forced extras).</summary>
    private async Task<IReadOnlySet<long>> PollChangedAsync(CancellationToken ct)
    {
        try
        {
            return await poll.FindChangedAsync(ct);
        }
        // Best effort, so a request-level timeout (an OperationCanceledException
        // while the passed token is live) must not fail the whole pass.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HashSet<long>();
        }
    }

    /// <summary>
    /// Due selection plus poll-changed extras (always forced: the poll
    /// already proved they are stale).
    /// </summary>
    private async Task<(int Enriched, int UpToDate, int Failed, List<string> FailedMessages)> EnrichWithFreshAsync(
        List<long> ids, bool force, List<long> freshIds, DateTimeOffset now, CancellationToken ct)
    {
        var (enriched, upToDate, failed, failedMessages) = await EnrichSelectedAsync(ids, force, now, ct);
        if (freshIds.Count == 0)
        {
            return (enriched, upToDate, failed, failedMessages);
        }

        var (freshEnriched, freshUpToDate, freshFailed, freshMessages) =
            await EnrichSelectedAsync(freshIds, true, now, ct);
        failedMessages.AddRange(freshMessages);
        return (enriched + freshEnriched, upToDate + freshUpToDate, failed + freshFailed, failedMessages);
    }

    private async Task<(int Enriched, int UpToDate, int Failed, List<string> FailedMessages)> EnrichSelectedAsync(
        List<long> ids, bool force, DateTimeOffset now, CancellationToken ct)
    {
        var enriched = 0;
        var upToDate = 0;
        var failed = 0;
        var failedMessages = new List<string>();

        // Loaded before the batch so the job log can stream a line per app as
        // it finishes; a row deleted mid-pass falls back to its id.
        var meta = (await db.Apps.AsNoTracking()
                .Where(a => ids.Contains(a.Id))
                .Select(a => new { a.Id, a.Slug, a.Name, a.DisplayName })
                .ToListAsync(ct))
            .ToDictionary(a => a.Id);

        var session = JobContext.Current;
        var results = await BulkEnricher.EnrichManyAsync(
            ids, (id, c) => enrich.EnrichAsync(id, force, now, c), enrichment.MaxParallelism, ct,
            onResult: (id, r) =>
            {
                if (meta.TryGetValue(id, out var m))
                {
                    session.App(m.Slug, m.DisplayName ?? m.Name, r, id);
                }
                else
                {
                    session.App($"[{id}]", null, r, id);
                }
            });
        foreach (var (id, r) in results)
        {
            switch (r.Outcome)
            {
                case EnrichOutcome.Failed:
                    failed++;
                    if (r.Error is not null)
                    {
                        failedMessages.Add(meta.TryGetValue(id, out var m)
                            ? $"[{m.Slug}] {r.Error}"
                            : $"[{id}] {r.Error}");
                    }

                    break;
                case EnrichOutcome.UpToDate or EnrichOutcome.SkippedFresh:
                    upToDate++;
                    break;
                default: // Enriched, AvatarFallback
                    enriched++;
                    break;
            }
        }

        return (enriched, upToDate, failed, failedMessages);
    }

    /// <summary>
    /// <c>pages/ARCHIVED.md</c> is an exclusion list: entries found
    /// there are hidden from clients; entries that reappear upstream have the
    /// flag cleared and are forced through re-enrichment. Operator unlists
    /// (<see cref="UnlistedReason"/>) outrank it: those rows are left alone
    /// here and the URL set is returned so <see cref="ApplyUnlistedAsync"/>
    /// can hand a restored row back to the archived state.
    /// </summary>
    private async Task<(int Changed, List<ParseWarning> Warnings, HashSet<string> Urls)> ApplyArchivedAsync(CancellationToken ct)
    {
        var path = Path.Combine(options.ListPath, ArchivedPath);
        if (!File.Exists(path))
        {
            return (0, [], new HashSet<string>(StringComparer.Ordinal));
        }

        var archived = new AwesomeListParser().Parse(await File.ReadAllTextAsync(path, ct), "archived", allowUncategorized: true);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in archived.Categories)
        {
            foreach (var entry in category.Entries)
            {
                CollectUrls(entry, urls);
            }
        }

        // No early return on an empty set: an emptied ARCHIVED.md must still
        // clear previously archived rows below.

        var session = JobContext.Current;
        var changed = 0;
        var apps = await db.Apps.ToListAsync(ct);
        foreach (var app in apps)
        {
            var isArchived = urls.Contains(app.Url)
                || (app.SourceUrl is not null && urls.Contains(app.SourceUrl));
            if (isArchived && app.ExcludedReason != ArchivedReason && app.ExcludedReason != UnlistedReason)
            {
                app.Availability = Availability.Excluded;
                app.ExcludedReason = ArchivedReason;
                changed++;
                session.Decision($"archived: excluded '{app.Slug}'",
                    new { app.PackageName, app.Url }, appId: app.Id, slug: app.Slug);
            }
            else if (!isArchived && app.ExcludedReason == ArchivedReason)
            {
                app.ExcludedReason = null;
                app.LastCheckedAt = null; // force re-enrichment (re-classifies availability)
                app.LastError = null;
                changed++;
                session.Decision($"archived: restored '{app.Slug}'",
                    new { app.PackageName, app.Url }, appId: app.Id, slug: app.Slug);
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return (changed, archived.Warnings, urls);
    }

    /// <summary>
    /// <c>app_unlist_overrides</c> is an operator exclusion list: entries found
    /// there are hidden from clients while the upstream list still carries
    /// them; deleting the row restores the entry. The state is the same shape
    /// as the archived exclude (<see cref="Availability.Excluded"/> plus a
    /// reason), so every read path drops the row, but the operator reason
    /// keeps due selection from resurrecting it and outranks upstream reasons.
    /// </summary>
    private async Task<int> ApplyUnlistedAsync(ISet<string> archivedUrls, CancellationToken ct)
    {
        // The pass clock can be minutes stale by the time the row is stamped;
        // the tombstone must carry the commit time or a client that synced
        // mid-pass holds a cursor past removed_at and never sees the removal.
        var commitNow = DateTimeOffset.UtcNow;
        var slugs = (await db.AppUnlistOverrides.AsNoTracking()
                .Select(o => o.AppSlug)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var tombstones = await db.RemovedApps
            .ToDictionaryAsync(t => t.Slug, StringComparer.Ordinal);
        var apps = await db.Apps.ToListAsync(ct);

        // Variants are listed and served as their own rows, so unlisting a root
        // must hide its children too, or a companion build would stay available.
        var rootIds = apps
            .Where(a => slugs.Contains(a.Slug))
            .Select(a => a.Id)
            .ToHashSet();
        var unlistedIds = apps
            .Where(a => rootIds.Contains(a.Id)
                || (a.RootAppId is { } rootAppId && rootIds.Contains(rootAppId)))
            .Select(a => a.Id)
            .ToHashSet();

        var session = JobContext.Current;
        var changed = 0;
        var tombstonesChanged = false;
        foreach (var app in apps.Where(a => unlistedIds.Contains(a.Id)))
        {
            if (app.Availability != Availability.Excluded || app.ExcludedReason != UnlistedReason)
            {
                app.Availability = Availability.Excluded;
                app.ExcludedReason = UnlistedReason;
                app.UpdatedAt = commitNow;
                changed++;
                session.Decision($"unlisted: excluded '{app.Slug}'",
                    new { app.PackageName }, JobEventLevel.Info, appId: app.Id, slug: app.Slug);
            }

            // Same reasoning as the Shizuku gate: the delta feed drops excluded
            // rows, so the tombstone is the only signal that tells a client
            // which cached the app to delete it.
            if (!tombstones.ContainsKey(app.Slug))
            {
                var tombstone = new RemovedApp
                {
                    Slug = app.Slug,
                    Name = app.Name,
                    Listing = app.Listing,
                    RemovedAt = commitNow,
                };
                tombstones[app.Slug] = tombstone;
                db.RemovedApps.Add(tombstone);
                tombstonesChanged = true;
            }
        }

        // Deleted override rows restore the entry. A row still in ARCHIVED.md
        // hands the exclusion over to the archived reason instead of
        // resurfacing for one pass.
        foreach (var app in apps)
        {
            if (app.ExcludedReason != UnlistedReason || unlistedIds.Contains(app.Id))
            {
                continue;
            }

            if (archivedUrls.Contains(app.Url)
                || (app.SourceUrl is not null && archivedUrls.Contains(app.SourceUrl)))
            {
                app.ExcludedReason = ArchivedReason;
                changed++;
                session.Decision($"unlisted: handed back to archived '{app.Slug}'",
                    new { app.PackageName, app.Url }, appId: app.Id, slug: app.Slug);
                continue;
            }

            app.ExcludedReason = null;
            app.LastCheckedAt = null; // force re-enrichment (re-classifies availability)
            app.LastError = null;
            app.UpdatedAt = commitNow;
            if (tombstones.Remove(app.Slug, out var cleared))
            {
                db.RemovedApps.Remove(cleared);
                tombstonesChanged = true;
            }

            changed++;
            session.Decision($"unlisted: restored '{app.Slug}'",
                new { app.PackageName, app.Url }, appId: app.Id, slug: app.Slug);
        }

        if (changed > 0 || tombstonesChanged)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private static void CollectUrls(ParsedEntry entry, HashSet<string> urls)
    {
        urls.Add(entry.Url);
        if (entry.SourceUrl is not null)
        {
            urls.Add(entry.SourceUrl);
        }

        foreach (var child in entry.Children)
        {
            CollectUrls(child, urls);
        }
    }

    /// <summary>
    /// Direct-APK rows only stay available when the analyzed APK declares a
    /// Shizuku permission; a multi-app repo can otherwise contribute non-Shizuku
    /// APKs (plugins, companion builds) to a Shizuku store. Play/Link-only rows
    /// carry no analyzed permissions and are never touched. Operators can keep a
    /// package (<c>allow</c>) or silence its issue (<c>dontaudit</c>) through the
    /// <c>package_exceptions</c> table.
    /// </summary>
    /// <remarks>
    /// Runs after enrichment so it sees the fresh permission list, and before
    /// issue collection so the two stay consistent in one pass. Rows excluded
    /// here stay in the due/recheck selection (they carry this exact reason) so
    /// a later APK that declares the permission auto-heals them.
    ///
    /// Excluding a row also writes a <see cref="RemovedApp"/> tombstone, because
    /// the delta feed keys <c>/v1/changes removed[]</c> on that table and drops
    /// excluded rows from <c>added[]</c>/<c>updated[]</c>: without the tombstone
    /// a client that cached the app before the gate never learns to drop it.
    /// The write doubles as the backfill for rows excluded before tombstoning
    /// existed. Healing clears the tombstone and bumps <c>UpdatedAt</c> (an
    /// availability flip alone does not, see <see cref="ShizuDbContext"/>), so
    /// the app reaches clients again as an update instead of resurfacing via
    /// <c>added[]</c> with its original date.
    /// </remarks>
    private async Task<int> ApplyShizukuFilterAsync(CancellationToken ct)
    {
        // The gate runs after enrichment, so the pass-start `now` can be
        // minutes stale. Delta rows must carry the commit time or a client
        // that synced mid-pass holds a cursor past removed_at and never sees
        // the tombstone; only the commit time is a safe stamp.
        var commitNow = DateTimeOffset.UtcNow;
        var exceptions = await db.PackageExceptions.AsNoTracking()
            .ToDictionaryAsync(e => e.PackageName, e => e.Action, StringComparer.Ordinal);
        var tombstones = await db.RemovedApps
            .ToDictionaryAsync(t => t.Slug, StringComparer.Ordinal);
        var apps = await db.Apps
            .Where(a => a.PackageName != null
                && (a.Availability == Availability.DirectApk
                    || (a.Availability == Availability.Excluded
                        && a.ExcludedReason == ShizukuPermission.Reason)))
            .ToListAsync(ct);
        // A single recorded build declaring Shizuku is enough: the forge
        // primary can predate the app's Shizuku support while another
        // source's build already uses it.
        var shizukuApps = (await db.Downloads.AsNoTracking()
            .Where(d => d.ShizukuDeclared)
            .Select(d => d.AppId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        var session = JobContext.Current;
        var changed = 0;
        var tombstonesChanged = false;
        foreach (var app in apps)
        {
            var allowed = ShizukuPermission.IsDeclared(app.Permissions)
                || shizukuApps.Contains(app.Id)
                || app.ExcludeOverride
                || (exceptions.TryGetValue(app.PackageName!, out var action)
                    && action == PackageExceptionAction.Allow);
            if (allowed && app.ExcludedReason == ShizukuPermission.Reason)
            {
                app.Availability = Availability.DirectApk;
                app.ExcludedReason = null;
                app.UpdatedAt = commitNow;
                if (tombstones.Remove(app.Slug, out var cleared))
                {
                    db.RemovedApps.Remove(cleared);
                    tombstonesChanged = true;
                }

                changed++;
                session.Decision($"shizuku gate: healed '{app.Slug}'",
                    new { package = app.PackageName }, JobEventLevel.Info, appId: app.Id, slug: app.Slug);
            }
            else if (!allowed)
            {
                if (app.ExcludedReason != ShizukuPermission.Reason)
                {
                    app.Availability = Availability.Excluded;
                    app.ExcludedReason = ShizukuPermission.Reason;
                    changed++;
                    session.Decision($"shizuku gate: excluded '{app.Slug}'",
                        new { package = app.PackageName, reason = ShizukuPermission.Reason },
                        JobEventLevel.Info, appId: app.Id, slug: app.Slug);
                }

                // One tombstone per gated slug; re-excluding after a heal
                // writes a fresh one so clients drop the row again.
                if (!tombstones.ContainsKey(app.Slug))
                {
                    var tombstone = new RemovedApp
                    {
                        Slug = app.Slug,
                        Name = app.Name,
                        Listing = app.Listing,
                        RemovedAt = commitNow,
                    };
                    tombstones[app.Slug] = tombstone;
                    db.RemovedApps.Add(tombstone);
                    tombstonesChanged = true;
                }
            }
        }

        if (changed > 0 || tombstonesChanged)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    /// <summary>
    /// One-shot icon re-render over every app with a recorded APK (the
    /// <c>--refresh-icons</c> run). Renderer upgrades never reach unchanged
    /// releases through normal passes (304 keeps the old icon file), so this
    /// re-downloads + re-resolves each icon. Two phases: per-app
    /// download + analyze + stage (parallel, no Gradle), then batched Gradle
    /// invocations render the staged XML icons in chunks of
    /// <see cref="EnrichmentOptions.IconBatchChunkSize"/> (a task graph +
    /// test JVM per icon costs minutes; shared, seconds per icon). Every
    /// chunk runs isolated so its JVMs exit before the next one, and a
    /// failed chunk only fails its own apps; the refresh continues. The warm
    /// daemon is stopped once, after the last chunk. The caller owns the job
    /// run row (kind <c>icon_refresh</c>, which HEAD tracking ignores); this
    /// method only streams events into the ambient session.
    /// Force re-renders and rewrites every icon (equal bytes count as
    /// refreshed): the only way to catch self-consistent wrong files
    /// (e.g. a swapped pair whose hashes match their rows).
    /// </summary>
    public async Task<IconRefreshResult> RefreshIconsAsync(CancellationToken ct = default, bool force = false)
    {
        var session = JobContext.Current;
        _fdroid?.BeginRun();
        if (enrichment.SkipApkAnalysis)
        {
            throw new InvalidOperationException(
                "Enrichment:SkipApkAnalysis is enabled; icon refresh needs APK analysis. Disable it first.");
        }

        var ids = await db.Apps.AsNoTracking()
            .Where(a => a.Availability == Availability.DirectApk && a.Downloads.Any(d => d.IsPrimary))
            .Select(a => a.Id)
            .ToListAsync(ct);
        session?.Phase("icons", $"refreshing icons for {ids.Count} apps", new { force });

        var refreshed = 0;
        var current = 0;
        var failed = 0;
        var errors = new List<string>();
        void Tally(long id, EnrichResult r)
        {
            JobContext.Current?.App($"[{id}]", null, r, id);
            switch (r.Outcome)
            {
                case EnrichOutcome.Enriched:
                    refreshed++;
                    break;
                case EnrichOutcome.Failed:
                    failed++;
                    if (r.Error is not null)
                    {
                        errors.Add($"[{id}] {r.Error}");
                    }

                    break;
                default: // UpToDate, SkippedFresh (no recorded APK / same icon)
                    current++;
                    break;
            }
        }

        var batchWorkDir = Path.Combine(Path.GetTempPath(), $"shizu-batch-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(batchWorkDir);

            // Phase A: downloads + staging fan out; raster icons resolve
            // immediately, XML icons come back pending.
            var pending = new List<(long Id, BatchRenderRequest Request)>();
            var prepared = await BulkEnricher.EnrichManyAsync<long, PrepareIconResult>(
                ids, (id, c) => enrich.PrepareIconRefreshAsync(id, batchWorkDir, $"b{id}", c, force),
                enrichment.MaxParallelism,
                ex => new PrepareIconResult(EnrichOutcome.Failed, $"enrich crashed: {ex.Message}", null),
                ct);
            foreach (var (id, r) in prepared)
            {
                if (r.Pending is { } p)
                {
                    pending.Add((id, new BatchRenderRequest(p.DrawableName, p.RootFile)));
                }
                else
                {
                    Tally(id, new EnrichResult(r.Outcome, r.Error));
                }
            }

            session?.Phase("icons", $"staged {prepared.Count} apps, {pending.Count} XML icons to render");

            // Phase B: chunked Gradle invocations over everything staged.
            // Each chunk isolates its JVMs (the renderer passes
            // --no-daemon) so memory cannot accumulate across a backfill,
            // and a failed chunk only fails its own apps: one bad chunk
            // must not strand the rest of the refresh.
            var pngs = new Dictionary<long, byte[]?>();
            var chunkSize = Math.Max(1, enrichment.IconBatchChunkSize);
            for (var offset = 0; offset < pending.Count; offset += chunkSize)
            {
                var chunk = pending.GetRange(offset, Math.Min(chunkSize, pending.Count - offset));
                session?.Render($"render chunk {offset + 1}-{offset + chunk.Count} of {pending.Count} icons");
                byte[]?[] rendered;
                try
                {
                    rendered = await renderer.RenderBatchAsync(
                        Path.Combine(batchWorkDir, "res"),
                        chunk.Select(p => p.Request).ToList(),
                        LauncherIconService.RenderSize, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.LogWarning(
                        ex, "Icon batch chunk {Offset}-{End} of {Total} failed; continuing.",
                        offset, offset + chunk.Count, pending.Count);
                    session?.Render(
                        $"render chunk {offset + 1}-{offset + chunk.Count} failed: {ex.Message}",
                        level: JobEventLevel.Warning);
                    foreach (var (id, _) in chunk)
                    {
                        failed++;
                        errors.Add($"[{id}] Icon refresh: batch render failed: {ex.Message}");
                    }

                    continue;
                }

                for (var i = 0; i < chunk.Count; i++)
                {
                    pngs[chunk[i].Id] = rendered[i];
                }
            }

            // Phase C: adopt the renders (parallel row updates, no Gradle).
            // Only ids with a render attempt are committed: failed chunks
            // are already tallied, and a blank entry means "no output" for
            // that icon (commit keeps the current icon).
            // The adaptive flag travels per app: only <adaptive-icon>
            // roots count, plain vectors stay false.
            var attempted = pending.Where(p => pngs.ContainsKey(p.Id)).ToList();
            var adaptive = attempted.ToDictionary(p => p.Id, p => p.Request.IsAdaptive);
            var commits = await BulkEnricher.EnrichManyAsync(
                attempted.Select(p => p.Id).ToList(),
                (id, c) => enrich.CommitIconRefreshAsync(id, pngs[id], c, force, adaptive[id]),
                enrichment.MaxParallelism, ct);
            foreach (var (id, r) in commits)
            {
                Tally(id, r);
            }
        }
        finally
        {
            try { Directory.Delete(batchWorkDir, recursive: true); } catch { /* best effort */ }
            // One stop, after the last chunk: fresh chunks already exited
            // their own JVMs, this frees the daemon left warm by single
            // renders. Runs after a cancelled refresh too.
            await renderer.StopGradleDaemonsAsync();
        }

        session?.Phase("icons", "icon refresh complete",
            new { refreshed, current, failed, errors = errors.Count });
        return new IconRefreshResult(ids.Count, refreshed, current, failed) { Errors = errors };
    }

    /// <summary>
    /// Screenshots-only maintenance pass over every served app (the admin
    /// trigger): re-resolves F-Droid/Izzy and forces the repo fallback past
    /// its recheck window. No APK work; the caller owns the job run row
    /// (kind <c>screenshot_refresh</c>). Errors per app are tallied and
    /// returned, never thrown.
    /// </summary>
    public async Task<ScreenshotRefreshResult> RefreshScreenshotsAsync(CancellationToken ct = default)
    {
        var session = JobContext.Current;
        _fdroid?.BeginRun();

        var ids = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded)
            .Select(a => a.Id)
            .ToListAsync(ct);
        session?.Phase("screenshots", $"refreshing screenshots for {ids.Count} apps");

        var updated = 0;
        var current = 0;
        var failed = 0;
        var errors = new List<string>();
        var results = await BulkEnricher.EnrichManyAsync(
            ids, (id, c) => enrich.RefreshScreenshotsAsync(id, c),
            enrichment.MaxParallelism, ct,
            onResult: (id, r) => JobContext.Current?.App($"[{id}]", null, r, id));
        foreach (var (id, r) in results)
        {
            switch (r.Outcome)
            {
                case EnrichOutcome.Enriched:
                    updated++;
                    break;
                case EnrichOutcome.Failed:
                    failed++;
                    if (r.Error is not null)
                    {
                        errors.Add($"[{id}] {r.Error}");
                    }

                    break;
                default: // UpToDate and any skip outcome
                    current++;
                    break;
            }
        }

        return new ScreenshotRefreshResult(ids.Count, updated, current, failed) { Errors = errors };
    }

    /// <summary>
    /// Batched icon pass that follows a full recheck when
    /// <c>BatchIconsOnFullPass</c> is on: enrichment resolved raster icons
    /// only, this renders every XML icon with one Gradle invocation.
    /// Best-effort: a broken icon batch must never fail the pass it rides on.
    /// </summary>
    private async Task BatchRenderIconsAsync(CancellationToken ct)
    {
        if (enrichment.SkipApkAnalysis)
        {
            return;
        }

        try
        {
            var result = await RefreshIconsAsync(ct);
            _log?.LogInformation(
                "Full-pass icon batch: {Checked} apps, {Refreshed} refreshed, {Current} already current, {Failed} failed.",
                result.Checked, result.Refreshed, result.AlreadyCurrent, result.Failed);
            JobContext.Current?.Phase("icons", "full-pass icon batch complete",
                new
                {
                    checkedApps = result.Checked,
                    refreshed = result.Refreshed,
                    alreadyCurrent = result.AlreadyCurrent,
                    failed = result.Failed,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "Full-pass icon batch failed; XML icons keep their raster fallbacks.");
        }
    }

    private async Task<SyncPassResult> FinishRunAsync(
        JobSession session, string trigger, string? head,
        int added, int updated, int removed,
        int enriched, int upToDate, int failed,
        IReadOnlyList<long> drainedIds, IReadOnlyList<ParseWarning> parseWarnings, bool refreshParse,
        int archivedChanged, int unlistedChanged, DateTimeOffset started, CancellationToken ct,
        IReadOnlyList<string>? failedMessages = null)
    {
        var now = DateTimeOffset.UtcNow;
        // Only the requests seen at pass start are consumed: a webhook that
        // lands while this pass runs must stay pending for the immediate
        // follow-up pass, otherwise it would be marked processed without its
        // list state ever being parsed.
        foreach (var request in await db.SyncRequests
            .Where(r => drainedIds.Contains(r.Id) && !r.Processed)
            .ToListAsync(ct))
        {
            request.Processed = true;
            request.ProcessedAt = now;
        }

        // Staleness is evaluated against the pass clock (started), not the
        // finished time: they differ only by the pass duration in production,
        // but tests run passes with a fixed historical clock.
        var issues = await CollectIssuesAsync(parseWarnings, refreshParse, started, now, ct);
        var parseIssues = issues.Count(i => i.Kind == IssueKind.Parse);
        var enrichIssues = issues.Count(i => i.Kind == IssueKind.Enrich);
        var qualityIssues = issues.Count(i => i.Kind == IssueKind.Quality);

        // The snapshot holds only the latest completed run: successful passes
        // replace it wholesale (due-only passes keep the parse rows, whose
        // source list did not change). Failed passes never reach here, so a
        // crashed pass keeps the previous good snapshot. Without a run row
        // (sink unavailable) the replace is skipped too: the old snapshot
        // stays valid and no issue needs the run FK.
        if (session.RunId is { } jobRunId)
        {
            if (refreshParse)
            {
                await db.SyncIssues.ExecuteDeleteAsync(ct);
            }
            else
            {
                await db.SyncIssues.Where(i => i.Kind != IssueKind.Parse).ExecuteDeleteAsync(ct);
            }

            foreach (var issue in issues)
            {
                issue.JobRunId = jobRunId;
            }

            db.SyncIssues.AddRange(issues);
            await db.SaveChangesAsync(ct);
        }

        // Mirror the snapshot into the job event stream: one event per issue
        // makes GET /v1/issues reconstructable from the run detail page.
        foreach (var issue in issues)
        {
            session.Event(
                issue.Kind == IssueKind.Enrich ? JobEventLevel.Warning : JobEventLevel.Info,
                JobEventType.Issue,
                issue.Message,
                appId: issue.AppId,
                slug: issue.Slug,
                data: new { kind = issue.Kind.ToString(), rule = issue.Rule, location = issue.Location });
        }

        await session.FinishAsync(
            new JobFinish(
                JobStatus.Succeeded,
                Summary: $"+{added} ~{updated} -{removed}, {enriched} enriched, {upToDate} current, {failed} failed, {issues.Count} issues",
                ItemsTotal: enriched + upToDate + failed,
                ItemsOk: enriched,
                ItemsSkipped: upToDate,
                ItemsFailed: failed,
                Reference: head,
                Metadata: new
                {
                    added,
                    updated,
                    removed,
                    drained = drainedIds.Count,
                    parseWarnings = parseWarnings.Count,
                    archivedChanged,
                    unlistedChanged,
                    issueCount = issues.Count,
                    parse = parseIssues,
                    enrich = enrichIssues,
                    quality = qualityIssues,
                }),
            ct);

        return new SyncPassResult(trigger, head, added, updated, removed,
            enriched, upToDate, failed, drainedIds.Count, parseIssues,
            archivedChanged, unlistedChanged, false, null)
        {
            FailedMessages = failedMessages ?? [],
        };
    }

    /// <summary>
    /// Current health snapshot rows for the run. Parse rows come from this
    /// pass; enrich rows reflect every row carrying <c>last_error</c> right
    /// now (not just this pass); quality rows are recomputed over the catalog.
    /// </summary>
    private async Task<List<SyncIssue>> CollectIssuesAsync(
        IReadOnlyList<ParseWarning> parseWarnings, bool refreshParse,
        DateTimeOffset referenceNow, DateTimeOffset now, CancellationToken ct)
    {
        var issues = new List<SyncIssue>();
        if (refreshParse)
        {
            foreach (var warning in parseWarnings)
            {
                issues.Add(new SyncIssue
                {
                    Kind = IssueKind.Parse,
                    Rule = "parse_warning",
                    Message = warning.Message,
                    Location = warning.Location,
                    CreatedAt = now,
                });
            }
        }

        var failures = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && a.LastError != null)
            .Select(a => new { a.Id, a.Slug, a.LastError })
            .ToListAsync(ct);
        foreach (var failure in failures)
        {
            issues.Add(new SyncIssue
            {
                Kind = IssueKind.Enrich,
                Rule = "enrich_failed",
                AppId = failure.Id,
                Slug = failure.Slug,
                Message = failure.LastError!,
                CreatedAt = now,
            });
        }

        var quality = await CatalogHealthCheck.CheckAsync(db, referenceNow, enrichment.SuccessRecheckInterval, ct);
        foreach (var finding in quality)
        {
            issues.Add(new SyncIssue
            {
                Kind = IssueKind.Quality,
                Rule = finding.Rule,
                AppId = finding.AppId,
                Slug = finding.Slug,
                Message = finding.Message,
                CreatedAt = now,
            });
        }

        // Direct-APK rows whose analyzed APK declares no Shizuku permission are
        // flagged unless an operator entry exists. Either action suppresses the
        // issue: `allow` keeps the row, `dontaudit` hides it silently.
        var exceptionPackages = await db.PackageExceptions.AsNoTracking()
            .Select(e => e.PackageName)
            .ToListAsync(ct);
        var exceptionSet = new HashSet<string>(exceptionPackages, StringComparer.Ordinal);
        // A recorded build on any source can be the Shizuku witness; keep the
        // gate and the report in agreement.
        var shizukuApps = (await db.Downloads.AsNoTracking()
            .Where(d => d.ShizukuDeclared)
            .Select(d => d.AppId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();
        var shizukuCandidates = await db.Apps.AsNoTracking()
            .Where(a => a.PackageName != null
                && (a.Availability == Availability.DirectApk
                    || (a.Availability == Availability.Excluded && a.ExcludedReason == ShizukuPermission.Reason)))
            .Select(a => new { a.Id, a.Slug, a.Permissions, a.PackageName })
            .ToListAsync(ct);
        foreach (var row in shizukuCandidates)
        {
            if (ShizukuPermission.IsDeclared(row.Permissions)
                || shizukuApps.Contains(row.Id)
                || exceptionSet.Contains(row.PackageName!))
            {
                continue;
            }

            issues.Add(new SyncIssue
            {
                Kind = IssueKind.Quality,
                Rule = ShizukuPermission.Rule,
                AppId = row.Id,
                Slug = row.Slug,
                Message = ShizukuPermission.Reason,
                CreatedAt = now,
            });
        }

        return issues;
    }
}
