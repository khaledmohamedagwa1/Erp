using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Parties.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "crm",
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
                name: "PaymentTerms",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Installments = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTerms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerGroup",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultPriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultPaymentTermsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGroup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerGroup_PaymentTerms_DefaultPaymentTermsId",
                        column: x => x.DefaultPaymentTermsId,
                        principalSchema: "crm",
                        principalTable: "PaymentTerms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxRegistrationNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    PaymentTermsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Addresses = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Contacts = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Supplier", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Supplier_PaymentTerms_PaymentTermsId",
                        column: x => x.PaymentTermsId,
                        principalSchema: "crm",
                        principalTable: "PaymentTerms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Customer",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomerGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditLimit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CreditAction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OnHold = table.Column<bool>(type: "bit", nullable: false),
                    IsWalkIn = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxRegistrationNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    PaymentTermsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Addresses = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Contacts = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customer_CustomerGroup_CustomerGroupId",
                        column: x => x.CustomerGroupId,
                        principalSchema: "crm",
                        principalTable: "CustomerGroup",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Customer_PaymentTerms_PaymentTermsId",
                        column: x => x.PaymentTermsId,
                        principalSchema: "crm",
                        principalTable: "PaymentTerms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customer_CustomerGroupId",
                schema: "crm",
                table: "Customer",
                column: "CustomerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Customer_PaymentTermsId",
                schema: "crm",
                table: "Customer",
                column: "PaymentTermsId");

            migrationBuilder.CreateIndex(
                name: "IX_Customer_TenantId",
                schema: "crm",
                table: "Customer",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Customer_TenantId_CompanyId_Code",
                schema: "crm",
                table: "Customer",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customer_TenantId_CompanyId_NameEn",
                schema: "crm",
                table: "Customer",
                columns: new[] { "TenantId", "CompanyId", "NameEn" });

            migrationBuilder.CreateIndex(
                name: "UX_Customer_WalkIn",
                schema: "crm",
                table: "Customer",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true,
                filter: "[IsWalkIn] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroup_DefaultPaymentTermsId",
                schema: "crm",
                table: "CustomerGroup",
                column: "DefaultPaymentTermsId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroup_TenantId",
                schema: "crm",
                table: "CustomerGroup",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroup_TenantId_CompanyId_Code",
                schema: "crm",
                table: "CustomerGroup",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "crm",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTerms_TenantId",
                schema: "crm",
                table: "PaymentTerms",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTerms_TenantId_CompanyId_Code",
                schema: "crm",
                table: "PaymentTerms",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_PaymentTermsId",
                schema: "crm",
                table: "Supplier",
                column: "PaymentTermsId");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId",
                schema: "crm",
                table: "Supplier",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId_CompanyId_Code",
                schema: "crm",
                table: "Supplier",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId_CompanyId_NameEn",
                schema: "crm",
                table: "Supplier",
                columns: new[] { "TenantId", "CompanyId", "NameEn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customer",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Supplier",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "CustomerGroup",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "PaymentTerms",
                schema: "crm");
        }
    }
}
