using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "apk_label",
                table: "apps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "apps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "root_app_id",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_root_app_id",
                table: "apps",
                column: "root_app_id");

            migrationBuilder.AddForeignKey(
                name: "FK_apps_apps_root_app_id",
                table: "apps",
                column: "root_app_id",
                principalTable: "apps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_apps_apps_root_app_id",
                table: "apps");

            migrationBuilder.DropIndex(
                name: "IX_apps_root_app_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "apk_label",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "root_app_id",
                table: "apps");
        }
    }
}
