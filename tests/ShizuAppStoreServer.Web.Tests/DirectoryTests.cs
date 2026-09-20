using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class CategoriesTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task TreeHasSubtreeCountsExcludingExcluded()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            var vendor = Seeds.NewCategory("vendor-specific", "Vendor-specific");
            var miui = Seeds.NewCategory("miui", "MIUI", parent: vendor);
            var libs = Seeds.NewCategory("coroutines", "Coroutines", section: CategorySection.Libraries);
            db.Categories.AddRange(audio, vendor, miui, libs);
            db.Apps.AddRange(
                Seeds.NewApp("micup", audio),
                Seeds.NewApp("tuner", audio),
                Seeds.NewApp("aura", miui),
                Seeds.NewApp("pixel-only", miui),
                Seeds.NewApp("hidden", miui, availability: Availability.Excluded),
                Seeds.NewApp("libx", libs));
        });

        var response = await factory.NewClient().GetAsync("/v1/categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = (await response.Content.ReadFromJsonAsync<List<CategoryNodeDto>>(Json))!;

        Assert.Equal(3, tree.Count); // audio, coroutines, vendor-specific (name order)
        var audio = tree.Single(n => n.Slug == "audio");
        Assert.Equal(2, audio.AppCount);
        Assert.Empty(audio.Children);
        Assert.Equal("apps", audio.Section);

        var vendor = tree.Single(n => n.Slug == "vendor-specific");
        Assert.Single(vendor.Children); // excluded row does not create nodes, only counts
        Assert.Equal(2, vendor.Children[0].AppCount); // aura + pixel-only, not hidden
        Assert.Equal(2, vendor.AppCount); // subtree total

        var libs = tree.Single(n => n.Slug == "coroutines");
        Assert.Equal("libraries", libs.Section);
        Assert.Equal(1, libs.AppCount);
    }

    [Fact]
    public async Task TreeIsNameSortedPerLevel()
    {
        await factory.ResetAsync(db =>
        {
            var vendor = Seeds.NewCategory("vendor-specific", "Vendor-specific");
            var audio = Seeds.NewCategory("audio", "Audio");
            var libs = Seeds.NewCategory("coroutines", "Coroutines", section: CategorySection.Libraries);
            var oneui = Seeds.NewCategory("oneui", "OneUI", parent: vendor);
            var pixel = Seeds.NewCategory("pixel", "Pixel", parent: vendor);
            var miui = Seeds.NewCategory("miui", "MIUI", parent: vendor);
            db.Categories.AddRange(vendor, audio, libs, oneui, pixel, miui);
        });

        var response = await factory.NewClient().GetAsync("/v1/categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = (await response.Content.ReadFromJsonAsync<List<CategoryNodeDto>>(Json))!;

        Assert.Equal(["Audio", "Coroutines", "Vendor-specific"], tree.Select(n => n.Name));
        var vendor = tree.Single(n => n.Slug == "vendor-specific");
        Assert.Equal(["MIUI", "OneUI", "Pixel"], vendor.Children.Select(n => n.Name));
    }

    [Fact]
    public async Task TreeCountsScopeToRequestedListings()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.AddRange(
                Seeds.NewApp("micup", audio),
                Seeds.NewApp("aura", audio, listing: Listing.ClosedSource));
        });

        var main = await factory.NewClient().GetAsync("/v1/categories");
        var mainTree = (await main.Content.ReadFromJsonAsync<List<CategoryNodeDto>>(Json))!;
        Assert.Equal(1, mainTree.Single(n => n.Slug == "audio").AppCount);

        var both = await factory.NewClient()
            .GetAsync("/v1/categories?listing=main,closed_source");
        var bothTree = (await both.Content.ReadFromJsonAsync<List<CategoryNodeDto>>(Json))!;
        Assert.Equal(2, bothTree.Single(n => n.Slug == "audio").AppCount);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await factory.NewClient().GetAsync("/v1/categories?listing=bogus")).StatusCode);
    }

    [Fact]
    public async Task TreeEtagReturns304()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp("micup", audio));
        });

        var client = factory.NewClient();
        var first = await client.GetAsync("/v1/categories");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/v1/categories");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }
}

public sealed class ChangesTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const string Since = "2026-06-01T00:00:00Z";

    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul1 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul2 = new(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul3 = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SinceIsRequiredAndValidated()
    {
        var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/changes?since=not-a-date")).StatusCode);
    }

    [Fact]
    public async Task BucketsAddedUpdatedAndRemoved()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.AddRange(
                Seeds.NewApp("fresh", audio, addedAt: Jul2, updatedAt: Jul3), // added
                Seeds.NewApp("bumped", audio, addedAt: Jan, updatedAt: Jul1), // updated
                Seeds.NewApp("bumped2", audio, addedAt: Jan, updatedAt: Jul3), // updated
                Seeds.NewApp("stale", audio, addedAt: Jan, updatedAt: Jan), // neither
                Seeds.NewApp("hidden-fresh", audio, addedAt: Jul2, updatedAt: Jul2,
                    availability: Availability.Excluded)); // never listed
            db.RemovedApps.AddRange(
                new RemovedApp { Slug = "gone", Name = "Gone", Listing = Listing.Main, RemovedAt = Jul2 },
                new RemovedApp { Slug = "long-gone", Listing = Listing.Main, RemovedAt = Jan });
        });

        var response = await factory.NewClient().GetAsync("/v1/changes?since=" + Since);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = (await response.Content.ReadFromJsonAsync<ChangesDto>(Json))!;

        Assert.Equal(["fresh"], changes.Added.Select(a => a.Slug));
        // Oldest-first, already-added rows are not repeated in updated[].
        Assert.Equal(["bumped", "bumped2"], changes.Updated.Select(a => a.Slug));
        Assert.DoesNotContain(changes.Added.Concat(changes.Updated),
            a => a.Slug == "stale" || a.Slug == "hidden-fresh");
        Assert.Equal(["gone"], changes.Removed.Select(r => r.Slug));
        Assert.Equal("Gone", changes.Removed[0].Name);
    }

    [Fact]
    public async Task ListingFilterScopesChangesAndTombstones()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.AddRange(
                Seeds.NewApp("main-fresh", audio, addedAt: Jul2, updatedAt: Jul2),
                Seeds.NewApp("closed-fresh", audio, listing: Listing.ClosedSource,
                    addedAt: Jul2, updatedAt: Jul2));
            db.RemovedApps.AddRange(
                new RemovedApp
                {
                    Slug = "main-gone", Name = "Main Gone",
                    Listing = Listing.Main, RemovedAt = Jul2,
                },
                new RemovedApp
                {
                    Slug = "closed-gone", Name = "Closed Gone",
                    Listing = Listing.ClosedSource, RemovedAt = Jul2,
                });
        });

        var mainResponse = await factory.NewClient().GetAsync("/v1/changes?since=" + Since);
        var main = (await mainResponse.Content.ReadFromJsonAsync<ChangesDto>(Json))!;
        Assert.Equal(["main-fresh"], main.Added.Select(a => a.Slug));
        Assert.Equal(["main-gone"], main.Removed.Select(r => r.Slug));

        var bothResponse = await factory.NewClient()
            .GetAsync("/v1/changes?since=" + Since + "&listing=main,closed_source");
        var both = (await bothResponse.Content.ReadFromJsonAsync<ChangesDto>(Json))!;
        Assert.Equal(["closed-fresh", "main-fresh"], both.Added.Select(a => a.Slug).Order());
        Assert.Equal(["closed-gone", "main-gone"], both.Removed.Select(r => r.Slug).Order());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await factory.NewClient()
                .GetAsync("/v1/changes?since=" + Since + "&listing=bogus")).StatusCode);
    }

    [Fact]
    public async Task InstallsUpdatedCarriesOnlyPostCursorCounts()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            var counted = Seeds.NewApp("counted", audio, addedAt: Jan, updatedAt: Jan);
            counted.InstallCount = 5;
            counted.InstallCountUpdatedAt = Jul2;
            var oldCount = Seeds.NewApp("old-count", audio, addedAt: Jan, updatedAt: Jan);
            oldCount.InstallCount = 9;
            oldCount.InstallCountUpdatedAt = Jan;
            var hiddenCount = Seeds.NewApp("hidden-count", audio, addedAt: Jan, updatedAt: Jan,
                availability: Availability.Excluded);
            hiddenCount.InstallCount = 3;
            hiddenCount.InstallCountUpdatedAt = Jul3;
            db.Apps.AddRange(
                counted,
                oldCount,
                Seeds.NewApp("never-installed", audio, addedAt: Jan, updatedAt: Jan),
                hiddenCount);
        });

        var response = await factory.NewClient().GetAsync("/v1/changes?since=" + Since);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = (await response.Content.ReadFromJsonAsync<ChangesDto>(Json))!;

        // Only the post-cursor count rides along; nothing here qualifies as
        // added/updated, and the excluded row never appears.
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Updated);
        Assert.Equal(new Dictionary<string, long> { ["counted"] = 5 }, changes.InstallsUpdated);
    }

    [Fact]
    public async Task PurgeRequestedAtIsNullWithoutConfigFlag()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/v1/changes?since=" + Since);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = (await response.Content.ReadFromJsonAsync<ChangesDto>(Json))!;

        Assert.Null(changes.CatalogPurgeRequestedAt);
    }

    [Fact]
    public async Task PurgeRequestedAtSurfacesTheFlagAndIgnoresGarbage()
    {
        var purgeAt = new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
        await factory.ResetAsync(db =>
        {
            db.ConfigFlags.Add(new ConfigFlag
            {
                Key = ConfigFlags.CatalogPurgeRequestedAt,
                Value = purgeAt.ToString("O"),
                UpdatedAt = purgeAt,
            });
        });

        var response = await factory.NewClient().GetAsync("/v1/changes?since=" + Since);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = (await response.Content.ReadFromJsonAsync<ChangesDto>(Json))!;
        Assert.Equal(purgeAt, changes.CatalogPurgeRequestedAt);

        await factory.ResetAsync(db =>
        {
            db.ConfigFlags.Add(new ConfigFlag
            {
                Key = ConfigFlags.CatalogPurgeRequestedAt,
                Value = "not-a-date",
                UpdatedAt = purgeAt,
            });
        });

        var garbage = (await (await factory.NewClient().GetAsync("/v1/changes?since=" + Since))
            .Content.ReadFromJsonAsync<ChangesDto>(Json))!;
        Assert.Null(garbage.CatalogPurgeRequestedAt);
    }
}

public sealed class MetaTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ReturnsCountsAndLatestCommit()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.AddRange(
                Seeds.NewApp("micup", audio),
                Seeds.NewApp("hidden", audio, availability: Availability.Excluded));
            db.SyncRuns.AddRange(
                new SyncRun { StartedAt = DateTimeOffset.UtcNow, Trigger = "backfill", HeadCommit = "abc123" },
                new SyncRun { StartedAt = DateTimeOffset.UtcNow, Trigger = "scheduled", HeadCommit = "def456" });
        });

        var response = await factory.NewClient().GetAsync("/v1/meta");
        var metaBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {metaBody}");
        var meta = JsonSerializer.Deserialize<MetaDto>(metaBody, Json)!;

        Assert.Equal("def456", meta.ListCommit);
        Assert.Equal(1, meta.Counts.Apps);
        Assert.Equal(1, meta.Counts.Categories);
        Assert.True(meta.GeneratedAt <= DateTimeOffset.UtcNow);
        Assert.False(meta.UseInstallCountsForPopularity);
    }

    [Fact]
    public async Task MetaSurfacesPopularityFlag()
    {
        await factory.ResetAsync(db =>
        {
            db.ConfigFlags.Add(new ConfigFlag
            {
                Key = ConfigFlags.UseInstallCountsForPopularity,
                Value = "true",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        });

        var response = await factory.NewClient().GetAsync("/v1/meta");
        var metaBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {metaBody}");
        Assert.True(JsonSerializer.Deserialize<MetaDto>(metaBody, Json)!.UseInstallCountsForPopularity);
    }

    [Fact]
    public async Task NoRunsYieldsNullCommit()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/v1/meta");
        var meta = (await response.Content.ReadFromJsonAsync<MetaDto>(Json))!;

        Assert.Null(meta.ListCommit);
        Assert.Equal(0, meta.Counts.Apps);
    }
}

public sealed class HealthAndOpenApiTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task HealthzIsOk()
    {
        var response = await factory.NewClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = (await response.Content.ReadFromJsonAsync<HealthDto>(Json))!;
        Assert.Equal("ok", health.Status);
    }

    [Fact]
    public async Task OpenApiSpecIsServed()
    {
        var response = await factory.NewClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/v1/apps", body);
    }
}
