using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "Batch",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ManufactureDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batch", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CostLayer",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValuationAreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OriginalQuantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    RemainingQuantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostLayer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryBalance",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnHand = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Reserved = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryBalance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovement",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValuationAreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostingSequence = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SerialId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MovementType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Direction = table.Column<short>(type: "smallint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseQuantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceModule = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceDocType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceDocId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferenceNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ReversalOfId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    JournalPurpose = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RelatedMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovement", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemValuation",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValuationAreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostingMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AverageCost = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    LastCost = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemValuation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LayerConsumption",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LayerConsumption", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpeningStock",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningStock", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "inventory",
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
                name: "SerialNumber",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Serial = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CurrentWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerialNumber", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustment",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StockCountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NetCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockCount",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SnapshotAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCount", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockReservation",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemandType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DemandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemandLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Consumed = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Released = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockTransfer",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransitWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceiptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfer", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batch_TenantId",
                schema: "inventory",
                table: "Batch",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Batch_TenantId_ProductId_Number",
                schema: "inventory",
                table: "Batch",
                columns: new[] { "TenantId", "ProductId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostLayer_ReceiptMovementId",
                schema: "inventory",
                table: "CostLayer",
                column: "ReceiptMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostLayer_TenantId",
                schema: "inventory",
                table: "CostLayer",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CostLayer_TenantId_ValuationAreaId_ProductId_VariantKey_Sequence",
                schema: "inventory",
                table: "CostLayer",
                columns: new[] { "TenantId", "ValuationAreaId", "ProductId", "VariantKey", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_TenantId",
                schema: "inventory",
                table: "InventoryBalance",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_TenantId_WarehouseId",
                schema: "inventory",
                table: "InventoryBalance",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "UX_InventoryBalance_Key",
                schema: "inventory",
                table: "InventoryBalance",
                columns: new[] { "TenantId", "ProductId", "VariantKey", "WarehouseId", "BatchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovement_TenantId",
                schema: "inventory",
                table: "InventoryMovement",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovement_TenantId_LegalEntityId_ValuationAreaId",
                schema: "inventory",
                table: "InventoryMovement",
                columns: new[] { "TenantId", "LegalEntityId", "ValuationAreaId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovement_TenantId_ProductId_WarehouseId_TransactionDate",
                schema: "inventory",
                table: "InventoryMovement",
                columns: new[] { "TenantId", "ProductId", "WarehouseId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "UX_InventoryMovement_Source",
                schema: "inventory",
                table: "InventoryMovement",
                columns: new[] { "TenantId", "SourceDocType", "SourceDocId", "SourceLineId", "MovementType", "Direction", "BatchId", "SerialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemValuation_TenantId",
                schema: "inventory",
                table: "ItemValuation",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_ItemValuation_Key",
                schema: "inventory",
                table: "ItemValuation",
                columns: new[] { "TenantId", "ValuationAreaId", "ProductId", "VariantKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LayerConsumption_LayerId",
                schema: "inventory",
                table: "LayerConsumption",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_LayerConsumption_MovementId",
                schema: "inventory",
                table: "LayerConsumption",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_LayerConsumption_TenantId",
                schema: "inventory",
                table: "LayerConsumption",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStock_TenantId",
                schema: "inventory",
                table: "OpeningStock",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStock_TenantId_CompanyId_Number",
                schema: "inventory",
                table: "OpeningStock",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "inventory",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SerialNumber_TenantId",
                schema: "inventory",
                table: "SerialNumber",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SerialNumber_TenantId_ProductId_Serial",
                schema: "inventory",
                table: "SerialNumber",
                columns: new[] { "TenantId", "ProductId", "Serial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustment_TenantId",
                schema: "inventory",
                table: "StockAdjustment",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustment_TenantId_CompanyId_Number",
                schema: "inventory",
                table: "StockAdjustment",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCount_TenantId",
                schema: "inventory",
                table: "StockCount",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCount_TenantId_CompanyId_Number",
                schema: "inventory",
                table: "StockCount",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservation_TenantId",
                schema: "inventory",
                table: "StockReservation",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservation_TenantId_DemandType_DemandId_DemandLineId",
                schema: "inventory",
                table: "StockReservation",
                columns: new[] { "TenantId", "DemandType", "DemandId", "DemandLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfer_TenantId",
                schema: "inventory",
                table: "StockTransfer",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfer_TenantId_CompanyId_Number",
                schema: "inventory",
                table: "StockTransfer",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            // The inventory ledger is append-only (Part 7.2): corrections are reversal movements, never edits.
            migrationBuilder.Sql("""
                CREATE TRIGGER inventory.TR_InventoryMovement_Immutable ON inventory.InventoryMovement
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Inventory movements are immutable. Post a reversal instead.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS inventory.TR_InventoryMovement_Immutable;");

            migrationBuilder.DropTable(
                name: "Batch",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "CostLayer",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "InventoryBalance",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "InventoryMovement",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ItemValuation",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "LayerConsumption",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "OpeningStock",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "SerialNumber",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockAdjustment",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockCount",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockReservation",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockTransfer",
                schema: "inventory");
        }
    }
}
