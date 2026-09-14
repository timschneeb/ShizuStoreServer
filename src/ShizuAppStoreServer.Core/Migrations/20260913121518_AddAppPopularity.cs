using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppPopularity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "download_total",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "stars",
                table: "apps",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "download_total",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "stars",
                table: "apps");
        }
    }
}
