using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "Brand",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brand", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "catalog",
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
                name: "PriceList",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceList", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductCategory",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultCostingMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PostingGroup = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCategory_ProductCategory_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "catalog",
                        principalTable: "ProductCategory",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UnitOfMeasure",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Class = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitOfMeasure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Product",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BaseUomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Tracking = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExpiryTracked = table.Column<bool>(type: "bit", nullable: false),
                    TaxCategory = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CostingMethodOverride = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PostingGroupOverride = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Product", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Product_Brand_BrandId",
                        column: x => x.BrandId,
                        principalSchema: "catalog",
                        principalTable: "Brand",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Product_ProductCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "ProductCategory",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Product_UnitOfMeasure_BaseUomId",
                        column: x => x.BaseUomId,
                        principalSchema: "catalog",
                        principalTable: "UnitOfMeasure",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PriceListItem",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinQuantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceListItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceListItem_PriceList_PriceListId",
                        column: x => x.PriceListId,
                        principalSchema: "catalog",
                        principalTable: "PriceList",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PriceListItem_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceListItem_UnitOfMeasure_UomId",
                        column: x => x.UomId,
                        principalSchema: "catalog",
                        principalTable: "UnitOfMeasure",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProductBarcode",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UomId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductBarcode", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductBarcode_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductUnit",
                schema: "catalog",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FactorToBase = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUnit", x => new { x.ProductId, x.UomId });
                    table.ForeignKey(
                        name: "FK_ProductUnit_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductUnit_UnitOfMeasure_UomId",
                        column: x => x.UomId,
                        principalSchema: "catalog",
                        principalTable: "UnitOfMeasure",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProductVariant",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Attributes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVariant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductVariant_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Brand_TenantId",
                schema: "catalog",
                table: "Brand",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Brand_TenantId_CompanyId_Code",
                schema: "catalog",
                table: "Brand",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "catalog",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceList_TenantId",
                schema: "catalog",
                table: "PriceList",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceList_TenantId_CompanyId_Code",
                schema: "catalog",
                table: "PriceList",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItem_PriceListId_ProductId_UomId",
                schema: "catalog",
                table: "PriceListItem",
                columns: new[] { "PriceListId", "ProductId", "UomId" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItem_ProductId",
                schema: "catalog",
                table: "PriceListItem",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItem_TenantId",
                schema: "catalog",
                table: "PriceListItem",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItem_UomId",
                schema: "catalog",
                table: "PriceListItem",
                column: "UomId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_BaseUomId",
                schema: "catalog",
                table: "Product",
                column: "BaseUomId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_BrandId",
                schema: "catalog",
                table: "Product",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_CategoryId",
                schema: "catalog",
                table: "Product",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_TenantId",
                schema: "catalog",
                table: "Product",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_TenantId_CompanyId_NameEn",
                schema: "catalog",
                table: "Product",
                columns: new[] { "TenantId", "CompanyId", "NameEn" });

            migrationBuilder.CreateIndex(
                name: "IX_Product_TenantId_CompanyId_Sku",
                schema: "catalog",
                table: "Product",
                columns: new[] { "TenantId", "CompanyId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductBarcode_ProductId",
                schema: "catalog",
                table: "ProductBarcode",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductBarcode_TenantId",
                schema: "catalog",
                table: "ProductBarcode",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductBarcode_TenantId_CompanyId_Barcode",
                schema: "catalog",
                table: "ProductBarcode",
                columns: new[] { "TenantId", "CompanyId", "Barcode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategory_ParentId",
                schema: "catalog",
                table: "ProductCategory",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategory_TenantId",
                schema: "catalog",
                table: "ProductCategory",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategory_TenantId_CompanyId_Code",
                schema: "catalog",
                table: "ProductCategory",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnit_TenantId",
                schema: "catalog",
                table: "ProductUnit",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnit_UomId",
                schema: "catalog",
                table: "ProductUnit",
                column: "UomId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariant_ProductId",
                schema: "catalog",
                table: "ProductVariant",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariant_TenantId",
                schema: "catalog",
                table: "ProductVariant",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariant_TenantId_CompanyId_Sku",
                schema: "catalog",
                table: "ProductVariant",
                columns: new[] { "TenantId", "CompanyId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnitOfMeasure_TenantId",
                schema: "catalog",
                table: "UnitOfMeasure",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UnitOfMeasure_TenantId_CompanyId_Code",
                schema: "catalog",
                table: "UnitOfMeasure",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "PriceListItem",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductBarcode",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductUnit",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductVariant",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "PriceList",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Product",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Brand",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductCategory",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "UnitOfMeasure",
                schema: "catalog");
        }
    }
}
