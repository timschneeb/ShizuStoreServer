namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Resolves the real client IP behind the Cloudflare Tunnel. The tunnel
/// terminates the connection on loopback, so the socket peer is never the
/// client; Cloudflare rewrites <c>CF-Connecting-IP</c>, which therefore wins
/// over the client-supplied <c>X-Forwarded-For</c>. The socket peer is the
/// last resort for callers that reach Kestrel directly. The public rate limiter
/// partitions on this value, so the guarantee depends on the origin staying
/// bound to loopback and on Cloudflare overwriting its header.
/// </summary>
public static class ClientIp
{
    /// <summary>Forwarded client IP, or the socket peer; null only when both are absent.</summary>
    public static string? Resolve(HttpContext context)
    {
        var request = context.Request;
        return Header(request, "CF-Connecting-IP")
            ?? FirstHop(Header(request, "X-Forwarded-For"))
            ?? context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>Header value, null when absent or empty.</summary>
    public static string? Header(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>First hop of an <c>X-Forwarded-For</c> chain, or null.</summary>
    public static string? FirstHop(string? forwardedFor)
    {
        if (string.IsNullOrWhiteSpace(forwardedFor))
        {
            return null;
        }

        var comma = forwardedFor.IndexOf(',');
        return (comma >= 0 ? forwardedFor[..comma] : forwardedFor).Trim();
    }
}
