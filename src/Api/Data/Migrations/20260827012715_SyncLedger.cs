using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BasPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DirtyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_states_bas_periods_BasPeriodId",
                        column: x => x.BasPeriodId,
                        principalTable: "bas_periods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_states_BasPeriodId",
                table: "sync_states",
                column: "BasPeriodId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_states_Status_NextAttemptAt",
                table: "sync_states",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_states");
        }
    }
}
