using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class AccountingTests(ErpApiFixture fixture)
{
    private sealed record Books(ErpApiFixture.TenantSession T, Guid LegalEntityId, Dictionary<string, Guid> Accounts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>New tenant with the generic chart seeded and a FY2026 calendar.</summary>
    private async Task<Books> NewBooksAsync()
    {
        var t = await fixture.NewTenantAsync();
        var seed = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId);
        seed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var le = await LegalEntityAsync(t);
        var fy = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", t.CompanyId, new { legalEntityId = le, startDate = "2026-01-01" });
        fy.StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Books(t, le, await AccountsAsync(t));
    }

    private static async Task<Guid> LegalEntityAsync(ErpApiFixture.TenantSession t)
    {
        var list = await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().First().GetProperty("id").GetGuid();
    }

    private static async Task<Dictionary<string, Guid>> AccountsAsync(ErpApiFixture.TenantSession t)
    {
        var list = await (await t.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> CreateJournalAsync(Books b, string date, params (string Code, decimal Debit, decimal Credit)[] lines) =>
        await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/journals", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId,
            journalDate = date,
            description = "Test journal",
            lines = lines.Select(l => new { accountId = b.Accounts[l.Code], debit = l.Debit, credit = l.Credit }).ToArray(),
        });

    private static async Task<JsonElement> PostNewJournalAsync(Books b, string date, params (string Code, decimal Debit, decimal Credit)[] lines)
    {
        var created = await CreateJournalAsync(b, date, lines);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var posted = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{id}/post", b.T.CompanyId);
        posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());
        return await posted.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Runs work as an internal caller (another module): tenant set, optional transaction, no HTTP company header.</summary>
    private async Task<T> AsModuleAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work, bool transaction = true)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var session = scope.ServiceProvider.GetRequiredService<ErpDbSession>();
        if (transaction)
        {
            await session.BeginTransactionAsync();
        }

        var result = await work(scope.ServiceProvider);
        if (transaction)
        {
            await session.CommitAsync();
        }

        return result;
    }

    private static JournalRequest SalesInvoice(Guid le, Guid sourceId, string currency = "EGP", decimal rate = 1m) => new(
        le, new DateOnly(2026, 11, 27), SourceModules.Sales, "SalesInvoice", sourceId, "invoice", "INV test", currency, rate,
        [
            new(PostingRoles.CustomerReceivable, null, 1140m, 0m, PartyType: "Customer", PartyId: Guid.NewGuid()),
            new(PostingRoles.SalesRevenue, null, 0m, 1000m),
            new(PostingRoles.OutputTax, null, 0m, 140m),
            new(PostingRoles.CostOfGoodsSold, null, 625m, 0m),
            new(PostingRoles.Inventory, null, 0m, 625m),
        ]);

    [Fact]
    public async Task Chart_template_seeding_is_idempotent_and_maps_every_posting_role()
    {
        var t = await fixture.NewTenantAsync();
        var first = await (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        first.GetProperty("accountsCreated").GetInt32().ShouldBeGreaterThan(50);
        first.GetProperty("rulesCreated").GetInt32().ShouldBe(PostingRoles.All.Count);

        var second = await (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        second.GetProperty("accountsCreated").GetInt32().ShouldBe(0);
        second.GetProperty("rulesCreated").GetInt32().ShouldBe(0);

        var le = await LegalEntityAsync(t);
        var unresolved = await AsModuleAsync(t.TenantId, sp =>
        {
            sp.GetRequiredService<ITenantContext>().SetCompany(t.CompanyId);
            return sp.GetRequiredService<ILedgerQueries>().GetUnresolvedRolesAsync(le, PostingRoles.All);
        }, transaction: false);
        unresolved.ShouldBeEmpty();

        var accounts = await (await t.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ar = accounts.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1210");
        ar.GetProperty("controlSubledger").GetString().ShouldBe("Receivables");
        ar.GetProperty("allowManualPosting").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Fiscal_year_has_twelve_periods_and_overlaps_are_rejected()
    {
        var b = await NewBooksAsync();
        var years = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/fiscal-years?legalEntityId={b.LegalEntityId}", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var year = years.EnumerateArray().Single();
        year.GetProperty("code").GetString().ShouldBe("FY2026");
        year.GetProperty("periods").GetArrayLength().ShouldBe(12);

        var overlap = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", b.T.CompanyId, new { legalEntityId = b.LegalEntityId, startDate = "2026-06-01" });
        overlap.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await overlap.ErrorCodeAsync()).ShouldBe("accounting.fiscal-year-overlaps");
    }

    [Fact]
    public async Task Manual_journals_post_with_gapless_numbers_and_the_trial_balance_balances()
    {
        var b = await NewBooksAsync();
        var j1 = await PostNewJournalAsync(b, "2026-03-31", ("5310", 25000m, 0m), ("2410", 0m, 25000m));
        var j2 = await PostNewJournalAsync(b, "2026-04-30", ("1120", 1000000m, 0m), ("3100", 0m, 1000000m));
        var j3 = await PostNewJournalAsync(b, "2026-05-15", ("5410", 75.5m, 0m), ("1120", 0m, 75.5m));
        new[] { j1, j2, j3 }.Select(j => j.GetProperty("number").GetString()).ShouldBe(["JV-2026-000001", "JV-2026-000002", "JV-2026-000003"]);
        j1.GetProperty("status").GetString().ShouldBe("Posted");

        var tbResponse = await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/reports/trial-balance?legalEntityId={b.LegalEntityId}&from=2026-04-01&to=2026-12-31", b.T.CompanyId);
        tbResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await tbResponse.Content.ReadAsStringAsync(Ct));
        var tb = await tbResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        tb.GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        tb.GetProperty("totalDebit").GetDecimal().ShouldBe(1000075.5m);
        var rent = tb.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "5310");
        rent.GetProperty("openingBalance").GetDecimal().ShouldBe(25000m);
        rent.GetProperty("debit").GetDecimal().ShouldBe(0m);
        var bank = tb.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "1120");
        bank.GetProperty("closingBalance").GetDecimal().ShouldBe(999924.5m);

        var gl = await (await b.T.Client.GetAsCompanyAsync(
                $"/api/v1/accounting/reports/general-ledger?legalEntityId={b.LegalEntityId}&accountId={b.Accounts["1120"]}&from=2026-01-01&to=2026-12-31", b.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        gl.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("balance").GetDecimal()).ShouldBe([1000000m, 999924.5m]);

        // Running balances maintained in the posting transaction reconcile with the journal lines.
        await using var conn = await fixture.OpenAsTenantAsync(b.T.TenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SUM(BaseDebit), SUM(BaseCredit) FROM accounting.AccountBalance WHERE LegalEntityId = @le";
        cmd.Parameters.AddWithValue("@le", b.LegalEntityId);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        reader.GetDecimal(0).ShouldBe(1025075.5m);
        reader.GetDecimal(1).ShouldBe(1025075.5m);
    }

    [Fact]
    public async Task Unbalanced_journals_and_control_accounts_are_refused()
    {
        var b = await NewBooksAsync();
        var unbalanced = await CreateJournalAsync(b, "2026-02-10", ("5310", 100m, 0m), ("2410", 0m, 90m));
        var id = (await unbalanced.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var post = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{id}/post", b.T.CompanyId);
        (await post.ErrorCodeAsync()).ShouldBe("accounting.journal-unbalanced");

        var control = await CreateJournalAsync(b, "2026-02-10", ("1210", 100m, 0m), ("4110", 0m, 100m));
        var controlId = (await control.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var controlPost = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{controlId}/post", b.T.CompanyId);
        (await controlPost.ErrorCodeAsync()).ShouldBe("accounting.control-account-restricted");

        var group = await CreateJournalAsync(b, "2026-02-10", ("11", 100m, 0m), ("4110", 0m, 100m));
        var groupId = (await group.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{groupId}/post", b.T.CompanyId)).ErrorCodeAsync())
            .ShouldBe("accounting.not-posting-account");
    }

    [Fact]
    public async Task Closed_periods_reject_postings_and_close_in_order()
    {
        var b = await NewBooksAsync();
        var year = (await (await b.T.Client.GetAsCompanyAsync("/api/v1/accounting/fiscal-years", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Single();
        var periods = year.GetProperty("periods").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();

        var outOfOrder = await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/periods/{periods[1]}/status", b.T.CompanyId, new { status = "Closed" });
        (await outOfOrder.ErrorCodeAsync()).ShouldBe("accounting.period-order");
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/periods/{periods[0]}/status", b.T.CompanyId, new { status = "Closed" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/accounting/periods/{periods[1]}/status", b.T.CompanyId, new { status = "SoftClosed" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var january = await CreateJournalAsync(b, "2026-01-15", ("5310", 10m, 0m), ("2410", 0m, 10m));
        var janId = (await january.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var rejected = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{janId}/post", b.T.CompanyId);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await rejected.ErrorCodeAsync()).ShouldBe("accounting.period-closed");

        // The administrator holds accounting.period.post-soft-closed, so February still accepts postings.
        await PostNewJournalAsync(b, "2026-02-15", ("5310", 10m, 0m), ("2410", 0m, 10m));

        // A module posting (no user) cannot use a soft-closed period, and a date with no fiscal year is refused.
        var soft = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(
            SalesInvoice(b.LegalEntityId, Guid.NewGuid()) with { JournalDate = new DateOnly(2026, 2, 20) }));
        soft.Error.Code.ShouldBe("accounting.period-closed");
        var noYear = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(
            SalesInvoice(b.LegalEntityId, Guid.NewGuid()) with { JournalDate = new DateOnly(2030, 1, 1) }));
        noYear.Error.Code.ShouldBe("accounting.period-not-found");
    }

    [Fact]
    public async Task Manual_journal_reversal_posts_the_opposite_and_cannot_be_repeated()
    {
        var b = await NewBooksAsync();
        var original = await PostNewJournalAsync(b, "2026-06-30", ("5310", 5000m, 0m), ("2410", 0m, 5000m));
        var id = original.GetProperty("id").GetGuid();

        var reversed = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{id}/reverse", b.T.CompanyId, new { reversalDate = "2026-07-01" });
        reversed.StatusCode.ShouldBe(HttpStatusCode.OK, await reversed.Content.ReadAsStringAsync(Ct));
        var reversal = await reversed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        reversal.GetProperty("reversalOfId").GetGuid().ShouldBe(id);
        reversal.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountCode").GetString() == "5310").GetProperty("credit").GetDecimal().ShouldBe(5000m);

        var reloaded = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals/{id}", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        reloaded.GetProperty("status").GetString().ShouldBe("Reversed");

        var again = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{id}/reverse", b.T.CompanyId, new { reversalDate = "2026-07-02" });
        (await again.ErrorCodeAsync()).ShouldBe("accounting.journal-already-reversed");

        var tb = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/reports/trial-balance?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31", b.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        tb.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("code").GetString() == "5310").GetProperty("closingBalance").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task Modules_post_through_the_contract_idempotently_inside_their_transaction()
    {
        var b = await NewBooksAsync();
        var source = Guid.NewGuid();

        var first = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(SalesInvoice(b.LegalEntityId, source)));
        first.IsSuccess.ShouldBeTrue(first.IsFailure ? first.Error.Message : null);
        first.Value.TotalBaseDebit.ShouldBe(1765m);
        first.Value.AlreadyPosted.ShouldBeFalse();

        var retry = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(SalesInvoice(b.LegalEntityId, source)));
        retry.Value.AlreadyPosted.ShouldBeTrue();
        retry.Value.JournalId.ShouldBe(first.Value.JournalId);

        var journal = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals/{first.Value.JournalId}", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        journal.GetProperty("sourceModule").GetString().ShouldBe("sales");
        journal.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("accountCode").GetString()).ShouldBe(["1210", "4110", "2310", "5110", "1310"]);

        // A module journal is cancelled by its module, not from the accounting screens.
        var manualReverse = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{first.Value.JournalId}/reverse", b.T.CompanyId, new { reversalDate = "2026-11-28" });
        (await manualReverse.ErrorCodeAsync()).ShouldBe("accounting.reverse-from-source-document");

        var reversal = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>()
            .ReverseAsync(b.LegalEntityId, "SalesInvoice", source, "invoice", new DateOnly(2026, 11, 28)));
        reversal.IsSuccess.ShouldBeTrue(reversal.IsFailure ? reversal.Error.Message : null);
        var reversalAgain = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>()
            .ReverseAsync(b.LegalEntityId, "SalesInvoice", source, "invoice", new DateOnly(2026, 11, 28)));
        reversalAgain.Value.JournalId.ShouldBe(reversal.Value.JournalId);

        var arBalance = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<ILedgerQueries>()
            .GetRoleBalanceAsync(b.LegalEntityId, PostingRoles.CustomerReceivable, new DateOnly(2026, 12, 31)), transaction: false);
        arBalance.ShouldBe(0m);
    }

    [Fact]
    public async Task Contract_requires_a_transaction_and_a_posting_rule()
    {
        var b = await NewBooksAsync();
        var noTx = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(SalesInvoice(b.LegalEntityId, Guid.NewGuid())), transaction: false);
        noTx.Error.Code.ShouldBe("accounting.transaction-required");

        var unseeded = await fixture.NewTenantAsync();
        var le = await LegalEntityAsync(unseeded);
        var missing = await AsModuleAsync(unseeded.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(SalesInvoice(le, Guid.NewGuid())));
        missing.Error.Code.ShouldBe("accounting.posting-rule-missing");
        missing.Error.Message.ShouldContain(PostingRoles.CustomerReceivable);
    }

    [Fact]
    public async Task Foreign_currency_posting_converts_per_line_and_books_the_rounding_residual()
    {
        var b = await NewBooksAsync();
        var request = new JournalRequest(b.LegalEntityId, new DateOnly(2026, 8, 1), SourceModules.Finance, "Receipt", Guid.NewGuid(), "receipt", "USD receipt", "USD", 1.5m,
        [
            new(PostingRoles.BankAccount, null, 0.01m, 0m),
            new(PostingRoles.BankAccount, null, 0.01m, 0m),
            new(PostingRoles.BankAccount, null, 0.01m, 0m),
            new(PostingRoles.CustomerReceivable, null, 0m, 0.03m),
        ]);
        var posted = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>().PostAsync(request));
        posted.IsSuccess.ShouldBeTrue(posted.IsFailure ? posted.Error.Message : null);

        var journal = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals/{posted.Value.JournalId}", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var rounding = journal.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountCode").GetString() == "5630");
        rounding.GetProperty("baseCredit").GetDecimal().ShouldBe(0.01m);
        journal.GetProperty("totalBaseDebit").GetDecimal().ShouldBe(journal.GetProperty("totalBaseCredit").GetDecimal());

        var badRate = await AsModuleAsync(b.T.TenantId, sp => sp.GetRequiredService<IJournalPostingService>()
            .PostAsync(SalesInvoice(b.LegalEntityId, Guid.NewGuid(), "EGP", 2m)));
        badRate.Error.Code.ShouldBe("accounting.invalid-exchange-rate");
    }

    [Fact]
    public async Task Books_are_isolated_between_tenants_and_companies()
    {
        var a = await NewBooksAsync();
        var journal = await PostNewJournalAsync(a, "2026-09-30", ("5310", 1m, 0m), ("2410", 0m, 1m));
        var b = await NewBooksAsync();

        (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals/{journal.GetProperty("id").GetGuid()}", b.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var foreignTb = await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/reports/trial-balance?legalEntityId={a.LegalEntityId}&from=2026-01-01&to=2026-12-31", b.T.CompanyId);
        (await foreignTb.ErrorCodeAsync()).ShouldBe("accounting.legal-entity-not-found");

        var second = await a.T.Client.PostAsJsonAsync("/api/v1/organization/companies", new
        {
            code = "BOOKS2", nameEn = "Second", nameAr = "الثانية", baseCurrency = "EGP", businessPreset = "services",
        }, Ct);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var accounts = await (await a.T.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", secondId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        accounts.GetArrayLength().ShouldBe(0);

        // A module posting for company A's legal entity while the scope is pinned to another company is refused.
        var crossCompany = await AsModuleAsync(a.T.TenantId, sp =>
        {
            sp.GetRequiredService<ITenantContext>().SetCompany(secondId);
            return sp.GetRequiredService<IJournalPostingService>().PostAsync(SalesInvoice(a.LegalEntityId, Guid.NewGuid()));
        });
        crossCompany.Error.Code.ShouldBe("accounting.legal-entity-not-in-company");
    }
}
