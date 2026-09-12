using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>App directory: filtered list plus per-slug detail.</summary>
[ApiController]
[Route("v1/apps")]
[EnableRateLimiting("api")]
public sealed class AppsController(ShizuDbContext db) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    /// <summary>
    /// Filtered app list. <c>category</c> is a category slug and includes
    /// its subcategories; <c>sort</c> is <c>updated|added|name</c>.
    /// <c>excluded</c> rows are never returned.
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "apps-list")]
    [ResponseCache(Duration = 60)]
    [ProducesResponseType<PagedAppsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedAppsDto>> List(
        [FromQuery] string? category,
        [FromQuery] string? q,
        [FromQuery] string? license,
        [FromQuery] string? listing,
        [FromQuery] string? availability,
        [FromQuery] string? type,
        [FromQuery] string? recommended,
        [FromQuery] int page = 1,
        [FromQuery(Name = "pageSize")] int pageSize = DefaultPageSize,
        [FromQuery] string? sort = "updated",
        [FromQuery] string? order = null,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return Problem("page must be >= 1.", statusCode: StatusCodes.Status400BadRequest);
        }

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Apps.AsNoTracking().Where(a => a.Availability != Availability.Excluded);

        if (category is not null)
        {
            var ids = await CategorySubtreeIdsAsync(category, ct);
            if (ids is null)
            {
                return Problem($"Unknown category '{category}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            query = query.Where(a => ids.Contains(a.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            query = query.Where(a =>
                a.Name.ToLower().Contains(needle)
                || a.Description.ToLower().Contains(needle)
                || (a.PackageName != null && a.PackageName.ToLower().Contains(needle)));
        }

        if (!string.IsNullOrWhiteSpace(license))
        {
            var want = license.Trim().ToLowerInvariant();
            query = query.Where(a => a.License != null && a.License.ToLower() == want);
        }

        if (listing is not null)
        {
            if (!ApiEnums.TryParseListing(listing, out var listingValue))
            {
                return Problem($"Invalid listing '{listing}'. Use main|closed_source.", statusCode: StatusCodes.Status400BadRequest);
            }

            query = query.Where(a => a.Listing == listingValue);
        }

        if (availability is not null)
        {
            if (!ApiEnums.TryParseAvailability(availability, out var availabilityValue))
            {
                return Problem($"Invalid availability '{availability}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            query = query.Where(a => a.Availability == availabilityValue);
        }

        if (type is not null)
        {
            if (!ApiEnums.TryParseAppType(type, out var typeValue))
            {
                return Problem($"Invalid type '{type}'. Use app|library|flow.", statusCode: StatusCodes.Status400BadRequest);
            }

            query = query.Where(a => a.Type == typeValue);
        }

        if (recommended is not null)
        {
            if (!bool.TryParse(recommended, out var recommendedValue))
            {
                return Problem($"Invalid recommended '{recommended}'. Use true|false.", statusCode: StatusCodes.Status400BadRequest);
            }

            query = query.Where(a => a.IsRecommended == recommendedValue);
        }

        var sortKey = sort?.Trim().ToLowerInvariant();
        if (sortKey is not ("updated" or "added" or "name"))
        {
            return Problem($"Invalid sort '{sort}'. Use updated|added|name.", statusCode: StatusCodes.Status400BadRequest);
        }

        var descending = order?.Trim().ToLowerInvariant() switch
        {
            null or "" => sortKey != "name",
            "desc" => true,
            "asc" => false,
            _ => (bool?)null,
        };
        if (descending is null)
        {
            return Problem($"Invalid order '{order}'. Use asc|desc.", statusCode: StatusCodes.Status400BadRequest);
        }

        // NOTE: ordering + paging run in memory, not in SQL. The SQLite
        // provider used by integration tests cannot ORDER BY DateTimeOffset
        // columns (throws NotSupportedException); Npgsql translates the same
        // LINQ fine. The catalog is tiny (~hundreds of rows), so this costs
        // nothing and keeps both providers green.
        var total = await query.CountAsync(ct);
        var rows = await query.Include(a => a.Category).ToListAsync(ct);

        var ordered = (sortKey, descending.Value) switch
        {
            ("name", true) => rows.OrderByDescending(a => a.Name).ThenBy(a => a.Id),
            ("name", false) => rows.OrderBy(a => a.Name).ThenBy(a => a.Id),
            ("added", true) => rows.OrderByDescending(a => a.AddedAt).ThenBy(a => a.Id),
            ("added", false) => rows.OrderBy(a => a.AddedAt).ThenBy(a => a.Id),
            (_, true) => rows.OrderByDescending(a => a.UpdatedAt).ThenBy(a => a.Id),
            (_, false) => rows.OrderBy(a => a.UpdatedAt).ThenBy(a => a.Id),
        };

        var items = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Ok(new PagedAppsDto(items.Select(AppMapper.ToSummary).ToList(), total, page, pageSize));
    }

    /// <summary>
    /// Full app detail. Supports <c>If-None-Match</c> (ETag derived from
    /// the row id + <c>updated_at</c>). <c>excluded</c> rows read as 404.
    /// </summary>
    [HttpGet("{slug}")]
    [OutputCache(PolicyName = "app-detail")]
    [ResponseCache(Duration = 60)]
    [ProducesResponseType<AppDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppDetailDto>> Detail(string slug, CancellationToken ct = default)
    {
        var app = await db.Apps.AsNoTracking()
            .Include(a => a.Category).ThenInclude(c => c!.Parent)
            .Include(a => a.Parent)
            .FirstOrDefaultAsync(a => a.Slug == slug && a.Availability != Availability.Excluded, ct);

        if (app is null)
        {
            return NotFound();
        }

        var etag = $"\"{app.UpdatedAt.UtcTicks}-{app.Id}\"";
        if (Request.Headers.IfNoneMatch == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        return Ok(AppMapper.ToDetail(app, AppMapper.CategoryPath(app.Category)));
    }

    /// <summary>
    /// Category ids for <paramref name="slug"/> plus all descendants.
    /// Null when the slug is unknown. The table is tiny (~15 rows), so this
    /// runs fully in memory.
    /// </summary>
    private async Task<HashSet<long>?> CategorySubtreeIdsAsync(string slug, CancellationToken ct)
    {
        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var root = categories.FirstOrDefault(c => c.Slug == slug);
        if (root is null)
        {
            return null;
        }

        var byParent = categories
            .Where(c => c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var ids = new HashSet<long> { root.Id };
        var stack = new Stack<long>();
        stack.Push(root.Id);
        while (stack.TryPop(out var id))
        {
            if (byParent.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    if (ids.Add(child))
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        return ids;
    }
}
