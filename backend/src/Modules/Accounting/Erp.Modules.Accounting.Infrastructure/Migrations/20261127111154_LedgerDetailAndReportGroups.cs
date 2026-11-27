using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Accounting.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LedgerDetailAndReportGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostingRole",
                schema: "accounting",
                table: "JournalLine",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeId",
                schema: "accounting",
                table: "JournalLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxableBase",
                schema: "accounting",
                table: "JournalLine",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportGroup",
                schema: "accounting",
                table: "Account",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "None");

            // Existing accounts: the income-statement group the statements derived from the posting rules until now.
            migrationBuilder.Sql("""
                UPDATE a SET ReportGroup = CASE a.Type WHEN 'Revenue' THEN 'OtherIncome' WHEN 'Expense' THEN 'OperatingExpenses' ELSE 'None' END
                FROM accounting.Account a;

                UPDATE a SET ReportGroup = 'Revenue'
                FROM accounting.Account a
                WHERE a.Type = 'Revenue' AND EXISTS (SELECT 1 FROM accounting.AccountDetermination r
                    WHERE r.AccountId = a.Id AND r.Role IN ('SalesRevenue', 'SalesReturns', 'SalesDiscount', 'ServiceRevenue'));

                UPDATE a SET ReportGroup = 'CostOfSales'
                FROM accounting.Account a
                WHERE a.Type = 'Expense' AND EXISTS (SELECT 1 FROM accounting.AccountDetermination r
                    WHERE r.AccountId = a.Id AND r.Role IN ('CostOfGoodsSold', 'PurchasePriceVariance', 'CostTrueUp', 'ProductionVariance', 'AbnormalScrap'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostingRole",
                schema: "accounting",
                table: "JournalLine");

            migrationBuilder.DropColumn(
                name: "TaxCodeId",
                schema: "accounting",
                table: "JournalLine");

            migrationBuilder.DropColumn(
                name: "TaxableBase",
                schema: "accounting",
                table: "JournalLine");

            migrationBuilder.DropColumn(
                name: "ReportGroup",
                schema: "accounting",
                table: "Account");
        }
    }
}
