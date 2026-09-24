using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dhizuku_declared",
                table: "app_downloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "tracker_signatures",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "trackers",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dhizuku_declared",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "tracker_signatures",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "trackers",
                table: "app_downloads");
        }
    }
}
