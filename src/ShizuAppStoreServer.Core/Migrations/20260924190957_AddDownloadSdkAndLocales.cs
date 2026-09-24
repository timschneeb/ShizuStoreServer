using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadSdkAndLocales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "compile_sdk",
                table: "app_downloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "locales",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "target_sdk",
                table: "app_downloads",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compile_sdk",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "locales",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "target_sdk",
                table: "app_downloads");
        }
    }
}
