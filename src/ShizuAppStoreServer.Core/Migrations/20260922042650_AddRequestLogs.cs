using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "request_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    query_string = table.Column<string>(type: "text", nullable: true),
                    raw_target = table.Column<string>(type: "text", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    scheme = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: true),
                    raw_request = table.Column<string>(type: "text", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    status_code = table.Column<short>(type: "smallint", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: true),
                    remote_ip = table.Column<string>(type: "text", nullable: true),
                    client_ip = table.Column<string>(type: "text", nullable: true),
                    forwarded_for = table.Column<string>(type: "text", nullable: true),
                    cf_ray = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "text", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_request_logs_seen_at",
                table: "request_logs",
                column: "seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_logs");
        }
    }
}
