using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Pos.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OfflineForeignTender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OfflineAllocatedTo",
                schema: "pos",
                table: "PosTerminal",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsOffline",
                schema: "pos",
                table: "PosReceipt",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfflineCreatedAtUtc",
                schema: "pos",
                table: "PosReceipt",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfflineAllocatedTo",
                schema: "pos",
                table: "PosTerminal");

            migrationBuilder.DropColumn(
                name: "IsOffline",
                schema: "pos",
                table: "PosReceipt");

            migrationBuilder.DropColumn(
                name: "OfflineCreatedAtUtc",
                schema: "pos",
                table: "PosReceipt");
        }
    }
}
