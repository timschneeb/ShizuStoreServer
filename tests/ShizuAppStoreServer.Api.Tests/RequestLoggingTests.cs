using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Tracking;

namespace ShizuAppStoreServer.Api.Tests;

internal static class RequestLogFlush
{
    /// <summary>
    /// The worker is registered as an <see cref="IHostedService"/> only, so
    /// tests resolve it through that collection and flush deterministically.
    /// </summary>
    public static async Task NowAsync(ShizuApiFactory factory) =>
        await factory.Services.GetServices<IHostedService>().OfType<RequestLogWorker>().Single()
            .FlushAsync(CancellationToken.None);
}

public sealed class RequestLoggingTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static HttpClient ClientWithAgent(ShizuApiFactory factory, string agent)
    {
        var client = factory.NewClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
        return client;
    }

    /// <summary>
    /// Drains hits left buffered by earlier tests in this class before the
    /// database reset, so they cannot land in a fresh database.
    /// </summary>
    private async Task ResetAsync()
    {
        await RequestLogFlush.NowAsync(factory);
        await factory.ResetAsync(_ => { });
    }

    [Fact]
    public async Task LogsEveryEndpointExceptIcons()
    {
        await ResetAsync();
        var client = factory.NewClient();

        var icon = await client.GetAsync($"/icons/{new string('a', 64)}.png");
        var health = await client.GetAsync("/healthz");
        var meta = await client.GetAsync("/v1/meta");
        var missing = await client.GetAsync("/does-not-exist");

        await RequestLogFlush.NowAsync(factory);

        var rows = await factory.QueryAsync(db => db.RequestLogs.OrderBy(x => x.Id).ToListAsync());
        Assert.Equal(3, rows.Count);
        Assert.Equal(
            ["/healthz", "/v1/meta", "/does-not-exist"],
            rows.Select(r => r.Path));
        Assert.Equal([200, 200, 404], rows.Select(r => (int)r.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, icon.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.All(rows, r => Assert.Null(r.UserAgent));
    }

    [Fact]
    public async Task SkipsShizuStoreClientsButLogsNearMisses()
    {
        await ResetAsync();

        await ClientWithAgent(factory, "ShizuStore/1.1.0").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStore/1.1.0-abc1234").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStore/1.1.0 (Android 14; Pixel 8)").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStore (Debug)/1.1.0").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStoreWeb/1.0").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStoreWeb/2.3.4").GetAsync("/v1/meta");
        await ClientWithAgent(factory, "ShizuStore/latest").GetAsync("/v1/meta");

        await RequestLogFlush.NowAsync(factory);

        var row = await factory.QueryAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal("ShizuStore/latest", row.UserAgent);
    }

    [Fact]
    public async Task CapturesRequestDetailsHeadersAndRawRequest()
    {
        await ResetAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/apps?page=2&q=x");
        request.Headers.TryAddWithoutValidation("User-Agent", "curl/8.0");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-admin-secret");
        request.Headers.TryAddWithoutValidation("Origin", "https://example.test");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "203.0.113.7");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7, 198.51.100.9");
        request.Headers.TryAddWithoutValidation("CF-Ray", "8a1b2c3d4e5f6a7b-FRA");
        request.Headers.TryAddWithoutValidation("CF-IPCountry", "DE");

        var response = await factory.NewClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await RequestLogFlush.NowAsync(factory);

        var row = await factory.QueryAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal("GET", row.Method);
        Assert.Equal("/v1/apps", row.Path);
        Assert.Equal("?page=2&q=x", row.QueryString);
        Assert.Equal("/v1/apps?page=2&q=x", row.RawTarget);
        Assert.Equal("HTTP/1.1", row.Protocol);
        Assert.Equal("http", row.Scheme);
        Assert.Equal("localhost", row.Host);
        Assert.Equal(200, row.StatusCode);
        Assert.True(row.DurationMs >= 0);
        Assert.Equal("curl/8.0", row.UserAgent);
        Assert.Equal("https://example.test", row.Origin);
        Assert.Equal("203.0.113.7", row.ClientIp);
        Assert.Equal("203.0.113.7, 198.51.100.9", row.ForwardedFor);
        Assert.Equal("8a1b2c3d4e5f6a7b-FRA", row.CfRay);
        Assert.Equal("DE", row.Country);
        Assert.False(string.IsNullOrEmpty(row.TraceId));
        Assert.Equal(TimeSpan.Zero, row.SeenAt.Offset);

        // Headers are stored verbatim, tokens included (no redaction).
        Assert.Contains("\"User-Agent\":\"curl/8.0\"", row.Headers);
        Assert.Contains("\"Authorization\":\"Bearer test-admin-secret\"", row.Headers);
        Assert.StartsWith(
            "GET /v1/apps?page=2&q=x HTTP/1.1\r\nHost: localhost\r\n", row.RawRequest);
        Assert.Contains("Authorization: Bearer test-admin-secret\r\n", row.RawRequest);
        Assert.EndsWith("\r\n\r\n", row.RawRequest);
    }

    [Fact]
    public async Task StoresLongUserAgentsWithoutTruncation()
    {
        await ResetAsync();
        var agent = "agent/" + new string('x', 700);

        await ClientWithAgent(factory, agent).GetAsync("/v1/meta");
        await RequestLogFlush.NowAsync(factory);

        var row = await factory.QueryAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal(agent, row.UserAgent);
    }
}

/// <summary>Request logging with its own low-limit host (1 req/min).</summary>
public sealed class RequestLogRateLimitTests : IDisposable
{
    private readonly ShizuApiFactory _factory = new(1, "test-admin-secret");

    [Fact]
    public async Task RateLimitedRequestsAreLogged()
    {
        await _factory.ResetAsync(_ => { });
        var client = _factory.NewClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/meta")).StatusCode);

        await RequestLogFlush.NowAsync(_factory);

        var rows = await _factory.QueryAsync(db => db.RequestLogs.OrderBy(x => x.Id).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Equal([200, 429], rows.Select(r => (int)r.StatusCode));
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>Request logging with a configured IP exclusion list.</summary>
public sealed class RequestLogExcludedIpTests : IDisposable
{
    private readonly ShizuApiFactory _factory =
        new(100_000, "test-admin-secret", excludedIps: "203.0.113.7, 2001:db8::7");

    [Fact]
    public async Task ExcludedCloudflareIpIsNotLogged()
    {
        await _factory.ResetAsync(_ => { });
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/meta");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "203.0.113.7");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.9");

        await _factory.NewClient().SendAsync(request);
        await RequestLogFlush.NowAsync(_factory);

        Assert.Empty(await _factory.QueryAsync(db => db.RequestLogs.ToListAsync()));
    }

    [Fact]
    public async Task ExcludedForwardedForHopIsNotLogged()
    {
        await _factory.ResetAsync(_ => { });
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/meta");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7, 198.51.100.9");

        await _factory.NewClient().SendAsync(request);
        await RequestLogFlush.NowAsync(_factory);

        Assert.Empty(await _factory.QueryAsync(db => db.RequestLogs.ToListAsync()));
    }

    [Fact]
    public async Task UnlistedIpIsStillLogged()
    {
        await _factory.ResetAsync(_ => { });
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/meta");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "203.0.113.8");

        await _factory.NewClient().SendAsync(request);
        await RequestLogFlush.NowAsync(_factory);

        var row = await _factory.QueryAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal("203.0.113.8", row.ClientIp);
    }

    [Fact]
    public async Task SpoofedForwardedForDoesNotHideTheCloudflareClient()
    {
        await _factory.ResetAsync(_ => { });
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/meta");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.9");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");

        await _factory.NewClient().SendAsync(request);
        await RequestLogFlush.NowAsync(_factory);

        var row = await _factory.QueryAsync(db => db.RequestLogs.SingleAsync());
        Assert.Equal("198.51.100.9", row.ClientIp);
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>Worker loop on a private SQLite connection, so the timer is not raced by test queries.</summary>
public sealed class RequestLogWorkerTests
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

        var tracker = new RequestLogTracker(new RequestLogOptions());
        tracker.Record(SampleHit("/v1/meta"));
        var worker = new RequestLogWorker(
            tracker,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new RequestLogOptions { FlushInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<RequestLogWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        await using var verify = new ShizuDbContext(options);
        var row = await verify.RequestLogs.SingleAsync();
        Assert.Equal("/v1/meta", row.Path);
        Assert.Equal(200, row.StatusCode);
    }

    private static RequestLogHit SampleHit(string path) => new(
        DateTimeOffset.UtcNow, "GET", path, null, path, "HTTP/1.1", "http", "localhost",
        $"GET {path} HTTP/1.1\r\n\r\n", "{}", 200, 1, null, null, null, null, null, null, null, null);
}

public sealed class ClientUserAgentMatcherTests
{
    [Theory]
    [InlineData("ShizuStore/1.1.0", true)]
    [InlineData("ShizuStore/1.1.0-abc1234", true)]
    [InlineData("ShizuStore/1.1.0 (Android 14; Pixel 8)", true)]
    [InlineData("ShizuStore/10.20.30", true)]
    [InlineData("ShizuStore (Debug)/1.1.0", true)]
    [InlineData("ShizuStore (Debug)/1.1.0 (Android 14; Pixel 8)", true)]
    [InlineData("ShizuStoreWeb/1.0", true)]
    [InlineData("ShizuStoreWeb/2.3.4", true)]
    [InlineData("ShizuStoreWeb/", true)]
    [InlineData("ShizuStoreWeb", false)]
    [InlineData("shizustoreweb/1.0", false)]
    [InlineData("Mozilla/5.0 ShizuStoreWeb/1.0", false)]
    [InlineData("ShizuStore/latest", false)]
    [InlineData("ShizuStore/1.1", false)]
    [InlineData("ShizuStore/", false)]
    [InlineData("ShizuStore", false)]
    [InlineData("ShizuStore (Debug)/latest", false)]
    [InlineData("ShizuStore (Debug)/1.1", false)]
    [InlineData("ShizuStore (debug)/1.1.0", false)]
    [InlineData("ShizuStore(Debug)/1.1.0", false)]
    [InlineData("shizustore/1.1.0", false)]
    [InlineData("Mozilla/5.0 ShizuStore/1.1.0", false)]
    [InlineData("Mozilla/5.0 ShizuStore (Debug)/1.1.0", false)]
    [InlineData("curl/8.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesOnlyClientReleasePrefixes(string? userAgent, bool expected) =>
        Assert.Equal(expected, ClientUserAgentMatcher.IsClient(userAgent));
}

public sealed class RequestLogOptionsTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", true)]
    [InlineData("203.0.113.7, 198.51.100.9", "198.51.100.9", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    [InlineData("2001:DB8::7", "2001:db8::7", true)]
    [InlineData("", "203.0.113.7", false)]
    [InlineData("203.0.113.7", null, false)]
    [InlineData("203.0.113.7", " ", false)]
    public void MatchesOnlyListedClientIps(string excludedIps, string? ip, bool expected)
    {
        var options = new RequestLogOptions { ExcludedIps = excludedIps };

        Assert.Equal(expected, options.IsExcludedIp(ip));
    }
}
