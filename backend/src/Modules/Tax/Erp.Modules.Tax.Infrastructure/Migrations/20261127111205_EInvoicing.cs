using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Tax.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EInvoiceSubmission",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceModule = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceDocType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceDocId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Counter = table.Column<long>(type: "bigint", nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PreviousHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    QrCode = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Errors = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EInvoiceSubmission", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EInvoicingSettings",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LastCounter = table.Column<long>(type: "bigint", nullable: false),
                    LastHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EInvoicingSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceSubmission_TenantId",
                schema: "tax",
                table: "EInvoiceSubmission",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceSubmission_TenantId_CompanyId_Status",
                schema: "tax",
                table: "EInvoiceSubmission",
                columns: new[] { "TenantId", "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceSubmission_TenantId_SourceDocType_SourceDocId",
                schema: "tax",
                table: "EInvoiceSubmission",
                columns: new[] { "TenantId", "SourceDocType", "SourceDocId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EInvoicingSettings_TenantId",
                schema: "tax",
                table: "EInvoicingSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EInvoicingSettings_TenantId_CompanyId",
                schema: "tax",
                table: "EInvoicingSettings",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EInvoiceSubmission",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "EInvoicingSettings",
                schema: "tax");
        }
    }
}
