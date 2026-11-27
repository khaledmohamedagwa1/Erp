using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class FinanceTests(ErpApiFixture fixture)
{
    private sealed record Books(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid CustomerId, Guid SupplierId, Guid BankId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly D = new(2026, 3, 10);

    /// <summary>Tenant with the generic chart, FY2026, a customer, a supplier and an EGP bank account.</summary>
    private async Task<Books> NewBooksAsync()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var le = (await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .EnumerateArray().First().GetProperty("id").GetGuid();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", t.CompanyId, new { legalEntityId = le, startDate = "2026-01-01" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var customer = await IdAsync(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers", t.CompanyId,
            new { code = "C1", kind = "Business", nameEn = "Delta Retail", nameAr = "دلتا", currency = "EGP" }));
        var supplier = await IdAsync(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/suppliers", t.CompanyId,
            new { code = "S1", nameEn = "Nile Supplies", nameAr = "النيل", currency = "EGP" }));
        var bank = await IdAsync(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/bank-accounts", t.CompanyId,
            new { legalEntityId = le, code = "CIB", name = "CIB current", currency = "EGP", bankName = "CIB" }));
        return new Books(t, le, customer, supplier, bank);
    }

    private static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Calls finance contracts as another module would: tenant + company set, inside a transaction.</summary>
    private async Task<T> AsModuleAsync<T>(Books b, Func<IServiceProvider, Task<T>> work, bool transaction = true)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(b.T.TenantId);
        tenant.SetCompany(b.T.CompanyId);
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

    private Task<IReadOnlyList<OpenItemSummary>> OpenInvoiceAsync(Books b, PartyKind party, decimal amount, string currency = "EGP", decimal rate = 1m, string docType = "SalesInvoice", Guid? docId = null) =>
        AsModuleAsync(b, async sp =>
        {
            var id = docId ?? Guid.NewGuid();
            var module = party == PartyKind.Customer ? SourceModules.Sales : SourceModules.Procurement;
            var result = await sp.GetRequiredService<ISubledgerService>().CreateOpenItemsAsync(new OpenItemRequest(party, party == PartyKind.Customer ? b.CustomerId : b.SupplierId,
                b.LegalEntityId, module, docType, id, $"DOC-{Guid.NewGuid().ToString()[..6]}", D, currency, rate, amount, null), Ct);
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);

            // The source module's own journal (what Sales/Procurement will post), so the control accounts mirror the sub-ledger.
            var abs = Math.Abs(amount);
            var control = party == PartyKind.Customer ? PostingRoles.CustomerReceivable : PostingRoles.SupplierPayable;
            var other = party == PartyKind.Customer ? PostingRoles.SalesRevenue : PostingRoles.PurchasePriceVariance;
            var controlDebit = (party == PartyKind.Customer) == (amount > 0);
            JournalLineRequest[] lines = controlDebit
                ? [new(control, null, abs, 0m), new(other, null, 0m, abs)]
                : [new(control, null, 0m, abs), new(other, null, abs, 0m)];
            var posted = await sp.GetRequiredService<IJournalPostingService>().PostAsync(new JournalRequest(b.LegalEntityId, D, module, docType, id, "invoice",
                "Test document", currency, currency == "EGP" ? 1m : rate, lines), Ct);
            posted.IsSuccess.ShouldBeTrue(posted.IsFailure ? posted.Error.Code : null);
            return result.Value;
        });

    private static async Task<JsonElement> PostAsync(Books b, string url, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url).WithCompany(b.T.CompanyId);
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        var response = await b.T.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> JournalAsync(Books b, Guid journalId) =>
        await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals/{journalId}", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static decimal Line(JsonElement journal, string accountCode, string side) =>
        journal.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("accountCode").GetString() == accountCode).Sum(l => l.GetProperty(side).GetDecimal());

    private Task<decimal> RoleBalanceAsync(Books b, string role) =>
        AsModuleAsync(b, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(b.LegalEntityId, role, new DateOnly(2026, 12, 31), Ct), transaction: false);

    [Fact]
    public async Task Receipt_settles_two_invoices_keeps_the_remainder_as_advance_and_ledger_matches_subledger()
    {
        var b = await NewBooksAsync();
        var inv1 = (await OpenInvoiceAsync(b, PartyKind.Customer, 1140m)).Single();
        var inv2 = (await OpenInvoiceAsync(b, PartyKind.Customer, 2000m)).Single();
        (await OpenInvoiceAsync(b, PartyKind.Customer, 500m)).Single();

        var draft = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date = "2026-03-15", method = "BankTransfer", cashBankAccountId = b.BankId, amount = 3500m,
            allocations = new[] { new { openItemId = inv1.Id, amount = 1140m }, new { openItemId = inv2.Id, amount = 1500m } },
        });
        var receiptId = await IdAsync(draft);
        var posted = await PostAsync(b, $"/api/v1/finance/receipts/{receiptId}/post");
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("number").GetString().ShouldBe("RCT-2026-000001");
        posted.GetProperty("unallocatedAmount").GetDecimal().ShouldBe(860m);

        var journal = await JournalAsync(b, posted.GetProperty("journalId").GetGuid());
        journal.GetProperty("totalBaseDebit").GetDecimal().ShouldBe(journal.GetProperty("totalBaseCredit").GetDecimal());
        Line(journal, "1120", "baseDebit").ShouldBe(3500m);
        Line(journal, "1210", "baseCredit").ShouldBe(2640m);
        Line(journal, "2160", "baseCredit").ShouldBe(860m);

        // Sub-ledger = ledger: receivables control equals the sum of open items; advances equal the unallocated remainder.
        var items = await AsModuleAsync(b, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Customer, b.CustomerId, cancellationToken: Ct), transaction: false);
        items.Sum(i => i.Sign * i.OpenBase).ShouldBe(1000m);
        (await RoleBalanceAsync(b, PostingRoles.CustomerReceivable)).ShouldBe(1000m);
        (await RoleBalanceAsync(b, PostingRoles.CustomerAdvance)).ShouldBe(-860m);

        var exposure = await AsModuleAsync(b, sp => sp.GetRequiredService<ICreditExposureQueries>().GetCustomerExposureAsync(b.CustomerId, Ct), transaction: false);
        exposure.OpenReceivablesBase.ShouldBe(1000m);
        exposure.UnallocatedAdvancesBase.ShouldBe(860m);
        exposure.NetExposureBase.ShouldBe(140m);

        // Later: allocate the advance to the remaining 500 invoice and the rest of inv2.
        var allocate = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/allocations")
        {
            Content = JsonContent.Create(new { date = "2026-03-20", allocations = new[] { new { openItemId = inv2.Id, amount = 500m } } }),
        }.WithCompany(b.T.CompanyId);
        allocate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var allocated = await b.T.Client.SendAsync(allocate, Ct);
        allocated.StatusCode.ShouldBe(HttpStatusCode.OK, await allocated.Content.ReadAsStringAsync(Ct));
        (await allocated.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("unallocatedAmount").GetDecimal().ShouldBe(360m);
        (await RoleBalanceAsync(b, PostingRoles.CustomerAdvance)).ShouldBe(-360m);
        (await RoleBalanceAsync(b, PostingRoles.CustomerReceivable)).ShouldBe(500m);

        var open = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/finance/open-items?partyType=Customer&partyId={b.CustomerId}&openOnly=false", b.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        open.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("status").GetString() == "Settled").ShouldBe(2);

        // Statement: invoices 3,640 − receipt 3,500 = 140 owed (receivables 500 − advance 360).
        var statement = await (await b.T.Client.GetAsCompanyAsync(
            $"/api/v1/finance/reports/customer-statement?customerId={b.CustomerId}&legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31", b.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        statement.GetProperty("closingBalance").GetDecimal().ShouldBe(140m);
        statement.GetProperty("lines").GetArrayLength().ShouldBe(4);

        var aging = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/finance/reports/ar-aging?legalEntityId={b.LegalEntityId}&asOf=2026-06-30", b.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        aging.GetProperty("totals").GetProperty("total").GetDecimal().ShouldBe(500m);
        aging.GetProperty("totals").GetProperty("over90").GetDecimal().ShouldBe(500m); // due 10 Mar, 112 days past due on 30 Jun
    }

    [Fact]
    public async Task Supplier_payment_with_withholding_matches_example_J6()
    {
        var b = await NewBooksAsync();
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/templates/egypt", b.T.CompanyId, new { })).IsSuccessStatusCode.ShouldBeTrue();
        var codes = await (await b.T.Client.GetAsCompanyAsync("/api/v1/tax/tax-codes", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var wht1 = codes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "WHT-1").GetProperty("id").GetGuid();
        var group = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/tax-groups", b.T.CompanyId, new
        {
            code = "WHT1", nameEn = "Withholding 1%", nameAr = "خصم 1%", components = new[] { new { taxCodeId = wht1, sequence = 1, compoundsOnPrevious = false } },
        }));
        var invoice = (await OpenInvoiceAsync(b, PartyKind.Supplier, 6954m, docType: "PurchaseInvoice")).Single();

        var paymentId = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/payments", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, partyId = b.SupplierId, date = "2026-03-20", method = "BankTransfer", cashBankAccountId = b.BankId, amount = 6893m,
            withholdingTaxGroupId = group, withholdingBase = 6100m, allocations = new[] { new { openItemId = invoice.Id, amount = 6954m } },
        }));
        var posted = await PostAsync(b, $"/api/v1/finance/payments/{paymentId}/post");
        posted.GetProperty("withholdingAmount").GetDecimal().ShouldBe(61m);
        posted.GetProperty("unallocatedAmount").GetDecimal().ShouldBe(0m);

        var journal = await JournalAsync(b, posted.GetProperty("journalId").GetGuid());
        Line(journal, "2110", "baseDebit").ShouldBe(6954m);
        Line(journal, "1120", "baseCredit").ShouldBe(6893m);
        Line(journal, "2320", "baseCredit").ShouldBe(61m);
        journal.GetProperty("lines").GetArrayLength().ShouldBe(3);

        var supplierItems = await AsModuleAsync(b, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Supplier, b.SupplierId, true, Ct), transaction: false);
        supplierItems.Single().Status.ShouldBe("Settled");

        var bank = await (await b.T.Client.GetAsCompanyAsync("/api/v1/finance/bank-accounts", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        bank.EnumerateArray().Single().GetProperty("balance").GetDecimal().ShouldBe(-6893m);
    }

    [Fact]
    public async Task Over_allocation_is_refused_and_posting_is_idempotent()
    {
        var b = await NewBooksAsync();
        var invoice = (await OpenInvoiceAsync(b, PartyKind.Customer, 1000m)).Single();

        var tooMuch = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date = "2026-03-15", method = "Cash", cashBankAccountId = b.BankId, amount = 500m,
            allocations = new[] { new { openItemId = invoice.Id, amount = 600m } },
        });
        tooMuch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await tooMuch.ErrorCodeAsync()).ShouldBe("finance.over-allocation");

        var beyondOpen = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date = "2026-03-15", method = "Cash", cashBankAccountId = b.BankId, amount = 5000m,
            allocations = new[] { new { openItemId = invoice.Id, amount = 1000.01m } },
        });
        (await beyondOpen.ErrorCodeAsync()).ShouldBe("finance.allocation-exceeds-open");

        var receiptId = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date = "2026-03-15", method = "Cash", cashBankAccountId = b.BankId, amount = 400m,
            allocations = new[] { new { openItemId = invoice.Id, amount = 400m } },
        }));
        var key = Guid.NewGuid().ToString();
        var first = await PostAsync(b, $"/api/v1/finance/receipts/{receiptId}/post", key);

        var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/post").WithCompany(b.T.CompanyId);
        replayRequest.Headers.Add("Idempotency-Key", key);
        var replay = await b.T.Client.SendAsync(replayRequest, Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotency-Replayed").Single().ShouldBe("true");
        (await replay.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("journalId").GetGuid().ShouldBe(first.GetProperty("journalId").GetGuid());

        // A new key cannot post the same document twice.
        var again = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/post").WithCompany(b.T.CompanyId);
        again.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await (await b.T.Client.SendAsync(again, Ct)).ErrorCodeAsync()).ShouldBe("finance.not-draft");

        var journals = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals?legalEntityId={b.LegalEntityId}&sourceModule=finance", b.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        journals.GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Foreign_currency_receipt_books_realized_gain_and_month_end_revaluation_is_reversed()
    {
        var b = await NewBooksAsync();
        foreach (var (code, name) in new[] { ("EGP", "Egyptian pound"), ("USD", "US dollar") })
        {
            await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/currencies", b.T.CompanyId, new { code, nameEn = name, nameAr = name, minorUnits = 2 });
        }

        var usdBank = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/bank-accounts", b.T.CompanyId,
            new { legalEntityId = b.LegalEntityId, code = "USD1", name = "USD account", currency = "USD" }));
        var invoice = (await OpenInvoiceAsync(b, PartyKind.Customer, 1000m, "USD", 50m)).Single();
        invoice.OriginalBase.ShouldBe(50_000m);
        (await OpenInvoiceAsync(b, PartyKind.Customer, 500m, "USD", 50m)).Single();

        // J-10: receive USD 1,000 at 51 → realized gain 1,000.
        var receiptId = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date = "2026-04-05", method = "BankTransfer", cashBankAccountId = usdBank, amount = 1000m,
            exchangeRate = 51m, allocations = new[] { new { openItemId = invoice.Id, amount = 1000m } },
        }));
        var posted = await PostAsync(b, $"/api/v1/finance/receipts/{receiptId}/post");
        var journal = await JournalAsync(b, posted.GetProperty("journalId").GetGuid());
        Line(journal, "1120", "baseDebit").ShouldBe(51_000m);
        Line(journal, "1210", "baseCredit").ShouldBe(50_000m);
        Line(journal, "4920", "baseCredit").ShouldBe(1_000m);

        // Month end: closing rate 52 → receivable USD 500 (+1,000) and bank USD 1,000 held at 51,000 (+1,000).
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/exchange-rates", b.T.CompanyId,
            new { fromCurrency = "USD", toCurrency = "EGP", type = "Closing", effectiveDate = "2026-06-30", rate = 52m })).IsSuccessStatusCode.ShouldBeTrue();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/finance/fx-revaluations")
        {
            Content = JsonContent.Create(new { legalEntityId = b.LegalEntityId, asOf = "2026-06-30" }),
        }.WithCompany(b.T.CompanyId);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var run = await b.T.Client.SendAsync(request, Ct);
        run.StatusCode.ShouldBe(HttpStatusCode.Created, await run.Content.ReadAsStringAsync(Ct));
        var revaluation = await run.Content.ReadFromJsonAsync<JsonElement>(Ct);
        revaluation.GetProperty("totalGain").GetDecimal().ShouldBe(2_000m);
        revaluation.GetProperty("totalLoss").GetDecimal().ShouldBe(0m);
        revaluation.GetProperty("reversalDate").GetString().ShouldBe("2026-07-01");

        var revJournal = await JournalAsync(b, revaluation.GetProperty("journalId").GetGuid());
        Line(revJournal, "1210", "baseDebit").ShouldBe(1_000m);
        Line(revJournal, "1120", "baseDebit").ShouldBe(1_000m);
        Line(revJournal, "4930", "baseCredit").ShouldBe(2_000m);
        var reversal = await JournalAsync(b, revaluation.GetProperty("reversalJournalId").GetGuid());
        reversal.GetProperty("journalDate").GetString().ShouldBe("2026-07-01");
        Line(reversal, "4930", "baseDebit").ShouldBe(2_000m);

        // After the reversal the unrealized gain nets to zero for the year.
        (await RoleBalanceAsync(b, PostingRoles.UnrealizedFxGain)).ShouldBe(0m);

        var rerun = new HttpRequestMessage(HttpMethod.Post, "/api/v1/finance/fx-revaluations")
        {
            Content = JsonContent.Create(new { legalEntityId = b.LegalEntityId, asOf = "2026-06-30" }),
        }.WithCompany(b.T.CompanyId);
        rerun.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await (await b.T.Client.SendAsync(rerun, Ct)).ErrorCodeAsync()).ShouldBe("finance.revaluation-exists");
    }

    [Fact]
    public async Task Subledger_contract_is_transactional_idempotent_and_protects_allocated_items()
    {
        var b = await NewBooksAsync();
        var docId = Guid.NewGuid();
        var outside = await AsModuleAsync(b, sp => sp.GetRequiredService<ISubledgerService>().CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Customer, b.CustomerId,
            b.LegalEntityId, "sales", "SalesInvoice", docId, "INV-9", D, "EGP", 1m, 100m, null), Ct), transaction: false);
        outside.Error.Code.ShouldBe("finance.transaction-required");

        var first = await OpenInvoiceAsync(b, PartyKind.Customer, 1000m, docId: docId);
        var second = await OpenInvoiceAsync(b, PartyKind.Customer, 1000m, docId: docId);
        second.Single().Id.ShouldBe(first.Single().Id);

        // Credit note of 300 applied to the invoice.
        var creditId = Guid.NewGuid();
        (await OpenInvoiceAsync(b, PartyKind.Customer, -300m, docType: "SalesCreditNote", docId: creditId)).Single().OriginalAmount.ShouldBe(300m);
        var applied = await AsModuleAsync(b, sp => sp.GetRequiredService<ISubledgerService>().ApplyCreditNoteAsync("SalesCreditNote", creditId, "SalesInvoice", docId, D, Ct));
        applied.Value.ShouldBe(300m);

        var cancel = await AsModuleAsync(b, sp => sp.GetRequiredService<ISubledgerService>().CancelOpenItemsAsync("SalesInvoice", docId, Ct));
        cancel.Error.ShouldBe(Erp.Modules.Finance.Domain.FinanceErrors.OpenItemAllocated);

        var untouched = Guid.NewGuid();
        await OpenInvoiceAsync(b, PartyKind.Customer, 50m, docId: untouched);
        (await AsModuleAsync(b, sp => sp.GetRequiredService<ISubledgerService>().CancelOpenItemsAsync("SalesInvoice", untouched, Ct))).IsSuccess.ShouldBeTrue();

        var items = await AsModuleAsync(b, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Customer, b.CustomerId, cancellationToken: Ct), transaction: false);
        items.Single().OpenAmount.ShouldBe(700m);
    }

    [Fact]
    public async Task Expense_lines_cannot_use_control_or_revenue_accounts()
    {
        var b = await NewBooksAsync();
        var accounts = (await (await b.T.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
        foreach (var code in new[] { "2110", "4110" })
        {
            var response = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/expenses", b.T.CompanyId, new
            {
                legalEntityId = b.LegalEntityId, date = "2026-03-31", payee = "Supplier", supplierId = b.SupplierId,
                lines = new[] { new { accountId = accounts[code], description = "Not an expense", netAmount = 100m } },
            });
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity, code);
            (await response.ErrorCodeAsync()).ShouldBe("finance.invalid-expense-account");
        }
    }

    [Fact]
    public async Task Expense_to_supplier_creates_a_payable_and_transfers_move_cash_with_charges()
    {
        var b = await NewBooksAsync();
        var accounts = (await (await b.T.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
        var expenseId = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/expenses", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, date = "2026-03-31", payee = "Supplier", supplierId = b.SupplierId, reference = "RENT-03",
            lines = new[] { new { accountId = accounts["5310"], description = "March rent", netAmount = 25_000m } },
        }));
        var expense = await PostAsync(b, $"/api/v1/finance/expenses/{expenseId}/post");
        expense.GetProperty("number").GetString().ShouldBe("EXP-2026-000001");
        var journal = await JournalAsync(b, expense.GetProperty("journalId").GetGuid());
        Line(journal, "5310", "baseDebit").ShouldBe(25_000m);
        Line(journal, "2110", "baseCredit").ShouldBe(25_000m);
        var payables = await AsModuleAsync(b, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Supplier, b.SupplierId, cancellationToken: Ct), transaction: false);
        payables.Single().OpenAmount.ShouldBe(25_000m);

        var cash = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/cash-accounts", b.T.CompanyId,
            new { legalEntityId = b.LegalEntityId, code = "SAFE", name = "Main safe", currency = "EGP" }));
        var transferId = await IdAsync(await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/internal-transfers", b.T.CompanyId, new
        {
            legalEntityId = b.LegalEntityId, date = "2026-03-31", fromAccountId = b.BankId, fromAmount = 10_000m, toAccountId = cash, toAmount = 10_000m, charges = 15m,
        }));
        var transfer = await PostAsync(b, $"/api/v1/finance/internal-transfers/{transferId}/post");
        var tj = await JournalAsync(b, transfer.GetProperty("journalId").GetGuid());
        Line(tj, "1110", "baseDebit").ShouldBe(10_000m);
        Line(tj, "5410", "baseDebit").ShouldBe(15m);
        Line(tj, "1120", "baseCredit").ShouldBe(10_015m);

        var movements = await (await b.T.Client.GetAsCompanyAsync($"/api/v1/finance/cash-bank-accounts/{b.BankId}/movements", b.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var movementId = movements.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();

        // Reconcile the bank statement: one line for the transfer.
        var rec = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/bank-reconciliations", b.T.CompanyId, new
        {
            cashBankAccountId = b.BankId, statementDate = "2026-03-31", statementBalance = -10_015m,
            lines = new[] { new { date = "2026-03-31", amount = -10_015m, description = "Transfer to safe", reference = (string?)null } },
        });
        rec.StatusCode.ShouldBe(HttpStatusCode.Created);
        var recBody = await rec.Content.ReadFromJsonAsync<JsonElement>(Ct);
        recBody.GetProperty("lines")[0].GetProperty("matchedMovementId").GetGuid().ShouldBe(movementId);
        var complete = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/finance/bank-reconciliations/{recBody.GetProperty("id").GetGuid()}/complete", b.T.CompanyId);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync(Ct));
        (await complete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Completed");
    }

    [Fact]
    public async Task Finance_data_is_isolated_between_tenants()
    {
        var a = await NewBooksAsync();
        var other = await NewBooksAsync();
        var invoice = (await OpenInvoiceAsync(a, PartyKind.Customer, 100m)).Single();
        var receiptId = await IdAsync(await a.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", a.T.CompanyId, new
        {
            legalEntityId = a.LegalEntityId, partyId = a.CustomerId, date = "2026-03-15", method = "Cash", cashBankAccountId = a.BankId, amount = 100m,
            allocations = new[] { new { openItemId = invoice.Id, amount = 100m } },
        }));

        (await other.T.Client.GetAsCompanyAsync($"/api/v1/finance/receipts/{receiptId}", other.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var openItems = await (await other.T.Client.GetAsCompanyAsync("/api/v1/finance/open-items", other.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        openItems.GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await other.T.Client.GetAsCompanyAsync("/api/v1/finance/open-items", a.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Allocating another tenant's open item is impossible even with its id.
        var foreign = await other.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/receipts", other.T.CompanyId, new
        {
            legalEntityId = other.LegalEntityId, partyId = other.CustomerId, date = "2026-03-15", method = "Cash", cashBankAccountId = other.BankId, amount = 100m,
            allocations = new[] { new { openItemId = invoice.Id, amount = 100m } },
        });
        (await foreign.ErrorCodeAsync()).ShouldBe("finance.open-item-not-found");

        // Using another tenant's legal entity is refused too.
        var foreignLe = await other.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/bank-accounts", other.T.CompanyId,
            new { legalEntityId = a.LegalEntityId, code = "X", name = "X", currency = "EGP" });
        (await foreignLe.ErrorCodeAsync()).ShouldBe("finance.legal-entity-not-found");
    }
}
