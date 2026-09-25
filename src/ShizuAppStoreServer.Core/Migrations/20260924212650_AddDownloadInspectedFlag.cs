using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadInspectedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "inspected",
                table: "app_downloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "inspected",
                table: "app_downloads");
        }
    }
}
