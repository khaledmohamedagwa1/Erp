using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Accounting.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StatementsYearEndOpeningBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAtUtc",
                schema: "accounting",
                table: "FiscalYear",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosingJournalId",
                schema: "accounting",
                table: "FiscalYear",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsClosed",
                schema: "accounting",
                table: "FiscalYear",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CashFlowCategory",
                schema: "accounting",
                table: "Account",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            // Existing balance-sheet posting accounts: cash for the generic template's cash/bank accounts, investing for PP&E,
            // financing for capital/retained earnings/opening equity, operating for the rest (editable afterwards).
            migrationBuilder.Sql("""
                UPDATE accounting.Account
                   SET CashFlowCategory = CASE
                       WHEN Code IN ('1110', '1120') THEN 'Cash'
                       WHEN Code IN ('1510') THEN 'Investing'
                       WHEN Code IN ('3100', '3200', '3300') THEN 'Financing'
                       ELSE 'Operating' END
                 WHERE IsPostingAccount = 1 AND [Type] IN ('Asset', 'Liability', 'Equity');
                """);

            migrationBuilder.CreateTable(
                name: "OpeningBalance",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpeningDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_OpeningBalance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpeningBalanceLine",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpeningBalanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Debit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Credit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningBalanceLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpeningBalanceLine_Account_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "accounting",
                        principalTable: "Account",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpeningBalanceLine_OpeningBalance_OpeningBalanceId",
                        column: x => x.OpeningBalanceId,
                        principalSchema: "accounting",
                        principalTable: "OpeningBalance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalance_TenantId",
                schema: "accounting",
                table: "OpeningBalance",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalance_TenantId_LegalEntityId",
                schema: "accounting",
                table: "OpeningBalance",
                columns: new[] { "TenantId", "LegalEntityId" },
                unique: true,
                filter: "[Status] = 'Posted'");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLine_AccountId",
                schema: "accounting",
                table: "OpeningBalanceLine",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLine_OpeningBalanceId",
                schema: "accounting",
                table: "OpeningBalanceLine",
                column: "OpeningBalanceId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalanceLine_TenantId",
                schema: "accounting",
                table: "OpeningBalanceLine",
                column: "TenantId");

            // Database-level immutability of posted journals (Part 12.4, ADR-010). Corrections are reversal journals.
            // Lines: no UPDATE or DELETE once their journal is posted or reversed (drafts stay editable).
            migrationBuilder.Sql("""
                CREATE TRIGGER accounting.TR_JournalLine_Immutable ON accounting.JournalLine
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted d JOIN accounting.JournalEntry j ON j.Id = d.JournalEntryId WHERE j.[Status] <> 'Draft')
                        THROW 51001, 'Lines of posted journals are immutable. Post a reversal journal instead.', 1;
                END
                """);

            // Headers: posted/reversed journals cannot be deleted; the only permitted change is Posted -> Reversed with the
            // reversal link (plus audit stamps and rowversion, which the database maintains).
            migrationBuilder.Sql("""
                CREATE TRIGGER accounting.TR_JournalEntry_Immutable ON accounting.JournalEntry
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                    BEGIN
                        IF EXISTS (SELECT 1 FROM deleted WHERE [Status] <> 'Draft')
                            THROW 51002, 'Posted journals cannot be deleted. Post a reversal journal instead.', 1;
                        RETURN;
                    END;

                    IF EXISTS (
                        SELECT 1
                        FROM deleted d
                        JOIN inserted i ON i.Id = d.Id
                        WHERE d.[Status] <> 'Draft'
                          AND (   (d.[Status] = 'Reversed' AND i.[Status] <> 'Reversed')
                               OR (d.[Status] = 'Posted' AND i.[Status] NOT IN ('Posted', 'Reversed'))
                               OR (d.ReversedById IS NOT NULL AND (i.ReversedById IS NULL OR i.ReversedById <> d.ReversedById))
                               OR EXISTS (
                                    SELECT d.TenantId, d.CompanyId, d.LegalEntityId, d.Number, d.JournalDate, d.FiscalPeriodId, d.SourceModule,
                                           d.SourceDocType, d.SourceDocId, d.Purpose, d.[Description], d.Currency, d.ExchangeRate, d.ReversalOfId,
                                           d.PostedBy, d.PostedAtUtc, d.CreatedAtUtc, d.CreatedBy
                                    EXCEPT
                                    SELECT i.TenantId, i.CompanyId, i.LegalEntityId, i.Number, i.JournalDate, i.FiscalPeriodId, i.SourceModule,
                                           i.SourceDocType, i.SourceDocId, i.Purpose, i.[Description], i.Currency, i.ExchangeRate, i.ReversalOfId,
                                           i.PostedBy, i.PostedAtUtc, i.CreatedAtUtc, i.CreatedBy)))
                        THROW 51003, 'Posted journals are immutable; only the change from Posted to Reversed is allowed.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS accounting.TR_JournalEntry_Immutable;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS accounting.TR_JournalLine_Immutable;");

            migrationBuilder.DropTable(
                name: "OpeningBalanceLine",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "OpeningBalance",
                schema: "accounting");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                schema: "accounting",
                table: "FiscalYear");

            migrationBuilder.DropColumn(
                name: "ClosingJournalId",
                schema: "accounting",
                table: "FiscalYear");

            migrationBuilder.DropColumn(
                name: "IsClosed",
                schema: "accounting",
                table: "FiscalYear");

            migrationBuilder.DropColumn(
                name: "CashFlowCategory",
                schema: "accounting",
                table: "Account");
        }
    }
}
