using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Manufacturing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StandardCostingCapacityRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkingDaysMask",
                schema: "manufacturing",
                table: "WorkCenter",
                type: "int",
                nullable: false,
                defaultValue: 127);

            migrationBuilder.AddColumn<decimal>(
                name: "ConversionVariance",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "CostVersionId",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostingMethod",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Actual");

            migrationBuilder.AddColumn<decimal>(
                name: "MaterialPriceVariance",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaterialUsageVariance",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "OrderType",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<decimal>(
                name: "OtherVariance",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "ReworkOfOrderId",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardLaborUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardMachineUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardMaterialUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardOverheadUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Substitutions",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "VariantId",
                schema: "manufacturing",
                table: "ProductionOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostingMethod",
                schema: "manufacturing",
                table: "ItemPlanningParameters",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreferredSupplierId",
                schema: "manufacturing",
                table: "ItemPlanningParameters",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AbsorptionRun",
                schema: "manufacturing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbsorptionRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CostPool",
                schema: "manufacturing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WorkCenterIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpenseAccountIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                    table.PrimaryKey("PK_CostPool", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CostVersion",
                schema: "manufacturing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActivatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Items = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostVersion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingSettings",
                schema: "manufacturing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefaultCostingMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbsorptionRun_TenantId",
                schema: "manufacturing",
                table: "AbsorptionRun",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AbsorptionRun_TenantId_LegalEntityId_PeriodFrom_PeriodTo",
                schema: "manufacturing",
                table: "AbsorptionRun",
                columns: new[] { "TenantId", "LegalEntityId", "PeriodFrom", "PeriodTo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostPool_TenantId",
                schema: "manufacturing",
                table: "CostPool",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CostPool_TenantId_CompanyId_Code",
                schema: "manufacturing",
                table: "CostPool",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostVersion_TenantId",
                schema: "manufacturing",
                table: "CostVersion",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CostVersion_TenantId_CompanyId_Code",
                schema: "manufacturing",
                table: "CostVersion",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingSettings_TenantId",
                schema: "manufacturing",
                table: "ManufacturingSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingSettings_TenantId_CompanyId",
                schema: "manufacturing",
                table: "ManufacturingSettings",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbsorptionRun",
                schema: "manufacturing");

            migrationBuilder.DropTable(
                name: "CostPool",
                schema: "manufacturing");

            migrationBuilder.DropTable(
                name: "CostVersion",
                schema: "manufacturing");

            migrationBuilder.DropTable(
                name: "ManufacturingSettings",
                schema: "manufacturing");

            migrationBuilder.DropColumn(
                name: "WorkingDaysMask",
                schema: "manufacturing",
                table: "WorkCenter");

            migrationBuilder.DropColumn(
                name: "ConversionVariance",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "CostVersionId",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "CostingMethod",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "MaterialPriceVariance",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "MaterialUsageVariance",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "OrderType",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "OtherVariance",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "ReworkOfOrderId",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "StandardLaborUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "StandardMachineUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "StandardMaterialUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "StandardOverheadUnitCost",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "Substitutions",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "VariantId",
                schema: "manufacturing",
                table: "ProductionOrder");

            migrationBuilder.DropColumn(
                name: "CostingMethod",
                schema: "manufacturing",
                table: "ItemPlanningParameters");

            migrationBuilder.DropColumn(
                name: "PreferredSupplierId",
                schema: "manufacturing",
                table: "ItemPlanningParameters");
        }
    }
}
