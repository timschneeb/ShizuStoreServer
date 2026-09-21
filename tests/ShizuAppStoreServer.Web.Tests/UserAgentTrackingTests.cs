using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Tracking;

namespace ShizuAppStoreServer.Web.Tests;

internal static class UserAgentFlush
{
    /// <summary>
    /// The worker is registered as an <see cref="IHostedService"/> only, so
    /// tests resolve it through that collection and flush deterministically.
    /// </summary>
    public static async Task NowAsync(ShizuApiFactory factory) =>
        await factory.Services.GetServices<IHostedService>().OfType<UserAgentTrackingWorker>().Single()
            .FlushAsync(CancellationToken.None);
}

public sealed class UserAgentTrackingTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static HttpClient ClientWithAgent(ShizuApiFactory factory, string agent)
    {
        var client = factory.NewClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
        return client;
    }

    [Fact]
    public async Task AggregatesRequestsPerUserAgentAndUtcDay()
    {
        await factory.ResetAsync(_ => { });
        const string agent = "ShizuStore/1.1.0 (Android 14; Pixel 8)";
        var client = ClientWithAgent(factory, agent);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/apps")).StatusCode);

        await UserAgentFlush.NowAsync(factory);

        var row = await factory.QueryAsync(db => db.ClientUserAgents.SingleAsync());
        Assert.Equal(agent, row.UserAgent);
        Assert.Equal(2, row.RequestCount);
        Assert.Equal("/v1/apps", row.LastPath);
        Assert.True(row.FirstSeenAt <= row.LastSeenAt);
        Assert.Equal(TimeSpan.Zero, row.FirstSeenAt.Offset);

        var day = await factory.QueryAsync(db => db.ClientUserAgentDays.SingleAsync());
        Assert.Equal(agent, day.UserAgent);
        Assert.Equal(today, day.Day);
        Assert.Equal(2, day.RequestCount);
    }

    [Fact]
    public async Task KeepsDistinctUserAgentsSeparate()
    {
        await factory.ResetAsync(_ => { });
        await ClientWithAgent(factory, "ShizuStore/1.0.0").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStore/1.1.0").GetAsync("/v1/meta");

        await UserAgentFlush.NowAsync(factory);

        var rows = await factory.QueryAsync(
            db => db.ClientUserAgents.OrderBy(x => x.UserAgent).ToListAsync());
        Assert.Equal(["ShizuStore/1.0.0", "ShizuStore/1.1.0"], rows.Select(x => x.UserAgent));
        Assert.All(rows, x => Assert.Equal(1, x.RequestCount));
    }

    [Fact]
    public async Task IgnoresHealthIconsAdminAndRequestsWithoutUserAgent()
    {
        await factory.ResetAsync(_ => { });
        var client = ClientWithAgent(factory, "ShizuStore/1.1.0");

        await client.GetAsync("/healthz");
        await client.GetAsync($"/icons/{new string('a', 64)}.png");
        await client.PostAsync("/v1/admin/sync", new StringContent("{}", Encoding.UTF8, "application/json"));
        await factory.NewClient().GetAsync("/v1/meta");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/meta")).StatusCode);

        await UserAgentFlush.NowAsync(factory);

        var row = await factory.QueryAsync(db => db.ClientUserAgents.SingleAsync());
        Assert.Equal("/v1/meta", row.LastPath);
        Assert.Equal(1, row.RequestCount);
    }

    [Fact]
    public async Task TruncatesLongUserAgents()
    {
        await factory.ResetAsync(_ => { });
        var agent = "ShizuStore/1.1.0 " + new string('x', 700);
        await ClientWithAgent(factory, agent).GetAsync("/v1/meta");

        await UserAgentFlush.NowAsync(factory);

        var row = await factory.QueryAsync(db => db.ClientUserAgents.SingleAsync());
        Assert.Equal(ClientUserAgent.MaxUserAgentLength, row.UserAgent.Length);
        Assert.Equal(agent[..ClientUserAgent.MaxUserAgentLength], row.UserAgent);
    }

    [Fact]
    public async Task FlushMergesCountsAndSplitsDays()
    {
        await factory.ResetAsync(_ => { });
        var first = new DateTimeOffset(2026, 9, 20, 23, 58, 0, TimeSpan.Zero);
        var second = first.AddMinutes(5);

        await factory.QueryAsync(async db =>
        {
            await UserAgentStatsRecorder.FlushAsync(db,
            [
                new UserAgentHit("agent-a", "/v1/apps", first),
                new UserAgentHit("agent-a", "/v1/changes", first.AddMinutes(1)),
            ]);
            await UserAgentStatsRecorder.FlushAsync(db, [new UserAgentHit("agent-a", "/v1/meta", second)]);
            return 0;
        });

        var row = await factory.QueryAsync(db => db.ClientUserAgents.SingleAsync());
        Assert.Equal(3, row.RequestCount);
        Assert.Equal(first, row.FirstSeenAt);
        Assert.Equal(second, row.LastSeenAt);
        Assert.Equal("/v1/meta", row.LastPath);

        var days = (await factory.QueryAsync(db => db.ClientUserAgentDays.ToListAsync()))
            .OrderBy(d => d.Day)
            .ToList();
        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 9, 20), days[0].Day);
        Assert.Equal(2, days[0].RequestCount);
        Assert.Equal(new DateOnly(2026, 9, 21), days[1].Day);
        Assert.Equal(1, days[1].RequestCount);
    }
}

/// <summary>User-agent tracking with its own low-limit host (1 req/min).</summary>
public sealed class UserAgentTrackingRateLimitTests : IDisposable
{
    private readonly ShizuApiFactory _factory = new(1, "test-admin-secret");

    [Fact]
    public async Task RateLimitedRequestsAreNotTracked()
    {
        await _factory.ResetAsync(_ => { });
        var client = _factory.NewClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ShizuStore/1.1.0");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/meta")).StatusCode);

        await UserAgentFlush.NowAsync(_factory);

        var row = await _factory.QueryAsync(db => db.ClientUserAgents.SingleAsync());
        Assert.Equal(1, row.RequestCount);
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>Worker loop on a private SQLite connection, so the timer is not raced by test queries.</summary>
public sealed class UserAgentTrackingWorkerTests
{
    [Fact]
    public async Task FlushesBufferedHitsOnIntervalAndShutdown()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ShizuDbContext>().UseSqlite(connection).Options;
        await using (var db = new ShizuDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<ShizuDbContext>(o => o.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        var tracker = new UserAgentTracker(new UserAgentTrackingOptions());
        tracker.Record(new UserAgentHit("agent-w", "/v1/meta", DateTimeOffset.UtcNow));
        var worker = new UserAgentTrackingWorker(
            tracker,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new UserAgentTrackingOptions { FlushInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<UserAgentTrackingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        await using var verify = new ShizuDbContext(options);
        var row = await verify.ClientUserAgents.SingleAsync();
        Assert.Equal("agent-w", row.UserAgent);
        Assert.Equal(1, row.RequestCount);
    }
}
