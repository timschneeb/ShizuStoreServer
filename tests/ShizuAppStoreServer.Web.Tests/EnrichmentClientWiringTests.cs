using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Guards the live-found defect (2026-09-12): tokens configured via env
/// but never applied, so every forge call silently went anonymous and the
/// backfill died on 403s. Executes the production registration.
/// </summary>
public sealed class EnrichmentClientWiringTests
{
    private sealed class CaptureHandler(List<HttpRequestMessage> sink) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            sink.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"message":"nope"}"""),
            });
        }
    }

    private static void CaptureAll(IServiceCollection services, List<HttpRequestMessage> sink)
    {
        services.AddTransient<CaptureHandler>(_ => new CaptureHandler(sink));
        services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<CaptureHandler>());
    }

    private static HttpRequestMessage SingleRequestTo(List<HttpRequestMessage> sink, string host) =>
        Assert.Single(sink, r => r.RequestUri!.Host == host);

    [Fact]
    public async Task TypedClientsSendConfiguredTokens()
    {
        var sink = new List<HttpRequestMessage>();
        var services = new ServiceCollection();
        Program.ConfigureEnrichmentClients(services,
            new EnrichmentOptions { GitHubToken = "gh-test", GitLabToken = "gl-test" });
        CaptureAll(services, sink);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider;

        var github = scoped.GetRequiredService<IGitHubReleaseClient>();
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            github.GetLatestReleaseAsync("o", "r", null));
        var githubRequest = SingleRequestTo(sink, "api.github.com");
        Assert.Equal("Bearer", githubRequest.Headers.Authorization?.Scheme);
        Assert.Equal("gh-test", githubRequest.Headers.Authorization?.Parameter);

        var gitlab = scoped.GetRequiredService<IGitLabReleaseClient>();
        try
        {
            await gitlab.GetLatestReleaseAsync("o/r", null);
        }
        catch (Exception)
        {
            // Any outcome is fine: the request was attempted and captured.
        }

        var gitlabRequest = SingleRequestTo(sink, "gitlab.com");
        Assert.Equal("gl-test", Assert.Single(gitlabRequest.Headers.GetValues("PRIVATE-TOKEN")));
    }

    [Fact]
    public async Task MissingTokensSendAnonymousRequests()
    {
        var sink = new List<HttpRequestMessage>();
        var services = new ServiceCollection();
        Program.ConfigureEnrichmentClients(services, new EnrichmentOptions());
        CaptureAll(services, sink);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var github = scope.ServiceProvider.GetRequiredService<IGitHubReleaseClient>();
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            github.GetLatestReleaseAsync("o", "r", null));
        Assert.Null(SingleRequestTo(sink, "api.github.com").Headers.Authorization);
    }
}
