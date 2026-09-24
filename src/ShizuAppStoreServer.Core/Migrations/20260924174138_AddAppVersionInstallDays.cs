using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppVersionInstallDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_version_install_days",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    version_code = table.Column<long>(type: "bigint", nullable: false),
                    install_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    install_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_version_install_days", x => new { x.app_id, x.version_code, x.install_type, x.day });
                    table.ForeignKey(
                        name: "FK_app_version_install_days_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_version_install_days");
        }
    }
}
