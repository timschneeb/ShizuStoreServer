using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class AppsTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private async Task SeedAsync(Action<ShizuDbContext> seed)
    {
        await factory.ResetAsync(seed);
    }

    private static void SeedDirectory(ShizuDbContext db)
    {
        var audio = Seeds.NewCategory("audio", "Audio");
        var vendor = Seeds.NewCategory("vendor-specific", "Vendor-specific");
        var miui = Seeds.NewCategory("miui", "MIUI", parent: vendor);
        db.Categories.AddRange(audio, vendor, miui);
        db.Apps.AddRange(
            Seeds.NewApp("micup", audio, license: "MIT", recommended: true,
                packageName: "com.example.micup",
                addedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                updatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            Seeds.NewApp("tuner", audio, license: "GPL-3.0",
                addedAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                updatedAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)),
            Seeds.NewApp("aura", miui, license: "MIT", listing: Listing.ClosedSource,
                type: AppType.Library, availability: Availability.DirectApk,
                addedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                updatedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            Seeds.NewApp("hidden", audio, availability: Availability.Excluded));
    }

    private async Task<PagedAppsDto> GetPageAsync(string query)
    {
        var response = await factory.NewClient().GetAsync("/v1/apps" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<PagedAppsDto>(body, Json)!;
    }

    [Fact]
    public async Task ListHidesExcludedApps()
    {
        await SeedAsync(SeedDirectory);

        var page = await GetPageAsync("");

        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(50, page.PageSize);
        Assert.Equal(3, page.Items.Count);
        Assert.DoesNotContain(page.Items, a => a.Slug == "hidden");
    }

    [Fact]
    public async Task ListFiltersByCategoryIncludingSubcategories()
    {
        await SeedAsync(SeedDirectory);

        var parent = await GetPageAsync("?category=vendor-specific");
        Assert.Single(parent.Items);
        Assert.Equal("aura", parent.Items[0].Slug);

        var child = await GetPageAsync("?category=miui");
        Assert.Single(child.Items);

        var audio = await GetPageAsync("?category=audio");
        Assert.Equal(2, audio.Total);

        var unknown = await factory.NewClient().GetAsync("/v1/apps?category=nope");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task ListSearchAndFlagFilters()
    {
        await SeedAsync(SeedDirectory);
        var client = factory.NewClient();

        var byName = await GetPageAsync("?q=MICUP");
        Assert.Single(byName.Items);

        var byPackage = await GetPageAsync("?q=com.example.micup");
        Assert.Single(byPackage.Items);

        var byDesc = await GetPageAsync("?q=tuner+desc");
        Assert.Single(byDesc.Items);
        Assert.Equal("tuner", byDesc.Items[0].Slug);

        var recommended = await GetPageAsync("?recommended=true");
        Assert.Single(recommended.Items);

        var mit = await GetPageAsync("?license=mit");
        Assert.Equal(2, mit.Total);

        var closed = await GetPageAsync("?listing=closed_source");
        Assert.Single(closed.Items);

        var library = await GetPageAsync("?type=library");
        Assert.Single(library.Items);

        var direct = await GetPageAsync("?availability=direct_apk");
        Assert.Single(direct.Items);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?listing=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?type=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?availability=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?recommended=maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?sort=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?order=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?page=0")).StatusCode);
    }

    [Fact]
    public async Task ListPaginationAndSort()
    {
        await factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(cat);
            var names = new[] { "echo", "delta", "charlie", "bravo", "alpha" };
            for (var i = 0; i < names.Length; i++)
            {
                db.Apps.Add(Seeds.NewApp(names[i], cat,
                    updatedAt: new DateTimeOffset(2026, 1, i + 1, 0, 0, 0, TimeSpan.Zero)));
            }
        });

        // Default sort is updated desc: alpha (Jan 5), bravo (Jan 4) on page 1.
        var p1 = await GetPageAsync("?pageSize=2");
        Assert.Equal(5, p1.Total);
        Assert.Equal(["alpha", "bravo"], p1.Items.Select(a => a.Slug));

        var p2 = await GetPageAsync("?pageSize=2&page=2");
        Assert.Equal(["charlie", "delta"], p2.Items.Select(a => a.Slug));

        var byName = await GetPageAsync("?sort=name&pageSize=5");
        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"],
            byName.Items.Select(a => a.Slug));

        var byNameDesc = await GetPageAsync("?sort=name&order=desc&pageSize=5");
        Assert.Equal("echo", byNameDesc.Items[0].Slug);

        // pageSize clamps to the 200 maximum.
        var clamped = await GetPageAsync("?pageSize=500");
        Assert.Equal(200, clamped.PageSize);
    }

    [Fact]
    public async Task ListSortsByPopularity()
    {
        await factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(cat);
            var low = Seeds.NewApp("low", cat);
            low.Stars = 5;
            low.DownloadTotal = 1000;
            var high = Seeds.NewApp("high", cat);
            high.Stars = 900;
            high.DownloadTotal = 50;
            var popular = Seeds.NewApp("popular", cat);
            popular.Stars = 50;
            popular.DownloadTotal = 1_000_000;
            db.Apps.AddRange(low, high, popular);
        });

        var byStars = await GetPageAsync("?sort=stars&pageSize=5");
        Assert.Equal(["high", "popular", "low"], byStars.Items.Select(a => a.Slug));
        Assert.Equal(900, byStars.Items[0].Stars);

        var byDownloads = await GetPageAsync("?sort=downloads&pageSize=5");
        Assert.Equal(["popular", "low", "high"], byDownloads.Items.Select(a => a.Slug));
        Assert.Equal(1_000_000, byDownloads.Items[0].DownloadTotal);
    }

    [Fact]
    public async Task SummaryCarriesListUpdatedAt()
    {
        var when = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(cat);
            db.Apps.Add(Seeds.NewApp("listed", cat, listUpdatedAt: when));
        });

        var page = await GetPageAsync("?q=listed");

        Assert.Single(page.Items);
        Assert.Equal(when.UtcDateTime, page.Items[0].ListUpdatedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task DisplayNameIsServedWhenPresent()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp("toolbox", audio, name: "Toolbox",
                displayName: "Example Plugin (Toolbox)"));
        });

        var page = await GetPageAsync("?q=toolbox");
        Assert.Equal("Example Plugin (Toolbox)", page.Items[0].Name);

        var response = await factory.NewClient().GetAsync("/v1/apps/toolbox");
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>(Json))!;
        Assert.Equal("Example Plugin (Toolbox)", detail.Name);
    }

    [Fact]
    public async Task DetailReturnsFullShape()
    {
        await factory.ResetAsync(db =>
        {
            var vendor = Seeds.NewCategory("vendor-specific", "Vendor-specific");
            var miui = Seeds.NewCategory("miui", "MIUI", parent: vendor);
            db.Categories.AddRange(vendor, miui);
            var parent = Seeds.NewApp("ashell", miui, availability: Availability.DirectApk);
            parent.PackageName = "com.example.ashell";
            db.Apps.Add(parent);
            db.Apps.Add(Seeds.NewApp("ashell-you", miui, parent: parent));
        });

        var response = await factory.NewClient().GetAsync("/v1/apps/ashell-you");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>(Json))!;

        Assert.Equal("ashell-you", detail.Slug);
        Assert.Equal("ashell", detail.ParentSlug);
        Assert.Equal("miui", detail.CategorySlug);
        Assert.Equal(
            [("vendor-specific", "Vendor-specific"), ("miui", "MIUI")],
            detail.CategoryPath.Select(p => (p.Slug, p.Name)));
        Assert.StartsWith("https://github.com/example/", detail.Url);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task SummaryCarriesAuthorAndDetailCarriesExtras()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp(
                "authored",
                audio,
                authorKey: "github:papergray",
                authorName: "papergray",
                authorUrl: "https://github.com/papergray",
                permissions: ["android.permission.INTERNET"],
                fullDescription: "# Readme"));
        });

        var page = await GetPageAsync("?q=authored");
        Assert.Equal("github:papergray", page.Items[0].AuthorKey);
        Assert.Equal("papergray", page.Items[0].AuthorName);

        var response = await factory.NewClient().GetAsync("/v1/apps/authored");
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>(Json))!;
        Assert.Equal("papergray", detail.AuthorName);
        Assert.Equal("https://github.com/papergray", detail.AuthorUrl);
        Assert.Equal(["android.permission.INTERNET"], detail.Permissions);
        Assert.Equal("# Readme", detail.FullDescription);
    }

    [Fact]
    public async Task SourceNameIsFriendlyAndListingVersionIsUsed()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp(
                "playonly",
                audio,
                url: "https://play.google.com/store/apps/details?id=com.ysy.switcherfiveg",
                availability: Availability.PlayRedirect,
                versionName: "2.6.0-new"));
            var forge = Seeds.NewApp("forge", audio, url: "https://github.com/example/forge");
            forge.SourceKind = SourceKind.GitHub;
            db.Apps.Add(forge);
        });

        var page = await GetPageAsync("?q=playonly");
        Assert.Equal("Play Store", page.Items[0].SourceName);
        Assert.Equal("2.6.0-new", page.Items[0].VersionName);

        var forgePage = await GetPageAsync("?q=forge");
        Assert.Equal("GitHub", forgePage.Items[0].SourceName);

        var response = await factory.NewClient().GetAsync("/v1/apps/playonly");
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>(Json))!;
        Assert.Equal("Play Store", detail.SourceName);
        Assert.Equal("2.6.0-new", detail.VersionName);
    }

    [Fact]
    public async Task DetailExposesSignatureKeyedDownloads()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            var app = Seeds.NewApp("dual", audio, availability: Availability.DirectApk);
            app.PackageName = "com.example.dual";
            app.IconAdaptive = true;
            app.Downloads.Add(new AppDownload
            {
                Source = SourceKind.GitHub,
                ApkUrl = "https://github.com/example/dual/releases/dual_42.apk",
                VersionCode = 42,
                VersionName = "4.2",
                SigSha256 = "980c",
                SigMd5 = "c7b1",
                SigKey = "980c",
                IsPrimary = true,
                ResolvedAt = app.UpdatedAt,
            });
            app.Downloads.Add(new AppDownload
            {
                Source = SourceKind.FDroid,
                ApkUrl = "https://f-droid.org/repo/com.example.dual_40.apk",
                VersionCode = 40,
                VersionName = "4.0",
                SizeBytes = 1234,
                Sha256 = "aaaa",
                SigSha256 = "1111",
                SigMd5 = "b10a",
                SigKey = "1111",
                IsPrimary = false,
                ResolvedAt = app.UpdatedAt,
            });
            db.Apps.Add(app);
        });

        var response = await factory.NewClient().GetAsync("/v1/apps/dual");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>(Json))!;

        Assert.True(detail.IconAdaptive);
        Assert.Equal(42, detail.VersionCode); // top-level version comes from the primary download
        Assert.Equal(2, detail.Downloads.Count);
        var primary = detail.Downloads.Single(d => d.Primary);
        Assert.Equal("github", primary.Source);
        Assert.Equal("980c", primary.SigSha256);
        Assert.Equal("c7b1", primary.SigMd5);
        Assert.Equal(42, primary.VersionCode);
        var variant = detail.Downloads.Single(d => d.Source == "fdroid");
        Assert.Equal("https://f-droid.org/repo/com.example.dual_40.apk", variant.ApkUrl);
        Assert.Equal(40, variant.VersionCode);
        Assert.Equal("4.0", variant.VersionName);
        Assert.Equal(1234, variant.Size);
        Assert.Equal("aaaa", variant.Sha256);
        Assert.Equal("1111", variant.SigSha256);
        Assert.Equal("b10a", variant.SigMd5);
    }

    [Fact]
    public void TestHostRunsInTestingEnvironment()
    {
        // The Program.cs startup tool probe (aapt2/apksigner must exist)
        // only runs outside Testing, keeping these suites hermetic.
        Assert.Equal("Testing",
            factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public async Task RecordInstallIncrementsCountWithoutTouchingUpdatedAt()
    {
        await SeedAsync(SeedDirectory);
        var client = factory.NewClient();

        var first = await client.PostAsync("/v1/apps/micup/installs", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = (await first.Content.ReadFromJsonAsync<InstallRecordedDto>(Json))!;
        Assert.Equal("micup", body.Slug);
        Assert.Equal(1, body.InstallCount);

        var second = await client.PostAsync("/v1/apps/micup/installs", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<InstallRecordedDto>(Json))!.InstallCount);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/v1/apps/no-such-app/installs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/v1/apps/hidden/installs", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.Slug == "micup");
        Assert.Equal(2, app.InstallCount);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), app.UpdatedAt);

        var detail = (await (await client.GetAsync("/v1/apps/micup"))
            .Content.ReadFromJsonAsync<AppDetailDto>(Json))!;
        Assert.Equal(2, detail.InstallCount);
    }

    [Fact]
    public async Task DetailNotFoundAndExcludedHidden()
    {
        await SeedAsync(SeedDirectory);
        var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/v1/apps/no-such-app")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/v1/apps/hidden")).StatusCode);
    }

    [Fact]
    public async Task DetailEtagReturns304()
    {
        await SeedAsync(SeedDirectory);
        var client = factory.NewClient();

        var first = await client.GetAsync("/v1/apps/micup");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!;
        Assert.NotNull(etag);

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/v1/apps/micup");
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }
}
