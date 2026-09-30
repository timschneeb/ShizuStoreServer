using ShizuAppStoreServer.Api;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Counts successful app detail fetches as views. Registered before
/// <c>UseOutputCache</c> so cached responses are counted too, and records
/// after the response so 404s and rate limits stay out.
/// </summary>
public sealed class AppViewMetricsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ShizuMetrics metrics)
    {
        await next(context);

        if (context.Request.Method != HttpMethods.Get
            || context.Response.StatusCode is < StatusCodes.Status200OK or >= StatusCodes.Status400BadRequest)
        {
            return;
        }

        if (context.Request.RouteValues["slug"] is string slug && slug.Length > 0)
        {
            metrics.AppViewed(slug);
        }
    }
}
