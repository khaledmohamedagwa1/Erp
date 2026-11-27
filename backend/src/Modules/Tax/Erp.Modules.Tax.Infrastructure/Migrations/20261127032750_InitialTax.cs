using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Tax.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tax");

            migrationBuilder.CreateTable(
                name: "Currency",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MinorUnits = table.Column<int>(type: "int", nullable: false),
                    CashRoundingIncrement = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currency", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRate",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ToCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "tax",
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
                name: "TaxCode",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Recoverable = table.Column<bool>(type: "bit", nullable: false),
                    ReportingBox = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Jurisdiction = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCode", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxGroup",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxRate",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaxCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxRate_TaxCode_TaxCodeId",
                        column: x => x.TaxCodeId,
                        principalSchema: "tax",
                        principalTable: "TaxCode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxCategoryMapping",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TaxGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCategoryMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxCategoryMapping_TaxGroup_TaxGroupId",
                        column: x => x.TaxGroupId,
                        principalSchema: "tax",
                        principalTable: "TaxGroup",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TaxGroupComponent",
                schema: "tax",
                columns: table => new
                {
                    TaxGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    TaxCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompoundsOnPrevious = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxGroupComponent", x => new { x.TaxGroupId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_TaxGroupComponent_TaxCode_TaxCodeId",
                        column: x => x.TaxCodeId,
                        principalSchema: "tax",
                        principalTable: "TaxCode",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaxGroupComponent_TaxGroup_TaxGroupId",
                        column: x => x.TaxGroupId,
                        principalSchema: "tax",
                        principalTable: "TaxGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Currency_TenantId",
                schema: "tax",
                table: "Currency",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Currency_TenantId_Code",
                schema: "tax",
                table: "Currency",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRate_TenantId",
                schema: "tax",
                table: "ExchangeRate",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRate_TenantId_FromCurrency_ToCurrency_Type_EffectiveDate",
                schema: "tax",
                table: "ExchangeRate",
                columns: new[] { "TenantId", "FromCurrency", "ToCurrency", "Type", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "tax",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxCategoryMapping_TaxGroupId",
                schema: "tax",
                table: "TaxCategoryMapping",
                column: "TaxGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCategoryMapping_TenantId",
                schema: "tax",
                table: "TaxCategoryMapping",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCategoryMapping_TenantId_CompanyId_CategoryCode_Direction",
                schema: "tax",
                table: "TaxCategoryMapping",
                columns: new[] { "TenantId", "CompanyId", "CategoryCode", "Direction" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCode_TenantId",
                schema: "tax",
                table: "TaxCode",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCode_TenantId_CompanyId_Code",
                schema: "tax",
                table: "TaxCode",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxGroup_TenantId",
                schema: "tax",
                table: "TaxGroup",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxGroup_TenantId_CompanyId_Code",
                schema: "tax",
                table: "TaxGroup",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxGroupComponent_TaxCodeId",
                schema: "tax",
                table: "TaxGroupComponent",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxGroupComponent_TenantId",
                schema: "tax",
                table: "TaxGroupComponent",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRate_TaxCodeId",
                schema: "tax",
                table: "TaxRate",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRate_TenantId",
                schema: "tax",
                table: "TaxRate",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Currency",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "ExchangeRate",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "TaxCategoryMapping",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "TaxGroupComponent",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "TaxRate",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "TaxGroup",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "TaxCode",
                schema: "tax");
        }
    }
}
