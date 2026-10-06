using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppPublishedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            // Every row that existed before the publish gate was already being
            // served, so it must stay visible; only rows created after this
            // change start unpublished.
            migrationBuilder.Sql(
                "UPDATE apps SET published_at = COALESCE(last_checked_at, added_at, updated_at);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "published_at",
                table: "apps");
        }
    }
}
