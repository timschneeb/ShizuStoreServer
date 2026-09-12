using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddApkSourceLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "apk_source",
                table: "apps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "apk_source_ref",
                table: "apps",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // Backfill the lock for rows that already served an APK: F-Droid
            // and Izzy URLs are recognizable, everything else follows the
            // entry's source kind. Existing rows without an APK stay null.
            migrationBuilder.Sql(
                """
                UPDATE apps SET apk_source = CASE
                    WHEN apk_url ILIKE '%f-droid.org%' THEN 'FDroid'
                    WHEN apk_url ILIKE '%izzysoft.de%' THEN 'Izzy'
                    WHEN source_kind IN ('GitHub', 'GitLab', 'Codeberg') THEN source_kind
                    ELSE 'Other'
                END
                WHERE apk_url IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE apps SET apk_source_ref = package_name
                WHERE apk_source IN ('FDroid', 'Izzy') AND package_name IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "apk_source",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_source_ref",
                table: "apps");
        }
    }
}
