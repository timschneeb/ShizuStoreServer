using System.Net;
using System.Text.Json;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api.Tests;

public sealed class TrendingTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // ResetAsync persists only after the seed action returns, so tests wire
    // day rows through these instances instead of querying the database.
    private static (App Micup, App Tuner, App Hidden, App Pending) SeedDirectory(ShizuDbContext db)
    {
        var audio = Seeds.NewCategory("audio", "Audio");
        db.Categories.Add(audio);
        var micup = Seeds.NewApp("micup", audio, license: "MIT",
            packageName: "com.example.micup");
        var tuner = Seeds.NewApp("tuner", audio, license: "GPL-3.0");
        var hidden = Seeds.NewApp("hidden", audio, availability: Availability.Excluded);
        var pending = Seeds.NewApp("pending", audio, published: false);
        db.Apps.AddRange(micup, tuner, hidden, pending);
        return (micup, tuner, hidden, pending);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await factory.NewClient().GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    [Fact]
    public async Task TrendingRanksByWindowInstallsAndHidesUnpublished()
    {
        var today = Today;
        await factory.ResetAsync(db =>
        {
            var (micup, tuner, hidden, _) = SeedDirectory(db);
            db.AppInstallDays.AddRange(
                new AppInstallDay { App = micup, Day = today, InstallCount = 3 },
                new AppInstallDay { App = tuner, Day = today, InstallCount = 5 },
                new AppInstallDay { App = tuner, Day = today.AddDays(-2), InstallCount = 2 },
                // Excluded rows never trend, whatever their counts.
                new AppInstallDay { App = hidden, Day = today, InstallCount = 99 });
        });

        var trending = await GetAsync<TrendingDto>("/v1/trending");

        Assert.Equal(7, trending.WindowDays);
        Assert.Equal("installs", trending.Sort);
        Assert.Equal(["tuner", "micup"], trending.Items.Select(i => i.Slug));
        Assert.Equal([7L, 3L], trending.Items.Select(i => i.Installs));
        Assert.Equal([0L, 0L], trending.Items.Select(i => i.PreviousInstalls));
        Assert.Equal([7L, 3L], trending.Items.Select(i => i.Delta));
    }

    [Fact]
    public async Task TrendingGrowthSortUsesPreviousWindowDelta()
    {
        var today = Today;
        await factory.ResetAsync(db =>
        {
            var (micup, tuner, _, _) = SeedDirectory(db);
            db.AppInstallDays.AddRange(
                // micup: big last window, small now -> large negative delta.
                new AppInstallDay { App = micup, Day = today.AddDays(-8), InstallCount = 100 },
                new AppInstallDay { App = micup, Day = today, InstallCount = 5 },
                // tuner: nothing before, some now -> positive delta.
                new AppInstallDay { App = tuner, Day = today, InstallCount = 3 });
        });

        var installs = await GetAsync<TrendingDto>("/v1/trending?sort=installs");
        Assert.Equal(["micup", "tuner"], installs.Items.Select(i => i.Slug));

        var growth = await GetAsync<TrendingDto>("/v1/trending?sort=growth");
        Assert.Equal("growth", growth.Sort);
        Assert.Equal(["tuner", "micup"], growth.Items.Select(i => i.Slug));
        Assert.Equal([3L, -95L], growth.Items.Select(i => i.Delta));
        Assert.Equal([0L, 100L], growth.Items.Select(i => i.PreviousInstalls));
    }

    [Fact]
    public async Task TrendingRejectsOutOfRangeParameters()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/trending?days=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/trending?days=91")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/trending?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/trending?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/trending?sort=nope")).StatusCode);
    }

    [Fact]
    public async Task HistoryZeroFillsInstallsAndCarriesStars()
    {
        var today = Today;
        var windowStart = today.AddDays(-6);
        await factory.ResetAsync(db =>
        {
            var (micup, _, _, _) = SeedDirectory(db);
            db.AppInstallDays.Add(
                new AppInstallDay { App = micup, Day = today.AddDays(-2), InstallCount = 4 });
            db.AppStarDays.AddRange(
                new AppStarDay { App = micup, Day = today.AddDays(-20), Stars = 7 },
                new AppStarDay { App = micup, Day = today.AddDays(-2), Stars = 9 });
        });

        var history = await GetAsync<AppHistoryDto>("/v1/apps/micup/history?days=7");

        Assert.Equal("micup", history.Slug);
        Assert.Equal(7, history.Installs.Count);
        Assert.Equal(Day(windowStart), history.Installs[0].Day);
        Assert.Equal(Day(today), history.Installs[^1].Day);
        Assert.Equal(4, history.Installs.Single(d => d.Day == Day(today.AddDays(-2))).Count);
        Assert.Equal(6, history.Installs.Count(d => d.Count == 0));

        Assert.Equal(7, history.Stars.Count);
        Assert.Equal([7, 7, 7, 7, 9, 9, 9], history.Stars.Select(s => s.Stars));
    }

    [Fact]
    public async Task HistoryOmitsStarDaysBeforeFirstSnapshot()
    {
        var today = Today;
        await factory.ResetAsync(db =>
        {
            var (micup, _, _, _) = SeedDirectory(db);
            db.AppStarDays.Add(new AppStarDay { App = micup, Day = today.AddDays(-1), Stars = 5 });
        });

        var history = await GetAsync<AppHistoryDto>("/v1/apps/micup/history?days=7");

        // Installs still cover the whole window with zeros.
        Assert.Equal(7, history.Installs.Count);
        Assert.All(history.Installs, d => Assert.Equal(0, d.Count));

        // Stars start at the first known snapshot instead of inventing values.
        Assert.Equal(2, history.Stars.Count);
        Assert.Equal(Day(today.AddDays(-1)), history.Stars[0].Day);
        Assert.Equal([5, 5], history.Stars.Select(s => s.Stars));
    }

    [Fact]
    public async Task HistoryRejectsHiddenUnknownAndBadDays()
    {
        var client = factory.NewClient();
        await factory.ResetAsync(db => { SeedDirectory(db); });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/apps/hidden/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/apps/pending/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/apps/no-such-app/history")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/apps/micup/history?days=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/apps/micup/history?days=366")).StatusCode);
    }
}
