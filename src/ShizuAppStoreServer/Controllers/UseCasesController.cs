using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>
/// Structured Shizuku capabilities derived from the AI usage reports
/// (<c>use_cases</c>), with the number of visible apps per case. Inactive
/// vocabulary rows and empty cases are hidden.
/// </summary>
[ApiController]
[Route("v1/use-cases")]
[EnableRateLimiting("api")]
public sealed class UseCasesController(ShizuDbContext db) : ControllerBase
{
    /// <summary>
    /// Cases ordered by app count (descending) then name. <c>listing</c> is a
    /// comma-separated subset of <c>main|closed_source</c> (absent = <c>main</c>;
    /// the closed-source listing is opt-in). Supports <c>If-None-Match</c>.
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "use-cases")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<IReadOnlyList<UseCaseCountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<UseCaseCountDto>>> List(
        [FromQuery] string? listing, CancellationToken ct = default)
    {
        var listings = ApiEnums.ParseListingSet(listing);
        if (listings is null)
        {
            return Problem(
                $"Invalid listing '{listing}'. Use main|closed_source (comma-separated).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var counts = await db.AppUseCases.AsNoTracking()
            .Where(link => link.App!.Availability != Availability.Excluded
                && link.App.PublishedAt != null
                && listings.Contains(link.App.Listing))
            .GroupBy(link => link.UseCaseId)
            .Select(g => new { UseCaseId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var countById = counts.ToDictionary(x => x.UseCaseId, x => x.Count);

        // Name-sorted (case-insensitive, id breaks ties); the items below
        // re-sort by count without dropping cases.
        var useCases = (await db.UseCases.AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.Id)
                .ToListAsync(ct))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Id)
            .ToList();

        var items = useCases
            .Select(u => new UseCaseCountDto(u.Slug, u.Name, countById.GetValueOrDefault(u.Id)))
            .Where(x => x.AppCount > 0)
            .OrderByDescending(x => x.AppCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var maxAppUpdated = (await db.Apps.AsNoTracking()
            .Where(a => a.PublishedAt != null)
            .Select(a => (DateTimeOffset?)a.UpdatedAt)
            .ToListAsync(ct)).Max();
        var maxUseCaseUpdated = useCases.Count == 0 ? null : (DateTimeOffset?)useCases.Max(u => u.UpdatedAt);

        var etag = $"\"uc-{items.Count}-{items.Sum(x => x.AppCount)}-{maxAppUpdated?.UtcTicks ?? 0}-{maxUseCaseUpdated?.UtcTicks ?? 0}\"";
        if (Request.Headers.IfNoneMatch == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        return Ok(items);
    }
}
