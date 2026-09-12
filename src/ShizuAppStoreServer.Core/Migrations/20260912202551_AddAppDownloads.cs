using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_downloads",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    apk_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    archive_entry = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    version_code = table.Column<long>(type: "bigint", nullable: true),
                    version_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    sig_sha256 = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    sig_md5 = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    min_sdk = table.Column<int>(type: "integer", nullable: true),
                    sig_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_downloads", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_downloads_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_downloads_app_id",
                table: "app_downloads",
                column: "app_id",
                unique: true,
                filter: "is_primary");

            migrationBuilder.CreateIndex(
                name: "IX_app_downloads_app_id_sig_key",
                table: "app_downloads",
                columns: new[] { "app_id", "sig_key" },
                unique: true);

            // Backfill: primary row from the served APK columns.
            migrationBuilder.Sql(
                """
                INSERT INTO app_downloads
                    (app_id, source, source_ref, apk_url, archive_entry, version_code, version_name,
                     size_bytes, sha256, sig_sha256, sig_md5, min_sdk, sig_key, is_primary, resolved_at)
                SELECT
                    a.id,
                    COALESCE(NULLIF(a.apk_source, ''), 'Other'),
                    NULL,
                    a.apk_url,
                    a.apk_archive_entry,
                    a.version_code,
                    a.version_name,
                    a.apk_size,
                    a.apk_sha256,
                    a.sig_sha256,
                    a.sig_md5,
                    a.min_sdk,
                    lower(COALESCE(
                        NULLIF(split_part(COALESCE(a.sig_sha256, ''), ' ', 1), ''),
                        NULLIF(split_part(COALESCE(a.sig_md5, ''), ' ', 1), ''),
                        'url:' || a.apk_url)),
                    TRUE,
                    now()
                FROM apps a
                WHERE a.apk_url IS NOT NULL AND a.apk_url <> '';
                """);

            // Backfill: F-Droid alternate row, skipped when the same signing
            // identity is already present (reproducible build).
            migrationBuilder.Sql(
                """
                INSERT INTO app_downloads
                    (app_id, source, source_ref, apk_url, archive_entry, version_code, version_name,
                     size_bytes, sha256, sig_sha256, sig_md5, min_sdk, sig_key, is_primary, resolved_at)
                SELECT
                    a.id,
                    'FDroid',
                    a.package_name,
                    a.fdroid_apk_url,
                    NULL,
                    a.fdroid_version_code,
                    a.fdroid_version_name,
                    a.fdroid_apk_size,
                    a.fdroid_apk_sha256,
                    a.fdroid_sig_sha256,
                    a.fdroid_sig_md5,
                    a.min_sdk,
                    lower(COALESCE(
                        NULLIF(split_part(COALESCE(a.fdroid_sig_sha256, ''), ' ', 1), ''),
                        NULLIF(split_part(COALESCE(a.fdroid_sig_md5, ''), ' ', 1), ''),
                        'url:' || a.fdroid_apk_url)),
                    NOT EXISTS (SELECT 1 FROM app_downloads d WHERE d.app_id = a.id AND d.is_primary),
                    now()
                FROM apps a
                WHERE a.fdroid_apk_url IS NOT NULL
                  AND a.fdroid_apk_url <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM app_downloads d
                      WHERE d.app_id = a.id
                        AND d.sig_key = lower(COALESCE(
                            NULLIF(split_part(COALESCE(a.fdroid_sig_sha256, ''), ' ', 1), ''),
                            NULLIF(split_part(COALESCE(a.fdroid_sig_md5, ''), ' ', 1), ''),
                            'url:' || a.fdroid_apk_url)));
                """);

            migrationBuilder.DropColumn(
                name: "apk_archive_entry",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_sha256",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_size",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_source",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_source_ref",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_url",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_apk_sha256",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_apk_size",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_apk_url",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_sig_md5",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_sig_sha256",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_version_code",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_version_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "min_sdk",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "sig_md5",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "sig_sha256",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "version_code",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "version_name",
                table: "apps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_downloads");

            migrationBuilder.AddColumn<string>(
                name: "apk_archive_entry",
                table: "apps",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "apk_sha256",
                table: "apps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "apk_size",
                table: "apps",
                type: "bigint",
                nullable: true);

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

            migrationBuilder.AddColumn<string>(
                name: "apk_url",
                table: "apps",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_apk_sha256",
                table: "apps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fdroid_apk_size",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_apk_url",
                table: "apps",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_sig_md5",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_sig_sha256",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fdroid_version_code",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_version_name",
                table: "apps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_sdk",
                table: "apps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sig_md5",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sig_sha256",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "version_code",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "version_name",
                table: "apps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
