using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class IconsTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task ServesPngWithImmutableCache()
    {
        var bytes = Convert.FromHexString("89504E470D0A1A0A"); // PNG magic, body irrelevant
        await File.WriteAllBytesAsync(Path.Combine(factory.IconDir, Sha + ".png"), bytes);

        var response = await factory.NewClient().GetAsync($"/icons/{Sha}.png");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} (dir={factory.IconDir})");
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
        Assert.Contains("max-age=86400", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task MalformedHashIs400AndMissingFileIs404()
    {
        var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/icons/not-hex-at-all.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/icons/abc.png")).StatusCode);

        var missing = new string('0', 64);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/icons/{missing}.png")).StatusCode);
    }
}

public sealed class AdminTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const string Token = "test-admin-secret";

    private static StringContent Body(string json = """{"reason":"webhook-test"}""") =>
        new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Authorized(string json) =>
        new(HttpMethod.Post, "/v1/admin/sync")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Headers = { { "Authorization", $"Bearer {Token}" } },
        };

    [Fact]
    public async Task ValidTokenQueuesRequestAndWakesTheWorker()
    {
        await factory.ResetAsync(_ => { });
        var client = factory.NewClient();
        const string json = """{"reason":"webhook-test"}""";

        var response = await client.SendAsync(Authorized(json));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<SyncAcceptedDto>(Json))!;
        Assert.True(accepted.Queued);
        await factory.QueryAsync(async db =>
        {
            var row = await db.SyncRequests.SingleAsync();
            Assert.Equal("webhook-test", row.Reason);
            Assert.False(row.Processed);
            return 0;
        });
    }

    [Fact]
    public async Task MissingOrWrongTokenIs401()
    {
        var client = factory.NewClient();

        var missing = await client.PostAsync("/v1/admin/sync", Body());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        const string json = """{"reason":"x"}""";
        var wrong = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/sync")
        {
            Content = Body(json),
            Headers = { { "Authorization", "Bearer other-secret" } },
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        var malformed = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/sync")
        {
            Content = Body(json),
            Headers = { { "Authorization", Token } },
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(malformed)).StatusCode);
    }

    [Fact]
    public async Task NoTokenConfiguredIs503()
    {
        using var unconfigured = new ShizuApiFactory(100_000, adminSecret: null);
        var client = unconfigured.NewClient();

        var response = await client.SendAsync(Authorized("""{"reason":"x"}"""));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

/// <summary>Rate limiting with its own low-limit host (3 req/min).</summary>
public sealed class RateLimitTests : IClassFixture<ShizuApiFactory>, IDisposable
{
    private readonly ShizuApiFactory _factory = new(3, "test-admin-secret");

    [Fact]
    public async Task ExceedingLimitReturns429()
    {
        // No seed rows needed, but ResetAsync creates the schema (fresh
        // :memory: SQLite has no tables until EnsureCreated runs).
        await _factory.ResetAsync(_ => { });
        var client = _factory.NewClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/meta")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.GetAsync("/v1/meta")).StatusCode);
    }

    [Fact]
    public async Task IconsAreNotRateLimited()
    {
        await _factory.ResetAsync(_ => { });
        var client = _factory.NewClient();
        var sha = new string('a', 64);

        // The icon store is empty in tests, so a valid hash is a 404. The
        // point is that it is never a 429, no matter how many are requested.
        for (var i = 0; i < 8; i++)
        {
            var response = await client.GetAsync($"/icons/{sha}.png");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>Response compression: Brotli preferred, gzip fallback, identity untouched.</summary>
public sealed class CompressionTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    [Fact]
    public async Task MetaNegotiatesBrotli()
    {
        await factory.ResetAsync(_ => { });
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/meta");
        request.Headers.AcceptEncoding.ParseAdd("br");

        var response = await factory.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
        await using var stream = new BrotliStream(
            await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.True(doc.RootElement.TryGetProperty("counts", out _));
    }

    [Fact]
    public async Task GzipIsTheFallback()
    {
        await factory.ResetAsync(_ => { });
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/meta");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        var response = await factory.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task NoAcceptEncodingIsUncompressed()
    {
        await factory.ResetAsync(_ => { });
        var response = await factory.NewClient().GetAsync("/v1/meta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }
}
