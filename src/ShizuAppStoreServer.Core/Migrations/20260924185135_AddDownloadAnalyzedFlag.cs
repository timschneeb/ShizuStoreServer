using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadAnalyzedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "analyzed",
                table: "app_downloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Analyzed rows carry both certificate digests, so flag them to
            // keep the permission heal applying to pre-fix F-Droid rows.
            migrationBuilder.Sql("UPDATE app_downloads SET analyzed = true WHERE sig_sha256 IS NOT NULL;");

            // Legacy index-only rows stored the F-Droid v1 <sig> fingerprint,
            // which is not a certificate MD5; drop it so stale values cannot
            // leak into client fingerprint matching.
            migrationBuilder.Sql("UPDATE app_downloads SET sig_md5 = null WHERE analyzed = false AND source IN ('FDroid', 'Izzy');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "analyzed",
                table: "app_downloads");
        }
    }
}
