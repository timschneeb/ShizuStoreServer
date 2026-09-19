using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class ScreenshotRefreshTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const string Token = "test-admin-secret";
    private const string Route = "/v1/admin/refresh-screenshots";

    private static HttpRequestMessage Authorized(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, Route);
        request.Headers.Add("Authorization", $"Bearer {Token}");
        return request;
    }

    [Fact]
    public async Task RunsToCompletionWithEmptyCatalog()
    {
        await factory.ResetAsync(_ => { });
        var client = factory.NewClient();

        var start = await client.SendAsync(Authorized(HttpMethod.Post));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = (await start.Content.ReadFromJsonAsync<ScreenshotRefreshStatusDto>(Json))!;
        Assert.Equal("running", started.State);

        var status = await WaitForTerminalState(client);
        Assert.Equal("completed", status.State);
        Assert.Equal(0, status.Checked);
        Assert.Equal(0, status.Updated);
        Assert.Equal(0, status.Failed);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task BusyGateIs409()
    {
        await factory.ResetAsync(_ => { });
        var gate = factory.Services.GetRequiredService<SyncGate>();
        Assert.True(await gate.WaitAsync(0));
        try
        {
            var response = await factory.NewClient().SendAsync(Authorized(HttpMethod.Post));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task MissingOrWrongTokenIs401()
    {
        var client = factory.NewClient();

        var missing = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, Route));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Post, Route);
        wrong.Headers.Add("Authorization", "Bearer other-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        var status = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task NoTokenConfiguredIs503()
    {
        using var unconfigured = new ShizuApiFactory(100_000, adminSecret: null);
        var client = unconfigured.NewClient();

        var post = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, Route));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        var get = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
    }

    [Fact]
    public async Task CancelWithoutRunIs409()
    {
        await factory.ResetAsync(_ => { });
        var response = await factory.NewClient().SendAsync(Authorized(HttpMethod.Delete));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task<ScreenshotRefreshStatusDto> WaitForTerminalState(HttpClient client)
    {
        for (var i = 0; i < 400; i++)
        {
            var response = await client.SendAsync(Authorized(HttpMethod.Get));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var status = (await response.Content.ReadFromJsonAsync<ScreenshotRefreshStatusDto>(Json))!;
            if (status.State is "completed" or "failed")
            {
                return status;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Screenshots refresh did not reach a terminal state.");
    }
}
