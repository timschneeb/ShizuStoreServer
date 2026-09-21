using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAgentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_user_agent_days",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    request_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_user_agent_days", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_user_agents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    request_count = table.Column<long>(type: "bigint", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_user_agents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_user_agent_days_user_agent_day",
                table: "client_user_agent_days",
                columns: new[] { "user_agent", "day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_client_user_agents_user_agent",
                table: "client_user_agents",
                column: "user_agent",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_user_agent_days");

            migrationBuilder.DropTable(
                name: "client_user_agents");
        }
    }
}
