using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppOverridesAndScreenshotBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_overrides",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_slug = table.Column<string>(type: "text", nullable: false),
                    field = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    applied_value = table.Column<string>(type: "text", nullable: true),
                    baseline_value = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "blocked_screenshot_urls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    url = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocked_screenshot_urls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_overrides_app_slug_field",
                table: "app_overrides",
                columns: new[] { "app_slug", "field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blocked_screenshot_urls_url",
                table: "blocked_screenshot_urls",
                column: "url",
                unique: true);

            // Delta integrity: a manual override edit bumps the app clock, so
            // /v1/changes emits the entry even before the applier materializes it.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION app_overrides_touch_app() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE apps SET updated_at = now()
                    WHERE slug = COALESCE(NEW.app_slug, OLD.app_slug);
                    RETURN NULL;
                END $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER app_overrides_touch
                AFTER INSERT OR DELETE ON app_overrides
                FOR EACH ROW EXECUTE FUNCTION app_overrides_touch_app();
                """);

            // An UPDATE that only records the applier baseline must not fake an edit.
            migrationBuilder.Sql("""
                CREATE TRIGGER app_overrides_touch_update
                AFTER UPDATE ON app_overrides
                FOR EACH ROW
                WHEN (OLD.value IS DISTINCT FROM NEW.value
                    OR OLD.deleted_at IS DISTINCT FROM NEW.deleted_at
                    OR OLD.app_slug IS DISTINCT FROM NEW.app_slug)
                EXECUTE FUNCTION app_overrides_touch_app();
                """);

            // Blocking a URL also touches entries that only carry it through a
            // screenshots override, which lives in app_overrides.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION blocked_screenshot_urls_touch_apps() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    target_url text := COALESCE(NEW.url, OLD.url);
                BEGIN
                    UPDATE apps SET updated_at = now()
                    WHERE array_position(string_to_array(screenshots, E'\n'), target_url) IS NOT NULL
                        OR EXISTS (
                            SELECT 1 FROM app_overrides o
                            WHERE o.app_slug = apps.slug
                                AND o.field = 'screenshots'
                                AND o.deleted_at IS NULL
                                AND array_position(string_to_array(o.value, E'\n'), target_url) IS NOT NULL
                        );
                    RETURN NULL;
                END $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER blocked_screenshot_urls_touch
                AFTER INSERT OR DELETE ON blocked_screenshot_urls
                FOR EACH ROW EXECUTE FUNCTION blocked_screenshot_urls_touch_apps();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER blocked_screenshot_urls_touch_update
                AFTER UPDATE ON blocked_screenshot_urls
                FOR EACH ROW
                WHEN (OLD.url IS DISTINCT FROM NEW.url
                    OR OLD.deleted_at IS DISTINCT FROM NEW.deleted_at)
                EXECUTE FUNCTION blocked_screenshot_urls_touch_apps();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS blocked_screenshot_urls_touch_update ON blocked_screenshot_urls;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS blocked_screenshot_urls_touch ON blocked_screenshot_urls;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS blocked_screenshot_urls_touch_apps();");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS app_overrides_touch_update ON app_overrides;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS app_overrides_touch ON app_overrides;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_overrides_touch_app();");

            migrationBuilder.DropTable(
                name: "app_overrides");

            migrationBuilder.DropTable(
                name: "blocked_screenshot_urls");
        }
    }
}
