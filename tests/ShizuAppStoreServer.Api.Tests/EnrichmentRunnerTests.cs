using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Sync;
using Xunit;

namespace ShizuAppStoreServer.Api.Tests;

/// <summary>
/// Production runner path (real DI scopes, stubbed upstreams): an
/// infrastructure failure outside the enricher must record the row
/// error and surface the message, never fail silent; a fresh pass on an
/// app with known stars must refresh that day's star snapshot row.
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

    private ServiceProvider BuildProvider(IGitHubReleaseClient? github = null) => new ServiceCollection()
        .AddDbContext<ShizuDbContext>(o => o.UseSqlite(_connection))
        .AddSingleton(github ?? new BoomGitHub())
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

    private sealed class BoomGitHub : IGitHubReleaseClient
    {
        public Task<SourceRelease?> GetLatestReleaseAsync(
            SourceTarget target, string? etag, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>
    /// GitHub feed that fails the release list (403, no fallbacks) but serves
    /// the stats and star history the enrichment hook reads on its way to the
    /// failure path.
    /// </summary>
    private sealed class HistoryGitHub : IGitHubReleaseClient
    {
        public List<GitHubStarWeek> StarWeeks { get; } = [];
        public int StarRequests { get; private set; }

        public Task<SourceRelease?> GetLatestReleaseAsync(
            SourceTarget target, string? etag, CancellationToken ct) =>
            throw new GitHubApiException(System.Net.HttpStatusCode.Forbidden, "history test");

        public Task<IReadOnlyList<GitHubStarWeek>?> GetStarHistoryAsync(
            string owner, string repo, int lookbackDays, int maxPages, CancellationToken ct)
        {
            StarRequests++;
            return Task.FromResult<IReadOnlyList<GitHubStarWeek>?>(StarWeeks);
        }
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
        using var provider = BuildProvider();

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

    [Fact]
    public async Task RunnerUpsertsDailyStarSnapshotWhenStarsKnown()
    {
        _connection.Open();
        using var provider = BuildProvider();

        long appId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            db.Database.EnsureCreated();
            var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
            db.Categories.Add(category);
            var app = new App
            {
                Slug = "runner-stars",
                Name = "RunnerStars",
                Url = "https://github.com/example/stars",
                Listing = Listing.Main,
                Type = AppType.App,
                Category = category,
                AddedAt = T0,
                UpdatedAt = T0,
                // Freshly checked: the enricher returns SkippedFresh before
                // touching the (dead) upstreams, so only the snapshot runs.
                LastCheckedAt = T0,
                Stars = 7,
            };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            appId = app.Id;
        }

        using (var scope = provider.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, false, T0);
            Assert.Equal(EnrichOutcome.SkippedFresh, result.Outcome);
        }

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var row = Assert.Single(db.AppStarDays.AsNoTracking().ToList());
            Assert.Equal(appId, row.AppId);
            Assert.Equal(DateOnly.FromDateTime(T0.UtcDateTime), row.Day);
            Assert.Equal(7, row.Stars);

            var app = db.Apps.Single(a => a.Id == appId);
            app.Stars = 9;
            await db.SaveChangesAsync();
        }

        // A later pass refreshes today's row instead of adding a second one.
        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, false, T0);
        }

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var row = Assert.Single(db.AppStarDays.AsNoTracking().ToList());
            Assert.Equal(9, row.Stars);
        }
    }

    [Fact]
    public async Task RunnerBackfillsStarDaysFromGitHubFeed()
    {
        _connection.Open();
        var sunday = DateOnly.FromDateTime(T0.UtcDateTime); // T0 lands on a Sunday.
        var priorSunday = sunday.AddDays(-7);
        var github = new HistoryGitHub();
        github.StarWeeks.Add(new GitHubStarWeek(priorSunday, 9, [1, 2, 3, 1, 1, 1, 0]));
        github.StarWeeks.Add(new GitHubStarWeek(sunday, 10, [10, 0, 0, 0, 0, 0, 0]));
        using var provider = BuildProvider(github);

        long appId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            db.Database.EnsureCreated();
            var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
            db.Categories.Add(category);
            var app = new App
            {
                Slug = "runner-history",
                Name = "RunnerHistory",
                Url = "https://github.com/example/history",
                Listing = Listing.Main,
                Type = AppType.App,
                Category = category,
                Stars = 20,
                AddedAt = T0,
                UpdatedAt = T0,
            };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
            appId = app.Id;
        }

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, true, T0);
        }

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var stars = db.AppStarDays.AsNoTracking()
                .Where(d => d.AppId == appId)
                .OrderBy(d => d.Day)
                .ToList();
            // One row per day of the two weeks, not one row per week.
            Assert.Equal(8, stars.Count);
            Assert.Equal(
                Enumerable.Range(0, 8).Select(i => priorSunday.AddDays(i)),
                stars.Select(d => d.Day));
            // The feed's per-day gains are expanded and walked backward from
            // the star count: today = 20, each earlier day drops that day's
            // gain (newest week 10,0,0...; oldest week 1,2,3,1,1,1,0).
            Assert.Equal([2, 4, 7, 8, 9, 10, 10, 20], stars.Select(d => d.Stars));
        }

        // A later pass overwrites the same days instead of duplicating them,
        // re-anchoring on the grown star count.
        github.StarWeeks[1] = new GitHubStarWeek(sunday, 12, [12, 0, 0, 0, 0, 0, 0]);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var app = db.Apps.Single(a => a.Id == appId);
            app.Stars = 25;
            await db.SaveChangesAsync();
        }
        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, true, T0);
        }

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            Assert.Equal(8, db.AppStarDays.Count(d => d.AppId == appId));
            var starLevels = db.AppStarDays.AsNoTracking()
                .Where(d => d.AppId == appId)
                .OrderBy(d => d.Day)
                .Select(d => d.Stars)
                .ToList();
            Assert.Equal([5, 7, 10, 11, 12, 13, 13, 25], starLevels);
        }
    }

    [Fact]
    public async Task RunnerSkipsStarHistoryWhenStableAndCovered()
    {
        _connection.Open();
        var today = DateOnly.FromDateTime(T0.UtcDateTime);
        var windowStart = today.AddDays(-365);
        var github = new HistoryGitHub();
        github.StarWeeks.Add(new GitHubStarWeek(today.AddDays(-7), 9, [1, 2, 3, 1, 1, 1, 0]));
        using var provider = BuildProvider(github);

        long appId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            db.Database.EnsureCreated();
            var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
            db.Categories.Add(category);
            var app = new App
            {
                Slug = "runner-stable",
                Name = "RunnerStable",
                Url = "https://github.com/example/stable",
                Listing = Listing.Main,
                Type = AppType.App,
                Category = category,
                Stars = 20,
                AddedAt = T0,
                UpdatedAt = T0,
            };
            db.Apps.Add(app);
            // Hole-free year of levels already matching the live star count.
            for (var day = windowStart; day <= today; day = day.AddDays(1))
            {
                db.AppStarDays.Add(new AppStarDay { App = app, Day = day, Stars = 20 });
            }

            await db.SaveChangesAsync();
            appId = app.Id;
        }

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, true, T0);
        }

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            // Nothing can change without new stars, so the feed stays unread.
            Assert.Equal(0, github.StarRequests);
            Assert.Equal(366, db.AppStarDays.Count(d => d.AppId == appId));
        }
    }

    [Fact]
    public async Task RunnerRepairsHoledStarCoverageWithBackfill()
    {
        _connection.Open();
        var today = DateOnly.FromDateTime(T0.UtcDateTime);
        var windowStart = today.AddDays(-365);
        var hole = today.AddDays(-3);
        var github = new HistoryGitHub();
        github.StarWeeks.Add(new GitHubStarWeek(today.AddDays(-7), 9, [1, 2, 3, 1, 1, 1, 0]));
        github.StarWeeks.Add(new GitHubStarWeek(today, 10, [10, 0, 0, 0, 0, 0, 0]));
        using var provider = BuildProvider(github);

        long appId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            db.Database.EnsureCreated();
            var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
            db.Categories.Add(category);
            var app = new App
            {
                Slug = "runner-holed",
                Name = "RunnerHoled",
                Url = "https://github.com/example/holed",
                Listing = Listing.Main,
                Type = AppType.App,
                Category = category,
                Stars = 20,
                AddedAt = T0,
                UpdatedAt = T0,
            };
            db.Apps.Add(app);
            for (var day = windowStart; day <= today; day = day.AddDays(1))
            {
                if (day == hole)
                {
                    continue;
                }

                db.AppStarDays.Add(new AppStarDay { App = app, Day = day, Stars = 20 });
            }

            await db.SaveChangesAsync();
            appId = app.Id;
        }

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEnrichmentRunner>()
                .EnrichAsync(appId, true, T0);
        }

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            Assert.Equal(1, github.StarRequests);
            Assert.Equal(366, db.AppStarDays.Count(d => d.AppId == appId));
            // The hole sits inside the fetched range, so the walk rewrites it
            // (20 - 10 - 0 - 1 for the three newer days' gains).
            Assert.Equal(9, db.AppStarDays.Single(d => d.AppId == appId && d.Day == hole).Stars);
        }
    }
}
