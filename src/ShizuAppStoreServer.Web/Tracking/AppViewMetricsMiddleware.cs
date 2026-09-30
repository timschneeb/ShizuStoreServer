namespace ShizuAppStoreServer.Web.Tracking;

/// <summary>
/// Counts successful app page views. Registered before <c>UseOutputCache</c>
/// so cached pages are counted too, and records after the response so the 404
/// placeholder stays out.
/// </summary>
public sealed class AppViewMetricsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ShizuWebMetrics metrics)
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
