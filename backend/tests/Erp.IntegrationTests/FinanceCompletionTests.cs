using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Reversals, cross-currency allocation, GL bank reconciliation, withholding certificates, VAT return, e-invoicing, statement classification.</summary>
public class FinanceCompletionTests(ErpApiFixture fixture)
{
    private sealed record Books(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid CustomerId, Guid SupplierId, Guid BankId, Dictionary<string, Guid> Accounts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly D = new(2026, 3, 10);

    private async Task<Books> NewBooksAsync(bool egyptTaxes = false, string customerName = "Delta Retail")
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var le = (await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .EnumerateArray().First().GetProperty("id").GetGuid();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", t.CompanyId, new { legalEntityId = le, startDate = "2026-01-01" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        if (egyptTaxes)
        {
            (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/templates/egypt", t.CompanyId, new { })).IsSuccessStatusCode.ShouldBeTrue();
        }

        var customer = await IdAsync(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers", t.CompanyId,
            new { code = "C1", kind = "Business", nameEn = customerName, nameAr = "دلتا", currency = "EGP" }));
        var supplier = await IdAsync(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/suppliers", t.CompanyId,
            new { code = "S1", nameEn = "Nile Supplies", nameAr = "النيل", currency = "EGP" }));
        var bank = await IdAsync(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/finance/bank-accounts", t.CompanyId,
            new { legalEntityId = le, code = "CIB", name = "CIB current", currency = "EGP", bankName = "CIB" }));
        var accounts = (await (await t.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
        return new Books(t, le, customer, supplier, bank, accounts);
    }

    private static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Books b, HttpMethod method, string url, object? body = null, bool idempotent = false) =>
        b.T.Client.SendAsCompanyAsync(method, url, b.T.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static async Task<JsonElement> Get(Books b, string url) => await Ok(await b.T.Client.GetAsCompanyAsync(url, b.T.CompanyId));

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

    /// <summary>A receivable/payable plus the source module's journal, as Sales/Procurement would post it.</summary>
    private Task<OpenItemSummary> OpenInvoiceAsync(Books b, PartyKind party, decimal amount, string currency = "EGP", decimal rate = 1m) =>
        AsModuleAsync(b, async sp =>
        {
            var id = Guid.NewGuid();
            var module = party == PartyKind.Customer ? SourceModules.Sales : SourceModules.Procurement;
            var docType = party == PartyKind.Customer ? "SalesInvoice" : "PurchaseInvoice";
            var result = await sp.GetRequiredService<ISubledgerService>().CreateOpenItemsAsync(new OpenItemRequest(party, party == PartyKind.Customer ? b.CustomerId : b.SupplierId,
                b.LegalEntityId, module, docType, id, $"DOC-{Guid.NewGuid().ToString()[..6]}", D, currency, rate, amount, null), Ct);
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);
            var control = party == PartyKind.Customer ? PostingRoles.CustomerReceivable : PostingRoles.SupplierPayable;
            var other = party == PartyKind.Customer ? PostingRoles.SalesRevenue : PostingRoles.PurchasePriceVariance;
            JournalLineRequest[] lines = party == PartyKind.Customer
                ? [new(control, null, amount, 0m), new(other, null, 0m, amount)]
                : [new(control, null, 0m, amount), new(other, null, amount, 0m)];
            var posted = await sp.GetRequiredService<IJournalPostingService>().PostAsync(new JournalRequest(b.LegalEntityId, D, module, docType, id, "invoice",
                "Test document", currency, currency == "EGP" ? 1m : rate, lines), Ct);
            posted.IsSuccess.ShouldBeTrue(posted.IsFailure ? posted.Error.Code : null);
            return result.Value.Single();
        });

    private Task<decimal> RoleBalanceAsync(Books b, string role) =>
        AsModuleAsync(b, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(b.LegalEntityId, role, new DateOnly(2026, 12, 31), Ct), transaction: false);

    private async Task<IReadOnlyList<OpenItemView>> OpenItemsAsync(Books b, PartyKind party) =>
        await AsModuleAsync(b, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(party, party == PartyKind.Customer ? b.CustomerId : b.SupplierId, true, Ct),
            transaction: false);

    private static async Task<JsonElement> JournalAsync(Books b, Guid journalId) => await Get(b, $"/api/v1/accounting/journals/{journalId}");

    private static decimal Line(JsonElement journal, string accountCode, string side) =>
        journal.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("accountCode").GetString() == accountCode).Sum(l => l.GetProperty(side).GetDecimal());

    private static async Task<JsonElement> ReceiptAsync(Books b, Guid bank, decimal amount, string date, object[] allocations, decimal? rate = null)
    {
        var id = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/finance/receipts", new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date, method = "BankTransfer", cashBankAccountId = bank, amount, exchangeRate = rate, allocations,
        }));
        return await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{id}/post", idempotent: true));
    }

    private async Task<bool> TrialBalanceIsBalancedAsync(Books b) =>
        (await Get(b, $"/api/v1/accounting/reports/trial-balance?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31")).GetProperty("isBalanced").GetBoolean();

    [Fact]
    public async Task Reversing_a_receipt_undoes_allocations_advances_and_cash_and_respects_bank_reconciliation()
    {
        var b = await NewBooksAsync();
        var inv1 = await OpenInvoiceAsync(b, PartyKind.Customer, 1000m);
        var inv2 = await OpenInvoiceAsync(b, PartyKind.Customer, 300m);

        // 1,500 received: 1,000 settles inv1, 500 is an advance; later 300 of the advance settles inv2.
        var receipt = await ReceiptAsync(b, b.BankId, 1500m, "2026-03-15", [new { openItemId = inv1.Id, amount = 1000m }]);
        var receiptId = receipt.GetProperty("id").GetGuid();
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/allocations",
            new { date = "2026-03-18", allocations = new[] { new { openItemId = inv2.Id, amount = 300m } } }, idempotent: true));
        (await RoleBalanceAsync(b, PostingRoles.CustomerReceivable)).ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.CustomerAdvance)).ShouldBe(-200m);

        // A reason is required.
        (await (await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/reverse", new { date = "2026-03-25", reason = "" }, idempotent: true))
            .ErrorCodeAsync()).ShouldBe("validation-failed");

        var reversed = await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/reverse",
            new { date = "2026-03-25", reason = "Cheque bounced" }, idempotent: true));
        reversed.GetProperty("status").GetString().ShouldBe("Reversed");
        reversed.GetProperty("reversalReason").GetString().ShouldBe("Cheque bounced");
        reversed.GetProperty("unallocatedAmount").GetDecimal().ShouldBe(0m);
        reversed.GetProperty("allocations").EnumerateArray().Count(a => a.TryGetProperty("reversalOfId", out var r) && r.ValueKind != JsonValueKind.Null).ShouldBe(2);

        // Open items, control accounts and the bank all return to their state before the receipt.
        var items = await OpenItemsAsync(b, PartyKind.Customer);
        items.Single(i => i.Id == inv1.Id).OpenAmount.ShouldBe(1000m);
        items.Single(i => i.Id == inv2.Id).OpenAmount.ShouldBe(300m);
        items.All(i => i.Status == "Open").ShouldBeTrue();
        (await RoleBalanceAsync(b, PostingRoles.CustomerReceivable)).ShouldBe(1300m);
        (await RoleBalanceAsync(b, PostingRoles.CustomerAdvance)).ShouldBe(0m);
        var bank = (await Get(b, "/api/v1/finance/bank-accounts")).EnumerateArray().Single();
        bank.GetProperty("balance").GetDecimal().ShouldBe(0m);
        (await TrialBalanceIsBalancedAsync(b)).ShouldBeTrue();

        var statement = await Get(b, $"/api/v1/finance/reports/customer-statement?customerId={b.CustomerId}&legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        statement.GetProperty("closingBalance").GetDecimal().ShouldBe(1300m);
        (await (await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{receiptId}/reverse", new { date = "2026-03-26", reason = "again" }, idempotent: true))
            .ErrorCodeAsync()).ShouldBe("finance.not-posted");

        // A receipt in a completed bank reconciliation cannot be reversed until the reconciliation is reopened and the line unmatched.
        var second = await ReceiptAsync(b, b.BankId, 1000m, "2026-04-02", [new { openItemId = inv1.Id, amount = 1000m }]);
        var secondId = second.GetProperty("id").GetGuid();
        var rec = await Ok(await Send(b, HttpMethod.Post, "/api/v1/finance/bank-reconciliations", new
        {
            cashBankAccountId = b.BankId, statementDate = "2026-04-30", statementBalance = 1000m,
            lines = new[] { new { date = "2026-04-02", amount = 1000m, description = "Deposit", reference = (string?)null } },
        }));
        var recId = rec.GetProperty("id").GetGuid();
        (await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/bank-reconciliations/{recId}/complete"))).GetProperty("status").GetString().ShouldBe("Completed");
        (await (await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{secondId}/reverse", new { date = "2026-04-05", reason = "Wrong customer" }, idempotent: true))
            .ErrorCodeAsync()).ShouldBe("finance.movement-reconciled");

        var reopened = await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/bank-reconciliations/{recId}/reopen"));
        reopened.GetProperty("status").GetString().ShouldBe("Draft");
        (await (await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{secondId}/reverse", new { date = "2026-04-05", reason = "Wrong customer" }, idempotent: true))
            .ErrorCodeAsync()).ShouldBe("finance.movement-in-reconciliation");
        var lineId = reopened.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await Ok(await Send(b, HttpMethod.Put, $"/api/v1/finance/bank-reconciliations/{recId}/lines/{lineId}", new { movementId = (Guid?)null }));
        (await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/receipts/{secondId}/reverse", new { date = "2026-04-05", reason = "Wrong customer" }, idempotent: true)))
            .GetProperty("status").GetString().ShouldBe("Reversed");
        (await RoleBalanceAsync(b, PostingRoles.CustomerReceivable)).ShouldBe(1300m);
        (await TrialBalanceIsBalancedAsync(b)).ShouldBeTrue();
    }

    [Fact]
    public async Task Cross_currency_allocation_books_the_realized_difference_and_journals_carry_the_transaction_currency()
    {
        var b = await NewBooksAsync();
        foreach (var (code, name) in new[] { ("EGP", "Egyptian pound"), ("USD", "US dollar") })
        {
            await Send(b, HttpMethod.Post, "/api/v1/tax/currencies", new { code, nameEn = name, nameAr = name, minorUnits = 2 });
        }

        await Ok(await Send(b, HttpMethod.Post, "/api/v1/tax/exchange-rates", new { fromCurrency = "USD", toCurrency = "EGP", type = "Spot", effectiveDate = "2026-04-01", rate = 50m }));
        var usd100 = await OpenInvoiceAsync(b, PartyKind.Customer, 100m, "USD", 48m);  // 4,800 EGP
        var usd50 = await OpenInvoiceAsync(b, PartyKind.Customer, 50m, "USD", 48m);    // 2,400 EGP

        // EGP receipt: USD 100 converted at the spot rate (5,000 EGP), USD 50 at an agreed 2,450 EGP. Gains: 200 + 50.
        var receipt = await ReceiptAsync(b, b.BankId, 7450m, "2026-04-05",
        [
            new { openItemId = usd100.Id, amount = 100m, settlementAmount = (decimal?)null },
            new { openItemId = usd50.Id, amount = 50m, settlementAmount = (decimal?)2450m },
        ]);
        receipt.GetProperty("unallocatedAmount").GetDecimal().ShouldBe(0m);
        var allocations = receipt.GetProperty("allocations").EnumerateArray().ToList();
        allocations.Single(a => a.GetProperty("openItemId").GetGuid() == usd100.Id).GetProperty("settlementAmount").GetDecimal().ShouldBe(5000m);
        allocations.Sum(a => a.GetProperty("realizedFxBase").GetDecimal()).ShouldBe(250m);

        var journal = await JournalAsync(b, receipt.GetProperty("journalId").GetGuid());
        journal.GetProperty("currency").GetString().ShouldBe("EGP");
        Line(journal, "1120", "baseDebit").ShouldBe(7450m);
        Line(journal, "1210", "baseCredit").ShouldBe(7200m);
        Line(journal, "4920", "baseCredit").ShouldBe(250m);
        (await OpenItemsAsync(b, PartyKind.Customer)).All(i => i.Status == "Settled").ShouldBeTrue();
        (await RoleBalanceAsync(b, PostingRoles.CustomerReceivable)).ShouldBe(0m);

        // A cross-currency allocation without a rate on the date is refused.
        var usd10 = await OpenInvoiceAsync(b, PartyKind.Customer, 10m, "USD", 48m);
        (await (await Send(b, HttpMethod.Post, "/api/v1/finance/receipts", new
        {
            legalEntityId = b.LegalEntityId, partyId = b.CustomerId, date = "2026-03-01", method = "Cash", cashBankAccountId = b.BankId, amount = 500m,
            allocations = new[] { new { openItemId = usd10.Id, amount = 10m } },
        })).ErrorCodeAsync()).ShouldBe("finance.cross-currency-rate-missing");

        // A USD receipt into a USD account: the journal is in USD at the receipt rate; the gain is a base-only line.
        var usdBank = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/finance/bank-accounts",
            new { legalEntityId = b.LegalEntityId, code = "USD1", name = "USD account", currency = "USD" }));
        var usdReceipt = await ReceiptAsync(b, usdBank, 10m, "2026-04-06", [new { openItemId = usd10.Id, amount = 10m }], rate: 51m);
        var usdJournal = await JournalAsync(b, usdReceipt.GetProperty("journalId").GetGuid());
        usdJournal.GetProperty("currency").GetString().ShouldBe("USD");
        usdJournal.GetProperty("exchangeRate").GetDecimal().ShouldBe(51m);
        Line(usdJournal, "1120", "debit").ShouldBe(10m);
        Line(usdJournal, "1120", "baseDebit").ShouldBe(510m);
        Line(usdJournal, "1210", "credit").ShouldBe(10m);
        Line(usdJournal, "1210", "baseCredit").ShouldBe(480m);
        Line(usdJournal, "4920", "credit").ShouldBe(0m);
        Line(usdJournal, "4920", "baseCredit").ShouldBe(30m);
        (await TrialBalanceIsBalancedAsync(b)).ShouldBeTrue();
    }

    [Fact]
    public async Task Bank_reconciliation_matches_general_ledger_lines_posted_outside_finance()
    {
        var b = await NewBooksAsync();
        var dedicated = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/finance/bank-accounts",
            new { legalEntityId = b.LegalEntityId, code = "NBE", name = "NBE current", currency = "EGP", glAccountId = b.Accounts["1120"] }));

        // A manual journal (capital injected straight into the bank) never passes through Finance.
        var created = await Ok(await Send(b, HttpMethod.Post, "/api/v1/accounting/journals", new
        {
            legalEntityId = b.LegalEntityId, journalDate = "2026-05-03", description = "Capital injection",
            lines = new[] { new { accountId = b.Accounts["1120"], debit = 750m, credit = 0m }, new { accountId = b.Accounts["3100"], debit = 0m, credit = 750m } },
        }));
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/accounting/journals/{created.GetProperty("id").GetGuid()}/post"));

        var rec = await Ok(await Send(b, HttpMethod.Post, "/api/v1/finance/bank-reconciliations", new
        {
            cashBankAccountId = dedicated, statementDate = "2026-05-31", statementBalance = 750m,
            lines = new[] { new { date = "2026-05-04", amount = 750m, description = "Capital", reference = (string?)null } },
        }));
        rec.GetProperty("bookBalance").GetDecimal().ShouldBe(750m);
        var matched = rec.GetProperty("lines")[0].GetProperty("matchedMovementId").GetGuid();
        var movements = await Get(b, $"/api/v1/finance/cash-bank-accounts/{dedicated}/movements");
        var movement = movements.GetProperty("items").EnumerateArray().Single();
        movement.GetProperty("id").GetGuid().ShouldBe(matched);
        movement.GetProperty("sourceDocType").GetString().ShouldBe("GlJournalLine");
        (await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/bank-reconciliations/{rec.GetProperty("id").GetGuid()}/complete")))
            .GetProperty("status").GetString().ShouldBe("Completed");

        // Syncing again imports nothing new.
        (await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/cash-bank-accounts/{dedicated}/sync-gl", new { upTo = "2026-12-31" })))
            .GetProperty("imported").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Withholding_certificates_list_payments_once_print_and_block_reversal()
    {
        var b = await NewBooksAsync(egyptTaxes: true);
        var codes = await Get(b, "/api/v1/tax/tax-codes");
        var wht1 = codes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "WHT-1").GetProperty("id").GetGuid();
        var group = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/tax/tax-groups", new
        {
            code = "WHT1", nameEn = "Withholding 1%", nameAr = "خصم 1%", components = new[] { new { taxCodeId = wht1, sequence = 1, compoundsOnPrevious = false } },
        }));
        var invoice = await OpenInvoiceAsync(b, PartyKind.Supplier, 6954m);
        var paymentId = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/finance/payments", new
        {
            legalEntityId = b.LegalEntityId, partyId = b.SupplierId, date = "2026-03-20", method = "BankTransfer", cashBankAccountId = b.BankId, amount = 6893m,
            withholdingTaxGroupId = group, withholdingBase = 6100m, allocations = new[] { new { openItemId = invoice.Id, amount = 6954m } },
        }));
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/payments/{paymentId}/post", idempotent: true));

        var request = new { legalEntityId = b.LegalEntityId, kind = "Issued", partyId = b.SupplierId, periodFrom = "2026-03-01", periodTo = "2026-03-31", issueDate = "2026-04-05" };
        var certificate = await Ok(await Send(b, HttpMethod.Post, "/api/v1/finance/withholding-certificates", request, idempotent: true));
        certificate.GetProperty("number").GetString().ShouldBe("WHT-2026-000001");
        certificate.GetProperty("totalWithheldBase").GetDecimal().ShouldBe(61m);
        certificate.GetProperty("totalTaxableBase").GetDecimal().ShouldBe(6100m);
        certificate.GetProperty("lines").GetArrayLength().ShouldBe(1);

        // The payment is on an active certificate: no second certificate, no reversal.
        (await (await Send(b, HttpMethod.Post, "/api/v1/finance/withholding-certificates", request, idempotent: true)).ErrorCodeAsync()).ShouldBe("finance.no-withholding");
        (await (await Send(b, HttpMethod.Post, $"/api/v1/finance/payments/{paymentId}/reverse", new { date = "2026-03-28", reason = "Duplicate" }, idempotent: true))
            .ErrorCodeAsync()).ShouldBe("finance.on-withholding-certificate");

        var html = await b.T.Client.GetAsCompanyAsync($"/api/v1/reports/documents/withholding-certificate/{certificate.GetProperty("id").GetGuid()}", b.T.CompanyId);
        html.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await html.Content.ReadAsStringAsync(Ct)).ShouldContain("WHT-2026-000001");

        var report = await Get(b, $"/api/v1/reports/tax/withholding?from=2026-01-01&to=2026-12-31&supplierId={b.SupplierId}");
        var row = report.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("withheldBase").GetDecimal().ShouldBe(61m);
        row.GetProperty("certificate").GetString().ShouldBe("WHT-2026-000001");

        // Cancelling releases the payment: it can be certified again (next number) or reversed.
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/withholding-certificates/{certificate.GetProperty("id").GetGuid()}/cancel"));
        (await Ok(await Send(b, HttpMethod.Post, "/api/v1/finance/withholding-certificates", request, idempotent: true)))
            .GetProperty("number").GetString().ShouldBe("WHT-2026-000002");
    }

    [Fact]
    public async Task Vat_return_groups_tax_by_box_and_reconciles_to_the_tax_accounts()
    {
        var b = await NewBooksAsync(egyptTaxes: true);
        var codes = (await Get(b, "/api/v1/tax/tax-codes")).EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("id").GetGuid());
        var groups = (await Get(b, "/api/v1/tax/tax-groups")).EnumerateArray().ToDictionary(g => g.GetProperty("code").GetString()!, g => g.GetProperty("id").GetGuid());

        // Output tax as Sales posts it (tax code and taxable amount on the line): 2,000 at 14 %.
        await AsModuleAsync(b, async sp =>
        {
            var id = Guid.NewGuid();
            var posted = await sp.GetRequiredService<IJournalPostingService>().PostAsync(new JournalRequest(b.LegalEntityId, new DateOnly(2026, 5, 10), SourceModules.Sales,
                "SalesInvoice", id, "invoice", "Invoice", "EGP", 1m,
                [
                    new(PostingRoles.CustomerReceivable, null, 2280m, 0m),
                    new(PostingRoles.SalesRevenue, null, 0m, 2000m),
                    new(PostingRoles.OutputTax, null, 0m, 280m, new PostingContext(TaxCodeId: codes["VAT14-OUT"]), TaxableAmount: 2000m),
                ]), Ct);
            posted.IsSuccess.ShouldBeTrue(posted.IsFailure ? posted.Error.Code : null);
            return 0;
        });

        // Input tax through a Finance expense with the standard purchase VAT group: 500 net, 70 VAT.
        var expenseId = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/finance/expenses", new
        {
            legalEntityId = b.LegalEntityId, date = "2026-05-12", payee = "CashBank", cashBankAccountId = b.BankId, reference = "R-1",
            lines = new[] { new { accountId = b.Accounts["5310"], description = "Rent", netAmount = 500m, taxGroupId = groups["VAT14-PURCH"] } },
        }));
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/finance/expenses/{expenseId}/post", idempotent: true));

        // A manual adjustment to the output tax account has no tax code: it shows as an unreconciled difference.
        var manual = await Send(b, HttpMethod.Post, "/api/v1/accounting/journals", new
        {
            legalEntityId = b.LegalEntityId, journalDate = "2026-05-20", description = "VAT adjustment",
            lines = new[] { new { accountId = b.Accounts["5310"], debit = 0m, credit = 10m }, new { accountId = b.Accounts["2310"], debit = 10m, credit = 0m } },
        });
        var manualAllowed = manual.StatusCode == HttpStatusCode.Created;
        if (manualAllowed)
        {
            await Ok(await Send(b, HttpMethod.Post, $"/api/v1/accounting/journals/{(await manual.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid()}/post"));
        }

        var report = await Get(b, "/api/v1/reports/tax/vat-return?from=2026-05-01&to=2026-05-31");
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        var output = rows.Single(r => r.GetProperty("direction").GetString() == "Output");
        output.GetProperty("taxCode").GetString().ShouldBe("VAT14-OUT");
        output.GetProperty("box").GetString().ShouldBe("VAT-STANDARD");
        output.GetProperty("taxable").GetDecimal().ShouldBe(2000m);
        output.GetProperty("tax").GetDecimal().ShouldBe(280m);
        output.GetProperty("taxableBasis").GetString().ShouldBe("Posted");
        var input = rows.Single(r => r.GetProperty("direction").GetString() == "Input");
        input.GetProperty("box").GetString().ShouldBe("VAT-INPUT");
        input.GetProperty("taxable").GetDecimal().ShouldBe(500m);
        input.GetProperty("tax").GetDecimal().ShouldBe(70m);

        var parameters = report.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("labelEn").GetString()!, p => p.GetProperty("value").GetString()!);
        parameters["Net tax payable"].ShouldBe("210.00");
        parameters["Net movement on tax accounts (GL)"].ShouldBe(manualAllowed ? "200.00" : "210.00");
        parameters["Unreconciled difference (e.g. manual journals)"].ShouldBe(manualAllowed ? "-10.00" : "0.00");
        (await RoleBalanceAsync(b, PostingRoles.OutputTax) + await RoleBalanceAsync(b, PostingRoles.InputTax)).ShouldBe(manualAllowed ? -200m : -210m);
    }

    [Fact]
    public async Task Account_classification_drives_statements_instead_of_posting_rules()
    {
        var b = await NewBooksAsync();
        var account = await Get(b, "/api/v1/accounting/accounts");
        account.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "4110").GetProperty("reportGroup").GetString().ShouldBe("Revenue");
        account.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "5110").GetProperty("reportGroup").GetString().ShouldBe("CostOfSales");
        account.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "5310").GetProperty("reportGroup").GetString().ShouldBe("OperatingExpenses");

        var created = await Ok(await Send(b, HttpMethod.Post, "/api/v1/accounting/journals", new
        {
            legalEntityId = b.LegalEntityId, journalDate = "2026-06-10", description = "Freight in",
            lines = new[] { new { accountId = b.Accounts["5310"], debit = 400m, credit = 0m }, new { accountId = b.Accounts["3100"], debit = 0m, credit = 400m } },
        }));
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/accounting/journals/{created.GetProperty("id").GetGuid()}/post"));

        static decimal Section(JsonElement statement, string key) =>
            statement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("total").GetDecimal();

        var before = await Get(b, $"/api/v1/accounting/reports/income-statement?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        Section(before, "operating-expenses").ShouldBe(400m);
        Section(before, "cost-of-sales").ShouldBe(0m);

        // Moving the account to cost of sales changes the statement; the posting rules are untouched.
        (await Ok(await Send(b, HttpMethod.Put, $"/api/v1/accounting/accounts/{b.Accounts["5310"]}/report-group", new { group = "CostOfSales" })))
            .GetProperty("reportGroup").GetString().ShouldBe("CostOfSales");
        var after = await Get(b, $"/api/v1/accounting/reports/income-statement?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        Section(after, "cost-of-sales").ShouldBe(400m);
        Section(after, "operating-expenses").ShouldBe(0m);
        after.GetProperty("grossProfit").GetProperty("amount").GetDecimal().ShouldBe(-400m);
        (await (await Send(b, HttpMethod.Put, $"/api/v1/accounting/accounts/{b.Accounts["5310"]}/report-group", new { group = "Revenue" })).ErrorCodeAsync())
            .ShouldBe("accounting.report-group-invalid");

        // Cash flow: an unclassified balance-sheet account is listed separately; a category override moves it.
        var bankLoan = await IdAsync(await Send(b, HttpMethod.Post, "/api/v1/accounting/accounts", new
        {
            code = "2510", nameEn = "Bank loan", nameAr = "قرض بنكي", type = "Liability", parentId = (Guid?)null, isPostingAccount = true, controlSubledger = (string?)null,
            cashFlowCategory = "None",
        }));
        var loan = await Ok(await Send(b, HttpMethod.Post, "/api/v1/accounting/journals", new
        {
            legalEntityId = b.LegalEntityId, journalDate = "2026-06-15", description = "Loan drawn",
            lines = new[] { new { accountId = b.Accounts["1120"], debit = 5000m, credit = 0m }, new { accountId = bankLoan, debit = 0m, credit = 5000m } },
        }));
        await Ok(await Send(b, HttpMethod.Post, $"/api/v1/accounting/journals/{loan.GetProperty("id").GetGuid()}/post"));
        var cashFlow = await Get(b, $"/api/v1/accounting/reports/cash-flow?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        Section(cashFlow, "unclassified").ShouldBe(5000m);
        cashFlow.GetProperty("reconciles").GetBoolean().ShouldBeTrue();

        await Ok(await Send(b, HttpMethod.Put, $"/api/v1/accounting/accounts/{bankLoan}/cash-flow-category", new { category = "Financing" }));
        var reclassified = await Get(b, $"/api/v1/accounting/reports/cash-flow?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        reclassified.GetProperty("sections").EnumerateArray().Any(s => s.GetProperty("key").GetString() == "unclassified").ShouldBeFalse();
        reclassified.GetProperty("netCashFromFinancing").GetDecimal().ShouldBe(5400m);
        reclassified.GetProperty("reconciles").GetBoolean().ShouldBeTrue();
    }
}
