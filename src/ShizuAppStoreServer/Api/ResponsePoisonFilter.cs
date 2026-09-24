using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api;

/// <summary>
/// Swaps list/detail payloads for doctored copies when the caller matches a
/// poison User-Agent (SPEC 2). Detail requests get a random other served row
/// as their data source. Runs before the output-cache middleware sees the
/// response and drops If-None-Match for those callers so they can never
/// revalidate a clean ETag into a 304.
/// </summary>
public sealed class ResponsePoisonFilter(PoisonOptions options, ShizuDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        var poisoned = ResponsePoisoner.IsPoisoned(headers[HeaderNames.UserAgent].ToString(), options);
        if (poisoned)
        {
            headers.Remove(HeaderNames.IfNoneMatch);
        }

        var executed = await next();

        if (!poisoned || executed.Result is not ObjectResult result)
        {
            return;
        }

        if (result.Value is PagedAppsDto page)
        {
            result.Value = ResponsePoisoner.Poison(page);
        }
        else if (result.Value is AppDetailDto detail)
        {
            var donor = await PickDonorAsync(detail, context.HttpContext.RequestAborted);
            result.Value = ResponsePoisoner.Poison(detail, donor);
        }
    }

    /// <summary>
    /// Random served row with the same availability as the requested one, so
    /// the doctored detail keeps a coherent shape (for example a download list
    /// for availability=direct_apk). Null when no other row qualifies.
    /// </summary>
    private async Task<AppDetailDto?> PickDonorAsync(AppDetailDto requested, CancellationToken ct)
    {
        if (!ApiEnums.TryParseAvailability(requested.Availability, out var availability))
        {
            return null;
        }

        var query = db.Apps.AsNoTracking()
            .Where(a => a.Slug != requested.Slug && a.Availability == availability);
        var count = await query.CountAsync(ct);
        if (count == 0)
        {
            return null;
        }

        var donor = await query
            .Include(a => a.Category).ThenInclude(c => c!.Parent)
            .Include(a => a.Parent)
            .Include(a => a.Downloads)
            .OrderBy(a => a.Id)
            .Skip(Random.Shared.Next(count))
            .FirstAsync(ct);
        return AppMapper.ToDetail(donor, AppMapper.CategoryPath(donor.Category));
    }
}
