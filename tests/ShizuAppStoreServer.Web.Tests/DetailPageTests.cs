using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Mapping;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class DetailPageTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string AndroidUserAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36";

    [Fact]
    public async Task Detail_RendersHeaderVersionLineAndMeta()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.AuthorName = "Jane Doe";
            app.AuthorUrl = "https://example.com/jane";
            app.SourceKind = SourceKind.GitHub;
            app.License = "MIT";
            app.PackageName = "com.example.app";
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("<h1>Foo</h1>", html);
        Assert.Contains("Jane Doe", html);
        Assert.Contains("https://example.com/jane", html);
        Assert.Contains("1.10 &#xB7; GitHub", html);
        Assert.Contains("MIT", html);
    }

    [Fact]
    public async Task Detail_PrefersDisplayName()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "List Foo", 1);
            app.DisplayName = "Fancy Foo";
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("<h1>Fancy Foo</h1>", html);
        Assert.DoesNotContain("<h1>List Foo</h1>", html);
    }

    [Fact]
    public async Task Detail_OgMetadataUsesTrimmedNameAndIcon()
    {
        var iconHash = new string('a', 64);
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.DisplayName = "Foo (root-name)";
            app.IconHash = iconHash;
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("<title>Foo - ShizuStore</title>", html);
        Assert.Contains("<meta property=\"og:title\" content=\"Foo\"", html);
        Assert.Contains("<meta property=\"og:site_name\" content=\"ShizuStore\"", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"https://shizustore.com/icons/{iconHash}.png\"", html);
        Assert.Contains($"<meta name=\"twitter:image\" content=\"https://shizustore.com/icons/{iconHash}.png\"", html);
        Assert.Contains("<h1>Foo (root-name)</h1>", html);
    }

    [Fact]
    public async Task Detail_RendersTagChips()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.License = "MIT";
            app.IsRecommended = true;
            app.RequiresRoot = true;
            app.PackageName = "com.example.app";
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk", versionCode: 10, packageName: "com.example.app"));
            var download = db.ChangeTracker.Entries<AppDownload>().First().Entity;
            download.DhizukuDeclared = true;
            download.MinSdk = 24;
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("detail-tags", html);
        Assert.Contains("<a class=\"label-chip\" href=\"/apps?category=tools\">", html);
        Assert.Contains("Tools", html);
        Assert.Contains("Android 7.0&#x2B;", html);
        Assert.Contains("ago", html);
        Assert.Contains("MIT", html);
        Assert.Contains("Dhizuku", html);
        Assert.Contains("Recommended", html);
        Assert.Contains("Requires root", html);
        Assert.Contains("#i-android", html);
    }

    [Fact]
    public async Task Detail_RendersStatsStripForDirectApk()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.SourceKind = SourceKind.GitHub;
            app.InstallCount = 4200;
            app.DownloadTotal = 12000;
            app.Stars = 1500;
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
            var download = db.ChangeTracker.Entries<AppDownload>().First().Entity;
            download.SizeBytes = 5_000_000;
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("4.2k", html);
        Assert.Contains("12k", html);
        Assert.Contains("1.5k", html);
        Assert.Contains("5 MB", html);
        Assert.Contains("ShizuStore", html);
        Assert.Contains(">GitHub<", html);
        Assert.Contains(">Stars<", html);
        Assert.Contains(">Size<", html);
        Assert.Contains("Number of installs reported by other ShizuStore users.", html);
        Assert.Contains("Number of downloads counted at GitHub.", html);
    }

    [Fact]
    public async Task Detail_PlayRedirectShowsStoreNoticeAndButton()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.Availability = Availability.PlayRedirect;
            app.StoreUrl = "https://play.google.com/store/apps/details?id=com.example.app";
            db.Add(app);
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("This app is only available on Google Play. Tap to open the listing.", html);
        Assert.Contains("View on Play", html);
        Assert.Contains("https://play.google.com/store/apps/details?id=com.example.app", html);
        Assert.DoesNotContain("Download APK", html);
    }

    [Fact]
    public async Task Detail_LinkOnlyShowsWebsiteButton()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.Availability = Availability.LinkOnly;
            db.Add(app);
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("View website", html);
        Assert.DoesNotContain("View on Play", html);
        Assert.DoesNotContain("Download APK", html);
    }

    [Fact]
    public async Task Detail_OpenInAppOnlyForAndroid()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var client = factory.NewClient();
        var desktop = await GetBodyAsync(client, "/apps/foo");
        var android = await GetBodyAsync(client, "/apps/foo", AndroidUserAgent);

        Assert.DoesNotContain("shizustore://apps/foo", desktop);
        Assert.Contains("shizustore://apps/foo", android);
        Assert.Contains("Open in ShizuStore", android);
        Assert.DoesNotContain("id=\"app-nudge\"", desktop);
        Assert.Contains("id=\"app-nudge\"", android);
        Assert.Contains(WebAppFactory.ApkUrl, android);
    }

    [Fact]
    public async Task Detail_RendersBillingAndClosedSourceNotices()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.Listing = Listing.ClosedSource;
            app.HasPaid = true;
            app.HasIap = true;
            app.HasAds = true;
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("This is a paid app with in-app purchases.", html);
        Assert.Contains("This app contains ads.", html);
        Assert.Contains("This app is closed source. Its code is not public, so the community cannot easily verify what it does.", html);
    }

    [Fact]
    public async Task Detail_RendersTrackersDetails()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
            var download = db.ChangeTracker.Entries<AppDownload>().First().Entity;
            download.TrackerTags = ["Sentry:crash", "Sentry:analytics", "AdMob:ads"];
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("Detected trackers (2)", html);
        Assert.Contains("Sentry", html);
        Assert.Contains("crash, analytics", html);
        Assert.Contains("AdMob", html);
        Assert.Contains("Matched by Exodus Privacy code signatures. Not a complete list.", html);
    }

    [Fact]
    public async Task Detail_MarkdownIsSanitizedAndRelativeImagesResolve()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.Url = "https://github.com/example/foo";
            app.SourceUrl = "https://github.com/example/foo";
            app.SourceKind = SourceKind.GitHub;
            app.FullDescription = "**hello** <script>alert('x')</script> [bad](javascript:alert('x')) ![shot](docs/shot.png)";
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("<strong>hello</strong>", html);
        Assert.DoesNotContain("alert('x')", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("https://github.com/example/foo/docs/shot.png", html);
    }

    [Fact]
    public async Task Detail_RendersUsageChangelogPermissionsAndScreenshots()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var app = Seeds.App(1, "foo", "Foo", 1);
            app.UsageShort = "Reads package states.";
            app.UsageMarkdown = "Uses `pm` commands.";
            app.Changelog = "Fixed things.";
            app.Screenshots = ["https://example.com/shot1.png", "https://example.com/shot2.png"];
            app.Permissions = ["android.permission.INTERNET"];
            db.Add(app);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("How Shizuku is used", html);
        Assert.Contains("Reads package states.", html);
        Assert.Contains("AI-assisted analysis", html);
        Assert.Contains("<code>pm</code>", html);
        Assert.Contains("Changelog", html);
        Assert.Contains("What's new for version 1.10", html);
        Assert.Contains("Fixed things.", html);
        Assert.Contains("1 permission requested", html);
        Assert.Contains("android.permission.INTERNET", html);
        Assert.Contains("data-dialog=\"permissions-dialog\"", html);
        Assert.Contains("<mdui-dialog id=\"permissions-dialog\"", html);
        Assert.Contains("https://example.com/shot1.png", html);
        Assert.Contains("https://example.com/shot2.png", html);
        Assert.Contains("data-dialog=\"about-dialog\"", html);
        Assert.Contains("data-dialog=\"usage-dialog\"", html);
        Assert.Contains("data-dialog=\"changelog-dialog\"", html);
        Assert.Contains("id=\"about-dialog\"", html);
        Assert.Contains("id=\"usage-dialog\"", html);
        Assert.Contains("id=\"changelog-dialog\"", html);
        Assert.Contains("class=\"screenshot\"", html);
        Assert.Contains("id=\"screenshot-viewer\"", html);
        Assert.Contains("viewer-prev", html);
        Assert.Contains("viewer-next", html);
        Assert.Contains("viewer-close", html);
    }

    [Fact]
    public async Task Detail_SourcesDetailsListCandidates()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            db.Add(Seeds.Download(1, "https://example.com/foo-10.apk", primary: true, versionCode: 10));
            db.Add(Seeds.Download(1, "https://example.com/foo-8.zip", primary: false, versionCode: 8));
            db.Entry(db.Set<AppDownload>().Local.Last()).Entity.ArchiveEntry = "foo-8.apk";
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("2 sources", html);
        Assert.Contains("Selected", html);
        Assert.Contains("https://example.com/foo-10.apk", html);
        Assert.Contains("https://example.com/foo-8.zip", html);
        Assert.Contains("zip entry: foo-8.apk", html);
    }

    [Fact]
    public async Task Detail_SourcesMarkAbiVariants()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            db.Add(Seeds.Download(1, "https://example.com/foo-universal.apk", primary: true, versionCode: 10));
            db.Add(Seeds.Download(1, "https://example.com/foo-arm64.apk", primary: false, versionCode: 9));
            db.Add(Seeds.Download(1, "https://example.com/foo-legacy.apk", primary: false, versionCode: 8));
            var rows = db.ChangeTracker.Entries<AppDownload>().Select(e => e.Entity).ToList();
            rows.Single(r => r.ApkUrl.EndsWith("universal.apk")).Abis = ["arm64-v8a", "armeabi-v7a"];
            rows.Single(r => r.ApkUrl.EndsWith("arm64.apk")).Abi = "arm64-v8a";
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("class=\"label-chip source-abi\">Universal<", html);
        Assert.Contains("class=\"label-chip source-abi\">arm64-v8a<", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"label-chip source-abi\"").Count);
    }

    [Fact]
    public async Task Detail_OmitsSignatureLanguagesAndAppInformation()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
            var download = db.ChangeTracker.Entries<AppDownload>().First().Entity;
            download.TargetSdk = 35;
            download.SigSha256 = "aaaa bbbb";
            download.SigMd5 = "cccc";
            download.SignerDn = "CN=Foo";
            download.SignerScheme = "v1+v2+v3";
            download.SignerKeyAlgorithm = "RSA 2048";
            download.Abi = "arm64-v8a";
            download.Abis = ["arm64-v8a"];
            download.Locales = ["en", "de"];
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.DoesNotContain("Signature", html);
        Assert.DoesNotContain("Languages", html);
        Assert.DoesNotContain("App information", html);
        Assert.DoesNotContain("<dt>Type</dt>", html);
    }

    [Fact]
    public async Task Detail_RendersCarousels()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var foo = Seeds.App(1, "foo", "Foo", 1);
            foo.AuthorKey = "github:jane";
            db.Add(foo);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));

            var bar = Seeds.App(2, "bar", "Bar", 1);
            bar.AuthorKey = "github:jane";
            db.Add(bar);
            db.Add(Seeds.Download(2, "https://example.com/bar.apk"));

            var baz = Seeds.App(3, "baz", "Baz", 1);
            db.Add(baz);
            db.Add(Seeds.Download(3, "https://example.com/baz.apk"));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps/foo");

        Assert.Contains("More apps from this developer", html);
        Assert.Contains("More from this category", html);
        Assert.Contains("href=\"/apps/bar\"", html);
        Assert.Contains("href=\"/apps/baz\"", html);
        Assert.DoesNotContain("href=\"/apps/foo\"", html);
    }

    [Fact]
    public async Task Detail_UnknownAndExcludedReturnStyledNotFound()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            var excluded = Seeds.App(1, "gone", "Gone", 1);
            excluded.Availability = Availability.Excluded;
            db.Add(excluded);
        });

        var client = factory.NewClient();
        foreach (var slug in new[] { "missing", "gone" })
        {
            var response = await client.GetAsync($"/apps/{slug}");
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("App no longer available", html);
            Assert.Contains("#i-apps", html);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task Detail_SetsCacheAndMetaDescription()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));
        });

        var response = await factory.NewClient().GetAsync("/apps/foo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
        Assert.Contains("<meta name=\"description\" content=\"Foo description\"", html);
        var csp = response.Headers.TryGetValues("Content-Security-Policy", out var values)
            ? string.Join(" ", values)
            : string.Empty;
        Assert.Contains("script-src 'self'", csp);
    }

    private static async Task<string> GetBodyAsync(HttpClient client, string url, string? userAgent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (userAgent is not null)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
