using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartnerApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing partners registered under the signed-assertion scheme have no API key, and
            // one cannot be conjured for them - the key is never stored, so it can only be issued
            // through the console where an operator is there to catch it. Their ApiKeyHash stays
            // NULL, every token request is refused, and the fix is "New key" on the partner.

            migrationBuilder.DropColumn(
                name: "PublicKeyPem",
                table: "partners");

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "partners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyPrefix",
                table: "partners",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_partners_ApiKeyPrefix",
                table: "partners",
                column: "ApiKeyPrefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_partners_ApiKeyPrefix",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "ApiKeyPrefix",
                table: "partners");

            migrationBuilder.AddColumn<string>(
                name: "PublicKeyPem",
                table: "partners",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }
    }
}
