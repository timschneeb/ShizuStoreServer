using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Controllers;

/// <summary>
/// Operator endpoints for the AI source analysis: queue a backfill, inspect
/// the queue and read token/cost stats. Token-authenticated like the sync
/// webhook; never exposed to clients.
/// </summary>
[ApiController]
[Route("v1/admin/usage-analysis")]
[EnableRateLimiting("api")]
public sealed class AdminUsageAnalysisController(
    ShizuDbContext db,
    AdminOptions adminOptions,
    UsageAnalysisOptions options,
    IUsageAnalysisQueue queue) : ControllerBase
{
    /// <summary>
    /// Queues analyses. Defaults to apps that have never been analyzed;
    /// <c>stale</c> adds rows older than the current prompt/analysis
    /// generation, <c>slug</c> narrows to one app and <c>limit</c> caps the
    /// batch. <c>force</c> retries parked failures.
    /// </summary>
    [HttpPost("queue")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<UsageAnalysisQueueDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UsageAnalysisQueueDto>> Queue(
        [FromBody] UsageAnalysisQueueRequestDto? body, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var request = body ?? new UsageAnalysisQueueRequestDto();
        var added = await queue.BackfillAsync(
            request.OnlyMissing, request.Stale, request.Force, request.Slug, request.Limit, ct);
        return Accepted(new UsageAnalysisQueueDto(added));
    }

    /// <summary>
    /// Queues use case tag classification for apps with a stored report.
    /// Defaults to apps that were never tagged; <c>stale</c> also picks rows
    /// classified with an older tag prompt generation and <c>all</c> re-tags
    /// the whole catalog (used after vocabulary changes).
    /// </summary>
    [HttpPost("tag-backfill")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<UsageAnalysisQueueDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UsageAnalysisQueueDto>> TagBackfill(
        [FromBody] UsageTaggingQueueRequestDto? body, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var request = body ?? new UsageTaggingQueueRequestDto();
        var added = await queue.TaggingBackfillAsync(
            request.OnlyMissing, request.Stale, request.Force, request.All, request.Slug, request.Limit, ct);
        return Accepted(new UsageAnalysisQueueDto(added));
    }

    [HttpGet("status")]
    [ProducesResponseType<UsageAnalysisStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UsageAnalysisStatusDto>> Status(CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var now = DateTimeOffset.UtcNow;
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1), TimeSpan.Zero);
        var counts = await db.UsageAnalysisRuns
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
        var startedToday = (await db.UsageAnalysisRuns
            .Where(r => r.StartedAt != null)
            .Select(r => r.StartedAt)
            .ToListAsync(ct)).Count(s => s >= dayStart);
        var monthCosts = await db.UsageAnalysisRuns
            .Where(r => r.FinishedAt != null)
            .Select(r => new { r.FinishedAt, r.CostUsd })
            .ToListAsync(ct);
        var spentThisMonth = monthCosts.Where(c => c.FinishedAt >= monthStart).Sum(c => c.CostUsd);
        var failures = (await db.UsageAnalysisRuns
            .Where(r => r.Status == UsageAnalysisStatus.Failed)
            .Include(r => r.App)
            .ToListAsync(ct))
            .OrderByDescending(r => r.FinishedAt)
            .Take(10)
            .ToList();

        return Ok(new UsageAnalysisStatusDto(
            options.IsConfigured,
            options.Model,
            counts.GetValueOrDefault(UsageAnalysisStatus.Pending),
            counts.GetValueOrDefault(UsageAnalysisStatus.Running),
            counts.GetValueOrDefault(UsageAnalysisStatus.Succeeded),
            counts.GetValueOrDefault(UsageAnalysisStatus.Failed),
            startedToday,
            options.MaxRunsPerDay,
            spentThisMonth,
            options.MonthlyBudgetUsd,
            failures.Select(ToRun).ToList()));
    }

    [HttpGet("stats")]
    [ProducesResponseType<UsageAnalysisStatsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UsageAnalysisStatsDto>> Stats(
        [FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var window = Math.Clamp(days, 1, 365);
        var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(window - 1)), TimeSpan.Zero);
        var all = await db.UsageAnalysisRuns
            .Where(r => r.FinishedAt != null)
            .Select(r => new
            {
                r.Model,
                r.InputTokens,
                r.CachedInputTokens,
                r.OutputTokens,
                r.CostUsd,
                r.FinishedAt,
            })
            .ToListAsync(ct);
        var runs = all.Where(r => r.FinishedAt >= since).ToList();

        var dayGroups = runs
            .GroupBy(r => r.FinishedAt!.Value.UtcDateTime.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new UsageAnalysisDayDto(
                g.Key,
                g.Count(),
                g.Sum(r => r.InputTokens),
                g.Sum(r => r.OutputTokens),
                g.Sum(r => r.CostUsd)))
            .ToList();
        var modelGroups = runs
            .GroupBy(r => r.Model ?? "unknown")
            .OrderByDescending(g => g.Sum(r => r.CostUsd))
            .Select(g => new UsageAnalysisModelDto(
                g.Key,
                g.Count(),
                g.Sum(r => r.InputTokens),
                g.Sum(r => r.OutputTokens),
                g.Sum(r => r.CostUsd)))
            .ToList();

        return Ok(new UsageAnalysisStatsDto(
            runs.Count,
            runs.Sum(r => r.InputTokens),
            runs.Sum(r => r.CachedInputTokens),
            runs.Sum(r => r.OutputTokens),
            runs.Sum(r => r.CostUsd),
            dayGroups,
            modelGroups));
    }

    /// <summary>
    /// Clears the pending queue. Running rows are not touched; operators stop
    /// the service or wait for the current lane instead.
    /// </summary>
    [HttpDelete("pending")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UsageAnalysisQueueDto>> ClearPending(CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var removed = await db.UsageAnalysisRuns
            .Where(r => r.Status == UsageAnalysisStatus.Pending)
            .ExecuteDeleteAsync(ct);
        return Ok(new UsageAnalysisQueueDto(removed));
    }

    private ActionResult? Authorize()
    {
        var token = adminOptions.Token;
        if (string.IsNullOrEmpty(token))
        {
            return Problem("Usage analysis admin is not configured (missing admin token).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return AdminAuth.IsValidToken(Request, token)
            ? null
            : Problem("Invalid or missing token.", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static UsageAnalysisRunDto ToRun(UsageAnalysisRun run) => new(
        run.Id,
        run.App?.Slug,
        run.Model ?? string.Empty,
        run.Attempts,
        run.RepoCommit,
        run.Error,
        run.LogFile,
        run.InputTokens,
        run.CachedInputTokens,
        run.OutputTokens,
        run.CostUsd,
        run.ToolCalls,
        run.FinishedAt);
}
