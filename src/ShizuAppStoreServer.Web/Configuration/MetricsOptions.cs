namespace ShizuAppStoreServer.Web.Configuration;

/// <summary>
/// Prometheus scraping settings. The token comes from <c>Metrics:Token</c> or
/// the <c>SHIZU_METRICS_TOKEN</c> environment variable; without a token the
/// <c>/metrics</c> endpoint is not mapped.
/// </summary>
public sealed class MetricsOptions
{
    public bool Enabled { get; set; } = true;

    public string? Token { get; set; }
}
