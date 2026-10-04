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
/// the variant report. A changed artifact of a release whose report is
/// already stored (same release tag) is skipped: one release ships one run
/// even when it carries several same-package flavor artifacts.
/// </summary>
public interface IUsageAnalysisQueue
{
    /// <summary>
    /// Queues a run when the app is eligible and nothing is active. Never
    /// throws for an ineligible app; returns true when a run was added.
    /// <paramref name="releaseRef"/> is the forge release tag of the changed
    /// artifact, when one is known.
    /// </summary>
    Task<bool> EnqueueAsync(
        App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default);

    /// <summary>
    /// Queues a run for an artifact the enricher just analyzed. The call runs
    /// before the same save flips a fresh row from LinkOnly to DirectApk, so
    /// the availability gate cannot apply here; the repo demand still does.
    /// <paramref name="releaseRef"/> is the forge release tag of the analyzed
    /// artifact, when one is known.
    /// </summary>
    Task<bool> EnqueueForAnalyzedArtifactAsync(
        App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default);

    /// <summary>
    /// Operator backfill. <paramref name="onlyMissing"/> queues only apps with
    /// no stored analysis; <paramref name="includeStale"/> queues apps whose
    /// analysis predates the current prompt/analysis generation. Returns the
    /// number of runs added.
    /// </summary>
    Task<int> BackfillAsync(
        bool onlyMissing, bool includeStale, bool force, string? slug, int? limit, CancellationToken ct = default);

    /// <summary>
    /// Queues tag-only runs for apps whose stored report should be
    /// (re)classified. <paramref name="all"/> ignores the freshness checks,
    /// which is how a freshly promoted vocabulary reaches every existing
    /// report. Returns the number of runs added.
    /// </summary>
    Task<int> TaggingBackfillAsync(
        bool onlyMissing, bool includeStale, bool force, bool all, string? slug, int? limit,
        CancellationToken ct = default);
}

public sealed class UsageAnalysisQueue(
    ShizuDbContext db,
    UsageAnalysisOptions options,
    ILogger<UsageAnalysisQueue>? log = null) : IUsageAnalysisQueue
{
    private readonly HashSet<long> _queuedThisScope = [];

    public async Task<bool> EnqueueAsync(
        App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default)
    {
        if (!options.IsConfigured || (!artifactChanged && !firstAnalysis) || !IsEligible(app))
        {
            return false;
        }

        return await AddRunAsync(app, artifactChanged, firstAnalysis, releaseRef, ct);
    }

    public async Task<bool> EnqueueForAnalyzedArtifactAsync(
        App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default)
    {
        // ApplyAnalysisAsync records the download before it flips the row to
        // DirectApk, so requiring DirectApk here would silently drop every
        // first analysis of a new app. The caller only records artifacts it
        // analyzed, and those always serve a direct APK.
        if (!options.IsConfigured || (!artifactChanged && !firstAnalysis) || !HasAnalyzableRepo(app))
        {
            return false;
        }

        return await AddRunAsync(app, artifactChanged, firstAnalysis, releaseRef, ct);
    }

    private async Task<bool> AddRunAsync(
        App app, bool artifactChanged, bool firstAnalysis, string? releaseRef, CancellationToken ct)
    {
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

        // The stored report's release is the revision proxy available before
        // the runner clones the repo. Same tag means the same release even
        // when a different flavor artifact (phone/TV/Wear) triggered this
        // call, so the queue must not spend another analysis on it.
        var tag = releaseRef?.Trim();
        if (!string.IsNullOrEmpty(tag)
            && string.Equals(app.UsageReleaseRef, tag, StringComparison.Ordinal))
        {
            log?.LogDebug("Usage analysis for {Slug} skipped: release {Ref} already analyzed.", app.Slug, tag);
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

    public async Task<int> TaggingBackfillAsync(
        bool onlyMissing, bool includeStale, bool force, bool all, string? slug, int? limit,
        CancellationToken ct = default)
    {
        if (!options.IsConfigured || !options.TaggingEnabled)
        {
            return 0;
        }

        // Tagging reads stored report text, so only apps that have one and a
        // repo-derived report history are eligible. Excluded and unpublished
        // rows stay invisible to clients and out of the queue.
        var query = db.Apps
            .Where(a => a.Availability != Availability.Excluded && a.PublishedAt != null)
            .Where(a => a.UsageShort != null || a.UsageMarkdown != null);
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

        var parked = force
            ? new List<long>()
            : await db.UsageAnalysisRuns
                .Where(r => r.Kind == UsageAnalysisKind.Tagging
                    && r.Status == UsageAnalysisStatus.Failed
                    && r.Attempts >= options.RetryMaxAttempts)
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

            if (!HasAnalyzableRepo(app) || activeSet.Contains(app.Id) || parkedSet.Contains(app.Id))
            {
                continue;
            }

            var missing = app.UseCaseTagsAnalyzedAt is null;
            var stale = app.UseCaseTagsPromptVersion < options.TagPromptVersion;
            if (!all && !(onlyMissing && missing) && !(includeStale && stale))
            {
                continue;
            }

            db.UsageAnalysisRuns.Add(new UsageAnalysisRun
            {
                AppId = app.Id,
                Kind = UsageAnalysisKind.Tagging,
                Status = UsageAnalysisStatus.Pending,
                Trigger = JobTrigger.Backfill,
                PromptVersion = options.TagPromptVersion,
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
            "Use case tagging backfill queued {Count} runs (onlyMissing={OnlyMissing}, stale={Stale}, all={All}, force={Force}).",
            added, onlyMissing, includeStale, all, force);
        return added;
    }

    /// <summary>Direct-APK app (root or variant) with a GitHub/GitLab repo to read.</summary>
    private static bool IsEligible(App app) =>
        app.Availability == Availability.DirectApk && HasAnalyzableRepo(app);

    private static bool HasAnalyzableRepo(App app) =>
        app.Id != 0
        && RepoScreenshotResolver.TryParseRepo(app.Url, app.SourceUrl, out _);
}
