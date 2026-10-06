using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Live Postgres check of the delta triggers from
/// <c>20261006013128_AddAppOverridesAndScreenshotBlocks</c>. The hermetic
/// suites only simulate them on SQLite; this test runs the real migration
/// against a scratch database. Env-gated like the other live suites, e.g.:
/// <code>
/// SHIZU_REAL_POSTGRES="Host=127.0.0.1;Port=55432;Database=postgres;Username=shizu" \
///   dotnet test tests/ShizuAppStoreServer.Core.Tests
/// </code>
/// The connection must allow CREATE DATABASE; the scratch database is dropped
/// afterwards.
/// </summary>
public sealed class PostgresOverrideTriggerTests
{
    [Fact]
    public async Task MigrationTriggersBumpOnlyRealEdits()
    {
        var admin = Environment.GetEnvironmentVariable("SHIZU_REAL_POSTGRES");
        if (string.IsNullOrWhiteSpace(admin))
        {
            return;
        }

        var scratch = $"shizu_overrides_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{scratch}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<ShizuDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(admin) { Database = scratch }.ConnectionString)
                .Options;
            await using var db = new ShizuDbContext(options);
            await db.Database.MigrateAsync();

            var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var stamp = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
            var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
            db.Categories.Add(category);
            db.Apps.Add(new App
            {
                Slug = "tuner",
                Name = "Tuner",
                Url = "https://example.com/tuner",
                Listing = Listing.Main,
                Type = AppType.App,
                Category = category,
                AddedAt = old,
                UpdatedAt = old,
                Screenshots = ["https://example.com/screens/00.png"],
            });
            await db.SaveChangesAsync();

            await SetClockAsync(db, old);
            db.AppOverrides.Add(new AppOverride
            {
                AppSlug = "tuner", Field = "display_name", Value = "Overridden",
                CreatedAt = stamp, UpdatedAt = stamp,
            });
            await db.SaveChangesAsync();
            await AssertBumpedAsync(db);

            // The applier recording its baseline is an UPDATE, but not an edit.
            await SetClockAsync(db, old);
            var row = await db.AppOverrides.SingleAsync();
            row.AppliedValue = row.Value;
            row.BaselineValue = "natural";
            await db.SaveChangesAsync();
            Assert.Equal(old, await UpdatedAtAsync(db));

            await SetClockAsync(db, old);
            row.Value = "Edited";
            await db.SaveChangesAsync();
            await AssertBumpedAsync(db);

            await SetClockAsync(db, old);
            db.AppOverrides.Remove(row);
            await db.SaveChangesAsync();
            await AssertBumpedAsync(db);

            await SetClockAsync(db, old);
            db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl
            {
                Url = "https://example.com/screens/00.png", CreatedAt = stamp, UpdatedAt = stamp,
            });
            await db.SaveChangesAsync();
            await AssertBumpedAsync(db);

            // Only a URL or soft-delete edit is an edit; a note is bookkeeping.
            var blocked = await db.BlockedScreenshotUrls.SingleAsync();
            await SetClockAsync(db, old);
            blocked.Note = "false positive";
            await db.SaveChangesAsync();
            Assert.Equal(old, await UpdatedAtAsync(db));

            await SetClockAsync(db, old);
            blocked.DeletedAt = stamp;
            await db.SaveChangesAsync();
            await AssertBumpedAsync(db);

            await SetClockAsync(db, old);
            db.BlockedScreenshotUrls.Remove(blocked);
            await db.SaveChangesAsync();
            await AssertBumpedAsync(db);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Task<DateTimeOffset> UpdatedAtAsync(ShizuDbContext db) =>
        db.Apps.AsNoTracking().Where(a => a.Slug == "tuner").Select(a => a.UpdatedAt).SingleAsync();

    private static Task SetClockAsync(ShizuDbContext db, DateTimeOffset at) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE apps SET updated_at = {at} WHERE slug = 'tuner'");

    private static async Task AssertBumpedAsync(ShizuDbContext db)
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.True(await UpdatedAtAsync(db) >= before, "expected the trigger to bump apps.updated_at");
    }
}
