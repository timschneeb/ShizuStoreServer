using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

public interface IUsageAnalysisRunner
{
    /// <summary>
    /// Claims and processes one due queue entry. Returns false when the queue
    /// is empty, the analyzer is disabled or a budget is exhausted.
    /// </summary>
    Task<bool> RunNextAsync(CancellationToken ct = default);

    /// <summary>
    /// Requeues rows left <c>Running</c> by a process that died mid-analysis.
    /// Called once at worker startup so a restart never strands an app.
    /// </summary>
    Task<int> RecoverInterruptedAsync(CancellationToken ct = default);
}

/// <summary>
/// Executes one queued AI analysis end to end: snapshot, pre-scan, agent,
/// validation, storage. Failures are retried with linear backoff until
/// <c>RetryMaxAttempts</c>, then parked as failed. Token usage and cost are
/// recorded per attempt either way.
/// </summary>
public sealed class UsageAnalysisRunner(
    ShizuDbContext db,
    UsageAnalysisOptions options,
    IRepoSnapshotProvider snapshots,
    IUsageAnalysisAgent agent,
    IUseCaseTagger tagger,
    ILogger<UsageAnalysisRunner>? log = null,
    IUsageAnalysisLogWriter? logs = null,
    IJobLog? jobLog = null) : IUsageAnalysisRunner
{
    private readonly IJobLog _jobs = jobLog ?? NullJobLog.Instance;

    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var interrupted = await db.UsageAnalysisRuns
            .Where(r => r.Status == UsageAnalysisStatus.Running)
            .ToListAsync(ct);
        if (interrupted.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var parked = 0;
        foreach (var run in interrupted)
        {
            if (run.Attempts >= Math.Max(1, options.RetryMaxAttempts))
            {
                run.Status = UsageAnalysisStatus.Failed;
                run.Error = "interrupted by a restart and out of retries";
                run.FinishedAt = now;
                run.FinishedDay = DateOnly.FromDateTime(now.UtcDateTime);
                parked++;
            }
            else
            {
                run.Status = UsageAnalysisStatus.Pending;
            }

            run.StartedAt = null;
            run.NextAttemptAt = now;
        }

        await db.SaveChangesAsync(ct);
        log?.LogWarning("Recovered {Count} interrupted usage analysis run(s); {Parked} parked as failed.",
            interrupted.Count, parked);
        return interrupted.Count;
    }

    public async Task<bool> RunNextAsync(CancellationToken ct = default)
    {
        if (!options.IsConfigured)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (await IsOverBudgetAsync(now, ct))
        {
            return false;
        }

        // The queue holds at most a few hundred pending rows, and the SQLite
        // test provider cannot translate DateTimeOffset range comparisons, so
        // due filtering happens in memory.
        var pending = await db.UsageAnalysisRuns
            .Where(r => r.Status == UsageAnalysisStatus.Pending)
            .Select(r => new { r.Id, r.CreatedAt, r.NextAttemptAt })
            .ToListAsync(ct);
        var candidateId = pending
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .FirstOrDefault(r => r.NextAttemptAt <= now)?.Id ?? 0;
        if (candidateId == 0)
        {
            return false;
        }

        // Atomic claim: enrichment enqueues and other worker slots may race.
        var claimed = await db.UsageAnalysisRuns
            .Where(r => r.Id == candidateId && r.Status == UsageAnalysisStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, UsageAnalysisStatus.Running)
                .SetProperty(r => r.StartedAt, now)
                .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
        if (claimed == 0)
        {
            return false;
        }

        // ExecuteUpdate bypasses the change tracker; drop any locally tracked
        // copy so the reload below sees the claimed row (tests share the context).
        var tracked = db.ChangeTracker.Entries<UsageAnalysisRun>()
            .FirstOrDefault(e => e.Entity.Id == candidateId);
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        var run = await db.UsageAnalysisRuns
            .Include(r => r.App)
            .FirstAsync(r => r.Id == candidateId, ct);
        var app = run.App
            ?? throw new InvalidOperationException($"Usage analysis run {run.Id} has no app.");

        run.Model = options.Model;
        run.PromptVersion = run.Kind == UsageAnalysisKind.Tagging
            ? options.TagPromptVersion
            : options.PromptVersion;

        if (run.Kind == UsageAnalysisKind.Tagging)
        {
            return await RunTaggingAsync(run, app, now, ct);
        }

        var primary = await db.Downloads
            .Where(d => d.AppId == app.Id && d.IsPrimary)
            .Select(d => new { d.VersionName, d.ReleaseTag, d.ApkUrl })
            .FirstOrDefaultAsync(ct);
        var version = primary?.VersionName;

        await using var session = _jobs.Begin(new JobStart(
            JobKind.UsageAnalysis,
            run.Trigger ?? JobTrigger.Auto,
            now,
            Metadata: new { slug = app.Slug, appId = app.Id, version, attempt = run.Attempts },
            UsageAnalysisRunId: run.Id));
        session.Phase("analyze", $"analyzing {app.Slug}",
            new { app = app.Slug, package = app.PackageName, version, url = app.Url });

        // The agent enforces RunTimeout on itself and returns a timed-out
        // result with its partial transcript; this outer budget only guards a
        // stuck snapshot and adds the clone allowance.
        var wallBudget = options.CloneTimeout + options.RunTimeout + TimeSpan.FromMinutes(1);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(wallBudget);

        UsageAgentResult? result = null;
        string? error = null;
        RepoSnapshot? snapshot = null;
        try
        {
            snapshot = await snapshots.CreateAsync(
                app, version ?? app.VersionName, primary?.ReleaseTag, primary?.ApkUrl, budget.Token);
            if (snapshot is null)
            {
                error = "no analyzable GitHub/GitLab repository, or the checkout failed";
                session.Decision(error, level: JobEventLevel.Warning);
            }
            else
            {
                session.Phase("snapshot", $"cloned {snapshot.ForgeName} {snapshot.Ref} ({snapshot.Commit})",
                    new { forge = snapshot.ForgeName, @ref = snapshot.Ref, commit = snapshot.Commit });
                var context = new UsageContextBuilder(options).Build(app, snapshot, version ?? app.VersionName);
                var search = new RepoSearch(snapshot, options);
                result = await agent.AnalyzeAsync(app, context, search, budget.Token);
                if (result.TimedOut)
                {
                    error = $"timed out after {options.RunTimeout}";
                }
                else if (result.Report is null)
                {
                    error = "the model did not return a valid report";
                }

                run.RepoForge = snapshot.ForgeName;
                run.RepoCommit = snapshot.Commit;
                run.RepoRef = snapshot.Ref;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = $"timed out after {wallBudget}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            log?.LogWarning(ex, "Usage analysis failed for {Slug}.", app.Slug);
        }
        finally
        {
            snapshot?.Dispose();
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var input = result?.InputTokens ?? 0;
        var cached = result?.CachedInputTokens ?? 0;
        var output = result?.OutputTokens ?? 0;
        run.InputTokens += input;
        run.CachedInputTokens += cached;
        run.OutputTokens += output;
        run.ToolCalls += result?.ToolCalls ?? 0;
        run.CostUsd += ComputeCost(input, cached, output, options);

        if (result?.Report is { } report && error is null)
        {
            app.UsageShort = report.Short;
            app.UsageMarkdown = report.ComposedMarkdown;
            app.UsageMarkdownUsage = report.MarkdownUsage;
            app.UsageMarkdownApiUsage = report.MarkdownApiUsage;
            app.UsageMarkdownNotableDetails = report.MarkdownNotableDetails;
            app.UsageAnalyzedAt = finishedAt;
            app.UsageModel = options.Model;
            app.UsageCommit = run.RepoCommit;
            app.UsageReleaseRef = run.RepoRef;
            app.UsagePromptVersion = options.PromptVersion;
            app.UsageAnalysisVersion = options.AnalysisVersion;

            if (options.TaggingEnabled)
            {
                // Tagging is best effort on the analysis path: a classifier
                // failure must never discard a valid report, so the retry is a
                // dedicated tag-only run that does not clone the repo again.
                try
                {
                    var tagged = await tagger.TagAsync(app, ct);
                    input += tagged.InputTokens;
                    cached += tagged.CachedInputTokens;
                    output += tagged.OutputTokens;
                    run.InputTokens += tagged.InputTokens;
                    run.CachedInputTokens += tagged.CachedInputTokens;
                    run.OutputTokens += tagged.OutputTokens;
                    run.CostUsd += ComputeCost(tagged.InputTokens, tagged.CachedInputTokens, tagged.OutputTokens, options);
                    if (tagged.Succeeded)
                    {
                        session.Event(JobEventLevel.Info, JobEventType.AiResult,
                            $"use case tags ready for {app.Slug}",
                            data: new { slug = app.Slug, tags = tagged.TagCount, promoted = tagged.Promotions });
                    }
                    else
                    {
                        EnqueueTaggingRetry(app, tagged.Error, finishedAt);
                        session.Event(JobEventLevel.Warning, JobEventType.AiValidation,
                            $"use case tagging failed for {app.Slug}: {tagged.Error}",
                            data: new { slug = app.Slug, retry = true });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.LogWarning(ex, "Use case tagging failed for {Slug}.", app.Slug);
                    EnqueueTaggingRetry(app, ex.Message, finishedAt);
                }
            }

            // Usage text is detail-only, but a finished analysis is still a
            // change clients must learn about: bump the summary clock so
            // /v1/changes and the detail ETag pick it up.
            app.UpdatedAt = finishedAt;

            run.Status = UsageAnalysisStatus.Succeeded;
            run.Error = null;
            run.FinishedAt = finishedAt;
            run.FinishedDay = DateOnly.FromDateTime(finishedAt.UtcDateTime);
            var coverage = result.Coverage;
            log?.LogInformation(
                "Usage analysis succeeded for {Slug} in {Turns} turns, {Tools} tools, {Input}+{Output} tokens, ${Cost:F4}; surface {Surface}/{SurfaceTotal}, call sites {SitesRead}/{Sites}, {Rounds} coverage round(s), {SymbolTools} symbol tool call(s).",
                app.Slug, result.Turns, result.ToolCalls, input, output, run.CostUsd,
                coverage?.SurfaceInspected ?? 0, coverage?.SurfaceEntries ?? 0,
                coverage?.TracedCallSitesRead ?? 0, coverage?.TracedCallSites ?? 0,
                coverage?.Rounds ?? 0, coverage?.SymbolToolCalls ?? 0);
        }
        else
        {
            var terminal = run.Attempts >= Math.Max(1, options.RetryMaxAttempts);
            run.Status = terminal ? UsageAnalysisStatus.Failed : UsageAnalysisStatus.Pending;
            run.Error = Truncate(error ?? "analysis failed", 1024);
            run.NextAttemptAt = finishedAt + options.RetryBackoff * Math.Max(1, run.Attempts);
            run.FinishedAt = terminal ? finishedAt : null;
            run.FinishedDay = terminal ? DateOnly.FromDateTime(finishedAt.UtcDateTime) : null;
            log?.LogInformation(
                "Usage analysis attempt {Attempt} for {Slug} failed: {Error}",
                run.Attempts, app.Slug, run.Error);
        }

        if (logs is not null)
        {
            // The transcript page is the main debugging artifact, so it is
            // written even for failures and without the run cancellation token.
            run.LogFile = await logs.WriteAsync(run, app, version, finishedAt, result, error, CancellationToken.None);
            if (run.LogFile is not null)
            {
                log?.LogDebug("Usage analysis log for {Slug}: {File}.", app.Slug, run.LogFile);
            }
        }

        await db.SaveChangesAsync(ct);

        var succeeded = run.Status == UsageAnalysisStatus.Succeeded;
        session.Event(
            succeeded ? JobEventLevel.Info : JobEventLevel.Warning,
            JobEventType.AiResult,
            succeeded
                ? $"usage report ready for {app.Slug}"
                : $"usage analysis attempt {run.Attempts} failed: {run.Error}",
            data: new
            {
                slug = app.Slug,
                attempt = run.Attempts,
                retrying = run.Status == UsageAnalysisStatus.Pending,
                turns = result?.Turns,
                toolCalls = result?.ToolCalls,
                inputTokens = input,
                cachedInputTokens = cached,
                outputTokens = output,
                costUsd = run.CostUsd,
                surface = result?.Coverage?.SurfaceInspected,
                surfaceTotal = result?.Coverage?.SurfaceEntries,
                callSitesRead = result?.Coverage?.TracedCallSitesRead,
                callSites = result?.Coverage?.TracedCallSites,
                rounds = result?.Coverage?.Rounds,
            });

        await session.FinishAsync(new JobFinish(
            succeeded ? JobStatus.Succeeded : JobStatus.Failed,
            Summary: succeeded
                ? $"usage analysis for {app.Slug}: {result?.Turns} turns, ${run.CostUsd:F4}"
                : $"attempt {run.Attempts} for {app.Slug} failed: {run.Error}",
            Error: succeeded ? null : run.Error,
            ItemsTotal: 1,
            ItemsOk: succeeded ? 1 : 0,
            ItemsFailed: succeeded ? 0 : 1,
            Reference: run.RepoCommit,
            Metadata: new
            {
                slug = app.Slug,
                appId = app.Id,
                attempt = run.Attempts,
                retrying = run.Status == UsageAnalysisStatus.Pending,
                model = options.Model,
                promptVersion = options.PromptVersion,
                turns = result?.Turns,
                toolCalls = run.ToolCalls,
                inputTokens = input,
                cachedInputTokens = cached,
                outputTokens = output,
                costUsd = run.CostUsd,
                forge = run.RepoForge,
                commit = run.RepoCommit,
                @ref = run.RepoRef,
                logFile = run.LogFile,
            }));
        return true;
    }

    /// <summary>
    /// Executes a tag-only run against the app's stored report. No snapshot or
    /// tool work happens here, so the run is cheap and safe to backfill.
    /// </summary>
    private async Task<bool> RunTaggingAsync(
        UsageAnalysisRun run, App app, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = _jobs.Begin(new JobStart(
            JobKind.UsageAnalysis,
            run.Trigger ?? JobTrigger.Auto,
            now,
            Metadata: new { slug = app.Slug, appId = app.Id, kind = "tagging", attempt = run.Attempts },
            UsageAnalysisRunId: run.Id));
        session.Phase("tag", $"classifying {app.Slug}", new { app = app.Slug, commit = app.UsageCommit });

        UseCaseTagOutcome outcome;
        try
        {
            outcome = await tagger.TagAsync(app, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Use case tagging failed for {Slug}.", app.Slug);
            outcome = new UseCaseTagOutcome(false, true, ex.Message, 0, 0, 0, 0, 0, 0, false);
        }

        var finishedAt = DateTimeOffset.UtcNow;
        run.InputTokens += outcome.InputTokens;
        run.CachedInputTokens += outcome.CachedInputTokens;
        run.OutputTokens += outcome.OutputTokens;
        run.CostUsd += ComputeCost(outcome.InputTokens, outcome.CachedInputTokens, outcome.OutputTokens, options);

        if (outcome.Succeeded)
        {
            run.Status = UsageAnalysisStatus.Succeeded;
            run.Error = null;
            run.FinishedAt = finishedAt;
            run.FinishedDay = DateOnly.FromDateTime(finishedAt.UtcDateTime);
            log?.LogInformation(
                "Use case tagging succeeded for {Slug}: {Tags} tag(s), {Promotions} promotion(s), {Input}+{Output} tokens, ${Cost:F4}.",
                app.Slug, outcome.TagCount, outcome.Promotions, outcome.InputTokens, outcome.OutputTokens, run.CostUsd);
        }
        else
        {
            var terminal = !outcome.Retryable || run.Attempts >= Math.Max(1, options.RetryMaxAttempts);
            run.Status = terminal ? UsageAnalysisStatus.Failed : UsageAnalysisStatus.Pending;
            run.Error = Truncate(outcome.Error ?? "tagging failed", 1024);
            run.NextAttemptAt = finishedAt + options.RetryBackoff * Math.Max(1, run.Attempts);
            run.FinishedAt = terminal ? finishedAt : null;
            run.FinishedDay = terminal ? DateOnly.FromDateTime(finishedAt.UtcDateTime) : null;
            log?.LogInformation(
                "Use case tagging attempt {Attempt} for {Slug} failed: {Error}",
                run.Attempts, app.Slug, run.Error);
        }

        await db.SaveChangesAsync(ct);

        var succeeded = run.Status == UsageAnalysisStatus.Succeeded;
        session.Event(
            succeeded ? JobEventLevel.Info : JobEventLevel.Warning,
            succeeded ? JobEventType.AiResult : JobEventType.AiValidation,
            succeeded
                ? $"use case tags ready for {app.Slug}"
                : $"use case tagging attempt {run.Attempts} failed: {run.Error}",
            data: new
            {
                slug = app.Slug,
                kind = "tagging",
                attempt = run.Attempts,
                retrying = run.Status == UsageAnalysisStatus.Pending,
                tags = outcome.TagCount,
                promotions = outcome.Promotions,
                inputTokens = outcome.InputTokens,
                cachedInputTokens = outcome.CachedInputTokens,
                outputTokens = outcome.OutputTokens,
                costUsd = run.CostUsd,
            });

        await session.FinishAsync(new JobFinish(
            succeeded ? JobStatus.Succeeded : JobStatus.Failed,
            Summary: succeeded
                ? $"use case tagging for {app.Slug}: {outcome.TagCount} tag(s), ${run.CostUsd:F4}"
                : $"tagging attempt {run.Attempts} for {app.Slug} failed: {run.Error}",
            Error: succeeded ? null : run.Error,
            ItemsTotal: 1,
            ItemsOk: succeeded ? 1 : 0,
            ItemsFailed: succeeded ? 0 : 1,
            Metadata: new
            {
                slug = app.Slug,
                appId = app.Id,
                kind = "tagging",
                attempt = run.Attempts,
                retrying = run.Status == UsageAnalysisStatus.Pending,
                model = options.Model,
                promptVersion = options.TagPromptVersion,
                tags = outcome.TagCount,
                promotions = outcome.Promotions,
                inputTokens = outcome.InputTokens,
                cachedInputTokens = outcome.CachedInputTokens,
                outputTokens = outcome.OutputTokens,
                costUsd = run.CostUsd,
            }));
        return true;
    }

    private void EnqueueTaggingRetry(App app, string? error, DateTimeOffset now)
    {
        db.UsageAnalysisRuns.Add(new UsageAnalysisRun
        {
            AppId = app.Id,
            Kind = UsageAnalysisKind.Tagging,
            Status = UsageAnalysisStatus.Pending,
            Trigger = JobTrigger.Auto,
            PromptVersion = options.TagPromptVersion,
            CreatedAt = now,
            NextAttemptAt = now,
        });
        log?.LogInformation("Use case tagging retry queued for {Slug}: {Error}", app.Slug, error);
    }

    /// <summary>
    /// Cached tokens are billed at the cached rate but counted inside the
    /// input total, so only the uncached remainder pays the full input price.
    /// </summary>
    public static decimal ComputeCost(long input, long cached, long output, UsageAnalysisOptions options)
    {
        var uncached = Math.Max(0, input - Math.Max(0, cached));
        return (uncached * options.InputPricePerMillion
                + Math.Max(0, cached) * options.CachedInputPricePerMillion
                + Math.Max(0, output) * options.OutputPricePerMillion) / 1_000_000m;
    }

    private async Task<bool> IsOverBudgetAsync(DateTimeOffset now, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        if (options.MaxRunsPerDay > 0)
        {
            var starts = await db.UsageAnalysisRuns
                .Where(r => r.StartedAt != null)
                .Select(r => r.StartedAt)
                .ToListAsync(ct);
            if (starts.Count(s => s >= dayStart) >= options.MaxRunsPerDay)
            {
                return true;
            }
        }

        if (options.MonthlyBudgetUsd > 0)
        {
            var monthStart = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1), TimeSpan.Zero);
            // Materialize before summing: the SQLite test provider cannot
            // translate decimal aggregation, and a month holds few rows.
            var costs = await db.UsageAnalysisRuns
                .Where(r => r.FinishedAt != null)
                .Select(r => new { r.FinishedAt, r.CostUsd })
                .ToListAsync(ct);
            if (costs.Where(c => c.FinishedAt >= monthStart).Sum(c => c.CostUsd) >= options.MonthlyBudgetUsd)
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
