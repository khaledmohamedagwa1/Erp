using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Financial statements, year-end close, opening balances and database immutability of posted journals.</summary>
public class AccountingStatementsTests(ErpApiFixture fixture)
{
    private sealed record Books(ErpApiFixture.TenantSession T, Guid LegalEntityId, Dictionary<string, Guid> Accounts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Books> NewBooksAsync(params string[] fiscalYearStarts)
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var legal = await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var le = legal.EnumerateArray().First().GetProperty("id").GetGuid();
        foreach (var start in fiscalYearStarts.Length == 0 ? ["2026-01-01"] : fiscalYearStarts)
        {
            (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", t.CompanyId, new { legalEntityId = le, startDate = start }))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var accounts = await (await t.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Books(t, le, accounts.EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid()));
    }

    private static async Task<JsonElement> ManualAsync(Books b, string date, params (string Code, decimal Debit, decimal Credit, Guid? CostCenter)[] lines)
    {
        var created = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/journals", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId,
            journalDate = date,
            description = "Scenario",
            lines = lines.Select(l => new { accountId = b.Accounts[l.Code], debit = l.Debit, credit = l.Credit, costCenterId = l.CostCenter }).ToArray(),
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var posted = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{id}/post", b.T.CompanyId);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());
        return await posted.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Posts as another module (sub-ledger) through the contract, inside a transaction, as module handlers do.</summary>
    private async Task ModuleAsync(Books b, string module, string date, params (string Role, decimal Debit, decimal Credit)[] lines)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(b.T.TenantId);
        var session = scope.ServiceProvider.GetRequiredService<ErpDbSession>();
        await session.BeginTransactionAsync();
        var result = await scope.ServiceProvider.GetRequiredService<IJournalPostingService>().PostAsync(new JournalRequest(
            b.LegalEntityId, DateOnly.Parse(date), module, "TestDocument", Guid.NewGuid(), "test", "Scenario", "EGP", 1m,
            lines.Select(l => new JournalLineRequest(l.Role, null, l.Debit, l.Credit)).ToList()));
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : null);
        await session.CommitAsync();
    }

    private static async Task<JsonElement> GetAsync(Books b, string url)
    {
        var response = await b.T.Client.GetAsCompanyAsync(url, b.T.CompanyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Capital 100,000 into the bank; stock 30,000 bought on credit; sold for 50,000 on credit at cost 20,000; rent 5,000
    /// (cost centre); customer pays 40,000; equipment 12,000 from the bank; depreciation 1,000.
    /// </summary>
    private async Task<Guid> PostScenarioAsync(Books b)
    {
        var center = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/cost-centers", b.T.CompanyId, new { code = "ADMIN", name = "Administration" });
        center.StatusCode.ShouldBe(HttpStatusCode.Created);
        var costCenter = (await center.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await ManualAsync(b, "2026-01-10", ("1120", 100_000m, 0m, null), ("3100", 0m, 100_000m, null));
        await ModuleAsync(b, SourceModules.Procurement, "2026-02-01", (PostingRoles.Inventory, 30_000m, 0m), (PostingRoles.SupplierPayable, 0m, 30_000m));
        await ModuleAsync(b, SourceModules.Sales, "2026-03-01",
            (PostingRoles.CustomerReceivable, 50_000m, 0m), (PostingRoles.SalesRevenue, 0m, 50_000m),
            (PostingRoles.CostOfGoodsSold, 20_000m, 0m), (PostingRoles.Inventory, 0m, 20_000m));
        await ManualAsync(b, "2026-03-15", ("5310", 5_000m, 0m, costCenter), ("1120", 0m, 5_000m, null));
        await ModuleAsync(b, SourceModules.Finance, "2026-04-01", (PostingRoles.BankAccount, 40_000m, 0m), (PostingRoles.CustomerReceivable, 0m, 40_000m));
        await ManualAsync(b, "2026-05-01", ("1510", 12_000m, 0m, null), ("1120", 0m, 12_000m, null));
        await ManualAsync(b, "2026-06-30", ("5510", 1_000m, 0m, null), ("1590", 0m, 1_000m, null));
        return costCenter;
    }

    [Fact]
    public async Task Statements_derived_from_posted_journals_agree_with_each_other()
    {
        var b = await NewBooksAsync();
        var costCenter = await PostScenarioAsync(b);
        var q = $"legalEntityId={b.LegalEntityId}";

        var income = await GetAsync(b, $"/api/v1/accounting/reports/income-statement?{q}&from=2026-01-01&to=2026-12-31&compareFrom=2026-01-01&compareTo=2026-02-28");
        income.GetProperty("currency").GetString().ShouldBe("EGP");
        income.GetProperty("netRevenue").GetProperty("amount").GetDecimal().ShouldBe(50_000m);
        income.GetProperty("grossProfit").GetProperty("amount").GetDecimal().ShouldBe(30_000m);
        income.GetProperty("netProfit").GetProperty("amount").GetDecimal().ShouldBe(24_000m);
        income.GetProperty("netProfit").GetProperty("ytd").GetDecimal().ShouldBe(24_000m);
        income.GetProperty("netProfit").GetProperty("comparative").GetDecimal().ShouldBe(0m);

        var march = await GetAsync(b, $"/api/v1/accounting/reports/income-statement?{q}&from=2026-03-01&to=2026-03-31");
        march.GetProperty("netProfit").GetProperty("amount").GetDecimal().ShouldBe(25_000m);
        march.GetProperty("netProfit").GetProperty("ytd").GetDecimal().ShouldBe(25_000m);

        var admin = await GetAsync(b, $"/api/v1/accounting/reports/income-statement?{q}&from=2026-01-01&to=2026-12-31&costCenterId={costCenter}");
        admin.GetProperty("netProfit").GetProperty("amount").GetDecimal().ShouldBe(-5_000m);

        var balance = await GetAsync(b, $"/api/v1/accounting/reports/balance-sheet?{q}&asOf=2026-12-31&compareAsOf=2026-01-31");
        balance.GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        balance.GetProperty("totalAssets").GetProperty("amount").GetDecimal().ShouldBe(154_000m);
        balance.GetProperty("totalLiabilities").GetProperty("amount").GetDecimal().ShouldBe(30_000m);
        balance.GetProperty("totalEquity").GetProperty("amount").GetDecimal().ShouldBe(124_000m);
        balance.GetProperty("totalAssets").GetProperty("comparative").GetDecimal().ShouldBe(100_000m);

        var cash = await GetAsync(b, $"/api/v1/accounting/reports/cash-flow?{q}&from=2026-01-01&to=2026-12-31");
        cash.GetProperty("netProfit").GetDecimal().ShouldBe(24_000m);
        cash.GetProperty("netCashFromOperating").GetDecimal().ShouldBe(35_000m);
        cash.GetProperty("netCashFromInvesting").GetDecimal().ShouldBe(-12_000m);
        cash.GetProperty("netCashFromFinancing").GetDecimal().ShouldBe(100_000m);
        cash.GetProperty("openingCash").GetDecimal().ShouldBe(0m);
        cash.GetProperty("closingCash").GetDecimal().ShouldBe(123_000m);
        cash.GetProperty("reconciles").GetBoolean().ShouldBeTrue();

        // The second half of the year starts from the cash carried over from the first.
        var secondHalf = await GetAsync(b, $"/api/v1/accounting/reports/cash-flow?{q}&from=2026-04-01&to=2026-12-31");
        secondHalf.GetProperty("openingCash").GetDecimal().ShouldBe(95_000m);
        secondHalf.GetProperty("closingCash").GetDecimal().ShouldBe(123_000m);
        secondHalf.GetProperty("reconciles").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Statements_and_trial_balance_export_as_csv()
    {
        var b = await NewBooksAsync();
        await PostScenarioAsync(b);
        var q = $"legalEntityId={b.LegalEntityId}";

        var response = await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/reports/income-statement.csv?{q}&from=2026-01-01&to=2026-12-31", b.T.CompanyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = Encoding.UTF8.GetString(bytes[3..]);
        text.ShouldContain("net-profit,,Net profit,صافي الربح,24000.00,24000.00,");

        foreach (var url in new[]
                 {
                     $"/api/v1/accounting/reports/trial-balance.csv?{q}&from=2026-01-01&to=2026-12-31",
                     $"/api/v1/accounting/reports/balance-sheet.csv?{q}&asOf=2026-12-31",
                     $"/api/v1/accounting/reports/cash-flow.csv?{q}&from=2026-01-01&to=2026-12-31",
                 })
        {
            var csv = await b.T.Client.GetAsCompanyAsync(url, b.T.CompanyId);
            csv.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            (await csv.Content.ReadAsStringAsync(Ct)).ShouldContain("\r\n");
        }

        var tb = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/reports/trial-balance.csv?{q}&from=2026-01-01&to=2026-12-31", b.T.CompanyId)).Content.ReadAsStringAsync(Ct);
        tb.ShouldContain("Total,الإجمالي,,,258000.00,258000.00");
    }

    [Fact]
    public async Task Year_end_close_moves_profit_to_retained_earnings_and_can_be_undone()
    {
        var b = await NewBooksAsync("2026-01-01", "2027-01-01");
        await ManualAsync(b, "2026-05-01", ("1120", 1_000m, 0m, null), ("4110", 0m, 1_000m, null));
        await ManualAsync(b, "2026-06-01", ("5310", 300m, 0m, null), ("1120", 0m, 300m, null));
        var years = await GetAsync(b, $"/api/v1/accounting/fiscal-years?legalEntityId={b.LegalEntityId}");
        var fy2026 = years.EnumerateArray().Single(y => y.GetProperty("code").GetString() == "FY2026");
        var fyId = fy2026.GetProperty("id").GetGuid();
        var periods = fy2026.GetProperty("periods").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        var q = $"legalEntityId={b.LegalEntityId}";

        var early = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/fiscal-years/{fyId}/close", b.T.CompanyId);
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await early.ErrorCodeAsync()).ShouldBe("accounting.year-end-periods-not-ready");

        foreach (var period in periods.Take(11))
        {
            (await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/periods/{period}/status", b.T.CompanyId, new { status = "Closed" }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/periods/{periods[11]}/status", b.T.CompanyId, new { status = "SoftClosed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var close = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/fiscal-years/{fyId}/close", b.T.CompanyId);
        close.StatusCode.ShouldBe(HttpStatusCode.OK, await close.Content.ReadAsStringAsync(Ct));
        var closed = await close.Content.ReadFromJsonAsync<JsonElement>(Ct);
        closed.GetProperty("isClosed").GetBoolean().ShouldBeTrue();
        closed.GetProperty("netProfit").GetDecimal().ShouldBe(700m);
        var closingJournal = closed.GetProperty("closingJournalId").GetGuid();

        // Idempotent.
        var again = await (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/fiscal-years/{fyId}/close", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        again.GetProperty("closingJournalId").GetGuid().ShouldBe(closingJournal);

        // The income statement ignores the closing entry; the balance sheet now shows retained earnings instead of unclosed profit.
        var income = await GetAsync(b, $"/api/v1/accounting/reports/income-statement?{q}&from=2026-01-01&to=2026-12-31");
        income.GetProperty("netProfit").GetProperty("amount").GetDecimal().ShouldBe(700m);
        var balance = await GetAsync(b, $"/api/v1/accounting/reports/balance-sheet?{q}&asOf=2027-01-31");
        balance.GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        var equity = balance.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "equity").GetProperty("lines").EnumerateArray().ToList();
        equity.Single(l => l.GetProperty("code").GetString() == "3200").GetProperty("amount").GetDecimal().ShouldBe(700m);
        equity.ShouldNotContain(l => l.GetProperty("accountId").ValueKind == JsonValueKind.Null);

        // Balances carry into the next year: opening balances of 2027 in the trial balance.
        var tb = await GetAsync(b, $"/api/v1/accounting/reports/trial-balance?{q}&from=2027-01-01&to=2027-01-31");
        var tbLines = tb.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("openingBalance").GetDecimal());
        tbLines["1120"].ShouldBe(700m);
        tbLines["3200"].ShouldBe(-700m);
        tbLines.ShouldNotContainKey("4110");

        // The closed year is locked: no postings, no period changes.
        var late = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/journals", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, journalDate = "2026-12-31", description = "late",
            lines = new[] { new { accountId = b.Accounts["5310"], debit = 1m, credit = 0m }, new { accountId = b.Accounts["1120"], debit = 0m, credit = 1m } },
        });
        var lateId = (await late.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{lateId}/post", b.T.CompanyId)).ErrorCodeAsync())
            .ShouldBe("accounting.period-closed");
        (await (await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/periods/{periods[11]}/status", b.T.CompanyId, new { status = "Open" })).ErrorCodeAsync())
            .ShouldBe("accounting.fiscal-year-closed");

        // Reopen reverses the closing entry; closing again posts a new closing journal.
        var reopen = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/fiscal-years/{fyId}/reopen", b.T.CompanyId);
        reopen.StatusCode.ShouldBe(HttpStatusCode.OK, await reopen.Content.ReadAsStringAsync(Ct));
        var reopened = await GetAsync(b, $"/api/v1/accounting/reports/balance-sheet?{q}&asOf=2027-01-31");
        var reopenedEquity = reopened.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "equity").GetProperty("lines").EnumerateArray().ToList();
        reopenedEquity.ShouldNotContain(l => l.GetProperty("code").GetString() == "3200");
        reopenedEquity.Single(l => l.GetProperty("accountId").ValueKind == JsonValueKind.Null).GetProperty("amount").GetDecimal().ShouldBe(700m);
        (await GetAsync(b, $"/api/v1/accounting/journals/{closingJournal}")).GetProperty("status").GetString().ShouldBe("Reversed");

        var reclose = await (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/fiscal-years/{fyId}/close", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        reclose.GetProperty("isClosed").GetBoolean().ShouldBeTrue();
        reclose.GetProperty("closingJournalId").GetGuid().ShouldNotBe(closingJournal);
        reclose.GetProperty("netProfit").GetDecimal().ShouldBe(700m);
    }

    [Fact]
    public async Task Opening_balances_post_once_against_opening_equity_and_refuse_control_accounts()
    {
        var b = await NewBooksAsync();
        var control = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/opening-balances", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, openingDate = "2026-01-01", lines = new[] { new { accountId = b.Accounts["1210"], debit = 100m, credit = 0m } },
        });
        (await control.ErrorCodeAsync()).ShouldBe("accounting.opening-control-account");

        async Task<Guid> CreateAsync() => (await (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/opening-balances", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId,
            openingDate = "2026-01-01",
            description = "Migration from legacy system",
            lines = new[]
            {
                new { accountId = b.Accounts["1120"], debit = 5_000m, credit = 0m },
                new { accountId = b.Accounts["1510"], debit = 3_000m, credit = 0m },
                new { accountId = b.Accounts["2410"], debit = 0m, credit = 1_000m },
            },
        })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var first = await CreateAsync();
        var posted = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/opening-balances/{first}/post", b.T.CompanyId);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync(Ct));
        var doc = await posted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        doc.GetProperty("status").GetString().ShouldBe("Posted");
        doc.GetProperty("equityDifference").GetDecimal().ShouldBe(7_000m);

        var tb = await GetAsync(b, $"/api/v1/accounting/reports/trial-balance?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        tb.GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        var closing = tb.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("closingBalance").GetDecimal());
        closing["3300"].ShouldBe(-7_000m);

        var second = await CreateAsync();
        var duplicate = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/opening-balances/{second}/post", b.T.CompanyId);
        (await duplicate.ErrorCodeAsync()).ShouldBe("accounting.opening-balance-exists");

        var reverse = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/opening-balances/{first}/reverse", b.T.CompanyId, new { reversalDate = "2026-01-02" });
        reverse.StatusCode.ShouldBe(HttpStatusCode.OK, await reverse.Content.ReadAsStringAsync(Ct));
        (await reverse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Reversed");
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/opening-balances/{second}/post", b.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await GetAsync(b, $"/api/v1/accounting/opening-balances?legalEntityId={b.LegalEntityId}");
        list.EnumerateArray().Select(o => o.GetProperty("status").GetString()).Order().ShouldBe(["Posted", "Reversed"]);
    }

    [Fact]
    public async Task Database_triggers_block_changes_to_posted_journals()
    {
        var b = await NewBooksAsync();
        var posted = await ManualAsync(b, "2026-02-01", ("5310", 100m, 0m, null), ("1120", 0m, 100m, null));
        var journalId = posted.GetProperty("id").GetGuid();

        await using var sql = await fixture.OpenAsTenantAsync(b.T.TenantId);

        async Task<SqlException> Refused(string statement)
        {
            await using var command = sql.CreateCommand();
            command.CommandText = statement;
            command.Parameters.AddWithValue("@id", journalId);
            return await Should.ThrowAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct));
        }

        (await Refused("UPDATE accounting.JournalLine SET Debit = Debit + 1, BaseDebit = BaseDebit + 1 WHERE JournalEntryId = @id AND Debit > 0")).Message.ShouldContain("immutable");
        (await Refused("DELETE FROM accounting.JournalLine WHERE JournalEntryId = @id")).Message.ShouldContain("immutable");
        (await Refused("UPDATE accounting.JournalEntry SET [Description] = N'edited' WHERE Id = @id")).Message.ShouldContain("immutable");
        (await Refused("UPDATE accounting.JournalEntry SET JournalDate = '2026-02-02' WHERE Id = @id")).Message.ShouldContain("immutable");
        (await Refused("UPDATE accounting.JournalEntry SET [Status] = 'Draft' WHERE Id = @id")).Message.ShouldContain("immutable");
        (await Refused("DELETE FROM accounting.JournalEntry WHERE Id = @id")).Message.ShouldContain("cannot be deleted");

        // Drafts stay editable, and reversing (Posted -> Reversed) still works through the application.
        var draft = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/journals", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, journalDate = "2026-02-03", description = "draft",
            lines = new[] { new { accountId = b.Accounts["5310"], debit = 5m, credit = 0m }, new { accountId = b.Accounts["1120"], debit = 0m, credit = 5m } },
        });
        var draftId = (await draft.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await using (var edit = sql.CreateCommand())
        {
            edit.CommandText = "UPDATE accounting.JournalLine SET [Description] = N'ok' WHERE JournalEntryId = @id";
            edit.Parameters.AddWithValue("@id", draftId);
            (await edit.ExecuteNonQueryAsync(Ct)).ShouldBe(2);
        }

        var reverse = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{journalId}/reverse", b.T.CompanyId, new { reversalDate = "2026-02-05" });
        reverse.StatusCode.ShouldBe(HttpStatusCode.OK, await reverse.Content.ReadAsStringAsync(Ct));
        (await GetAsync(b, $"/api/v1/accounting/journals/{journalId}")).GetProperty("status").GetString().ShouldBe("Reversed");
        (await Refused("UPDATE accounting.JournalEntry SET [Status] = 'Posted' WHERE Id = @id")).Message.ShouldContain("immutable");
    }

    [Fact]
    public async Task Readiness_reports_missing_roles_and_fiscal_calendar()
    {
        var b = await NewBooksAsync();
        var ready = await GetAsync(b, $"/api/v1/accounting/readiness?legalEntityId={b.LegalEntityId}");
        ready.GetProperty("missingRoles").GetArrayLength().ShouldBe(0);
        ready.GetProperty("hasChartOfAccounts").GetBoolean().ShouldBeTrue();
        ready.GetProperty("hasFiscalYearForToday").GetBoolean().ShouldBeTrue();
        ready.GetProperty("isReady").GetBoolean().ShouldBeTrue();

        var bare = await fixture.NewTenantAsync();
        var legal = await (await bare.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", bare.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var le = legal.EnumerateArray().First().GetProperty("id").GetGuid();
        var notReady = await (await bare.Client.GetAsCompanyAsync($"/api/v1/accounting/readiness?legalEntityId={le}&roles=SalesRevenue,CustomerReceivable", bare.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        notReady.GetProperty("missingRoles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["SalesRevenue", "CustomerReceivable"]);
        notReady.GetProperty("isReady").GetBoolean().ShouldBeFalse();

        var unknown = await bare.Client.GetAsCompanyAsync($"/api/v1/accounting/readiness?legalEntityId={le}&roles=NoSuchRole", bare.CompanyId);
        (await unknown.ErrorCodeAsync()).ShouldBe("accounting.unknown-posting-role");
    }

    [Fact]
    public async Task Cash_flow_categories_can_be_changed_and_are_exposed()
    {
        var b = await NewBooksAsync();
        var accounts = await GetAsync(b, "/api/v1/accounting/accounts");
        accounts.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1120").GetProperty("cashFlowCategory").GetString().ShouldBe("Cash");

        var changed = await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/accounts/{b.Accounts["2410"]}/cash-flow-category", b.T.CompanyId, new { category = "Financing" });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("cashFlowCategory").GetString().ShouldBe("Financing");

        var refused = await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/accounts/{b.Accounts["4110"]}/cash-flow-category", b.T.CompanyId, new { category = "Operating" });
        (await refused.ErrorCodeAsync()).ShouldBe("accounting.cash-flow-category-balance-sheet-only");
    }
}
