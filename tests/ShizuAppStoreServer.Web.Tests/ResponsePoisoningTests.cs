using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Scraper poisoning (SPEC 2): matching User-Agents get doctored payloads
/// while everyone else is untouched. Output caching is off in this suite;
/// the cache-interaction cases live in
/// <see cref="ResponsePoisoningCacheTests"/>.
/// </summary>
public sealed class ResponsePoisoningTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private const string Httpx = "python-httpx/0.28.1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private HttpClient Client(string? userAgent = null)
    {
        var client = factory.NewClient();
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
        return client;
    }

    /// <summary>
    /// One app with stats far outside the randomizer ranges, so a poisoned
    /// value can never accidentally match the seeded one.
    /// </summary>
    private static App SeedApp(
        ShizuDbContext db,
        string slug,
        string name,
        int stars,
        long downloadTotal,
        long installCount,
        char marker)
    {
        var category = Seeds.NewCategory($"cat-{slug}", $"Category {slug}");
        db.Categories.Add(category);
        var app = Seeds.NewApp(slug, category, name: name);
        app.Stars = stars;
        app.DownloadTotal = downloadTotal;
        app.InstallCount = installCount;
        app.IconHash = new string(marker, 64);
        app.IconAdaptive = marker == 'a';
        var sig = new string(marker, 64);
        app.Downloads.Add(new AppDownload
        {
            Source = SourceKind.GitHub,
            ApkUrl = $"https://github.com/example/{slug}/releases/{slug}.apk",
            SigSha256 = sig,
            SigMd5 = sig,
            SigKey = sig,
            IsPrimary = true,
            ResolvedAt = app.UpdatedAt,
        });
        db.Apps.Add(app);
        return app;
    }

    private static void SeedTwoApps(ShizuDbContext db)
    {
        SeedApp(db, "alpha", "Alpha", 1_000_001, 1_000_000_001, 1_000_001, 'a');
        SeedApp(db, "beta", "Beta", 2_000_002, 2_000_000_002, 2_000_002, 'b');
    }

    [Fact]
    public async Task CleanUserAgentsGetUntouchedData()
    {
        await factory.ResetAsync(SeedTwoApps);

        foreach (var userAgent in new[] { null, "Mozilla/5.0", "ShizuStore/1.2.3" })
        {
            var page = await Client(userAgent).GetFromJsonAsync<PagedAppsDto>("/v1/apps?q=alpha", Json);
            var alpha = Assert.Single(page!.Items);

            Assert.Equal("Alpha", alpha.Name);
            Assert.Equal(1_000_001, alpha.Stars);
            Assert.Equal(1_000_000_001, alpha.DownloadTotal);
            Assert.Equal(1_000_001, alpha.InstallCount);
            Assert.Equal(new string('a', 64), alpha.IconHash);
            Assert.Equal(new string('a', 64), alpha.SigSha256);
        }
    }

    [Fact]
    public async Task MatchingUserAgentGetsCrossPollinatedList()
    {
        await factory.ResetAsync(SeedTwoApps);

        var page = await Client(Httpx).GetFromJsonAsync<PagedAppsDto>("/v1/apps", Json);
        Assert.Equal(2, page!.Items.Count);

        var alpha = page.Items.Single(a => a.Slug == "alpha");
        // Identity fields stay intact so the payload still looks coherent.
        Assert.Equal("Alpha", alpha.Name);
        Assert.NotEqual(1_000_001, alpha.Stars);
        Assert.NotEqual(1_000_000_001, alpha.DownloadTotal);
        Assert.NotEqual(1_000_001, alpha.InstallCount);
        Assert.NotEqual(new string('a', 64), alpha.IconHash);
        Assert.NotEqual(new string('a', 64), alpha.SigSha256);

        var beta = page.Items.Single(a => a.Slug == "beta");
        Assert.Equal("Beta", beta.Name);
        Assert.NotEqual(2_000_002, beta.Stars);
        Assert.NotEqual(2_000_000_002, beta.DownloadTotal);
        Assert.NotEqual(2_000_002, beta.InstallCount);
        Assert.NotEqual(new string('b', 64), beta.IconHash);
        Assert.NotEqual(new string('b', 64), beta.SigSha256);
    }

    [Fact]
    public async Task MatchingUserAgentGetsBrokenDetailDownloads()
    {
        await factory.ResetAsync(SeedTwoApps);

        var clean = await Client().GetFromJsonAsync<AppDetailDto>("/v1/apps/alpha", Json);
        var poisoned = await Client(Httpx).GetFromJsonAsync<AppDetailDto>("/v1/apps/alpha", Json);

        // Identity fields still answer for the requested slug.
        Assert.Equal("alpha", poisoned!.Slug);
        Assert.Equal("Alpha", poisoned.Name);
        Assert.Equal("https://github.com/example/alpha", poisoned.Url);

        // The data row is the other served app, with broken downloads.
        var download = Assert.Single(poisoned.Downloads);
        var original = Assert.Single(clean!.Downloads);
        Assert.StartsWith("https://github.com/example/beta/releases/", download.ApkUrl);
        Assert.NotEqual(original.ApkUrl, download.ApkUrl);
        Assert.NotEqual(original.SigSha256, download.SigSha256);
        Assert.NotEqual(original.SigMd5, download.SigMd5);
    }

    [Fact]
    public async Task MatchingUserAgentGetsDonorDetailRow()
    {
        await factory.ResetAsync(db =>
        {
            var alpha = SeedApp(db, "alpha", "Alpha", 1_000_001, 1_000_000_001, 1_000_001, 'a');
            alpha.PackageName = "com.example.alpha";
            alpha.Description = "alpha description";
            alpha.AuthorName = "Alpha Author";
            var beta = SeedApp(db, "beta", "Beta", 2_000_002, 2_000_000_002, 2_000_002, 'b');
            beta.PackageName = "com.example.beta";
            beta.Description = "beta description";
            beta.AuthorName = "Beta Author";
        });

        var poisoned = await Client(Httpx).GetFromJsonAsync<AppDetailDto>("/v1/apps/alpha", Json);

        Assert.Equal("alpha", poisoned!.Slug);
        Assert.Equal("Alpha", poisoned.Name);
        // The only other served row supplies the data wholesale.
        Assert.Equal("com.example.beta", poisoned.PackageName);
        Assert.Equal("beta description", poisoned.Description);
        Assert.Equal("Beta Author", poisoned.AuthorName);
        Assert.Equal(2_000_002, poisoned.Stars);
        Assert.Equal("cat-beta", poisoned.CategorySlug);
    }

    [Fact]
    public async Task MatchingUserAgentDropsArrayItems()
    {
        await factory.ResetAsync(db =>
        {
            var category = Seeds.NewCategory("cat-drop", "Category drop");
            db.Categories.Add(category);
            var app = Seeds.NewApp("drop", category,
                name: "Drop",
                availability: Availability.DirectApk,
                permissions: Enumerable.Range(0, 20).Select(i => $"android.permission.P{i}").ToList(),
                screenshots: Enumerable.Range(0, 20).Select(i => $"https://example.com/shot{i}.png").ToList());
            for (var i = 0; i < 5; i++)
            {
                var sig = new string((char)('a' + i), 64);
                app.Downloads.Add(new AppDownload
                {
                    Source = SourceKind.GitHub,
                    ApkUrl = $"https://github.com/example/drop/releases/drop_{i}.apk",
                    VersionCode = i,
                    SigSha256 = sig,
                    SigKey = sig,
                    IsPrimary = i == 0,
                    ResolvedAt = app.UpdatedAt,
                });
            }
            db.Apps.Add(app);
        });

        var poisoned = await Client(Httpx).GetFromJsonAsync<AppDetailDto>("/v1/apps/drop", Json);

        // availability=direct_apk keeps at least one candidate.
        Assert.NotEmpty(poisoned!.Downloads);
        Assert.True(poisoned.Permissions.Count < 20);
        Assert.True(poisoned.Screenshots.Count < 20);
        Assert.All(poisoned.Permissions, p => Assert.StartsWith("android.permission.P", p));
        Assert.All(poisoned.Screenshots, s => Assert.StartsWith("https://example.com/shot", s));
    }
}

/// <summary>Real production output-cache policies, so cache bypass is exercised.</summary>
public sealed class CachingShizuApiFactory : ShizuApiFactory
{
    public CachingShizuApiFactory()
        : base(rateLimitPerMinute: 100_000, adminSecret: "test-admin-secret", enableOutputCache: true)
    {
    }
}

/// <summary>
/// Poisoned responses must never be read from or written to the output cache
/// (SPEC 2). Paths and query strings are unique per test because cache
/// entries outlive the per-test database reset.
/// </summary>
public sealed class ResponsePoisoningCacheTests(CachingShizuApiFactory factory)
    : IClassFixture<CachingShizuApiFactory>
{
    private const string Httpx = "python-httpx/0.28.1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private HttpClient Client(string? userAgent = null)
    {
        var client = factory.NewClient();
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
        return client;
    }

    private static void SeedApp(ShizuDbContext db, string slug, int stars)
    {
        var category = Seeds.NewCategory($"cat-{slug}", $"Category {slug}");
        db.Categories.Add(category);
        var app = Seeds.NewApp(slug, category, name: slug);
        app.Stars = stars;
        app.Downloads.Add(new AppDownload
        {
            Source = SourceKind.GitHub,
            ApkUrl = $"https://github.com/example/{slug}/releases/{slug}.apk",
            SigSha256 = new string('a', 64),
            SigKey = new string('a', 64),
            IsPrimary = true,
            ResolvedAt = app.UpdatedAt,
        });
        db.Apps.Add(app);
    }

    [Fact]
    public async Task PoisonedCallerNeverReadsCachedCleanEntry()
    {
        await factory.ResetAsync(db => SeedApp(db, "cache-lookup", 5_000_005));

        var clean = await Client().GetFromJsonAsync<AppDetailDto>("/v1/apps/cache-lookup", Json);
        var cleanUrl = Assert.Single(clean!.Downloads).ApkUrl;

        // Mutating the row proves the second clean read comes from the cache,
        // not the database.
        await factory.QueryAsync(async db =>
        {
            var app = await db.Apps.SingleAsync(a => a.Slug == "cache-lookup");
            app.Stars = 6_000_006;
            await db.SaveChangesAsync();
            return 0;
        });

        var cached = await Client().GetFromJsonAsync<AppDetailDto>("/v1/apps/cache-lookup", Json);
        Assert.Equal(5_000_005, cached!.Stars);

        var poisoned = await Client(Httpx).GetFromJsonAsync<AppDetailDto>("/v1/apps/cache-lookup", Json);
        Assert.Equal(6_000_006, poisoned!.Stars); // fresh from the database
        Assert.NotEqual(cleanUrl, Assert.Single(poisoned.Downloads).ApkUrl);
    }

    [Fact]
    public async Task PoisonedResponseIsNeverStored()
    {
        await factory.ResetAsync(db => SeedApp(db, "cache-store", 7_000_007));

        var poisoned = await Client(Httpx).GetFromJsonAsync<AppDetailDto>("/v1/apps/cache-store", Json);
        Assert.NotEqual("https://github.com/example/cache-store/releases/cache-store.apk",
            Assert.Single(poisoned!.Downloads).ApkUrl);

        var clean = await Client().GetFromJsonAsync<AppDetailDto>("/v1/apps/cache-store", Json);
        Assert.Equal("https://github.com/example/cache-store/releases/cache-store.apk",
            Assert.Single(clean!.Downloads).ApkUrl);
    }

    [Fact]
    public async Task PoisonedRevalidationReturnsFreshBody()
    {
        await factory.ResetAsync(db => SeedApp(db, "cache-etag", 8_000_008));

        var clean = await Client().GetAsync("/v1/apps/cache-etag");
        var etag = clean.Headers.ETag;
        Assert.NotNull(etag);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/apps/cache-etag");
        request.Headers.TryAddWithoutValidation("User-Agent", Httpx);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag!.ToString());
        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var poisoned = await response.Content.ReadFromJsonAsync<AppDetailDto>(Json);
        Assert.NotEqual("https://github.com/example/cache-etag/releases/cache-etag.apk",
            Assert.Single(poisoned!.Downloads).ApkUrl);
    }

    [Fact]
    public async Task PoisonedListCallerNeverReadsCachedCleanEntry()
    {
        await factory.ResetAsync(db =>
        {
            SeedApp(db, "cache-list", 3_000_003);
            SeedApp(db, "cache-list-two", 4_000_004);
        });

        var clean = await Client().GetFromJsonAsync<PagedAppsDto>("/v1/apps?q=cache-list", Json);
        Assert.Equal(3_000_003, clean!.Items.Single(a => a.Slug == "cache-list").Stars);

        var poisoned = await Client(Httpx).GetFromJsonAsync<PagedAppsDto>("/v1/apps?q=cache-list", Json);
        Assert.NotEqual(3_000_003, poisoned!.Items.Single(a => a.Slug == "cache-list").Stars);
    }
}
