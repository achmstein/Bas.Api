using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncStateTransientAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransientAttemptCount",
                table: "sync_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransientAttemptCount",
                table: "sync_states");
        }
    }
}
