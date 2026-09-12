using Microsoft.AspNetCore.OutputCaching;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Output-cache policy that disables caching entirely (no lookup, no
/// storage). The test host replaces all five production policies with this
/// so tests sharing one server never read another test's seeded response.
/// Request <c>Cache-Control</c> headers do NOT bypass
/// <c>OutputCacheMiddleware</c> (it has no request-driven bypass, unlike the
/// old ResponseCaching middleware) — hence the swap, not a header.
/// </summary>
public sealed class NoOutputCachePolicy : IOutputCachePolicy
{
    public static readonly NoOutputCachePolicy Instance = new();

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        context.AllowCacheLookup = false;
        context.AllowCacheStorage = false;
        context.AllowLocking = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
