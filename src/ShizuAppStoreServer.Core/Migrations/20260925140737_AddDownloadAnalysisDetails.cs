using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadAnalysisDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "abis",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "analysis_version",
                table: "app_downloads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "localized_labels",
                table: "app_downloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "signer_dn",
                table: "app_downloads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_key_algorithm",
                table: "app_downloads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_scheme",
                table: "app_downloads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "abis",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "analysis_version",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "localized_labels",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "signer_dn",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "signer_key_algorithm",
                table: "app_downloads");

            migrationBuilder.DropColumn(
                name: "signer_scheme",
                table: "app_downloads");
        }
    }
}
