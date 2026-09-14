namespace ShizuAppStoreServer.Api;

/// <summary>Public-API behavior settings (config section <c>Api</c>).</summary>
public sealed class ApiOptions
{
    /// <summary>Fixed-window rate limit, requests/minute/client IP.</summary>
    public int RateLimitPerMinute { get; set; } = 100;

    /// <summary>Server-side output caching for <c>GET /v1/*</c>. Off in tests.</summary>
    public bool EnableOutputCache { get; set; } = true;
}
