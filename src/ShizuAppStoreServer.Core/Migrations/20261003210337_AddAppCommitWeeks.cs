using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppCommitWeeks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_commit_weeks",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    commits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_commit_weeks", x => new { x.app_id, x.week_start });
                    table.ForeignKey(
                        name: "FK_app_commit_weeks_apps_app_id",
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
                name: "app_commit_weeks");
        }
    }
}
