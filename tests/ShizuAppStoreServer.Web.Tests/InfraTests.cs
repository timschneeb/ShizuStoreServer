using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class InfraTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task Landing_ServesMinimalPage()
    {
        await factory.ResetAsync(_ => { });

        var client = factory.NewClient();
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ShizuStore", html);
        Assert.Contains("<mdui-top-app-bar class=\"site-top-bar\" scroll-behavior=\"elevate\" role=\"banner\">", html);
        Assert.Contains("<mdui-button variant=\"text\" href=\"/apps\">Apps</mdui-button>", html);
        Assert.Contains("<mdui-button-icon href=\"https://github.com/timschneeb/ShizuStore\"", html);
        Assert.Contains("id=\"i-github\"", html);
        Assert.Contains("https://github.com/timschneeb/ShizuStore", html);
        Assert.DoesNotContain("Get the ShizuStore app", html);
        Assert.Contains("An open-source app store for Shizuku apps", html);
        Assert.Contains("href=\"https://github.com/timschneeb/awesome-shizuku\"", html);
        Assert.Contains("Download APK", html);
        Assert.Contains("Browse apps", html);
        Assert.Contains("id=\"i-download\"", html);
        Assert.Contains(WebAppFactory.ApkUrl, html);
        Assert.Contains("href=\"/privacy\"", html);
        Assert.Contains("<meta property=\"og:site_name\" content=\"ShizuStore\"", html);
        Assert.Contains("property=\"og:image\" content=\"https://shizustore.com/apple-touch-icon.png\"", html);
        Assert.Contains("max-age=300", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Pages_SendSecurityHeaders()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task PrivacyPage_ServesPolicy()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/privacy");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Privacy policy</h1>", html);
        Assert.Contains("no user accounts", html);
        Assert.Contains("Cloudflare", html);
    }

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        await factory.ResetAsync(_ => { });

        var payload = await factory.NewClient().GetFromJsonAsync<JsonElement>("/healthz");

        Assert.Equal("ok", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AssetLinks_ServesConfiguredFingerprints()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/.well-known/assetlinks.json");
        var statements = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var statement = Assert.Single(statements!);
        Assert.Equal(
            "delegate_permission/common.handle_all_urls",
            statement.GetProperty("relation")[0].GetString());
        var target = statement.GetProperty("target");
        Assert.Equal(WebAppFactory.PackageName, target.GetProperty("package_name").GetString());
        Assert.Equal(
            WebAppFactory.Fingerprint,
            target.GetProperty("sha256_cert_fingerprints")[0].GetString());
        Assert.Contains("max-age=3600", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Icon_InvalidHash_ReturnsBadRequest()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/icons/not-a-hash.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Icon_MissingFile_ReturnsNotFound()
    {
        await factory.ResetAsync(_ => { });

        var hash = new string('a', 64);
        var response = await factory.NewClient().GetAsync($"/icons/{hash}.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Icon_ServesFileWithImmutableCache()
    {
        await factory.ResetAsync(_ => { });

        var hash = new string('b', 64);
        await File.WriteAllBytesAsync(
            Path.Combine(factory.IconDir, $"{hash}.png"),
            [0x89, 0x50, 0x4E, 0x47]);

        var response = await factory.NewClient().GetAsync($"/icons/{hash}.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal(4, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Sitemap_ListsRenderedDetailPages()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            var gone = Seeds.App(2, "gone", "Gone", 1);
            gone.Availability = Availability.Excluded;
            db.Add(gone);
        });

        var response = await factory.NewClient().GetAsync("/sitemap.xml");
        var xml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<loc>https://shizustore.com/apps/foo</loc>", xml);
        Assert.Contains("<lastmod>2026-02-01</lastmod>", xml);
        Assert.DoesNotContain("gone", xml);
        Assert.Contains("max-age=3600", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Robots_PointsAtSitemap()
    {
        await factory.ResetAsync(_ => { });

        var robots = await factory.NewClient().GetStringAsync("/robots.txt");

        Assert.Contains("Sitemap: https://shizustore.com/sitemap.xml", robots);
    }
}
