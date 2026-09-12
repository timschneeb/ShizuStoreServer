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
}
