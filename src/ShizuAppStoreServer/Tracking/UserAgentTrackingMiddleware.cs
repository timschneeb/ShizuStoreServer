using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Buffers one hit per tracked request. Registered before <c>UseOutputCache</c>
/// so cache hits are counted too, and records after the response so
/// rate-limited (429) requests stay out of the stats. Scope is client usage:
/// <c>/healthz</c>, <c>/icons/*</c> and operator <c>/v1/admin/*</c> are not
/// tracked. Requests without a User-Agent header are skipped.
/// </summary>
public sealed class UserAgentTrackingMiddleware(RequestDelegate next)
{
    private const int MaxPathLength = 512;

    public async Task InvokeAsync(HttpContext context, UserAgentTracker tracker)
    {
        await next(context);

        if (!IsTracked(context.Request.Path)
            || context.Response.StatusCode == StatusCodes.Status429TooManyRequests)
        {
            return;
        }

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return;
        }

        tracker.Record(new UserAgentHit(
            Truncate(userAgent, ClientUserAgent.MaxUserAgentLength),
            Truncate(context.Request.Path.Value ?? "/", MaxPathLength),
            DateTimeOffset.UtcNow));
    }

    private static bool IsTracked(PathString path) =>
        path.StartsWithSegments("/v1") && !path.StartsWithSegments("/v1/admin");

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
