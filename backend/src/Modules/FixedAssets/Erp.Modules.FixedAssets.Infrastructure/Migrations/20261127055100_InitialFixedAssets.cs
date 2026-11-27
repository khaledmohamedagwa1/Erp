using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.FixedAssets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialFixedAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assets");

            migrationBuilder.CreateTable(
                name: "AssetCategory",
                schema: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultMethod = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UsefulLifeMonths = table.Column<int>(type: "int", nullable: false),
                    SalvagePercent = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    DecliningRatePercent = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    PostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
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
                    table.PrimaryKey("PK_AssetCategory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DepreciationRun",
                schema: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Period = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReversalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReversalJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.PrimaryKey("PK_DepreciationRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "assets",
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
                name: "Asset",
                schema: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CustodianId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcquisitionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    InServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AcquisitionCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SalvageValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UsefulLifeMonths = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DecliningRatePercent = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    TotalUnits = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AccumulatedDepreciation = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PostingGroup = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AcquisitionMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AcquisitionJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceDocType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SourceDocId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisposalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DisposalProceeds = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    DisposalGainLoss = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    DisposalJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Schedule = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    History = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asset", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Asset_AssetCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "assets",
                        principalTable: "AssetCategory",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Asset_CategoryId",
                schema: "assets",
                table: "Asset",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_TenantId",
                schema: "assets",
                table: "Asset",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_TenantId_CompanyId_Number",
                schema: "assets",
                table: "Asset",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true,
                filter: "[Number] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_TenantId_LegalEntityId_Status",
                schema: "assets",
                table: "Asset",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategory_TenantId",
                schema: "assets",
                table: "AssetCategory",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategory_TenantId_CompanyId_Code",
                schema: "assets",
                table: "AssetCategory",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DepreciationRun_TenantId",
                schema: "assets",
                table: "DepreciationRun",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DepreciationRun_TenantId_LegalEntityId_Period",
                schema: "assets",
                table: "DepreciationRun",
                columns: new[] { "TenantId", "LegalEntityId", "Period" },
                unique: true,
                filter: "[Status] = 'Posted'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "assets",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Asset",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "DepreciationRun",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "AssetCategory",
                schema: "assets");
        }
    }
}
