using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api.Tests;

/// <summary>
/// SQLite equivalents of the delta triggers added by
/// <c>20261006013128_AddAppOverridesAndScreenshotBlocks</c>. The production
/// migrations only run on Npgsql; the hermetic host installs these to keep the
/// operator-edit delta contract (a real edit moves <c>apps.updated_at</c>, a
/// baseline-only write-back does not) testable end to end. Keep the WHEN
/// conditions in sync with the migration.
/// </summary>
internal static class SqliteOverrideTriggers
{
    /// <summary>
    /// (Re)installs the triggers so every one stamps <paramref name="stamp"/>
    /// as the bumped <c>apps.updated_at</c>. The literal matches the SQLite
    /// provider's DateTimeOffset text format.
    /// </summary>
    public static Task InstallAsync(ShizuDbContext db, DateTimeOffset stamp, CancellationToken ct = default)
    {
        var now = stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "+00:00";
        var sql = Ddl.Replace("$STAMP", now, StringComparison.Ordinal);
        return db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private const string Ddl = """
        DROP TRIGGER IF EXISTS app_overrides_touch_insert;
        DROP TRIGGER IF EXISTS app_overrides_touch_delete;
        DROP TRIGGER IF EXISTS app_overrides_touch_update;
        DROP TRIGGER IF EXISTS blocked_screenshot_urls_touch_insert;
        DROP TRIGGER IF EXISTS blocked_screenshot_urls_touch_delete;
        DROP TRIGGER IF EXISTS blocked_screenshot_urls_touch_update;

        CREATE TRIGGER app_overrides_touch_insert AFTER INSERT ON app_overrides
        BEGIN
            UPDATE apps SET updated_at = '$STAMP' WHERE slug = NEW.app_slug;
        END;

        CREATE TRIGGER app_overrides_touch_delete AFTER DELETE ON app_overrides
        BEGIN
            UPDATE apps SET updated_at = '$STAMP' WHERE slug = OLD.app_slug;
        END;

        CREATE TRIGGER app_overrides_touch_update AFTER UPDATE ON app_overrides
        WHEN OLD.value IS NOT NEW.value
            OR OLD.deleted_at IS NOT NEW.deleted_at
            OR OLD.app_slug IS NOT NEW.app_slug
        BEGIN
            UPDATE apps SET updated_at = '$STAMP' WHERE slug = NEW.app_slug;
        END;

        CREATE TRIGGER blocked_screenshot_urls_touch_insert AFTER INSERT ON blocked_screenshot_urls
        BEGIN
            UPDATE apps SET updated_at = '$STAMP'
            WHERE instr(char(10) || COALESCE(screenshots, '') || char(10),
                        char(10) || NEW.url || char(10)) > 0
                OR EXISTS (
                    SELECT 1 FROM app_overrides o
                    WHERE o.app_slug = apps.slug
                        AND o.field = 'screenshots'
                        AND o.deleted_at IS NULL
                        AND instr(char(10) || COALESCE(o.value, '') || char(10),
                                  char(10) || NEW.url || char(10)) > 0);
        END;

        CREATE TRIGGER blocked_screenshot_urls_touch_delete AFTER DELETE ON blocked_screenshot_urls
        BEGIN
            UPDATE apps SET updated_at = '$STAMP'
            WHERE instr(char(10) || COALESCE(screenshots, '') || char(10),
                        char(10) || OLD.url || char(10)) > 0
                OR EXISTS (
                    SELECT 1 FROM app_overrides o
                    WHERE o.app_slug = apps.slug
                        AND o.field = 'screenshots'
                        AND o.deleted_at IS NULL
                        AND instr(char(10) || COALESCE(o.value, '') || char(10),
                                  char(10) || OLD.url || char(10)) > 0);
        END;

        CREATE TRIGGER blocked_screenshot_urls_touch_update AFTER UPDATE ON blocked_screenshot_urls
        WHEN OLD.url IS NOT NEW.url OR OLD.deleted_at IS NOT NEW.deleted_at
        BEGIN
            UPDATE apps SET updated_at = '$STAMP'
            WHERE instr(char(10) || COALESCE(screenshots, '') || char(10),
                        char(10) || NEW.url || char(10)) > 0
                OR EXISTS (
                    SELECT 1 FROM app_overrides o
                    WHERE o.app_slug = apps.slug
                        AND o.field = 'screenshots'
                        AND o.deleted_at IS NULL
                        AND instr(char(10) || COALESCE(o.value, '') || char(10),
                                  char(10) || NEW.url || char(10)) > 0);
        END;
        """;
}
