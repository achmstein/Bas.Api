using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerIdentityAndBasPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Abn",
                table: "workers",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                table: "workers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "workers",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FamilyName",
                table: "workers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "workers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "workers",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TfnLast3",
                table: "workers",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "TfnProtected",
                table: "workers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "workers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "bas_periods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FinancialYear = table.Column<int>(type: "integer", nullable: false),
                    Quarter = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatementType = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    TotalSales = table.Column<int>(type: "integer", nullable: true),
                    GstOnSales = table.Column<int>(type: "integer", nullable: true),
                    GstOnPurchases = table.Column<int>(type: "integer", nullable: true),
                    TotalPurchases = table.Column<int>(type: "integer", nullable: true),
                    CashAccountingMethod = table.Column<bool>(type: "boolean", nullable: true),
                    InstalmentIncome = table.Column<int>(type: "integer", nullable: true),
                    AtoInstalmentAmount = table.Column<int>(type: "integer", nullable: true),
                    VariedInstalmentAmount = table.Column<int>(type: "integer", nullable: true),
                    VariationReasonCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    TotalSalaryWages = table.Column<int>(type: "integer", nullable: true),
                    AmountWithheld = table.Column<int>(type: "integer", nullable: true),
                    NetAmount = table.Column<int>(type: "integer", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bas_periods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bas_periods_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bas_periods_Status",
                table: "bas_periods",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_bas_periods_WorkerId_FinancialYear_Quarter",
                table: "bas_periods",
                columns: new[] { "WorkerId", "FinancialYear", "Quarter" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bas_periods");

            migrationBuilder.DropColumn(
                name: "Abn",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "FamilyName",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "TfnLast3",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "TfnProtected",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "workers");
        }
    }
}
