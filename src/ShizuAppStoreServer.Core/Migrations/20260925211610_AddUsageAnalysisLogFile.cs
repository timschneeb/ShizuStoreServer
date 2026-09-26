using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageAnalysisLogFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "log_file",
                table: "usage_analysis_runs",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "log_file",
                table: "usage_analysis_runs");
        }
    }
}
