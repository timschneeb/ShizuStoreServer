using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Parsing;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>Outcome of one sync pass (the <c>sync_runs</c> row carries the subset that has columns).</summary>
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

/// <summary>Outcome of a <c>--refresh-icons</c> run (console only, no <c>sync_runs</c> row).</summary>
public sealed record IconRefreshResult(
    int Checked,
    int Refreshed,
    int AlreadyCurrent,
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
/// apps plus poll-changed apps (force), or skip entirely (no
/// <c>sync_runs</c> row) when neither has anything.</item>
/// <item>Otherwise parse README (CLOSED_SOURCE is ignored; its rows sweep
/// out as stale), merge git history, upsert, apply ARCHIVED.md
/// exclusions, then enrich.</item>
/// </list>
/// Invariant: this scope makes no app mutations after the upserter saves, so
/// the final bookkeeping <c>SaveChanges</c> only writes request flags + the
/// <c>sync_runs</c> row (enrichment runs in per-app scopes via
/// <see cref="IEnrichmentRunner"/>). Never throws except on
/// <c>OperationCanceledException</c> or an unwritable DB, failures become
/// error results + error run rows.
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
    IRunLog? runLog = null)
{
    private readonly IRunLog _runLog = runLog ?? NullRunLog.Instance;

    /// <summary>Exclusion reason for apps listed in <c>pages/ARCHIVED.md</c>.</summary>
    public const string ArchivedReason = "Archived in the upstream list.";

    private const string ReadmePath = "README.md";
    private const string ArchivedPath = "pages/ARCHIVED.md";

    public async Task<SyncPassResult> RunAsync(
        string trigger, bool fullRecheck, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(trigger, fullRecheck, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The upserter may already have saved; discard any half-tracked
            // state so the error row below is the only thing written.
            db.ChangeTracker.Clear();
            var error = $"Sync pass failed: {ex.Message}";
            db.SyncRuns.Add(new SyncRun
            {
                StartedAt = now,
                FinishedAt = DateTimeOffset.UtcNow,
                Trigger = trigger,
                Error = error,
            });
            await db.SaveChangesAsync(ct);
            _runLog.Skip(trigger, DateTimeOffset.UtcNow, $"pass failed: {ex.Message}");
            return new SyncPassResult(trigger, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, error);
        }
    }

    private async Task<SyncPassResult> RunCoreAsync(
        string trigger, bool fullRecheck, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.SyncRequests
            .Where(r => !r.Processed)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        var effectiveTrigger = pending.Count > 0 ? "webhook" : trigger;

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
        var lastHead = await db.SyncRuns
            .OrderByDescending(r => r.Id)
            .Select(r => r.HeadCommit)
            .FirstOrDefaultAsync(ct);

        if (!fullRecheck && pending.Count == 0 && head is not null && head == lastHead)
        {
            var dueIds = await SelectDueAppIdsAsync(now, ct);
            var freshIds = (await PollChangedAsync(ct)).Except(dueIds).ToList();
            if (dueIds.Count == 0 && freshIds.Count == 0)
            {
                _runLog.Skip(effectiveTrigger, now, "nothing due");
                return new SyncPassResult(
                    effectiveTrigger, head, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, null);
            }

            _runLog.Begin(effectiveTrigger, fullRecheck, now, dueIds.Count + freshIds.Count);
            var (enriched, upToDate, failed, failedMessages) =
                await EnrichWithFreshAsync(dueIds, false, freshIds, now, ct);
            await ApplyShizukuFilterAsync(ct);
            return await FinishRunAsync(effectiveTrigger, head,
                0, 0, 0, enriched, upToDate, failed,
                0, [], false, 0, now, ct, failedMessages);
        }

        var readme = await File.ReadAllTextAsync(Path.Combine(options.ListPath, ReadmePath), ct);
        var parser = new AwesomeListParser();
        var mainDoc = parser.Parse(readme, "main");

        var history = await git.GetHistoryAsync(options.ListPath, ReadmePath, ct);

        // CLOSED_SOURCE.md is intentionally not read (all Play-only
        // proprietary entries, user call). The empty doc still marks the
        // listing as synced so pre-decision rows sweep out as stale.
        var closedDoc = new ParsedDocument { ListingName = "closed-source" };

        var counts = await upserter.UpsertAsync([mainDoc, closedDoc], history, now, ct);
        var (archivedChanged, archivedWarnings) = await ApplyArchivedAsync(ct);
        var parseWarnings = mainDoc.Warnings.Concat(archivedWarnings).ToList();

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
        _runLog.Begin(effectiveTrigger, fullRecheck, now, ids.Count + extraIds.Count);
        var (enrichedFull, upToDateFull, failedFull, failedMessagesFull) =
            await EnrichWithFreshAsync(ids, fullRecheck, extraIds, now, ct);
        await ApplyShizukuFilterAsync(ct);

        return await FinishRunAsync(effectiveTrigger, head,
            counts.Added, counts.Updated, counts.Removed,
            enrichedFull, upToDateFull, failedFull,
            pending.Count, parseWarnings, true, archivedChanged, now, ct, failedMessagesFull);
    }

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
            .Select(a => new { a.Id, a.LastCheckedAt, a.LastError })
            .ToListAsync(ct);
        return rows
            .Where(r => r.LastCheckedAt is null
                || r.LastCheckedAt + (r.LastError is null
                    ? enrichment.SuccessRecheckInterval
                    : enrichment.FailedRecheckInterval) <= now)
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>Poll-changed ids (best-effort: any failure means no forced extras).</summary>
    private async Task<IReadOnlySet<long>> PollChangedAsync(CancellationToken ct)
    {
        try
        {
            return await poll.FindChangedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

        // Loaded before the batch so the run log can stream a line per app as
        // it finishes; a row deleted mid-pass falls back to its id.
        var meta = (await db.Apps.AsNoTracking()
                .Where(a => ids.Contains(a.Id))
                .Select(a => new { a.Id, a.Slug, a.Name, a.DisplayName })
                .ToListAsync(ct))
            .ToDictionary(a => a.Id);

        var results = await BulkEnricher.EnrichManyAsync(
            ids, (id, c) => enrich.EnrichAsync(id, force, now, c), enrichment.MaxParallelism, ct,
            onResult: (id, r) =>
            {
                if (meta.TryGetValue(id, out var m))
                {
                    _runLog.App(m.Slug, m.DisplayName ?? m.Name, r);
                }
                else
                {
                    _runLog.App($"[{id}]", null, r);
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
                default: // Enriched, AvatarFallback, Excluded
                    enriched++;
                    break;
            }
        }

        return (enriched, upToDate, failed, failedMessages);
    }

    /// <summary>
    /// <c>pages/ARCHIVED.md</c> is an exclusion list: entries found
    /// there are hidden from clients; entries that reappear upstream have the
    /// flag cleared and are forced through re-enrichment.
    /// </summary>
    private async Task<(int Changed, List<ParseWarning> Warnings)> ApplyArchivedAsync(CancellationToken ct)
    {
        var path = Path.Combine(options.ListPath, ArchivedPath);
        if (!File.Exists(path))
        {
            return (0, []);
        }

        var archived = new AwesomeListParser().Parse(await File.ReadAllTextAsync(path, ct), "archived");
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

        var changed = 0;
        var apps = await db.Apps.ToListAsync(ct);
        foreach (var app in apps)
        {
            var isArchived = urls.Contains(app.Url)
                || (app.SourceUrl is not null && urls.Contains(app.SourceUrl));
            if (isArchived && app.ExcludedReason != ArchivedReason)
            {
                app.Availability = Availability.Excluded;
                app.ExcludedReason = ArchivedReason;
                changed++;
            }
            else if (!isArchived && app.ExcludedReason == ArchivedReason)
            {
                app.ExcludedReason = null;
                app.LastCheckedAt = null; // force re-enrichment (re-classifies availability)
                app.LastError = null;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return (changed, archived.Warnings);
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

        var changed = 0;
        var tombstonesChanged = false;
        foreach (var app in apps)
        {
            var allowed = ShizukuPermission.IsDeclared(app.Permissions)
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
            }
            else if (!allowed)
            {
                if (app.ExcludedReason != ShizukuPermission.Reason)
                {
                    app.Availability = Availability.Excluded;
                    app.ExcludedReason = ShizukuPermission.Reason;
                    changed++;
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
    /// download + analyze + stage (parallel, no Gradle), then ONE Gradle
    /// invocation renders every staged XML icon (a task graph + test JVM
    /// per icon costs minutes; shared, seconds per icon), then per-app
    /// commit. Writes no <c>sync_runs</c> row: a row would poison HEAD
    /// tracking (null head forces a full re-parse next loop).
    /// Force re-renders and rewrites every icon (equal bytes count as
    /// refreshed): the only way to catch self-consistent wrong files
    /// (e.g. a swapped pair whose hashes match their rows).
    /// </summary>
    public async Task<IconRefreshResult> RefreshIconsAsync(CancellationToken ct = default, bool force = false)
    {
        if (enrichment.SkipApkAnalysis)
        {
            throw new InvalidOperationException(
                "Enrichment:SkipApkAnalysis is enabled; icon refresh needs APK analysis. Disable it first.");
        }

        var ids = await db.Apps.AsNoTracking()
            .Where(a => a.Availability == Availability.DirectApk && a.Downloads.Any(d => d.IsPrimary))
            .Select(a => a.Id)
            .ToListAsync(ct);

        var refreshed = 0;
        var current = 0;
        var failed = 0;
        var errors = new List<string>();
        void Tally(long id, EnrichResult r)
        {
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

            // Phase B: one Gradle invocation for everything staged. A total
            // tool failure keeps every pending icon (per-icon fallbacks
            // cannot run without renders).
            var pngs = new Dictionary<long, byte[]?>();
            if (pending.Count > 0)
            {
                byte[]?[] rendered;
                try
                {
                    rendered = await renderer.RenderBatchAsync(
                        Path.Combine(batchWorkDir, "res"),
                        pending.Select(p => p.Request).ToList(),
                        LauncherIconService.RenderSize, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    foreach (var (id, _) in pending)
                    {
                        failed++;
                        errors.Add($"[{id}] Icon refresh: batch render failed: {ex.Message}");
                    }

                    return new IconRefreshResult(ids.Count, refreshed, current, failed) { Errors = errors };
                }

                for (var i = 0; i < pending.Count; i++)
                {
                    pngs[pending[i].Id] = rendered[i];
                }
            }

            // Phase C: adopt the renders (parallel row updates, no Gradle).
            // The adaptive flag travels per app: only <adaptive-icon>
            // roots count, plain vectors stay false.
            var adaptive = pending.ToDictionary(p => p.Id, p => p.Request.IsAdaptive);
            var commits = await BulkEnricher.EnrichManyAsync(
                pending.Select(p => p.Id).ToList(),
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
        }

        return new IconRefreshResult(ids.Count, refreshed, current, failed) { Errors = errors };
    }

    private async Task<SyncPassResult> FinishRunAsync(
        string trigger, string? head,
        int added, int updated, int removed,
        int enriched, int upToDate, int failed,
        int drained, IReadOnlyList<ParseWarning> parseWarnings, bool refreshParse, int archivedChanged,
        DateTimeOffset started, CancellationToken ct,
        IReadOnlyList<string>? failedMessages = null)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var request in await db.SyncRequests.Where(r => !r.Processed).ToListAsync(ct))
        {
            request.Processed = true;
            request.ProcessedAt = now;
        }

        // Staleness is evaluated against the pass clock (started), not the
        // finished time: they differ only by the pass duration in production,
        // but tests run passes with a fixed historical clock.
        var issues = await CollectIssuesAsync(parseWarnings, refreshParse, started, now, ct);

        // The snapshot holds only the latest completed run: successful passes
        // replace it wholesale (due-only passes keep the parse rows, whose
        // source list did not change). Failed passes never reach here, so a
        // crashed pass keeps the previous good snapshot.
        if (refreshParse)
        {
            await db.SyncIssues.ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.SyncIssues.Where(i => i.Kind != IssueKind.Parse).ExecuteDeleteAsync(ct);
        }

        var run = new SyncRun
        {
            StartedAt = started,
            FinishedAt = now,
            Trigger = trigger,
            HeadCommit = head,
            Added = added,
            Updated = updated,
            Removed = removed,
            Failed = failed,
            IssueCount = issues.Count,
        };
        db.SyncRuns.Add(run);
        foreach (var issue in issues)
        {
            issue.SyncRun = run;
        }

        db.SyncIssues.AddRange(issues);
        await db.SaveChangesAsync(ct);

        // Close the run log section, then mirror GET /v1/issues so the file
        // carries the same post-run health snapshot.
        _runLog.End(now, enriched, upToDate, failed);
        _runLog.Issues(run.Id, head, issues);

        return new SyncPassResult(trigger, head, added, updated, removed,
            enriched, upToDate, failed, drained, issues.Count(i => i.Kind == IssueKind.Parse),
            archivedChanged, false, null)
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
        var shizukuCandidates = await db.Apps.AsNoTracking()
            .Where(a => a.PackageName != null
                && (a.Availability == Availability.DirectApk
                    || (a.Availability == Availability.Excluded && a.ExcludedReason == ShizukuPermission.Reason)))
            .Select(a => new { a.Id, a.Slug, a.Permissions, a.PackageName })
            .ToListAsync(ct);
        foreach (var row in shizukuCandidates)
        {
            if (ShizukuPermission.IsDeclared(row.Permissions) || exceptionSet.Contains(row.PackageName!))
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
