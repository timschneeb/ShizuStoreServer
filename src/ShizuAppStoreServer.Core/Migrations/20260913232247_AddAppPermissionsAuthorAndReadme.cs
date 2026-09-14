using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppPermissionsAuthorAndReadme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author_key",
                table: "apps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "author_name",
                table: "apps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "author_url",
                table: "apps",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "full_description",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permissions",
                table: "apps",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "author_key",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "author_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "author_url",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "full_description",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "permissions",
                table: "apps");
        }
    }
}
