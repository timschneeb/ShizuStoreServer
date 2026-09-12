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
    public async Task DetailReturnsFullShape()
    {
        await factory.ResetAsync(db =>
        {
            var vendor = Seeds.NewCategory("vendor-specific", "Vendor-specific");
            var miui = Seeds.NewCategory("miui", "MIUI", parent: vendor);
            db.Categories.AddRange(vendor, miui);
            var parent = Seeds.NewApp("ashell", miui, availability: Availability.DirectApk);
            parent.PackageName = "com.example.ashell";
            parent.VersionCode = 42;
            parent.VersionName = "1.2.3";
            parent.ApkUrl = "https://github.com/example/ashell/releases/a.apk";
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
    public async Task DetailExposesSignaturesAndFdroidVariant()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            var app = Seeds.NewApp("dual", audio, availability: Availability.DirectApk);
            app.PackageName = "com.example.dual";
            app.VersionCode = 42;
            app.IconAdaptive = true;
            app.SigSha256 = "980c";
            app.SigMd5 = "c7b1";
            app.FdroidApkUrl = "https://f-droid.org/repo/com.example.dual_40.apk";
            app.FdroidVersionCode = 40;
            app.FdroidVersionName = "4.0";
            app.FdroidApkSize = 1234;
            app.FdroidApkSha256 = "aaaa";
            app.FdroidSigSha256 = "1111";
            app.FdroidSigMd5 = "b10a";
            db.Apps.Add(app);
        });

        var response = await factory.NewClient().GetAsync("/v1/apps/dual");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<AppDetailDto>(Json))!;

        Assert.Equal("980c", detail.SigSha256);
        Assert.Equal("c7b1", detail.SigMd5);
        Assert.True(detail.IconAdaptive);
        Assert.NotNull(detail.FdroidVariant);
        var variant = detail.FdroidVariant!;
        Assert.Equal("https://f-droid.org/repo/com.example.dual_40.apk", variant.ApkUrl);
        Assert.Equal(40, variant.VersionCode);
        Assert.Equal("4.0", variant.VersionName);
        Assert.Equal(1234, variant.ApkSize);
        Assert.Equal("aaaa", variant.ApkSha256);
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
