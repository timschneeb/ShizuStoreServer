using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRequestFull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "full",
                table: "sync_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "full",
                table: "sync_requests");
        }
    }
}
