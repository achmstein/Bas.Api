using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StorePlaintextTfnAndSigningKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows hold AES-GCM ciphertext that the new columns cannot interpret, and the
            // key that would decrypt them is being removed along with this change.
            //
            // Signing keys are regenerable, so drop them - the store mints a fresh one on the next
            // start. The only cost is that access tokens issued in the preceding ten minutes stop
            // verifying, which the partner handles by re-calling its own token route.
            migrationBuilder.Sql("DELETE FROM signing_keys;");

            // Worker TFNs cannot be recovered, so the identity has to be re-entered through
            // PUT /api/v1/workers/me. IsCompleteForLodgement goes false on its own, and submit
            // refuses until it is true, so nothing can reach Practice Manager half-identified.
            migrationBuilder.Sql("UPDATE workers SET \"TfnLast3\" = NULL;");

            migrationBuilder.DropColumn(
                name: "TfnProtected",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "PrivateKeyProtected",
                table: "signing_keys");

            migrationBuilder.AddColumn<string>(
                name: "Tfn",
                table: "workers",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivateKeyPem",
                table: "signing_keys",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tfn",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "PrivateKeyPem",
                table: "signing_keys");

            migrationBuilder.AddColumn<byte[]>(
                name: "TfnProtected",
                table: "workers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PrivateKeyProtected",
                table: "signing_keys",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
