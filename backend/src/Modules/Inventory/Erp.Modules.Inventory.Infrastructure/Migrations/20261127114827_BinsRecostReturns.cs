using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BinsRecostReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Direct",
                schema: "inventory",
                table: "StockTransfer",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BinBalance",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BinId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BinBalance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BinMovement",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BinId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InventoryMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceDocType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceDocId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BinMovement", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CostLayerAdjustment",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RemainingAtAdjustment = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostLayerAdjustment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecostRun",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValuationAreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TotalDifference = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    Corrections = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecostRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageBin",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsDefaultReceiving = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_StorageBin", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BinBalance_TenantId",
                schema: "inventory",
                table: "BinBalance",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BinBalance_TenantId_WarehouseId_ProductId",
                schema: "inventory",
                table: "BinBalance",
                columns: new[] { "TenantId", "WarehouseId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "UX_BinBalance_Key",
                schema: "inventory",
                table: "BinBalance",
                columns: new[] { "TenantId", "BinId", "ProductId", "VariantKey", "BatchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BinMovement_InventoryMovementId",
                schema: "inventory",
                table: "BinMovement",
                column: "InventoryMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_BinMovement_TenantId",
                schema: "inventory",
                table: "BinMovement",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BinMovement_TenantId_BinId",
                schema: "inventory",
                table: "BinMovement",
                columns: new[] { "TenantId", "BinId" });

            migrationBuilder.CreateIndex(
                name: "IX_CostLayerAdjustment_LayerId",
                schema: "inventory",
                table: "CostLayerAdjustment",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_CostLayerAdjustment_MovementId",
                schema: "inventory",
                table: "CostLayerAdjustment",
                column: "MovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostLayerAdjustment_TenantId",
                schema: "inventory",
                table: "CostLayerAdjustment",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RecostRun_TenantId",
                schema: "inventory",
                table: "RecostRun",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RecostRun_TenantId_ValuationAreaId_ProductId_Status",
                schema: "inventory",
                table: "RecostRun",
                columns: new[] { "TenantId", "ValuationAreaId", "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageBin_TenantId",
                schema: "inventory",
                table: "StorageBin",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageBin_TenantId_WarehouseId_Code",
                schema: "inventory",
                table: "StorageBin",
                columns: new[] { "TenantId", "WarehouseId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BinBalance",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "BinMovement",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "CostLayerAdjustment",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "RecostRun",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StorageBin",
                schema: "inventory");

            migrationBuilder.DropColumn(
                name: "Direct",
                schema: "inventory",
                table: "StockTransfer");
        }
    }
}
