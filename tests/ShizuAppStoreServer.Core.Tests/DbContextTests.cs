using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>DbContext save pipeline: UTC normalization for timestamptz.</summary>
public sealed class DbContextTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;

    public DbContextTests()
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

    [Fact]
    public async Task SaveChangesNormalizesDateTimeOffsetsToUtc()
    {
        // Same instant, hostile offsets: Npgsql rejects non-zero offsets
        // while SQLite stores anything, so normalize on save to keep both
        // providers green.
        var added = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.FromHours(2));
        var updated = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.FromHours(5, 30, 0));
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        _db.Categories.Add(category);
        _db.Apps.Add(new App
        {
            Slug = "tz",
            Name = "Tz",
            Url = "https://example.com/tz",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = added,
            UpdatedAt = updated,
        });
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        var saved = await _db.Apps.SingleAsync(a => a.Slug == "tz");

        Assert.Equal(TimeSpan.Zero, saved.AddedAt.Offset);
        Assert.Equal(TimeSpan.Zero, saved.UpdatedAt.Offset);
        Assert.Equal(added.UtcDateTime, saved.AddedAt.UtcDateTime);
        Assert.Equal(updated.UtcDateTime, saved.UpdatedAt.UtcDateTime);
    }

    [Fact]
    public async Task IconChangeBumpsUpdatedAt()
    {
        // The enrich pass rewrites IconHash and prunes the previous icon file,
        // so /v1/changes (keyed on UpdatedAt) must report the app when only the
        // icon changed; otherwise clients keep requesting the deleted hash.
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        var created = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);
        _db.Categories.Add(category);
        _db.Apps.Add(new App
        {
            Slug = "icon",
            Name = "Icon",
            Url = "https://example.com/icon",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = created,
            UpdatedAt = created,
            IconHash = "aaaa",
        });
        await _db.SaveChangesAsync();

        var app = await _db.Apps.SingleAsync(a => a.Slug == "icon");
        app.IconHash = "bbbb";
        await _db.SaveChangesAsync();

        Assert.True(app.UpdatedAt > created);
    }

    [Fact]
    public async Task PopularityChangeBumpsUpdatedAt()
    {
        // Stars and download totals are summary fields delivered through
        // /v1/changes, so a popularity-only enrich must bump UpdatedAt.
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        var created = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);
        _db.Categories.Add(category);
        _db.Apps.Add(new App
        {
            Slug = "popular",
            Name = "Popular",
            Url = "https://example.com/popular",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = created,
            UpdatedAt = created,
        });
        await _db.SaveChangesAsync();

        var app = await _db.Apps.SingleAsync(a => a.Slug == "popular");
        app.Stars = 120;
        app.DownloadTotal = 3400;
        await _db.SaveChangesAsync();

        Assert.True(app.UpdatedAt > created);
    }

    [Fact]
    public async Task UnrelatedChangeLeavesUpdatedAt()
    {
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        var created = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);
        _db.Categories.Add(category);
        _db.Apps.Add(new App
        {
            Slug = "plain",
            Name = "Plain",
            Url = "https://example.com/plain",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = created,
            UpdatedAt = created,
        });
        await _db.SaveChangesAsync();

        var app = await _db.Apps.SingleAsync(a => a.Slug == "plain");
        app.Description = "changed";
        await _db.SaveChangesAsync();

        Assert.Equal(created, app.UpdatedAt);
    }

    [Fact]
    public async Task VersionUpdatedAtChangeBumpsUpdatedAt()
    {
        // The release date rides along with a new APK version, which clients
        // must learn through /v1/changes, so it also bumps UpdatedAt.
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        var created = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);
        _db.Categories.Add(category);
        _db.Apps.Add(new App
        {
            Slug = "released",
            Name = "Released",
            Url = "https://example.com/released",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = created,
            UpdatedAt = created,
        });
        await _db.SaveChangesAsync();

        var app = await _db.Apps.SingleAsync(a => a.Slug == "released");
        app.VersionUpdatedAt = created.AddDays(3);
        await _db.SaveChangesAsync();

        Assert.True(app.UpdatedAt > created);
    }

    [Fact]
    public async Task ListUpdatedAtChangeBumpsUpdatedAt()
    {
        // The list-change date is a summary field delivered through
        // /v1/changes, so a list sync that only edits an entry must still
        // reach clients.
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        var created = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);
        _db.Categories.Add(category);
        _db.Apps.Add(new App
        {
            Slug = "listed",
            Name = "Listed",
            Url = "https://example.com/listed",
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = created,
            UpdatedAt = created,
        });
        await _db.SaveChangesAsync();

        var app = await _db.Apps.SingleAsync(a => a.Slug == "listed");
        app.ListUpdatedAt = created.AddDays(3);
        await _db.SaveChangesAsync();

        Assert.True(app.UpdatedAt > created);
    }
}
