using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Category tree with per-node app counts (excludes <c>excluded</c> rows).</summary>
[ApiController]
[Route("v1/categories")]
[EnableRateLimiting("api")]
public sealed class CategoriesController(ShizuDbContext db) : ControllerBase
{
    /// <summary>
    /// Full tree. <c>appCount</c> is the subtree total (node + descendants).
    /// <c>listing</c> is a comma-separated subset of
    /// <c>main|closed_source</c> (absent = <c>main</c>; the closed-source
    /// listing is opt-in). Supports <c>If-None-Match</c>.
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "categories")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<IReadOnlyList<CategoryNodeDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<CategoryNodeDto>>> Tree(
        [FromQuery] string? listing, CancellationToken ct = default)
    {
        var listings = ApiEnums.ParseListingSet(listing);
        if (listings is null)
        {
            return Problem(
                $"Invalid listing '{listing}'. Use main|closed_source (comma-separated).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Name-sorted per level (case-insensitive, id breaks ties); the tree
        // build below preserves this order for roots and children alike.
        var categories = (await db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .ToList();
        var counts = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && listings.Contains(a.Listing))
            .GroupBy(a => a.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var direct = counts.ToDictionary(x => x.CategoryId, x => x.Count);

        var childrenByParent = categories
            .Where(c => c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Subtree totals via DFS (table is tiny; no id-ordering assumptions).
        var totals = new Dictionary<long, int>();
        int Total(long id)
        {
            if (totals.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var sum = direct.GetValueOrDefault(id);
            if (childrenByParent.TryGetValue(id, out var kids))
            {
                sum += kids.Sum(k => Total(k.Id));
            }

            totals[id] = sum;
            return sum;
        }

        CategoryNodeDto Build(Category c) => new(
            c.Slug,
            c.Name,
            ApiEnums.ToApiString(c.Section),
            Total(c.Id),
            childrenByParent.TryGetValue(c.Id, out var kids)
                ? kids.Select(Build).ToList()
                : []);

        var roots = categories.Where(c => !c.ParentId.HasValue).Select(Build).ToList();
        var maxUpdated = (await db.Apps.AsNoTracking()
            .Select(a => (DateTimeOffset?)a.UpdatedAt)
            .ToListAsync(ct)).Max();
        
        var etag = $"\"cat-{categories.Count}-{totals.Values.Sum()}-{maxUpdated?.UtcTicks ?? 0}\"";
        if (Request.Headers.IfNoneMatch == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        return Ok(roots);
    }
}
