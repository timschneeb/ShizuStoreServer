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
    /// <c>installsUpdated</c> maps slug to install count for rows whose
    /// count moved since <c>since</c>; it never triggers refetches, clients
    /// apply it onto their stored rows directly.
    /// Entries are oldest-first so clients can apply them in order.
    /// <c>listing</c> is a comma-separated subset of
    /// <c>main|closed_source</c> (absent = <c>main</c>; the closed-source
    /// listing is opt-in) and filters added, updated, removed and
    /// <c>installsUpdated</c> alike.
    /// <c>excluded</c> rows never appear.
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "changes")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ChangesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChangesDto>> Get(
        [FromQuery] string? since,
        [FromQuery] string? listing,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(since)
            || !DateTimeOffset.TryParse(since, out var sinceValue))
        {
            return Problem("Query parameter 'since' is required (ISO-8601).", statusCode: StatusCodes.Status400BadRequest);
        }

        var listings = ApiEnums.ParseListingSet(listing);
        if (listings is null)
        {
            return Problem(
                $"Invalid listing '{listing}'. Use main|closed_source (comma-separated).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // NOTE: the since-comparisons AND the oldest-first ordering run in
        // memory (see AppsController: SQLite cannot compare or ORDER BY
        // DateTimeOffset in SQL; Npgsql can). Delta sets are small and this
        // endpoint is output-cached for 30 s.
        var all = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && listings.Contains(a.Listing))
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

        var removed = (await db.RemovedApps.AsNoTracking()
                .Where(t => listings.Contains(t.Listing))
                .ToListAsync(ct))
            .Where(t => t.RemovedAt >= sinceValue)
            .OrderBy(t => t.RemovedAt).ThenBy(t => t.Id)
            .ToList();

        var installsUpdated = all
            .Where(a => a.InstallCountUpdatedAt >= sinceValue)
            .OrderBy(a => a.InstallCountUpdatedAt).ThenBy(a => a.Id)
            .ToDictionary(a => a.Slug, a => a.InstallCount);

        return Ok(new ChangesDto(
            added.Select(AppMapper.ToSummary).ToList(),
            updated.Select(AppMapper.ToSummary).ToList(),
            removed.Select(AppMapper.ToRemoved).ToList(),
            installsUpdated));
    }
}
