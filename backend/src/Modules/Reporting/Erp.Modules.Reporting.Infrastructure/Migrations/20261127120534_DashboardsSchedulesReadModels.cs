using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Reporting.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DashboardsSchedulesReadModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailySalesSummary",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    BranchKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Invoices = table.Column<int>(type: "int", nullable: false),
                    CreditNotes = table.Column<int>(type: "int", nullable: false),
                    GrossSales = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Returns = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    NetRevenue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Cogs = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailySalesSummary", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dashboard",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SharedWithRoleIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Widgets = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboard", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedule",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReportKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Parameters = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Cron = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSchedule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockValueSnapshot",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockValueSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportScheduleRun",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportScheduleRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportScheduleRun_ReportSchedule_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "reporting",
                        principalTable: "ReportSchedule",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailySalesSummary_TenantId",
                schema: "reporting",
                table: "DailySalesSummary",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DailySalesSummary_TenantId_CompanyId_Date_BranchKey",
                schema: "reporting",
                table: "DailySalesSummary",
                columns: new[] { "TenantId", "CompanyId", "Date", "BranchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dashboard_TenantId",
                schema: "reporting",
                table: "Dashboard",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboard_TenantId_CompanyId_OwnerUserId",
                schema: "reporting",
                table: "Dashboard",
                columns: new[] { "TenantId", "CompanyId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedule_TenantId",
                schema: "reporting",
                table: "ReportSchedule",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedule_TenantId_CompanyId_OwnerUserId",
                schema: "reporting",
                table: "ReportSchedule",
                columns: new[] { "TenantId", "CompanyId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedule_TenantId_IsActive_NextRunAtUtc",
                schema: "reporting",
                table: "ReportSchedule",
                columns: new[] { "TenantId", "IsActive", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportScheduleRun_ScheduleId",
                schema: "reporting",
                table: "ReportScheduleRun",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportScheduleRun_TenantId",
                schema: "reporting",
                table: "ReportScheduleRun",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportScheduleRun_TenantId_ScheduleId_StartedAtUtc",
                schema: "reporting",
                table: "ReportScheduleRun",
                columns: new[] { "TenantId", "ScheduleId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StockValueSnapshot_TenantId",
                schema: "reporting",
                table: "StockValueSnapshot",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockValueSnapshot_TenantId_CompanyId_Date_WarehouseId",
                schema: "reporting",
                table: "StockValueSnapshot",
                columns: new[] { "TenantId", "CompanyId", "Date", "WarehouseId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailySalesSummary",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "Dashboard",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "ReportScheduleRun",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "StockValueSnapshot",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "ReportSchedule",
                schema: "reporting");
        }
    }
}
