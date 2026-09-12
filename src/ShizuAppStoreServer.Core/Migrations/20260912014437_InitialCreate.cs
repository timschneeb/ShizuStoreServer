using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    section = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sync_requests",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    processed = table.Column<bool>(type: "boolean", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    head_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    added = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    removed = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    license = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    listing = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_recommended = table.Column<bool>(type: "boolean", nullable: false),
                    has_paid = table.Column<bool>(type: "boolean", nullable: false),
                    has_iap = table.Column<bool>(type: "boolean", nullable: false),
                    has_ads = table.Column<bool>(type: "boolean", nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: true),
                    requires_root = table.Column<bool>(type: "boolean", nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    source_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    availability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    excluded_reason = table.Column<string>(type: "text", nullable: true),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    version_code = table.Column<long>(type: "bigint", nullable: true),
                    version_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    apk_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    apk_size = table.Column<long>(type: "bigint", nullable: true),
                    apk_sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    min_sdk = table.Column<int>(type: "integer", nullable: true),
                    store_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    icon_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    category_id = table.Column<long>(type: "bigint", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_apps_apps_parent_id",
                        column: x => x.parent_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_apps_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_versions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    version_code = table.Column<long>(type: "bigint", nullable: true),
                    version_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    apk_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_versions_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_versions_app_id_version_code",
                table: "app_versions",
                columns: new[] { "app_id", "version_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_availability",
                table: "apps",
                column: "availability");

            migrationBuilder.CreateIndex(
                name: "IX_apps_category_id",
                table: "apps",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_apps_parent_id",
                table: "apps",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_apps_slug",
                table: "apps",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_updated_at",
                table: "apps",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "IX_apps_url",
                table: "apps",
                column: "url");

            migrationBuilder.CreateIndex(
                name: "IX_categories_parent_id",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_categories_section_parent_id",
                table: "categories",
                columns: new[] { "section", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_slug",
                table: "categories",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_requests_processed",
                table: "sync_requests",
                column: "processed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_versions");

            migrationBuilder.DropTable(
                name: "sync_requests");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "categories");
        }
    }
}
