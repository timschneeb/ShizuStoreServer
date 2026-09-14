using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Dataset metadata for clients (sync bootstrap info).</summary>
[ApiController]
[Route("v1/meta")]
[EnableRateLimiting("api")]
public sealed class MetaController(ShizuDbContext db) : ControllerBase
{
    [HttpGet]
    [OutputCache(PolicyName = "meta")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<MetaDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MetaDto>> Get(CancellationToken ct = default)
    {
        var listCommit = await db.SyncRuns.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Select(r => r.HeadCommit)
            .FirstOrDefaultAsync(ct);

        var appCount = await db.Apps.AsNoTracking()
            .CountAsync(a => a.Availability != Availability.Excluded, ct);
        var categoryCount = await db.Categories.AsNoTracking().CountAsync(ct);

        return Ok(new MetaDto(DateTimeOffset.UtcNow, listCommit, new CountsDto(appCount, categoryCount)));
    }
}
