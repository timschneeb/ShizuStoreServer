using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppDownloadAbi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_downloads_app_id_sig_key",
                table: "app_downloads");

            migrationBuilder.AddColumn<string>(
                name: "abi",
                table: "app_downloads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_downloads_app_id_sig_key_abi",
                table: "app_downloads",
                columns: new[] { "app_id", "sig_key", "abi" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_downloads_app_id_sig_key_abi",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "abi",
                table: "app_downloads");

            migrationBuilder.CreateIndex(
                name: "IX_app_downloads_app_id_sig_key",
                table: "app_downloads",
                columns: new[] { "app_id", "sig_key" },
                unique: true);
        }
    }
}
