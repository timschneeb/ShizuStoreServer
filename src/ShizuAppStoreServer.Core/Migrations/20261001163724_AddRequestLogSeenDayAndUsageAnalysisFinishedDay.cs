using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestLogSeenDayAndUsageAnalysisFinishedDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "finished_day",
                table: "usage_analysis_runs",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "seen_day",
                table: "request_logs",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            // Backfill from the UTC timestamps, then drop the placeholder
            // default so future rows must set the day explicitly.
            migrationBuilder.Sql(
                "UPDATE request_logs SET seen_day = (seen_at AT TIME ZONE 'UTC')::date;");
            migrationBuilder.Sql(
                "UPDATE usage_analysis_runs SET finished_day = (finished_at AT TIME ZONE 'UTC')::date WHERE finished_at IS NOT NULL;");
            migrationBuilder.Sql(
                "ALTER TABLE request_logs ALTER COLUMN seen_day DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_usage_analysis_runs_finished_day",
                table: "usage_analysis_runs",
                column: "finished_day");

            migrationBuilder.CreateIndex(
                name: "IX_request_logs_seen_day",
                table: "request_logs",
                column: "seen_day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_usage_analysis_runs_finished_day",
                table: "usage_analysis_runs");

            migrationBuilder.DropIndex(
                name: "IX_request_logs_seen_day",
                table: "request_logs");

            migrationBuilder.DropColumn(
                name: "finished_day",
                table: "usage_analysis_runs");

            migrationBuilder.DropColumn(
                name: "seen_day",
                table: "request_logs");
        }
    }
}
