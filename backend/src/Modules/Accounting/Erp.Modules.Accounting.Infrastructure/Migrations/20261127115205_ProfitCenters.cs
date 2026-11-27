using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Accounting.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProfitCenters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProfitCenterId",
                schema: "accounting",
                table: "JournalLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProfitCenter",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.PrimaryKey("PK_ProfitCenter", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalLine_ProfitCenterId",
                schema: "accounting",
                table: "JournalLine",
                column: "ProfitCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfitCenter_TenantId",
                schema: "accounting",
                table: "ProfitCenter",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfitCenter_TenantId_CompanyId_BranchId",
                schema: "accounting",
                table: "ProfitCenter",
                columns: new[] { "TenantId", "CompanyId", "BranchId" },
                unique: true,
                filter: "[BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProfitCenter_TenantId_CompanyId_Code",
                schema: "accounting",
                table: "ProfitCenter",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLine_ProfitCenter_ProfitCenterId",
                schema: "accounting",
                table: "JournalLine",
                column: "ProfitCenterId",
                principalSchema: "accounting",
                principalTable: "ProfitCenter",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalLine_ProfitCenter_ProfitCenterId",
                schema: "accounting",
                table: "JournalLine");

            migrationBuilder.DropTable(
                name: "ProfitCenter",
                schema: "accounting");

            migrationBuilder.DropIndex(
                name: "IX_JournalLine_ProfitCenterId",
                schema: "accounting",
                table: "JournalLine");

            migrationBuilder.DropColumn(
                name: "ProfitCenterId",
                schema: "accounting",
                table: "JournalLine");
        }
    }
}
