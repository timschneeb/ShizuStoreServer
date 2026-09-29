using Microsoft.AspNetCore.OutputCaching;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>No-op policy replacing the real "pages" policy so tests always render fresh.</summary>
public sealed class NoOutputCachePolicy : IOutputCachePolicy
{
    public static readonly NoOutputCachePolicy Instance = new();

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
