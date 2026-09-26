using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadReleaseTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "release_tag",
                table: "app_downloads",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "release_tag",
                table: "app_downloads");
        }
    }
}
