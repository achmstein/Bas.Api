using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueWorkerPerPartnerLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_partner_user_links_WorkerId",
                table: "partner_user_links");

            // WorkerProvisioner has only ever created one link per worker, so no rows should
            // match — but if any hypothetical duplicate exists, keep the earliest by CreatedAt.
            // That is the exact row the webhook publisher used to resolve to, so behaviour is
            // preserved for it while the unique index below makes the invariant real.
            migrationBuilder.Sql(
                """
                DELETE FROM partner_user_links dup
                USING partner_user_links keep
                WHERE dup."WorkerId" = keep."WorkerId"
                  AND (dup."CreatedAt" > keep."CreatedAt"
                       OR (dup."CreatedAt" = keep."CreatedAt" AND dup."Id" > keep."Id"));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_partner_user_links_WorkerId",
                table: "partner_user_links",
                column: "WorkerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_partner_user_links_WorkerId",
                table: "partner_user_links");

            migrationBuilder.CreateIndex(
                name: "IX_partner_user_links_WorkerId",
                table: "partner_user_links",
                column: "WorkerId");
        }
    }
}
