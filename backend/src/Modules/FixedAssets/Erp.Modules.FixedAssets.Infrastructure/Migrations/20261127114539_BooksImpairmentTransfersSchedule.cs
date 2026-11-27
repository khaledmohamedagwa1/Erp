using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.FixedAssets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BooksImpairmentTransfersSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Books",
                schema: "assets",
                table: "Asset",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "N'[]'");

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                schema: "assets",
                table: "Asset",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<decimal>(
                name: "RevaluationLossInProfit",
                schema: "assets",
                table: "Asset",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RevaluationReserve",
                schema: "assets",
                table: "Asset",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "AssetSettings",
                schema: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AutoDepreciationEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RunOnDay = table.Column<int>(type: "int", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastRunSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DepreciationBook",
                schema: "assets",
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
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepreciationBook", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetSettings_TenantId",
                schema: "assets",
                table: "AssetSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetSettings_TenantId_CompanyId",
                schema: "assets",
                table: "AssetSettings",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DepreciationBook_TenantId",
                schema: "assets",
                table: "DepreciationBook",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DepreciationBook_TenantId_CompanyId_Code",
                schema: "assets",
                table: "DepreciationBook",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetSettings",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "DepreciationBook",
                schema: "assets");

            migrationBuilder.DropColumn(
                name: "Books",
                schema: "assets",
                table: "Asset");

            migrationBuilder.DropColumn(
                name: "Quantity",
                schema: "assets",
                table: "Asset");

            migrationBuilder.DropColumn(
                name: "RevaluationLossInProfit",
                schema: "assets",
                table: "Asset");

            migrationBuilder.DropColumn(
                name: "RevaluationReserve",
                schema: "assets",
                table: "Asset");
        }
    }
}
