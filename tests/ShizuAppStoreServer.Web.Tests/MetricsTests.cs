using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using ShizuAppStoreServer.Web.Tracking;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>The storefront's Prometheus endpoint is bearer-gated like the API's.</summary>
public sealed class MetricsTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task RequiresTheConfiguredBearerToken()
    {
        await factory.ResetAsync(_ => {});

        var anonymous = factory.NewClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/metrics")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/apps")).StatusCode);

        var client = factory.NewClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", WebAppFactory.MetricsToken);
        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE", body);
        Assert.Contains("http_server_request_duration_seconds", body);
        Assert.Contains("dotnet_gc_collections_total", body);
    }

    [Fact]
    public async Task AppPageViewsAreCountedBySlug()
    {
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using var views = new MetricCollector<long>(
            meterFactory, ShizuWebMetrics.MeterName, "shizu.app.views");

        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "demo", "Demo", 1));
        });

        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/apps/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/apps/demo")).StatusCode);

        await views.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(10));
        var view = Assert.Single(views.GetMeasurementSnapshot());
        Assert.Equal(1L, view.Value);
        Assert.Equal("demo", view.Tags["slug"]);
    }
}
