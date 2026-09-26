using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class UsageAnalysisApiTests : IClassFixture<UsageAnalysisApiTests.EnabledFactory>
{
    private const string Token = "test-admin-secret";
    private readonly EnabledFactory _factory;

    public UsageAnalysisApiTests(EnabledFactory factory) => _factory = factory;

    /// <summary>Analyzer enabled but the worker replaced with a no-op so tests stay hermetic.</summary>
    public sealed class EnabledFactory : ShizuApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UsageAnalysisOptions>();
                services.AddSingleton(new UsageAnalysisOptions
                {
                    Enabled = true,
                    MaxRunsPerDay = 500,
                    MonthlyBudgetUsd = 1000m,
                });
                services.RemoveAll<IUsageAnalysisRunner>();
                services.AddScoped<IUsageAnalysisRunner, NoopRunner>();
            });
        }

        private sealed class NoopRunner : IUsageAnalysisRunner
        {
            public Task<bool> RunNextAsync(CancellationToken ct = default) => Task.FromResult(false);

            public Task<int> RecoverInterruptedAsync(CancellationToken ct = default) => Task.FromResult(0);
        }
    }

    private static HttpClient Admin(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    [Fact]
    public async Task DetailCarriesTheAiUsageReport()
    {
        await _factory.ResetAsync(db =>
        {
            var app = Seeds.NewApp("usage", Seeds.NewCategory("apps"), availability: Availability.DirectApk);
            app.UsageShort = "Can install apps using PackageManager.";
            app.UsageMarkdown = "Installs via `PackageManager` (`Installer.kt:3`).";
            app.UsageAnalyzedAt = DateTimeOffset.UtcNow;
            app.UsageModel = "mimo-v2.6-flash";
            db.Apps.Add(app);
        });

        var response = await _factory.NewClient().GetAsync("/v1/apps/usage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>())!;
        Assert.Equal("Can install apps using PackageManager.", detail.UsageShort);
        Assert.Contains("Installer.kt:3", detail.UsageMarkdown);
        Assert.NotNull(detail.UsageAnalyzedAt);

        // The heuristic fields are gone from the wire shape.
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"managers\"", json);
        Assert.DoesNotContain("\"usageSummary\"", json);
        Assert.DoesNotContain("\"signals\"", json);
    }

    [Fact]
    public async Task QueueRequiresTokenAndQueuesUnanalyzedApps()
    {
        await _factory.ResetAsync(db =>
        {
            db.Categories.Add(Seeds.NewCategory("apps"));
            db.Apps.Add(Seeds.NewApp("queued", db.Categories.Local.Single(), availability: Availability.DirectApk));
            db.Apps.Add(Seeds.NewApp("norepo", db.Categories.Local.Single(), url: "https://example.com/app",
                availability: Availability.DirectApk));
        });

        var unauthorized = await _factory.NewClient().PostAsJsonAsync(
            "/v1/admin/usage-analysis/queue", new UsageAnalysisQueueRequestDto());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var response = await Admin(_factory.NewClient()).PostAsJsonAsync(
            "/v1/admin/usage-analysis/queue", new UsageAnalysisQueueRequestDto());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UsageAnalysisQueueDto>())!;
        Assert.Equal(1, result.Queued);

        var run = await _factory.QueryAsync(db => Task.FromResult(db.UsageAnalysisRuns.Single()));
        Assert.Equal(UsageAnalysisStatus.Pending, run.Status);
        Assert.Equal(_factory.Services.GetRequiredService<UsageAnalysisOptions>().PromptVersion, run.PromptVersion);
    }

    [Fact]
    public async Task StatusAndStatsReportQueueAndCost()
    {
        await _factory.ResetAsync(db =>
        {
            var app = Seeds.NewApp("status", Seeds.NewCategory("apps"), availability: Availability.DirectApk);
            db.Apps.Add(app);
            db.UsageAnalysisRuns.AddRange(
                new UsageAnalysisRun
                {
                    App = app,
                    Status = UsageAnalysisStatus.Succeeded,
                    Attempts = 1,
                    Model = "mimo-v2.6-flash",
                    InputTokens = 1000,
                    CachedInputTokens = 200,
                    OutputTokens = 300,
                    CostUsd = 0.01m,
                    CreatedAt = DateTimeOffset.UtcNow,
                    StartedAt = DateTimeOffset.UtcNow,
                    FinishedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                },
                new UsageAnalysisRun
                {
                    App = app,
                    Status = UsageAnalysisStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                });
        });

        var status = await Admin(_factory.NewClient())
            .GetFromJsonAsync<UsageAnalysisStatusDto>("/v1/admin/usage-analysis/status");
        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.Equal(1, status.Pending);
        Assert.Equal(1, status.Succeeded);
        Assert.Equal(1, status.StartedToday);
        Assert.Equal(0.01m, status.SpentThisMonthUsd);

        var stats = await Admin(_factory.NewClient())
            .GetFromJsonAsync<UsageAnalysisStatsDto>("/v1/admin/usage-analysis/stats");
        Assert.NotNull(stats);
        Assert.Equal(1, stats.Runs);
        Assert.Equal(1000, stats.InputTokens);
        Assert.Equal(0.01m, stats.CostUsd);
        var model = Assert.Single(stats.Models);
        Assert.Equal("mimo-v2.6-flash", model.Model);
    }

    [Fact]
    public async Task ClearPendingRemovesOnlyPendingRuns()
    {
        await _factory.ResetAsync(db =>
        {
            var app = Seeds.NewApp("clear", Seeds.NewCategory("apps"), availability: Availability.DirectApk);
            db.Apps.Add(app);
            db.UsageAnalysisRuns.AddRange(
                new UsageAnalysisRun
                {
                    App = app, Status = UsageAnalysisStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
                },
                new UsageAnalysisRun
                {
                    App = app, Status = UsageAnalysisStatus.Succeeded, Attempts = 1,
                    CreatedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                });
        });

        var response = await Admin(_factory.NewClient()).DeleteAsync("/v1/admin/usage-analysis/pending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UsageAnalysisQueueDto>())!;
        Assert.Equal(1, result.Queued);
        Assert.DoesNotContain(await _factory.QueryAsync(db => Task.FromResult(db.UsageAnalysisRuns.ToList())),
            r => r.Status == UsageAnalysisStatus.Pending);
    }
}
