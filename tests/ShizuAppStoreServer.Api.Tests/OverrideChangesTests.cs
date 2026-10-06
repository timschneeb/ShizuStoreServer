using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Overrides;

namespace ShizuAppStoreServer.Api.Tests;

/// <summary>
/// End-to-end delta contract for operator edits: a real override or blocklist
/// edit must surface through <c>/v1/changes</c> even before the next sync pass
/// materializes it. The trigger behavior itself lives in the Postgres
/// migration; <see cref="SqliteOverrideTriggers"/> reproduces it here.
/// </summary>
public sealed class OverrideChangesTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul2 = new(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul3 = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul4 = new(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jul5 = new(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
    private const string Since = "2026-07-01T00:00:00Z";

    [Fact]
    public async Task OverrideEditTouchesTheAppAndTheApplierMaterializes()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp("tuner", audio, name: "Tuner", displayName: "Tuner",
                addedAt: Jan, updatedAt: Jan));
        });
        await InstallTriggersAsync(Jul2);

        Assert.Equal(Jan, await UpdatedAtAsync("tuner"));
        Assert.DoesNotContain((await ChangesAsync()).Updated, a => a.Slug == "tuner");

        await InsertOverrideAsync("tuner", "display_name", "Overridden Tuner", Jul2);

        Assert.Equal(Jul2, await UpdatedAtAsync("tuner"));
        var pending = await ChangesAsync();
        Assert.Contains(pending.Updated, a => a.Slug == "tuner");
        // Only the trigger ran: the served value is still natural until a pass.
        Assert.Equal("Tuner", pending.Updated.Single(a => a.Slug == "tuner").Name);

        Assert.Equal(1, await ApplyOverridesAsync(Jul3));

        var row = await factory.QueryAsync(db => db.AppOverrides.AsNoTracking().SingleAsync());
        Assert.Equal("Overridden Tuner", row.AppliedValue);
        Assert.Equal("Tuner", row.BaselineValue);
        var materialized = await ChangesAsync();
        Assert.Equal("Overridden Tuner", materialized.Updated.Single(a => a.Slug == "tuner").Name);
    }

    [Fact]
    public async Task ValueEditsAndSoftDeletesBumpButBaselineWritesDoNot()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp("bump", audio, addedAt: Jan, updatedAt: Jan));
        });
        await InstallTriggersAsync(Jul2);

        await InsertOverrideAsync("bump", "display_name", "First", Jul2);
        Assert.Equal(Jul2, await UpdatedAtAsync("bump"));

        // The applier records the captured baseline after materializing; that
        // write-back must not look like an operator edit.
        await MutateOverrideAsync("bump", row =>
        {
            row.AppliedValue = row.Value;
            row.BaselineValue = "natural";
        });
        Assert.Equal(Jul2, await UpdatedAtAsync("bump"));

        await ResetClockAsync("bump", Jan);
        await MutateOverrideAsync("bump", row => row.Value = "Second");
        Assert.Equal(Jul2, await UpdatedAtAsync("bump"));

        await ResetClockAsync("bump", Jan);
        await MutateOverrideAsync("bump", row => row.DeletedAt = Jul3);
        Assert.Equal(Jul2, await UpdatedAtAsync("bump"));

        await ResetClockAsync("bump", Jan);
        await DeleteOverrideAsync("bump");
        Assert.Equal(Jul2, await UpdatedAtAsync("bump"));
    }

    [Fact]
    public async Task BlockedScreenshotEditsTouchOnlyAppsCarryingTheUrl()
    {
        const string url00 = "https://example.com/screens/00.png";
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.AddRange(
                Seeds.NewApp("has-00", audio, addedAt: Jan, updatedAt: Jan,
                    screenshots: [url00, "https://example.com/screens/01.png"]),
                Seeds.NewApp("has-02", audio, addedAt: Jan, updatedAt: Jan,
                    screenshots: ["https://example.com/screens/02.png"]),
                Seeds.NewApp("override-carrier", audio, addedAt: Jan, updatedAt: Jan));
            db.AppOverrides.Add(new AppOverride
            {
                AppSlug = "override-carrier", Field = "screenshots", Value = url00,
                CreatedAt = Jan, UpdatedAt = Jan,
            });
        });
        await InstallTriggersAsync(Jul2);

        await factory.QueryAsync(async db =>
        {
            db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl
            {
                Url = url00, CreatedAt = Jul2, UpdatedAt = Jul2,
            });
            await db.SaveChangesAsync();
            return true;
        });

        Assert.Equal(Jul2, await UpdatedAtAsync("has-00"));
        Assert.Equal(Jan, await UpdatedAtAsync("has-02"));
        // The active screenshots override references the URL the app will show.
        Assert.Equal(Jul2, await UpdatedAtAsync("override-carrier"));

        await ResetClockAsync("has-00", Jan);
        await ResetClockAsync("override-carrier", Jan);
        await MutateBlockedAsync(url => url.Note = "false detection");
        Assert.Equal(Jan, await UpdatedAtAsync("has-00"));
        Assert.Equal(Jan, await UpdatedAtAsync("override-carrier"));

        await ResetClockAsync("has-00", Jan);
        await ResetClockAsync("override-carrier", Jan);
        await MutateBlockedAsync(url => url.DeletedAt = Jul3);
        Assert.Equal(Jul2, await UpdatedAtAsync("has-00"));
        Assert.Equal(Jul2, await UpdatedAtAsync("override-carrier"));

        await ResetClockAsync("has-00", Jan);
        await ResetClockAsync("override-carrier", Jan);
        await factory.QueryAsync(async db =>
        {
            db.BlockedScreenshotUrls.Remove(await db.BlockedScreenshotUrls.SingleAsync());
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Equal(Jul2, await UpdatedAtAsync("has-00"));
        Assert.Equal(Jul2, await UpdatedAtAsync("override-carrier"));
    }

    [Fact]
    public async Task VisibilityOverrideRemovesThenRestoresThroughChanges()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp("hideable", audio, addedAt: Jan, updatedAt: Jan,
                availability: Availability.DirectApk));
        });
        await InstallTriggersAsync(Jul2);

        await InsertOverrideAsync("hideable", "availability", "excluded", Jul2);
        Assert.Contains((await ChangesAsync()).Updated, a => a.Slug == "hideable");

        Assert.Equal(1, await ApplyOverridesAsync(Jul3));
        var excluded = await factory.QueryAsync(db => db.Apps.AsNoTracking()
            .Select(a => new { a.Availability, a.ExcludedReason }).SingleAsync());
        Assert.Equal(Availability.Excluded, excluded.Availability);
        Assert.Equal(AppOverrideApplier.ExcludedReason, excluded.ExcludedReason);

        var removed = await ChangesAsync();
        Assert.DoesNotContain(removed.Updated, a => a.Slug == "hideable");
        var tombstone = removed.Removed.Single(r => r.Slug == "hideable");
        Assert.Equal(Jul3, tombstone.RemovedAt);

        await MutateOverrideAsync("hideable", row => row.DeletedAt = Jul4);
        Assert.Equal(1, await ApplyOverridesAsync(Jul5));

        var restored = await factory.QueryAsync(db => db.Apps.AsNoTracking().SingleAsync());
        Assert.Equal(Availability.DirectApk, restored.Availability);
        Assert.Null(restored.ExcludedReason);
        Assert.Null(restored.LastCheckedAt);
        Assert.False(await factory.QueryAsync(db => db.RemovedApps.AsNoTracking().AnyAsync()));
        Assert.Contains((await ChangesAsync()).Updated, a => a.Slug == "hideable");
    }

    [Fact]
    public async Task SourceKindOverrideEditTouchesAndForcesOneRecheck()
    {
        await factory.ResetAsync(db =>
        {
            var audio = Seeds.NewCategory("audio", "Audio");
            db.Categories.Add(audio);
            db.Apps.Add(Seeds.NewApp("instafel", audio, addedAt: Jan, updatedAt: Jan));
        });
        await InstallTriggersAsync(Jul2);

        await InsertOverrideAsync("instafel", "source_release_home", "instafel/u-rel", Jul2);

        // The trigger surfaces the edit immediately; the applier then consumes
        // the row so the next enrich resolves the new release home once.
        Assert.Equal(Jul2, await UpdatedAtAsync("instafel"));
        Assert.Contains((await ChangesAsync()).Updated, a => a.Slug == "instafel");

        Assert.Equal(1, await ApplyOverridesAsync(Jul3));

        var state = await factory.QueryAsync(db => db.Apps.AsNoTracking()
            .Select(a => new { a.LastCheckedAt, a.UpdatedAt }).SingleAsync());
        Assert.Null(state.LastCheckedAt);
        Assert.Equal(Jul2, state.UpdatedAt);
        var row = await factory.QueryAsync(db => db.AppOverrides.AsNoTracking().SingleAsync());
        Assert.Equal("instafel/u-rel", row.AppliedValue);
        Assert.Null(row.BaselineValue);

        // A consumed row is inert: only a value edit forces another recheck.
        await factory.QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE apps SET last_checked_at = {Jul4} WHERE slug = 'instafel'"));
        Assert.Equal(0, await ApplyOverridesAsync(Jul5));
        Assert.Equal(Jul4, await factory.QueryAsync(db =>
            db.Apps.AsNoTracking().Select(a => a.LastCheckedAt).SingleAsync()));
    }

    private Task InstallTriggersAsync(DateTimeOffset stamp) =>
        factory.QueryAsync(async db =>
        {
            await SqliteOverrideTriggers.InstallAsync(db, stamp);
            return true;
        });

    private async Task<ChangesDto> ChangesAsync()
    {
        var response = await factory.NewClient().GetAsync("/v1/changes?since=" + Since);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChangesDto>(Json))!;
    }

    private Task<DateTimeOffset> UpdatedAtAsync(string slug) =>
        factory.QueryAsync(db => db.Apps.AsNoTracking()
            .Where(a => a.Slug == slug).Select(a => a.UpdatedAt).SingleAsync());

    private Task ResetClockAsync(string slug, DateTimeOffset at) =>
        factory.QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE apps SET updated_at = {at} WHERE slug = {slug}"));

    private Task InsertOverrideAsync(string slug, string field, string value, DateTimeOffset at) =>
        factory.QueryAsync(async db =>
        {
            db.AppOverrides.Add(new AppOverride
            {
                AppSlug = slug, Field = field, Value = value, CreatedAt = at, UpdatedAt = at,
            });
            await db.SaveChangesAsync();
            return true;
        });

    private Task MutateOverrideAsync(string slug, Action<AppOverride> mutate) =>
        factory.QueryAsync(async db =>
        {
            mutate(await db.AppOverrides.SingleAsync(o => o.AppSlug == slug));
            await db.SaveChangesAsync();
            return true;
        });

    private Task DeleteOverrideAsync(string slug) =>
        factory.QueryAsync(async db =>
        {
            db.AppOverrides.Remove(await db.AppOverrides.SingleAsync(o => o.AppSlug == slug));
            await db.SaveChangesAsync();
            return true;
        });

    private Task MutateBlockedAsync(Action<BlockedScreenshotUrl> mutate) =>
        factory.QueryAsync(async db =>
        {
            mutate(await db.BlockedScreenshotUrls.SingleAsync());
            await db.SaveChangesAsync();
            return true;
        });

    private Task<int> ApplyOverridesAsync(DateTimeOffset now) =>
        factory.QueryAsync(db => new AppOverrideApplier(db).ApplyAsync(now));
}
