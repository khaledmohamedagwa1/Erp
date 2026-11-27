using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Sales.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.CreateTable(
                name: "DeliveryNote",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsNumbered = table.Column<bool>(type: "bit", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryNote", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesInvoice",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryNoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentTermsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditApproved = table.Column<bool>(type: "bit", nullable: false),
                    IssuedStock = table.Column<bool>(type: "bit", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpenAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CancelledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsNumbered = table.Column<bool>(type: "bit", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesInvoice", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrder",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    QuotationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditApproved = table.Column<bool>(type: "bit", nullable: false),
                    StockReserved = table.Column<bool>(type: "bit", nullable: false),
                    ShortClosed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsNumbered = table.Column<bool>(type: "bit", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesQuotation",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ConvertedOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsNumbered = table.Column<bool>(type: "bit", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesQuotation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturn",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SalesInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsNumbered = table.Column<bool>(type: "bit", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturn", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryNote_TenantId",
                schema: "sales",
                table: "DeliveryNote",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryNote_TenantId_CompanyId_CustomerId",
                schema: "sales",
                table: "DeliveryNote",
                columns: new[] { "TenantId", "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryNote_TenantId_CompanyId_Number",
                schema: "sales",
                table: "DeliveryNote",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryNote_TenantId_CompanyId_Status_DocumentDate",
                schema: "sales",
                table: "DeliveryNote",
                columns: new[] { "TenantId", "CompanyId", "Status", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryNote_TenantId_SalesOrderId",
                schema: "sales",
                table: "DeliveryNote",
                columns: new[] { "TenantId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "sales",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoice_TenantId",
                schema: "sales",
                table: "SalesInvoice",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoice_TenantId_CompanyId_CustomerId",
                schema: "sales",
                table: "SalesInvoice",
                columns: new[] { "TenantId", "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoice_TenantId_CompanyId_Number",
                schema: "sales",
                table: "SalesInvoice",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoice_TenantId_CompanyId_Status_DocumentDate",
                schema: "sales",
                table: "SalesInvoice",
                columns: new[] { "TenantId", "CompanyId", "Status", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoice_TenantId_DeliveryNoteId",
                schema: "sales",
                table: "SalesInvoice",
                columns: new[] { "TenantId", "DeliveryNoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoice_TenantId_SalesOrderId",
                schema: "sales",
                table: "SalesInvoice",
                columns: new[] { "TenantId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_TenantId",
                schema: "sales",
                table: "SalesOrder",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_TenantId_CompanyId_CustomerId",
                schema: "sales",
                table: "SalesOrder",
                columns: new[] { "TenantId", "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_TenantId_CompanyId_Number",
                schema: "sales",
                table: "SalesOrder",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_TenantId_CompanyId_Status_DocumentDate",
                schema: "sales",
                table: "SalesOrder",
                columns: new[] { "TenantId", "CompanyId", "Status", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesQuotation_TenantId",
                schema: "sales",
                table: "SalesQuotation",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesQuotation_TenantId_CompanyId_CustomerId",
                schema: "sales",
                table: "SalesQuotation",
                columns: new[] { "TenantId", "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesQuotation_TenantId_CompanyId_Number",
                schema: "sales",
                table: "SalesQuotation",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesQuotation_TenantId_CompanyId_Status_DocumentDate",
                schema: "sales",
                table: "SalesQuotation",
                columns: new[] { "TenantId", "CompanyId", "Status", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturn_TenantId",
                schema: "sales",
                table: "SalesReturn",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturn_TenantId_CompanyId_CustomerId",
                schema: "sales",
                table: "SalesReturn",
                columns: new[] { "TenantId", "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturn_TenantId_CompanyId_Number",
                schema: "sales",
                table: "SalesReturn",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturn_TenantId_CompanyId_Status_DocumentDate",
                schema: "sales",
                table: "SalesReturn",
                columns: new[] { "TenantId", "CompanyId", "Status", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturn_TenantId_SalesInvoiceId",
                schema: "sales",
                table: "SalesReturn",
                columns: new[] { "TenantId", "SalesInvoiceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryNote",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "SalesInvoice",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "SalesOrder",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "SalesQuotation",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "SalesReturn",
                schema: "sales");
        }
    }
}
