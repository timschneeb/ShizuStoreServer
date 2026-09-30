using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Api.Tests;

/// <summary>
/// The Prometheus endpoint: bearer gating, the exposition format and the
/// custom ShizuAppStore instruments. Scraping is operator traffic, so it must
/// also stay out of the request log.
/// </summary>
public sealed class MetricsTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private const string Token = "test-admin-secret";

    private HttpClient ClientWithToken()
    {
        var client = factory.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    [Fact]
    public async Task RequiresTheConfiguredBearerToken()
    {
        await factory.ResetAsync(_ => { });

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await factory.NewClient().GetAsync("/metrics")).StatusCode);

        var wrong = factory.NewClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/metrics")).StatusCode);

        var response = await ClientWithToken().GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScrapeExposesExpositionText()
    {
        await factory.ResetAsync(_ => { });
        await factory.NewClient().GetAsync("/v1/meta");

        var response = await ClientWithToken().GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE target_info gauge", body);
        Assert.Contains("dotnet_gc_collections_total", body);
    }

    [Fact]
    public async Task HttpMetricsCarrySemconvTags()
    {
        await factory.ResetAsync(_ => { });

        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<double>(
            meterFactory, "Microsoft.AspNetCore.Hosting", "http.server.request.duration");

        await factory.NewClient().GetAsync("/v1/meta");

        await collector.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal("GET", measurement.Tags["http.request.method"]);
        Assert.Equal("200", measurement.Tags["http.response.status_code"]?.ToString());
        Assert.True(measurement.Value > 0);
    }

    [Fact]
    public async Task InstallReportsAreCountedByType()
    {
        // Collectors observe this host's instruments directly: scrape bodies in
        // this process aggregate every test host's meters, so per-test values
        // cannot be asserted from a body.
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using var installs = new MetricCollector<long>(
            meterFactory, ShizuMetrics.MeterName, "shizu.installs.reported");
        using var catalog = new MetricCollector<long>(
            meterFactory, ShizuMetrics.MeterName, "shizu.catalog.apps");

        await factory.ResetAsync(db =>
        {
            var category = Seeds.NewCategory("tools");
            db.Categories.Add(category);
            db.Apps.Add(Seeds.NewApp("demo", category, availability: Availability.DirectApk));
        });

        var response = await factory.NewClient().PostAsync(
            "/v1/apps/demo/installs",
            new StringContent("""{"versionCode":7,"installType":"fresh"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await installs.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        var install = Assert.Single(installs.GetMeasurementSnapshot());
        Assert.Equal(1L, install.Value);
        Assert.Equal("fresh", install.Tags["install.type"]);

        catalog.RecordObservableInstruments();
        var apps = Assert.Single(catalog.GetMeasurementSnapshot());
        Assert.Equal("direct_apk", apps.Tags["availability"]);
        Assert.Equal(1L, apps.Value);
    }

    [Fact]
    public async Task AppDetailViewsAreCountedBySlug()
    {
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using var views = new MetricCollector<long>(
            meterFactory, ShizuMetrics.MeterName, "shizu.app.views");

        await factory.ResetAsync(db =>
        {
            var category = Seeds.NewCategory("tools");
            db.Categories.Add(category);
            db.Apps.Add(Seeds.NewApp("demo", category, availability: Availability.DirectApk));
        });

        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/apps/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/apps/demo")).StatusCode);

        await views.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        var view = Assert.Single(views.GetMeasurementSnapshot());
        Assert.Equal(1L, view.Value);
        Assert.Equal("demo", view.Tags["slug"]);
    }

    [Fact]
    public async Task JobRunsAreCountedByKindTriggerAndStatus()
    {
        await factory.ResetAsync(_ => { });

        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using var runs = new MetricCollector<long>(
            meterFactory, ShizuMetrics.MeterName, "shizu.jobs.runs");
        using var duration = new MetricCollector<double>(
            meterFactory, ShizuMetrics.MeterName, "shizu.jobs.duration");

        var jobs = factory.Services.GetRequiredService<IJobLog>();
        await using (var session = jobs.Begin(
            new JobStart(JobKind.Sync, JobTrigger.Cli, DateTimeOffset.UtcNow)))
        {
            await session.FinishAsync(new JobFinish(JobStatus.Succeeded), CancellationToken.None);
        }

        await runs.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        var run = Assert.Single(runs.GetMeasurementSnapshot());
        Assert.Equal(1L, run.Value);
        Assert.Equal("sync", run.Tags["job.kind"]);
        Assert.Equal("cli", run.Tags["job.trigger"]);
        Assert.Equal("succeeded", run.Tags["job.status"]);

        await duration.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        var timing = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("sync", timing.Tags["job.kind"]);
        Assert.True(timing.Value >= 0);
    }

    [Fact]
    public async Task ScrapesAreNotWrittenToTheRequestLog()
    {
        // Drain hits buffered by earlier tests in this class before the reset.
        await RequestLogFlush.NowAsync(factory);
        await factory.ResetAsync(_ => { });

        await factory.NewClient().GetAsync("/metrics"); // 401
        await ClientWithToken().GetAsync("/metrics"); // 200

        await RequestLogFlush.NowAsync(factory);

        Assert.Empty(await factory.QueryAsync(db => db.RequestLogs.ToListAsync()));
    }
}
