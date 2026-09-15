using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadPackageName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_downloads_app_id_sig_key_abi",
                table: "app_downloads");

            migrationBuilder.AddColumn<string>(
                name: "package_name",
                table: "app_downloads",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_downloads_app_id_package_name_sig_key_abi",
                table: "app_downloads",
                columns: new[] { "app_id", "package_name", "sig_key", "abi" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_downloads_app_id_package_name_sig_key_abi",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "package_name",
                table: "app_downloads");

            migrationBuilder.CreateIndex(
                name: "IX_app_downloads_app_id_sig_key_abi",
                table: "app_downloads",
                columns: new[] { "app_id", "sig_key", "abi" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
