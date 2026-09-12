using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddApkSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fdroid_apk_sha256",
                table: "apps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fdroid_apk_size",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_apk_url",
                table: "apps",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_sig_md5",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_sig_sha256",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fdroid_version_code",
                table: "apps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fdroid_version_name",
                table: "apps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sig_md5",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sig_sha256",
                table: "apps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fdroid_apk_sha256",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_apk_size",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_apk_url",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_sig_md5",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_sig_sha256",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_version_code",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "fdroid_version_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "sig_md5",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "sig_sha256",
                table: "apps");
        }
    }
}
