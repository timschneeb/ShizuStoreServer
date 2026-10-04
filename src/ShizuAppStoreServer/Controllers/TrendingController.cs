using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>
/// Fresh-install window ranking for the client's trending sort. The current
/// window is [today-days+1..today] in server UTC, compared against the
/// previous equal window; buckets come from app_version_install_days and only
/// count reports typed <c>fresh</c>, so updates and legacy unknown reports
/// never trend. Apps with no fresh installs in the window are omitted.
/// </summary>
[ApiController]
[Route("v1/trending")]
[EnableRateLimiting("api")]
public sealed class TrendingController(ShizuDbContext db) : ControllerBase
{
    private const int DefaultDays = 7;
    private const int MaxDays = 90;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    /// <summary>
    /// Ranked trending apps by fresh installs. <c>days</c> picks the window
    /// length, <c>limit</c> the result size, and <c>sort</c> is
    /// <c>installs</c> (window total) or <c>growth</c> (delta against the
    /// previous window, i.e. fastest growing).
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "trending")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<TrendingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TrendingDto>> Get(
        [FromQuery] int days = DefaultDays,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] string sort = "installs",
        CancellationToken ct = default)
    {
        if (days is < 1 or > MaxDays)
        {
            return Problem($"days must be between 1 and {MaxDays}.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (limit is < 1 or > MaxLimit)
        {
            return Problem($"limit must be between 1 and {MaxLimit}.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (sort is not ("installs" or "growth"))
        {
            return Problem("sort must be installs or growth.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Server UTC clock decides the window so every client agrees.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentStart = today.AddDays(-(days - 1));
        var previousStart = currentStart.AddDays(-days);

        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && a.PublishedAt != null)
            .Select(a => new { a.Id, a.Slug })
            .ToListAsync(ct);

        var current = await db.AppVersionInstallDays.AsNoTracking()
            .Where(v => v.InstallType == AppVersionInstallDay.Fresh && v.Day >= currentStart && v.Day <= today)
            .GroupBy(v => v.AppId)
            .Select(g => new { g.Key, Total = g.Sum(v => v.InstallCount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        var previous = await db.AppVersionInstallDays.AsNoTracking()
            .Where(v => v.InstallType == AppVersionInstallDay.Fresh && v.Day >= previousStart && v.Day < currentStart)
            .GroupBy(v => v.AppId)
            .Select(g => new { g.Key, Total = g.Sum(v => v.InstallCount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        // In-memory ordering matches List(): the SQLite test provider cannot
        // ORDER BY DateTimeOffset, and ranking needs tie-breaking by slug.
        var items = apps
            .Select(a =>
            {
                var window = current.GetValueOrDefault(a.Id);
                var before = previous.GetValueOrDefault(a.Id);
                return new TrendingItemDto(a.Slug, window, before, window - before);
            })
            .Where(i => i.Installs > 0)
            .OrderByDescending(i => sort == "growth" ? i.Delta : i.Installs)
            .ThenBy(i => i.Slug)
            .Take(limit)
            .ToList();

        return Ok(new TrendingDto(DateTimeOffset.UtcNow, days, sort, items));
    }
}
