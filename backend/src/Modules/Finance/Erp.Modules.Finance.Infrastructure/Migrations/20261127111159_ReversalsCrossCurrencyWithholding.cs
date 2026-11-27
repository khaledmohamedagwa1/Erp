using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Finance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReversalsCrossCurrencyWithholding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ReversalDate",
                schema: "finance",
                table: "Settlement",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                schema: "finance",
                table: "Settlement",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WithholdingCertificateId",
                schema: "finance",
                table: "Settlement",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalOfId",
                schema: "finance",
                table: "Allocation",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SettlementAmount",
                schema: "finance",
                table: "Allocation",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WithholdingCertificate",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TotalTaxableBase = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TotalWithheldBase = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithholdingCertificate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Settlement_TenantId_WithholdingCertificateId",
                schema: "finance",
                table: "Settlement",
                columns: new[] { "TenantId", "WithholdingCertificateId" });

            migrationBuilder.CreateIndex(
                name: "UX_CashMovement_GlJournalLine",
                schema: "finance",
                table: "CashMovement",
                columns: new[] { "TenantId", "CashBankAccountId", "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "[SourceDocType] = 'GlJournalLine'");

            migrationBuilder.CreateIndex(
                name: "IX_WithholdingCertificate_TenantId",
                schema: "finance",
                table: "WithholdingCertificate",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WithholdingCertificate_TenantId_CompanyId_Kind_PartyId",
                schema: "finance",
                table: "WithholdingCertificate",
                columns: new[] { "TenantId", "CompanyId", "Kind", "PartyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WithholdingCertificate",
                schema: "finance");

            migrationBuilder.DropIndex(
                name: "IX_Settlement_TenantId_WithholdingCertificateId",
                schema: "finance",
                table: "Settlement");

            migrationBuilder.DropIndex(
                name: "UX_CashMovement_GlJournalLine",
                schema: "finance",
                table: "CashMovement");

            migrationBuilder.DropColumn(
                name: "ReversalDate",
                schema: "finance",
                table: "Settlement");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                schema: "finance",
                table: "Settlement");

            migrationBuilder.DropColumn(
                name: "WithholdingCertificateId",
                schema: "finance",
                table: "Settlement");

            migrationBuilder.DropColumn(
                name: "ReversalOfId",
                schema: "finance",
                table: "Allocation");

            migrationBuilder.DropColumn(
                name: "SettlementAmount",
                schema: "finance",
                table: "Allocation");
        }
    }
}
