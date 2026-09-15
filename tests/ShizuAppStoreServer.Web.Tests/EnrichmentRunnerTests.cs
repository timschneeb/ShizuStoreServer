using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Sync;
using Xunit;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Production runner path (real DI scopes, stubbed upstreams): an
/// infrastructure failure outside the enricher must record the row
/// error and surface the message, never fail silent.
/// </summary>
public sealed class EnrichmentRunnerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _iconDir = Path.Combine(Path.GetTempPath(), "shizu-runner-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_iconDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class BoomGitHub : IGitHubReleaseClient
    {
        public Task<SourceRelease?> GetLatestReleaseAsync(
            SourceTarget target, string? etag, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("no network");
    }

    private sealed class FakeAapt2 : IAapt2Runner
    {
        public Task<string> DumpBadgingAsync(string apkPath, CancellationToken ct = default) =>
            throw new InvalidOperationException("unreached");
    }

    private sealed class FakeSigner : IApkSignerRunner
    {
        public Task<string> PrintCertsAsync(string apkPath, CancellationToken ct = default) =>
            throw new InvalidOperationException("unreached");
    }

    [Fact]
    public async Task RunnerCrashRecordsRowErrorAndMessage()
    {
        _connection.Open();
        using var provider = new ServiceCollection()
            .AddDbContext<ShizuDbContext>(o => o.UseSqlite(_connection))
            .AddSingleton<IGitHubReleaseClient>(new BoomGitHub())
            .AddSingleton<IGitLabReleaseClient>(new GitLabReleaseClient(new HttpClient(new DeadHandler())))
            .AddSingleton(new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(new DeadHandler()))))
            .AddSingleton<IAapt2Runner>(new FakeAapt2())
            .AddSingleton<IApkSignerRunner>(new FakeSigner())
            .AddSingleton<ILauncherIconService, LauncherIconService>()
            .AddSingleton(new HttpClient(new DeadHandler()))
            .AddSingleton(new EnrichmentOptions { IconStorePath = _iconDir })
            .AddScoped<AppEnricher>()
            .AddScoped<IEnrichmentRunner, EnrichmentRunner>()
            .BuildServiceProvider();

        long appId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            db.Database.EnsureCreated();
            var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
            db.Categories.Add(category);
            var app = new App
            {
                Slug = "runner-boom",
                Name = "RunnerBoom",
                Url = "https://github.com/example/boom",
                Listing = Listing.Main,
                Type = AppType.App,
                Category = category,
                AddedAt = T0,
                UpdatedAt = T0,
            };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            appId = app.Id;
        }

        EnrichResult result;
        using (var scope = provider.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, false, T0);
        }

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("boom", result.Error);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            Assert.Contains("boom", db.Apps.Single(a => a.Id == appId).LastError);
        }
    }
}
