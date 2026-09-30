namespace ShizuAppStoreServer.Api;

/// <summary>
/// Prometheus scraping settings. The token comes from <c>Metrics:Token</c>,
/// the <c>SHIZU_METRICS_TOKEN</c> environment variable, or when unset the
/// admin token; without any token the <c>/metrics</c> endpoint is not mapped.
/// </summary>
public sealed class MetricsOptions
{
    public bool Enabled { get; set; } = true;

    public string? Token { get; set; }
}
