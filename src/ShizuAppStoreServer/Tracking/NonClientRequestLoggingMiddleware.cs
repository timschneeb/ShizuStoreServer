using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Records every request whose User-Agent is not a ShizuStore client, on every
/// path: icons, health, 404s and 429s included. Three exceptions stay out of
/// the log: the bare host (the browser-facing redirect to the project repo),
/// operator <c>/v1/admin/*</c> traffic, and client IPs on the configured
/// exclusion list (<c>RequestLog:ExcludedIps</c>). Registered next to the UA
/// middleware (after response compression, before the output cache), so cache
/// hits and rate-limited requests are visible too. Records after the response;
/// headers and the raw request line are stored verbatim.
/// </summary>
public sealed class NonClientRequestLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, RequestLogTracker tracker, RequestLogOptions options)
    {
        if (IsExcluded(context.Request.Path)
            || ClientUserAgentMatcher.IsClient(context.Request.Headers.UserAgent.ToString())
            || options.IsExcludedIp(ClientIp(context)))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var statusCode = context.Response.StatusCode;
        try
        {
            await next(context);
            statusCode = context.Response.StatusCode;
        }
        catch
        {
            // No exception handler in the pipeline: the 500 is what the client
            // sees, so record it before the rethrow.
            statusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            tracker.Record(Capture(context, statusCode, (int)stopwatch.ElapsedMilliseconds));
        }
    }

    // Root is a browser entry point and admin traffic is operator-only;
    // neither says anything about how the public API is used.
    private static bool IsExcluded(PathString path) =>
        path == "/" || path.StartsWithSegments("/v1/admin");

    /// <summary>
    /// Real client IP for the exclusion list. Cloudflare rewrites
    /// <c>CF-Connecting-IP</c>, so it wins over the client-supplied
    /// <c>X-Forwarded-For</c>; the socket peer is the last resort.
    /// </summary>
    private static string? ClientIp(HttpContext context)
    {
        var request = context.Request;
        return Header(request, "CF-Connecting-IP")
            ?? FirstHop(Header(request, "X-Forwarded-For"))
            ?? context.Connection.RemoteIpAddress?.ToString();
    }

    private static RequestLogHit Capture(HttpContext context, int statusCode, int durationMs)
    {
        var request = context.Request;
        var host = request.Host.HasValue ? request.Host.Value : null;
        var forwardedFor = Header(request, "X-Forwarded-For");
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(rawTarget))
        {
            rawTarget = $"{request.Path}{request.QueryString}";
        }

        return new RequestLogHit(
            DateTimeOffset.UtcNow,
            request.Method,
            request.Path.Value ?? "/",
            request.QueryString.HasValue ? request.QueryString.Value : null,
            rawTarget,
            request.Protocol,
            request.Scheme,
            host,
            BuildRawRequest(request, rawTarget, host),
            SerializeHeaders(request),
            (short)statusCode,
            durationMs,
            Header(request, HeaderNames.UserAgent),
            Header(request, "Origin"),
            context.Connection.RemoteIpAddress?.ToString(),
            // Cloudflare Tunnel terminates the connection locally, so the
            // socket peer is loopback; CF-Connecting-IP carries the real client.
            Header(request, "CF-Connecting-IP") ?? FirstHop(forwardedFor),
            forwardedFor,
            Header(request, "CF-Ray"),
            Header(request, "CF-IPCountry"),
            context.TraceIdentifier);
    }

    /// <summary>Request line plus headers as received. No body: no endpoint takes one.</summary>
    private static string BuildRawRequest(HttpRequest request, string rawTarget, string? host)
    {
        var builder = new StringBuilder();
        builder.Append(request.Method).Append(' ').Append(rawTarget).Append(' ')
            .Append(request.Protocol).Append("\r\n");
        if (host is not null)
        {
            // Kestrel moves Host out of the header collection; put it back so
            // the reconstruction matches the wire request.
            builder.Append("Host: ").Append(host).Append("\r\n");
        }

        foreach (var header in request.Headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value.ToString()).Append("\r\n");
        }

        return builder.Append("\r\n").ToString();
    }

    private static string SerializeHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return JsonSerializer.Serialize(headers);
    }

    private static string? Header(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? FirstHop(string? forwardedFor)
    {
        if (string.IsNullOrWhiteSpace(forwardedFor))
        {
            return null;
        }

        var comma = forwardedFor.IndexOf(',');
        return (comma >= 0 ? forwardedFor[..comma] : forwardedFor).Trim();
    }
}
