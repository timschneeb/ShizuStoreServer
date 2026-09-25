using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "api_form",
                table: "app_downloads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "capabilities",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "managers",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "usage_evidence",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "usage_optional",
                table: "app_downloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "usage_source_scanned",
                table: "app_downloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "usage_summary",
                table: "app_downloads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_summary_hash",
                table: "app_downloads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_summary_model",
                table: "app_downloads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usage_summary_version",
                table: "app_downloads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "usage_version",
                table: "app_downloads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "app_signals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_signals", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_signals_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_signals_app_id",
                table: "app_signals",
                column: "app_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_signals");

            migrationBuilder.DropColumn(
                name: "api_form",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "capabilities",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "managers",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_evidence",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_optional",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_source_scanned",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_summary",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_summary_hash",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_summary_model",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_summary_version",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "usage_version",
                table: "app_downloads");
        }
    }
}
