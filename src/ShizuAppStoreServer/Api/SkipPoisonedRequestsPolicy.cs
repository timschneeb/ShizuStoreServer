using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace ShizuAppStoreServer.Api;

/// <summary>
/// Keeps doctored responses out of the output cache in both directions:
/// poisoned callers never read cached clean entries and never store their
/// own doctored entries (SPEC 2).
/// </summary>
public sealed class SkipPoisonedRequestsPolicy(PoisonOptions options) : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var headers = context.HttpContext.Request.Headers;
        if (ResponsePoisoner.IsPoisoned(headers[HeaderNames.UserAgent].ToString(), options))
        {
            context.EnableOutputCaching = false;
            context.AllowCacheLookup = false;
            context.AllowCacheStorage = false;
            context.AllowLocking = false;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
