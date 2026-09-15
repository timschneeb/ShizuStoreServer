using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "issue_count",
                table: "sync_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "sync_issues",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sync_run_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rule = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    app_id = table.Column<long>(type: "bigint", nullable: true),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_issues", x => x.id);
                    table.ForeignKey(
                        name: "FK_sync_issues_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sync_issues_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_issues_app_id",
                table: "sync_issues",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_issues_kind",
                table: "sync_issues",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "IX_sync_issues_rule",
                table: "sync_issues",
                column: "rule");

            migrationBuilder.CreateIndex(
                name: "IX_sync_issues_slug",
                table: "sync_issues",
                column: "slug");

            migrationBuilder.CreateIndex(
                name: "IX_sync_issues_sync_run_id",
                table: "sync_issues",
                column: "sync_run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_issues");

            migrationBuilder.DropColumn(
                name: "issue_count",
                table: "sync_runs");
        }
    }
}
