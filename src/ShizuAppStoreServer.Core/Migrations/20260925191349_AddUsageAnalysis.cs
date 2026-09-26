using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<int>(
                name: "usage_analysis_version",
                table: "apps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "usage_analyzed_at",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_commit",
                table: "apps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_markdown",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_model",
                table: "apps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usage_prompt_version",
                table: "apps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "usage_release_ref",
                table: "apps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_short",
                table: "apps",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "usage_analysis_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    repo_forge = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    repo_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    repo_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_version = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cached_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tool_calls = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_analysis_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_usage_analysis_runs_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_analysis_runs_app_id",
                table: "usage_analysis_runs",
                column: "app_id",
                unique: true,
                filter: "status in ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_usage_analysis_runs_app_id_created_at",
                table: "usage_analysis_runs",
                columns: new[] { "app_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_analysis_runs_status_next_attempt_at",
                table: "usage_analysis_runs",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_analysis_runs");

            migrationBuilder.DropColumn(
                name: "usage_analysis_version",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_analyzed_at",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_commit",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_markdown",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_model",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_prompt_version",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_release_ref",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_short",
                table: "apps");

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
                    confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
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
    }
}
