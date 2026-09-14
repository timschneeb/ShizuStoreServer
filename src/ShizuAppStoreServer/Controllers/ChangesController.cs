using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Incremental-sync delta feed over the catalog.</summary>
[ApiController]
[Route("v1/changes")]
[EnableRateLimiting("api")]
public sealed class ChangesController(ShizuDbContext db) : ControllerBase
{
    /// <summary>
    /// Apps added/updated plus slugs removed since <c>since</c> (ISO-8601, required).
    /// <c>added = added_at >= since</c>
    /// <c>updated = updated_at >= since</c> excluding already-added rows;
    /// <c>removed</c> = tombstones with <c>removed_at >= since</c>.
    /// Entries are oldest-first so clients can apply them in order.
    /// <c>excluded</c> rows never appear.
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "changes")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ChangesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChangesDto>> Get(
        [FromQuery] string? since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(since)
            || !DateTimeOffset.TryParse(since, out var sinceValue))
        {
            return Problem("Query parameter 'since' is required (ISO-8601).", statusCode: StatusCodes.Status400BadRequest);
        }

        // NOTE: the since-comparisons AND the oldest-first ordering run in
        // memory (see AppsController: SQLite cannot compare or ORDER BY
        // DateTimeOffset in SQL; Npgsql can). Delta sets are small and this
        // endpoint is output-cached for 30 s.
        var all = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded)
            .Include(a => a.Category)
            .Include(a => a.Downloads)
            .ToListAsync(ct);

        var added = all
            .Where(a => a.AddedAt >= sinceValue)
            .OrderBy(a => a.UpdatedAt).ThenBy(a => a.Id)
            .ToList();

        var updated = all
            .Where(a => a.UpdatedAt >= sinceValue && a.AddedAt < sinceValue)
            .OrderBy(a => a.UpdatedAt).ThenBy(a => a.Id)
            .ToList();

        var removed = (await db.RemovedApps.AsNoTracking().ToListAsync(ct))
            .Where(t => t.RemovedAt >= sinceValue)
            .OrderBy(t => t.RemovedAt).ThenBy(t => t.Id)
            .ToList();

        return Ok(new ChangesDto(
            added.Select(AppMapper.ToSummary).ToList(),
            updated.Select(AppMapper.ToSummary).ToList(),
            removed.Select(AppMapper.ToRemoved).ToList()));
    }
}
