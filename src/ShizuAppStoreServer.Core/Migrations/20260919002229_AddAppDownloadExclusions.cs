using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppDownloadExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_download_exclusions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_download_exclusions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_download_exclusions_app_slug_package_name",
                table: "app_download_exclusions",
                columns: new[] { "app_slug", "package_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_download_exclusions");
        }
    }
}
