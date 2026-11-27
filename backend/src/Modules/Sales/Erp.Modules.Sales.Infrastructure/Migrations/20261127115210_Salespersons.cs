using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Sales.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Salespersons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesReturn",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesQuotation",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesInvoice",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalespersonId",
                schema: "sales",
                table: "DeliveryNote",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesReturn");

            migrationBuilder.DropColumn(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesQuotation");

            migrationBuilder.DropColumn(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesOrder");

            migrationBuilder.DropColumn(
                name: "SalespersonId",
                schema: "sales",
                table: "SalesInvoice");

            migrationBuilder.DropColumn(
                name: "SalespersonId",
                schema: "sales",
                table: "DeliveryNote");
        }
    }
}
