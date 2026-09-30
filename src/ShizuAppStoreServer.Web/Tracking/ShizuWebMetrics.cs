using System.Diagnostics.Metrics;

namespace ShizuAppStoreServer.Web.Tracking;

/// <summary>
/// Storefront instruments on the shared <c>ShizuAppStore</c> meter so client
/// and webstore app views group under one Prometheus query. Recording is a
/// no-op until a metrics listener subscribes.
/// </summary>
public sealed class ShizuWebMetrics
{
    public const string MeterName = "ShizuAppStore";

    private readonly Counter<long> _appViews;

    public ShizuWebMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        // Description, name and unit are shared with the API's counter so
        // Prometheus sees one family across both jobs.
        _appViews = meter.CreateCounter<long>("shizu.app.views", unit: "{view}",
            description: "App detail views by slug (API fetches and webstore pages).");
    }

    public void AppViewed(string slug) =>
        _appViews.Add(1, new KeyValuePair<string, object?>("slug", slug));
}
