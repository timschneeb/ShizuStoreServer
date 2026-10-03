using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppStarDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_star_days",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    stars = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_star_days", x => new { x.app_id, x.day });
                    table.ForeignKey(
                        name: "FK_app_star_days_apps_app_id",
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
                name: "app_star_days");
        }
    }
}
