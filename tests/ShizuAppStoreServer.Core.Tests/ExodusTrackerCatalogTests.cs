using System.Net;
using System.Text;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic tests for the Exodus catalog fetch, cache and fallbacks.</summary>
public sealed class ExodusTrackerCatalogTests
{
    private const string Json = """
        {"trackers":{
          "2":{"name":"AppLovin","code_signature":"com.applovin.","categories":["Advertisement"]},
          "1":{"name":"Google Analytics","code_signature":"com.google.android.apps.analytics.","categories":["Analytics"]},
          "3":{"name":"Network Only","code_signature":"","categories":["Advertisement"]}}}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(handler(request));
        }
    }

    private static (ExodusTrackerCatalog Catalog, StubHandler Handler, string CachePath) Build(
        Action<EnrichmentOptions>? configure = null,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        string? cachePath = null)
    {
        cachePath ??= Path.Combine(Path.GetTempPath(), $"shizu-exodus-{Guid.NewGuid():N}.json");
        var handler = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Json),
        }));
        var options = new EnrichmentOptions { ExodusTrackerCachePath = cachePath };
        configure?.Invoke(options);
        return (new ExodusTrackerCatalog(new HttpClient(handler), options), handler, cachePath);
    }

    [Fact]
    public void ParserDropsNetworkOnlyEntriesAndOrdersById()
    {
        var parsed = ExodusTrackerParser.Parse(Encoding.UTF8.GetBytes(Json));

        Assert.Equal(new[] { 1, 2 }, parsed.Select(t => t.Id));
        Assert.Equal(["Advertisement"], parsed.Single(t => t.Id == 2).Categories);
        Assert.Equal(["Analytics"], parsed.Single(t => t.Id == 1).Categories);
    }

    [Fact]
    public void ParserToleratesAMissingTrackerMap() =>
        Assert.Empty(ExodusTrackerParser.Parse("{}"u8.ToArray()));

    [Fact]
    public async Task FetchesParsesAndCachesTheCatalog()
    {
        var (catalog, handler, cachePath) = Build();
        try
        {
            var first = await catalog.GetAsync();
            var second = await catalog.GetAsync();

            Assert.Equal(2, first.Count);
            Assert.Equal(2, second.Count);
            // The catalog is fresh in memory, so the endpoint is not hit again.
            Assert.Equal(1, handler.Calls);
            Assert.True(File.Exists(cachePath));
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task RefetchesAfterTheRefreshInterval()
    {
        var (catalog, handler, cachePath) = Build(o => o.ExodusTrackerRefreshInterval = TimeSpan.Zero);
        try
        {
            await catalog.GetAsync();
            await catalog.GetAsync();

            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task KeepsTheLastGoodCatalogWhenTheFetchFails()
    {
        var fail = false;
        var (catalog, _, cachePath) = Build(
            o => o.ExodusTrackerRefreshInterval = TimeSpan.Zero,
            _ => fail
                ? throw new HttpRequestException("offline")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
        try
        {
            Assert.Equal(2, (await catalog.GetAsync()).Count);
            fail = true;

            Assert.Equal(2, (await catalog.GetAsync()).Count);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task ReadsTheDiskCacheWhenTheFetchFails()
    {
        var (first, _, cachePath) = Build();
        try
        {
            Assert.Equal(2, (await first.GetAsync()).Count);

            var (second, handler, _) = Build(
                respond: _ => throw new HttpRequestException("offline"),
                cachePath: cachePath);

            Assert.Equal(2, (await second.GetAsync()).Count);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task ReturnsEmptyWhenTheUrlIsDisabled()
    {
        var (catalog, handler, cachePath) = Build(o => o.ExodusTrackerUrl = null);
        try
        {
            Assert.Empty(await catalog.GetAsync());
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }
}
