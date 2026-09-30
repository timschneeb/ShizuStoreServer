using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api;

/// <summary>
/// Custom instruments on the <c>ShizuAppStore</c> meter. Recording is a no-op
/// until a metrics listener subscribes, so callers need no Enabled check.
/// </summary>
public sealed class ShizuMetrics
{
    public const string MeterName = "ShizuAppStore";

    private readonly Counter<long> _jobRuns;
    private readonly Histogram<double> _jobDuration;
    private readonly Counter<long> _installReports;
    private readonly Counter<long> _appViews;

    public ShizuMetrics(IMeterFactory meterFactory, IServiceScopeFactory scopes, ILogger<ShizuMetrics> log)
    {
        var meter = meterFactory.Create(MeterName);
        _jobRuns = meter.CreateCounter<long>("shizu.jobs.runs", unit: "{run}",
            description: "Completed job runs by kind, trigger and status.");
        _jobDuration = meter.CreateHistogram<double>("shizu.jobs.duration", unit: "s",
            description: "Job run wall-clock duration by kind and trigger.");
        _installReports = meter.CreateCounter<long>("shizu.installs.reported", unit: "{install}",
            description: "Install reports accepted from clients, by install type.");
        // Description, name and unit are shared with the storefront's counter so
        // Prometheus sees one family across both jobs.
        _appViews = meter.CreateCounter<long>("shizu.app.views", unit: "{view}",
            description: "App detail views by slug (API fetches and webstore pages).");
        meter.CreateObservableGauge("shizu.catalog.apps", () => ObserveCatalogApps(scopes, log),
            description: "Catalog rows per availability.");
    }

    public void JobFinished(string kind, string trigger, string status, double durationSeconds)
    {
        var tags = new TagList
        {
            { "job.kind", kind },
            { "job.trigger", trigger },
            { "job.status", status },
        };
        _jobRuns.Add(1, tags);
        _jobDuration.Record(durationSeconds, tags);
    }

    public void InstallReported(string installType) =>
        _installReports.Add(1, new KeyValuePair<string, object?>("install.type", installType));

    public void AppViewed(string slug) =>
        _appViews.Add(1, new KeyValuePair<string, object?>("slug", slug));

    private static IEnumerable<Measurement<long>> ObserveCatalogApps(IServiceScopeFactory scopes, ILogger log)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var rows = db.Apps.AsNoTracking()
                .GroupBy(a => a.Availability)
                .Select(g => new { Availability = g.Key, Count = g.Count() })
                .ToList();
            return rows.Select(r => new Measurement<long>(
                r.Count,
                new KeyValuePair<string, object?>("availability", ApiEnums.ToApiString(r.Availability))));
        }
        catch (Exception ex)
        {
            // A broken collection must not fail the scrape.
            log.LogDebug(ex, "Catalog metrics collection failed");
            return [];
        }
    }
}
