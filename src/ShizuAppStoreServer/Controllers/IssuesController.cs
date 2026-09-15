using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Catalog health snapshot from the latest completed sync run.</summary>
[ApiController]
[Route("v1/issues")]
[EnableRateLimiting("api")]
public sealed class IssuesController(ShizuDbContext db) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    /// <summary>
    /// Issues from the latest completed run, oldest first. The snapshot holds
    /// only one run: successful passes replace it, skipped and failed passes
    /// leave the previous good snapshot in place.
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "issues")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<IssuesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<IssuesDto>> Get(
        [FromQuery] string? kind,
        [FromQuery] string? rule,
        [FromQuery] int page = 1,
        [FromQuery(Name = "pageSize")] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return Problem("page must be >= 1.", statusCode: StatusCodes.Status400BadRequest);
        }

        IssueKind? kindValue = null;
        if (kind is not null)
        {
            if (!Enum.TryParse<IssueKind>(kind, ignoreCase: true, out var parsed))
            {
                return Problem($"Invalid kind '{kind}'. Use parse|enrich|quality.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            kindValue = parsed;
        }

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var run = await db.SyncRuns.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        var all = await db.SyncIssues.AsNoTracking().ToListAsync(ct);

        // In memory like the other controllers: tiny table, identical
        // semantics on both DB providers.
        if (kindValue.HasValue)
        {
            all = all.Where(i => i.Kind == kindValue.Value).ToList();
        }

        if (!string.IsNullOrWhiteSpace(rule))
        {
            var want = rule.Trim();
            all = all.Where(i => i.Rule == want).ToList();
        }

        all = all.OrderBy(i => i.Kind).ThenBy(i => i.Rule).ThenBy(i => i.Slug).ThenBy(i => i.Id).ToList();

        var etag = $"\"{run?.Id ?? 0}-{all.Count}\"";
        if (Request.Headers.IfNoneMatch == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;

        var total = all.Count;
        var items = all
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new IssueDto(
                ApiEnums.ToApiString(i.Kind),
                i.Rule,
                i.Slug,
                i.Message,
                i.Location))
            .ToList();

        // Summary counts cover the unfiltered snapshot, so clients can show
        // per-kind badges while paging a filtered list. Queried separately
        // because the list above may be filtered.
        var unfiltered = await db.SyncIssues.AsNoTracking().ToListAsync(ct);
        var summary = new IssueSummaryDto(
            unfiltered.Count(i => i.Kind == IssueKind.Parse),
            unfiltered.Count(i => i.Kind == IssueKind.Enrich),
            unfiltered.Count(i => i.Kind == IssueKind.Quality),
            unfiltered.Count);

        return Ok(new IssuesDto(
            run?.Id,
            run?.HeadCommit,
            DateTimeOffset.UtcNow,
            summary,
            items,
            total,
            page,
            pageSize));
    }
}
