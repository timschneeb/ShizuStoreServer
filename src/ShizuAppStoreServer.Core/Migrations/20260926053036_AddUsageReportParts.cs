using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageReportParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "usage_markdown_api_usage",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_markdown_notable_details",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_markdown_usage",
                table: "apps",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "usage_markdown_api_usage",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_markdown_notable_details",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "usage_markdown_usage",
                table: "apps");
        }
    }
}
