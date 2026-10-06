using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Overrides;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Applier behavior: baseline capture, steady-state write-back without delta
/// churn, restore on soft delete, and visibility transitions.
/// </summary>
public sealed class AppOverrideApplierTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;

    public AppOverrideApplierTests()
    {
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<App> SeedAppAsync(string slug = "demo", Action<App>? configure = null)
    {
        var category = new Category { Name = "Tools", Slug = "tools", Section = CategorySection.Apps };
        var app = new App
        {
            Slug = slug,
            Name = "Demo",
            Url = $"https://example.com/{slug}",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            Availability = Availability.DirectApk,
            DisplayName = "Natural",
            AddedAt = T0,
            UpdatedAt = T0,
            LastCheckedAt = T0,
        };
        configure?.Invoke(app);
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();
        return app;
    }

    private async Task AddOverrideAsync(string slug, string field, string value)
    {
        _db.AppOverrides.Add(new AppOverride
        {
            AppSlug = slug,
            Field = field,
            Value = value,
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();
    }

    private AppOverrideApplier Applier() => new(_db);

    private ShizuDbContext NewContext() => new(new DbContextOptionsBuilder<ShizuDbContext>()
        .UseSqlite(_connection)
        .Options);

    [Fact]
    public async Task AppliesOverrideAndCapturesBaseline()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "display_name", "Overridden");
        _db.ChangeTracker.Clear();

        var changed = await Applier().ApplyAsync(T0.AddHours(1));

        Assert.Equal(1, changed);
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal("Overridden", app.DisplayName);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Equal("Natural", row.BaselineValue);
        Assert.Equal("Overridden", row.AppliedValue);
    }

    [Fact]
    public async Task SteadyStateWriteBackKeepsTheDeltaClock()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "display_name", "Overridden");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        // Simulate an enrichment pass rewriting the derived column.
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        app.DisplayName = "Drifted";
        await _db.SaveChangesAsync();
        var driftStamp = app.UpdatedAt;

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(2));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal("Overridden", app.DisplayName);
        Assert.Equal(driftStamp, app.UpdatedAt);
    }

    [Fact]
    public async Task SteadyStateWithoutDriftReportsNoWrite()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "display_name", "Overridden");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var changed = await Applier().ApplyAsync(T0.AddHours(2));

        Assert.Equal(0, changed);
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal("Overridden", app.DisplayName);
        Assert.Equal(T0.AddHours(1), app.UpdatedAt);
    }

    [Fact]
    public async Task StaleTrackedAppDoesNotSwallowTheWriteBack()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "display_name", "Overridden");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        // The pass scope tracked the app before enrichment; enrichment then
        // ran in another scope and drifted the overridden value.
        _db.ChangeTracker.Clear();
        var stale = await _db.Apps.SingleAsync();
        Assert.Equal("Overridden", stale.DisplayName);

        using var other = NewContext();
        var drifted = await other.Apps.SingleAsync();
        drifted.DisplayName = "Natural";
        await other.SaveChangesAsync();
        var driftStamp = drifted.UpdatedAt;
        other.ChangeTracker.Clear();

        var changed = await Applier().ApplyAsync(T0.AddHours(2));

        Assert.Equal(1, changed);
        other.ChangeTracker.Clear();
        var app = await other.Apps.SingleAsync();
        Assert.Equal("Overridden", app.DisplayName);
        Assert.Equal(driftStamp, app.UpdatedAt);
    }

    [Fact]
    public async Task StaleTrackedScreenshotsDoNotBlockThePurge()
    {
        await SeedAppAsync(configure: a => a.Screenshots = ["keep"]);

        // The pass scope tracked the app before the screenshots were updated.
        _db.ChangeTracker.Clear();
        var stale = await _db.Apps.SingleAsync();
        Assert.Equal(["keep"], stale.Screenshots);

        using var other = NewContext();
        var updated = await other.Apps.SingleAsync();
        updated.Screenshots = ["keep", "bad"];
        await other.SaveChangesAsync();

        _db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl
        {
            Url = "bad",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();

        var changed = await Applier().ApplyAsync(T0.AddHours(1));

        Assert.Equal(1, changed);
        other.ChangeTracker.Clear();
        var app = await other.Apps.SingleAsync();
        Assert.Equal(["keep"], app.Screenshots);
    }

    [Fact]
    public async Task EditedOverrideValueMovesTheDeltaClock()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "display_name", "First");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        row.Value = "Second";
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(2));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal("Second", app.DisplayName);
        Assert.Equal(T0.AddHours(2), app.UpdatedAt);
        row = await _db.AppOverrides.SingleAsync();
        Assert.Equal("Natural", row.BaselineValue);
        Assert.Equal("Second", row.AppliedValue);
    }

    [Fact]
    public async Task EditedOverrideValueThatMatchesTheServedValueKeepsTheClock()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "display_name", "First");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        // Natural drift lands on the same value the operator edits to.
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        app.DisplayName = "Second";
        app.UpdatedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        row.Value = "Second";
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(3));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal("Second", app.DisplayName);
        Assert.Equal(T0.AddHours(2), app.UpdatedAt);
        row = await _db.AppOverrides.SingleAsync();
        Assert.Equal("Second", row.AppliedValue);
    }

    [Fact]
    public async Task SoftDeleteRestoresBaselineAndForcesRecheck()
    {
        await SeedAppAsync(configure: a => a.LastError = "old");
        await AddOverrideAsync("demo", "display_name", "Overridden");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(3));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal("Natural", app.DisplayName);
        Assert.Null(app.LastCheckedAt);
        Assert.Null(app.LastError);
        Assert.False(await _db.AppOverrides.AnyAsync());
    }

    [Fact]
    public async Task ListOwnedRestoreLeavesTheRecheckScheduleAlone()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "name", "Renamed");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(3));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal("Demo", app.Name);
        Assert.Equal(T0, app.LastCheckedAt);
    }

    [Fact]
    public async Task AddedAtOverrideAppliesRestoresAndLeavesTheScheduleAlone()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "added_at", "2024-01-02T03:04:05+00:00");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(DateTimeOffset.Parse("2024-01-02T03:04:05+00:00"), app.AddedAt);
        Assert.Equal(T0.AddHours(1), app.UpdatedAt);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Equal(T0, DateTimeOffset.Parse(row.BaselineValue!));
        Assert.Equal("2024-01-02T03:04:05+00:00", row.AppliedValue);

        _db.ChangeTracker.Clear();
        row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(3));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(T0, app.AddedAt);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.False(await _db.AppOverrides.AnyAsync());
    }

    [Fact]
    public async Task InvalidAddedAtIsRejected()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "added_at", "not-a-date");
        _db.ChangeTracker.Clear();

        Assert.Equal(0, await Applier().ApplyAsync(T0.AddHours(1)));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(T0, app.AddedAt);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Null(row.BaselineValue);
        Assert.Null(row.AppliedValue);
    }

    [Fact]
    public async Task ListUpdatedAtOverrideAppliesRestoresAndLeavesTheScheduleAlone()
    {
        await SeedAppAsync(configure: app => app.ListUpdatedAt = T0);
        await AddOverrideAsync("demo", "list_updated_at", "2024-01-02T03:04:05+00:00");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(DateTimeOffset.Parse("2024-01-02T03:04:05+00:00"), app.ListUpdatedAt);
        Assert.Equal(T0.AddHours(1), app.UpdatedAt);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Equal(T0, DateTimeOffset.Parse(row.BaselineValue!));
        Assert.Equal("2024-01-02T03:04:05+00:00", row.AppliedValue);

        _db.ChangeTracker.Clear();
        row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(3));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(T0, app.ListUpdatedAt);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.False(await _db.AppOverrides.AnyAsync());
    }

    [Fact]
    public async Task InvalidListUpdatedAtIsRejected()
    {
        await SeedAppAsync(configure: app => app.ListUpdatedAt = T0);
        await AddOverrideAsync("demo", "list_updated_at", "not-a-date");
        _db.ChangeTracker.Clear();

        Assert.Equal(0, await Applier().ApplyAsync(T0.AddHours(1)));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(T0, app.ListUpdatedAt);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Null(row.BaselineValue);
        Assert.Null(row.AppliedValue);
    }

    [Fact]
    public async Task UnparsableAndUnknownFieldsAreSkipped()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "stars", "many");
        await AddOverrideAsync("demo", "id", "7");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Null(app.Stars);
        var stars = await _db.AppOverrides.SingleAsync(o => o.Field == "stars");
        Assert.Null(stars.BaselineValue);
        Assert.Null(stars.AppliedValue);
    }

    [Fact]
    public async Task ShortOrNonHexIconHashIsRejectedAndValidHashIsNormalized()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "icon_hash", "not-a-hash");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));
        _db.ChangeTracker.Clear();
        Assert.Null((await _db.Apps.SingleAsync()).IconHash);

        var hash = new string('A', 64);
        var row = await _db.AppOverrides.SingleAsync();
        row.Value = hash;
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(2));
        _db.ChangeTracker.Clear();
        Assert.Equal(new string('a', 64), (await _db.Apps.SingleAsync()).IconHash);
    }

    [Fact]
    public async Task NewlineListsParseAndEmptyLinesDrop()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "permissions", "A\n\nB\r\nC");
        await AddOverrideAsync("demo", "screenshots", "https://example.com/1.png\nhttps://example.com/2.png");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(["A", "B", "C"], app.Permissions);
        Assert.Equal(["https://example.com/1.png", "https://example.com/2.png"], app.Screenshots);
    }

    [Fact]
    public async Task VisibilityOverrideExcludesAndRestoreBringsTheAppBack()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "availability", "excluded");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(Availability.Excluded, app.Availability);
        Assert.Equal(AppOverrideApplier.ExcludedReason, app.ExcludedReason);
        Assert.Equal(T0.AddHours(1), app.UpdatedAt);
        var tombstone = await _db.RemovedApps.SingleAsync();
        Assert.Equal("demo", tombstone.Slug);
        Assert.Equal(T0.AddHours(1), tombstone.RemovedAt);

        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(3));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(Availability.DirectApk, app.Availability);
        Assert.Null(app.ExcludedReason);
        Assert.Null(app.LastCheckedAt);
        Assert.False(await _db.RemovedApps.AnyAsync());
        Assert.False(await _db.AppOverrides.AnyAsync());
    }

    [Fact]
    public async Task VisibilitySteadyStateWriteBackKeepsTheDeltaClock()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "availability", "link_only");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));
        _db.ChangeTracker.Clear();
        Assert.Equal(Availability.LinkOnly, (await _db.Apps.SingleAsync()).Availability);
        var storedRow = await _db.AppOverrides.AsNoTracking().SingleAsync();
        Assert.Equal("link_only", storedRow.AppliedValue);
        Assert.Equal("DirectApk", storedRow.BaselineValue);

        // Enrichment flips the row back to direct_apk; the applier re-asserts
        // link_only without reporting an edit.
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        app.Availability = Availability.DirectApk;
        await _db.SaveChangesAsync();
        var driftStamp = app.UpdatedAt;

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(2));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(Availability.LinkOnly, app.Availability);
        Assert.Equal(driftStamp, app.UpdatedAt);
    }

    [Fact]
    public async Task ReasonOverrideAppliesToAnAlreadyExcludedRow()
    {
        await SeedAppAsync(configure: a =>
        {
            a.Availability = Availability.Excluded;
            a.ExcludedReason = "Gate reason.";
        });
        await AddOverrideAsync("demo", "excluded_reason", "Custom reason.");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(Availability.Excluded, app.Availability);
        Assert.Equal("Custom reason.", app.ExcludedReason);
    }

    [Fact]
    public async Task UnlistAndArchiveReasonsOutrankVisibilityOverrides()
    {
        await SeedAppAsync(configure: a =>
        {
            a.Availability = Availability.Excluded;
            a.ExcludedReason = SyncService.UnlistedReason;
        });
        await AddOverrideAsync("demo", "availability", "link_only");
        _db.ChangeTracker.Clear();

        await Applier().ApplyAsync(T0.AddHours(1));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(Availability.Excluded, app.Availability);
        Assert.Equal(SyncService.UnlistedReason, app.ExcludedReason);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Null(row.AppliedValue);

        // Once the operator unlist is gone the pending override applies.
        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        app.Availability = Availability.DirectApk;
        app.ExcludedReason = null;
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(2));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(Availability.LinkOnly, app.Availability);
    }

    [Fact]
    public async Task OverridesForMissingAppsStayInert()
    {
        await AddOverrideAsync("ghost", "display_name", "Nope");
        _db.ChangeTracker.Clear();

        var changed = await Applier().ApplyAsync(T0.AddHours(1));

        Assert.Equal(0, changed);
        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Null(row.BaselineValue);
        Assert.Null(row.AppliedValue);
    }

    [Fact]
    public async Task PurgesBlockedScreenshotUrlsAndMovesTheDeltaClock()
    {
        await SeedAppAsync(configure: a => a.Screenshots =
            ["https://cdn.example/keep.png", "https://cdn.example/bad.png"]);
        _db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl
        {
            Url = "https://cdn.example/bad.png",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var changed = await Applier().ApplyAsync(T0.AddHours(1));

        Assert.Equal(1, changed);
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(["https://cdn.example/keep.png"], app.Screenshots);
        Assert.Equal(T0.AddHours(1), app.UpdatedAt);

        // The purge is a one-shot: a second pass finds nothing to change.
        _db.ChangeTracker.Clear();
        Assert.Equal(0, await Applier().ApplyAsync(T0.AddHours(2)));
        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(T0.AddHours(1), app.UpdatedAt);
    }

    [Fact]
    public async Task SoftDeletedBlockedUrlsDoNotPurge()
    {
        await SeedAppAsync(configure: a => a.Screenshots = ["https://cdn.example/bad.png"]);
        _db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl
        {
            Url = "https://cdn.example/bad.png",
            CreatedAt = T0,
            UpdatedAt = T0,
            DeletedAt = T0.AddMinutes(1),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(0, await Applier().ApplyAsync(T0.AddHours(1)));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(["https://cdn.example/bad.png"], app.Screenshots);
    }

    [Fact]
    public async Task SourceOverrideForcesOneRecheckAndIsConsumed()
    {
        await SeedAppAsync(configure: a => a.LastError = "old failure");
        await AddOverrideAsync("demo", "source_release_home", "instafel/u-rel");
        _db.ChangeTracker.Clear();

        var changed = await Applier().ApplyAsync(T0.AddHours(1));

        Assert.Equal(1, changed);
        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Null(app.LastCheckedAt);
        Assert.Null(app.LastError);

        // Source rows steer the seam; they never write app columns or capture
        // a baseline, and they leave the delta clock alone.
        Assert.Equal(T0, app.UpdatedAt);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Equal("instafel/u-rel", row.AppliedValue);
        Assert.Null(row.BaselineValue);

        // A consumed row is inert until the operator edits the value.
        app.LastCheckedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        Assert.Equal(0, await Applier().ApplyAsync(T0.AddHours(3)));
        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Equal(T0.AddHours(2), app.LastCheckedAt);
    }

    [Fact]
    public async Task EditedSourceValueForcesAnotherRecheck()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "source_release_home", "instafel/u-rel");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));
        _db.ChangeTracker.Clear();

        var row = await _db.AppOverrides.SingleAsync();
        row.Value = "instafel/v-rel";
        await _db.SaveChangesAsync();
        var app = await _db.Apps.SingleAsync();
        app.LastCheckedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(1, await Applier().ApplyAsync(T0.AddHours(3)));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Null(app.LastCheckedAt);
        row = await _db.AppOverrides.SingleAsync();
        Assert.Equal("instafel/v-rel", row.AppliedValue);
    }

    [Fact]
    public async Task SoftDeletedConsumedSourceRowIsRemovedWithOneRecheck()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "source_scan_all_releases", "true");
        _db.ChangeTracker.Clear();
        await Applier().ApplyAsync(T0.AddHours(1));
        _db.ChangeTracker.Clear();

        var row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();
        var app = await _db.Apps.SingleAsync();
        app.LastCheckedAt = T0.AddHours(2);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(1, await Applier().ApplyAsync(T0.AddHours(3)));

        _db.ChangeTracker.Clear();
        app = await _db.Apps.SingleAsync();
        Assert.Null(app.LastCheckedAt);
        Assert.Empty(await _db.AppOverrides.ToListAsync());
    }

    [Fact]
    public async Task NeverAppliedSoftDeletedSourceRowIsRemovedWithoutRecheck()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "source_release_home", "instafel/u-rel");
        _db.ChangeTracker.Clear();
        var row = await _db.AppOverrides.SingleAsync();
        row.DeletedAt = T0.AddHours(1);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(1, await Applier().ApplyAsync(T0.AddHours(2)));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Empty(await _db.AppOverrides.ToListAsync());
    }

    [Fact]
    public async Task InvalidSourceValueIsIgnored()
    {
        await SeedAppAsync();
        await AddOverrideAsync("demo", "source_release_home", "not-a-repo");
        _db.ChangeTracker.Clear();

        Assert.Equal(0, await Applier().ApplyAsync(T0.AddHours(1)));

        _db.ChangeTracker.Clear();
        var app = await _db.Apps.SingleAsync();
        Assert.Equal(T0, app.LastCheckedAt);
        var row = await _db.AppOverrides.SingleAsync();
        Assert.Null(row.AppliedValue);
        Assert.Null(row.BaselineValue);
    }
}
