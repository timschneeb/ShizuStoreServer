using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Puts AI source analysis on the work queue. Enqueue policy: only apps that
/// have never been analyzed, or whose artifact changed (new release). Routine
/// enrichment passes therefore never re-analyze an unchanged app, and the
/// initial catalog is populated through the explicit admin backfill. Root and
/// variant app rows are analyzed on their own, so a variant release refreshes
/// the variant report.
/// </summary>
public interface IUsageAnalysisQueue
{
    /// <summary>
    /// Queues a run when the app is eligible and nothing is active. Never
    /// throws for an ineligible app; returns true when a run was added.
    /// </summary>
    Task<bool> EnqueueAsync(App app, bool artifactChanged, bool firstAnalysis, CancellationToken ct = default);

    /// <summary>
    /// Operator backfill. <paramref name="onlyMissing"/> queues only apps with
    /// no stored analysis; <paramref name="includeStale"/> queues apps whose
    /// analysis predates the current prompt/analysis generation. Returns the
    /// number of runs added.
    /// </summary>
    Task<int> BackfillAsync(
        bool onlyMissing, bool includeStale, bool force, string? slug, int? limit, CancellationToken ct = default);
}

public sealed class UsageAnalysisQueue(
    ShizuDbContext db,
    UsageAnalysisOptions options,
    ILogger<UsageAnalysisQueue>? log = null) : IUsageAnalysisQueue
{
    private readonly HashSet<long> _queuedThisScope = [];

    public async Task<bool> EnqueueAsync(
        App app, bool artifactChanged, bool firstAnalysis, CancellationToken ct = default)
    {
        if (!options.IsConfigured || (!artifactChanged && !firstAnalysis))
        {
            return false;
        }

        if (!IsEligible(app))
        {
            return false;
        }

        if (_queuedThisScope.Contains(app.Id))
        {
            return false;
        }

        var active = await db.UsageAnalysisRuns.AnyAsync(
            r => r.AppId == app.Id && (r.Status == UsageAnalysisStatus.Pending || r.Status == UsageAnalysisStatus.Running), ct);
        if (active)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        db.UsageAnalysisRuns.Add(new UsageAnalysisRun
        {
            AppId = app.Id,
            Status = UsageAnalysisStatus.Pending,
            Trigger = JobTrigger.Auto,
            PromptVersion = options.PromptVersion,
            CreatedAt = now,
            NextAttemptAt = now,
        });
        _queuedThisScope.Add(app.Id);
        JobContext.Current?.Decision(
            $"usage analysis queued for {app.Slug}",
            new { appId = app.Id, artifactChanged, firstAnalysis });
        log?.LogDebug("Usage analysis queued for {Slug} (artifactChanged={Changed}, firstAnalysis={First}).",
            app.Slug, artifactChanged, firstAnalysis);
        return true;
    }

    public async Task<int> BackfillAsync(
        bool onlyMissing, bool includeStale, bool force, string? slug, int? limit, CancellationToken ct = default)
    {
        if (!options.IsConfigured)
        {
            return 0;
        }

        var query = db.Apps
            .Where(a => a.Availability != Availability.Excluded && a.PublishedAt != null);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            query = query.Where(a => a.Slug == slug);
        }

        var apps = await query.OrderBy(a => a.Id).ToListAsync(ct);
        var active = await db.UsageAnalysisRuns
            .Where(r => r.Status == UsageAnalysisStatus.Pending || r.Status == UsageAnalysisStatus.Running)
            .Select(r => r.AppId)
            .Distinct()
            .ToListAsync(ct);
        var activeSet = active.ToHashSet();

        // A parked failure should not be re-queued by every operator call
        // unless the operator explicitly asks for a forced retry.
        var parked = force
            ? new List<long>()
            : await db.UsageAnalysisRuns
                .Where(r => r.Status == UsageAnalysisStatus.Failed && r.Attempts >= options.RetryMaxAttempts)
                .Select(r => r.AppId)
                .Distinct()
                .ToListAsync(ct);
        var parkedSet = parked.ToHashSet();

        var max = limit is > 0 ? limit.Value : int.MaxValue;
        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var app in apps)
        {
            if (added >= max)
            {
                break;
            }

            if (!IsEligible(app) || activeSet.Contains(app.Id) || parkedSet.Contains(app.Id))
            {
                continue;
            }

            var missing = app.UsageAnalyzedAt is null;
            var stale = app.UsageAnalysisVersion < options.AnalysisVersion
                || app.UsagePromptVersion < options.PromptVersion;
            if (!(onlyMissing && missing) && !(includeStale && stale))
            {
                continue;
            }

            db.UsageAnalysisRuns.Add(new UsageAnalysisRun
            {
                AppId = app.Id,
                Status = UsageAnalysisStatus.Pending,
                Trigger = JobTrigger.Backfill,
                PromptVersion = options.PromptVersion,
                CreatedAt = now,
                NextAttemptAt = now,
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        log?.LogInformation(
            "Usage analysis backfill queued {Count} runs (onlyMissing={OnlyMissing}, stale={Stale}, force={Force}).",
            added, onlyMissing, includeStale, force);
        return added;
    }

    /// <summary>Direct-APK app (root or variant) with a GitHub/GitLab repo to read.</summary>
    private static bool IsEligible(App app) =>
        app.Id != 0
        && app.Availability == Availability.DirectApk
        && RepoScreenshotResolver.TryParseRepo(app.Url, app.SourceUrl, out _);
}
