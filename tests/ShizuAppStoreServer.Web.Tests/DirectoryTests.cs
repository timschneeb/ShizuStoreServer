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

        Assert.Equal(3, tree.Count); // audio, vendor-specific, coroutines (insertion order)
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
