using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Sentry;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>App directory: filtered list plus per-slug detail.</summary>
[ApiController]
[Route("v1/apps")]
[EnableRateLimiting("api")]
public sealed class AppsController(ShizuDbContext db, ShizuMetrics metrics) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    /// <summary>
    /// Filtered app list. <c>category</c> is a category slug and includes
    /// its subcategories; <c>sort</c> is <c>updated|added|name</c>.
    /// <c>listing</c> is a comma-separated subset of
    /// <c>main|closed_source</c> (absent = <c>main</c>; the closed-source
    /// listing is opt-in). <c>excluded</c> rows are never returned.
    /// </summary>
    /// <remarks>
    /// The advanced filtering options are not actually used by the client app.
    /// It pulls all entries on first launch, then does delta syncs to keep the local DB up-to-date.
    /// Filtering is done locally on the client.
    /// </remarks>
    [HttpGet]
    [OutputCache(PolicyName = "apps-list")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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

        var query = db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && a.PublishedAt != null);

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

        var listings = ApiEnums.ParseListingSet(listing);
        if (listings is null)
        {
            return Problem(
                $"Invalid listing '{listing}'. Use main|closed_source (comma-separated).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        query = query.Where(a => listings.Contains(a.Listing));

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
        if (sortKey is not ("updated" or "added" or "name" or "stars" or "downloads"))
        {
            return Problem(
                $"Invalid sort '{sort}'. Use updated|added|name|stars|downloads.",
                statusCode: StatusCodes.Status400BadRequest);
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
        var rows = await query.Include(a => a.Category).Include(a => a.Downloads).ToListAsync(ct);

        var ordered = (sortKey, descending.Value) switch
        {
            ("name", true) => rows.OrderByDescending(a => a.Name).ThenBy(a => a.Id),
            ("name", false) => rows.OrderBy(a => a.Name).ThenBy(a => a.Id),
            ("added", true) => rows.OrderByDescending(a => a.AddedAt).ThenBy(a => a.Id),
            ("added", false) => rows.OrderBy(a => a.AddedAt).ThenBy(a => a.Id),
            ("stars", true) => rows.OrderByDescending(a => a.Stars ?? -1).ThenBy(a => a.Id),
            ("stars", false) => rows.OrderBy(a => a.Stars ?? -1).ThenBy(a => a.Id),
            ("downloads", true) => rows.OrderByDescending(a => a.DownloadTotal ?? -1).ThenBy(a => a.Id),
            ("downloads", false) => rows.OrderBy(a => a.DownloadTotal ?? -1).ThenBy(a => a.Id),
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
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<AppDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppDetailDto>> Detail(string slug, CancellationToken ct = default)
    {
        // Group traces and any failure by app.
        SentrySdk.ConfigureScope(scope => scope.SetTag("app.slug", slug));

        var app = await db.Apps.AsNoTracking()
            .Include(a => a.Category).ThenInclude(c => c!.Parent)
            .Include(a => a.Parent)
            .Include(a => a.Downloads)
            .FirstOrDefaultAsync(
                a => a.Slug == slug && a.Availability != Availability.Excluded && a.PublishedAt != null, ct);

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
    /// Records one successful client install of <paramref name="slug"/>.
    /// The counter is atomic (<c>ExecuteUpdateAsync</c> bypasses
    /// <c>SaveChanges</c>, so <c>UpdatedAt</c> never bumps and the
    /// added/updated feed does not churn; the move is visible only through
    /// <c>installsUpdated</c> in <c>/v1/changes</c>).
    /// A second write upserts the per-UTC-day row in <c>app_install_days</c>,
    /// and a third the per-version/type row in <c>app_version_install_days</c>,
    /// all in one transaction.
    /// The JSON body is optional: <c>{ "versionCode": 123, "installType":
    /// "fresh" }</c>; absent, malformed or unknown values fall back to
    /// 0/unknown so older clients keep working. <c>excluded</c> rows read as 404.
    /// </summary>
    [HttpPost("{slug}/installs")]
    [ProducesResponseType<InstallRecordedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InstallRecordedDto>> RecordInstall(string slug, CancellationToken ct = default)
    {
        var (versionCode, installType) = await ReadInstallReportAsync(ct);

        SentrySdk.ConfigureScope(scope =>
        {
            scope.SetTag("app.slug", slug);
            scope.SetTag("install.type", installType);
            scope.SetTag("install.version_code", versionCode.ToString(CultureInfo.InvariantCulture));
        });
        SentrySdk.AddBreadcrumb("install reported", "install", data: new Dictionary<string, string>
        {
            ["slug"] = slug,
            ["version_code"] = versionCode.ToString(CultureInfo.InvariantCulture),
            ["install_type"] = installType,
        });

        // ExecuteUpdate is expression-based, so the timestamp is captured
        // into a local first; both columns land in one atomic UPDATE.
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var updated = await db.Apps
            .Where(a => a.Slug == slug && a.Availability != Availability.Excluded && a.PublishedAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.InstallCount, a => a.InstallCount + 1)
                .SetProperty(a => a.InstallCountUpdatedAt, now), ct);

        if (updated == 0)
        {
            return NotFound();
        }

        var app = await db.Apps.AsNoTracking()
            .Where(a => a.Slug == slug)
            .Select(a => new { a.Id, a.InstallCount })
            .FirstAsync(ct);

        // Server UTC clock decides the bucket; clients cannot backfill days.
        // ON CONFLICT covers Postgres and the SQLite test provider.
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_install_days (app_id, day, install_count)
            VALUES ({app.Id}, {day}, 1)
            ON CONFLICT (app_id, day)
            DO UPDATE SET install_count = app_install_days.install_count + 1
            """, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_version_install_days (app_id, version_code, install_type, day, install_count)
            VALUES ({app.Id}, {versionCode}, {installType}, {day}, 1)
            ON CONFLICT (app_id, version_code, install_type, day)
            DO UPDATE SET install_count = app_version_install_days.install_count + 1
            """, ct);

        await tx.CommitAsync(ct);

        metrics.InstallReported(installType);

        return Ok(new InstallRecordedDto(slug, app.InstallCount));
    }

    /// <summary>
    /// Daily install counts and star snapshots for the detail sparkline.
    /// Installs are zero-filled across the whole window; stars carry the
    /// newest known snapshot forward and, when the app had no snapshot
    /// before the window, start at its first in-window snapshot instead.
    /// <c>excluded</c> rows read as 404.
    /// </summary>
    [HttpGet("{slug}/history")]
    [OutputCache(PolicyName = "app-history")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<AppHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppHistoryDto>> History(
        string slug,
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        if (days is < 1 or > 365)
        {
            return Problem("days must be between 1 and 365.", statusCode: StatusCodes.Status400BadRequest);
        }

        var app = await db.Apps.AsNoTracking()
            .Where(a => a.Slug == slug && a.Availability != Availability.Excluded && a.PublishedAt != null)
            .Select(a => new { a.Id })
            .FirstOrDefaultAsync(ct);
        if (app is null)
        {
            return NotFound();
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = today.AddDays(-(days - 1));
        var dayFormat = static (DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var installRows = await db.AppInstallDays.AsNoTracking()
            .Where(d => d.AppId == app.Id && d.Day >= windowStart && d.Day <= today)
            .Select(d => new { d.Day, d.InstallCount })
            .ToListAsync(ct);
        var installsByDay = installRows.ToDictionary(d => d.Day, d => d.InstallCount);
        var installs = new List<InstallDayDto>(days);
        for (var day = windowStart; day <= today; day = day.AddDays(1))
        {
            installs.Add(new InstallDayDto(dayFormat(day), installsByDay.GetValueOrDefault(day)));
        }

        // Two bounded reads instead of the full history: the last pre-window
        // snapshot seeds the carry-in, the window rows fill day by day.
        var baseRow = await db.AppStarDays.AsNoTracking()
            .Where(d => d.AppId == app.Id && d.Day < windowStart)
            .OrderByDescending(d => d.Day)
            .Select(d => new { d.Day, d.Stars })
            .FirstOrDefaultAsync(ct);
        var starRows = await db.AppStarDays.AsNoTracking()
            .Where(d => d.AppId == app.Id && d.Day >= windowStart && d.Day <= today)
            .OrderBy(d => d.Day)
            .Select(d => new { d.Day, d.Stars })
            .ToListAsync(ct);

        var stars = new List<StarDayDto>(starRows.Count + 1);
        var pointerDay = baseRow?.Day ?? DateOnly.MaxValue;
        var pointerStars = baseRow?.Stars ?? 0;
        var starIndex = 0;
        for (var day = windowStart; day <= today; day = day.AddDays(1))
        {
            while (starIndex < starRows.Count && starRows[starIndex].Day <= day)
            {
                pointerDay = starRows[starIndex].Day;
                pointerStars = starRows[starIndex].Stars;
                starIndex++;
            }

            if (pointerDay > day)
            {
                // No snapshot exists yet on this day (first-ever one lands
                // mid-window); omit leading days rather than invent values.
                continue;
            }

            stars.Add(new StarDayDto(dayFormat(day), pointerStars));
        }

        return Ok(new AppHistoryDto(slug, DateTimeOffset.UtcNow, installs, stars));
    }

    /// <summary>
    /// Optional JSON body of an install report. Anything unparseable or out of
    /// range degrades to 0/unknown instead of failing the install count.
    /// </summary>
    private async Task<(long VersionCode, string InstallType)> ReadInstallReportAsync(CancellationToken ct)
    {
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);

        long versionCode = 0;
        string? installType = null;
        try
        {
            using var json = JsonDocument.Parse(body.ToArray());
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("versionCode", out var versionProp)
                    && versionProp.ValueKind == JsonValueKind.Number
                    && versionProp.TryGetInt64(out var parsed)
                    && parsed > 0)
                {
                    versionCode = parsed;
                }

                if (json.RootElement.TryGetProperty("installType", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String)
                {
                    installType = typeProp.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // An empty or non-JSON body is a valid older-client report.
        }

        return (versionCode, AppVersionInstallDay.NormalizeInstallType(installType));
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
            if (!byParent.TryGetValue(id, out var children))
            {
                continue;
            }

            foreach (var child in children.Where(ids.Add))
            {
                stack.Push(child);
            }
        }

        return ids;
    }
}
