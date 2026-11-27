using System.Net;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Employees and salespersons, salesperson defaulting, commissions, salesperson reports, profit centers and the invoice Closed status.</summary>
public class PeopleTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Branch, Guid Pcs, Guid Plate, Guid Repair, Guid Services,
        Guid PriceList, Guid Customer);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(ErpApiFixture.TenantSession t, HttpMethod method, string url, object? body = null, bool idempotent = false) =>
        t.Client.SendAsCompanyAsync(method, url, t.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static Task<HttpResponseMessage> Post(Shop s, string url, object? body = null) => Send(s.T, HttpMethod.Post, url, body, idempotent: true);

    private static Task<HttpResponseMessage> Put(Shop s, string url, object body) => Send(s.T, HttpMethod.Put, url, body);

    private static async Task<JsonElement> Get(Shop s, string url) => await Ok(await s.T.Client.GetAsCompanyAsync(url, s.T.CompanyId));

    /// <summary>
    /// Generic chart, FY2026, Egyptian VAT 14 %, a stockable plate (GOODS, 20 in stock at 60) and a repair service (SERVICES), an exclusive EGP
    /// price list (plate 100, repair 50) and a customer using it.
    /// </summary>
    private async Task<Shop> NewShopAsync()
    {
        var t = await fixture.NewTenantAsync();
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/accounting/coa-templates/generic"));
        var le = (await Ok(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId))).EnumerateArray().First().GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" }));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/tax/templates/egypt", new { }));
        var main = (await Ok(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId))).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN");
        var uoms = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/uoms/seed-standard"))).EnumerateArray()
            .ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());

        async Task<Guid> Category(string code) => (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/categories",
            new { code, nameEn = code, nameAr = code, defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))).GetProperty("id").GetGuid();

        var goods = await Category("GOODS");
        var services = await Category("SERVICES");

        async Task<Guid> Product(string sku, string type, Guid category) => (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/products", new
        {
            sku, nameEn = sku, nameAr = sku, type, baseUomId = uoms["PCS"], categoryId = category, tracking = "None", expiryTracked = false, taxCategory = "standard",
        }))).GetProperty("id").GetGuid();

        var plate = await Product("PLATE", "Stockable", goods);
        var repair = await Product("REPAIR", "Service", services);
        var list = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })))
            .GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = plate, uomId = uoms["PCS"], minQuantity = 0m, price = 100m }));
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = repair, uomId = uoms["PCS"], minQuantity = 0m, price = 50m }));
        var customer = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/parties/customers",
            new { code = "C1", kind = "Business", nameEn = "Delta Retail", nameAr = "دلتا", currency = "EGP", priceListId = list }))).GetProperty("id").GetGuid();

        var s = new Shop(t, le, main.GetProperty("id").GetGuid(), main.GetProperty("branchId").GetGuid(), uoms["PCS"], plate, repair, services, list, customer);
        var doc = await Ok(await Send(t, HttpMethod.Post, "/api/v1/inventory/opening-stock",
            new { warehouseId = s.Main, date = "2026-01-15", lines = new[] { new { productId = plate, uomId = s.Pcs, quantity = 20m, unitCost = 60m } } }));
        var id = doc.GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/submit"));
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/approve"));
        await Ok(await Post(s, $"/api/v1/inventory/opening-stock/{id}/post"));
        return s;
    }

    private static async Task<Guid> EmployeeAsync(Shop s, string code, bool salesperson, Guid? planId = null, Guid? userId = null) =>
        (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/people/employees", new
        {
            code,
            details = new { nameEn = $"Employee {code}", nameAr = $"موظف {code}", branchId = s.Branch, department = "Sales", jobTitle = "Rep", userId, isSalesperson = salesperson, commissionPlanId = planId },
        }))).GetProperty("id").GetGuid();

    private static object Line(Guid product, Guid uom, decimal quantity) => new { productId = product, uomId = uom, quantity };

    private static async Task<JsonElement> PostInvoiceAsync(Shop s, object request)
    {
        var draft = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/sales/invoices", request));
        var id = draft.GetProperty("id").GetGuid();
        (await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/submit"))).GetProperty("status").GetString().ShouldBe("Approved");
        return await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/post"));
    }

    private static async Task<JsonElement> CreditNoteAsync(Shop s, JsonElement invoice, Guid product, decimal quantity)
    {
        var line = invoice.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("productId").GetGuid() == product).GetProperty("id").GetGuid();
        var draft = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/sales/returns", new
        {
            salesInvoiceId = invoice.GetProperty("id").GetGuid(), date = "2026-06-20", reason = "Damaged", lines = new[] { new { invoiceLineId = line, quantity, restock = true } },
        }));
        return await Ok(await Post(s, $"/api/v1/sales/returns/{draft.GetProperty("id").GetGuid()}/post"));
    }

    private async Task<T> AsModuleAsync<T>(Shop s, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(s.T.TenantId);
        tenant.SetCompany(s.T.CompanyId);
        return await work(scope.ServiceProvider);
    }

    private Task<decimal> RoleAsync(Shop s, string role) =>
        AsModuleAsync(s, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(s.LegalEntityId, role, new DateOnly(2026, 12, 31), Ct));

    private async Task ProcessOutboxAsync()
    {
        var processor = fixture.Factory.Services.GetRequiredService<Erp.Infrastructure.Outbox.IOutboxProcessor>();
        for (var i = 0; i < 5; i++)
        {
            await processor.ProcessPendingAsync(Ct);
        }
    }

    private async Task<JsonElement> InvoiceUntilAsync(Shop s, Guid invoiceId, string status)
    {
        var current = await Get(s, $"/api/v1/sales/invoices/{invoiceId}");
        for (var i = 0; i < 20 && current.GetProperty("status").GetString() != status; i++)
        {
            await ProcessOutboxAsync();
            current = await Get(s, $"/api/v1/sales/invoices/{invoiceId}");
        }

        return current;
    }

    [Fact]
    public async Task Employees_are_validated_and_salespersons_default_from_the_customer_through_the_sales_documents()
    {
        var s = await NewShopAsync();
        var rep = await EmployeeAsync(s, "E1", salesperson: true, userId: s.T.AdminUserId);
        var clerk = await EmployeeAsync(s, "E2", salesperson: false);
        var other = await EmployeeAsync(s, "E3", salesperson: true);

        // Codes are unique; a user links to one employee; plans must exist.
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/people/employees", new { code = "E1", details = new { nameEn = "X", nameAr = "X", isSalesperson = false } }))
            .ErrorCodeAsync()).ShouldBe("people.employee-code-taken");
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/people/employees",
            new { code = "E9", details = new { nameEn = "X", nameAr = "X", isSalesperson = false, userId = s.T.AdminUserId } })).ErrorCodeAsync()).ShouldBe("people.user-already-linked");
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/people/employees",
            new { code = "E8", details = new { nameEn = "X", nameAr = "X", isSalesperson = true, commissionPlanId = Guid.NewGuid() } })).ErrorCodeAsync()).ShouldBe("people.plan-not-found");
        (await Get(s, "/api/v1/people/employees?salespersons=true")).EnumerateArray().Select(e => e.GetProperty("code").GetString()).ShouldBe(["E1", "E3"]);

        // Only active salespersons can be the customer's default.
        (await (await Put(s, $"/api/v1/parties/customers/{s.Customer}/salesperson", new { salespersonId = clerk })).ErrorCodeAsync()).ShouldBe("parties.salesperson-invalid");
        (await Ok(await Put(s, $"/api/v1/parties/customers/{s.Customer}/salesperson", new { salespersonId = rep }))).GetProperty("salespersonId").GetGuid().ShouldBe(rep);

        // Quotation → order → invoice → credit note all carry the customer's salesperson.
        var quotation = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/sales/quotations",
            new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-01", validUntil = "2027-12-31", lines = new[] { Line(s.Plate, s.Pcs, 1) } }));
        quotation.GetProperty("salespersonId").GetGuid().ShouldBe(rep);
        var quotationId = quotation.GetProperty("id").GetGuid();
        await Ok(await Post(s, $"/api/v1/sales/quotations/{quotationId}/send"));
        await Ok(await Post(s, $"/api/v1/sales/quotations/{quotationId}/accept"));
        var order = await Ok(await Post(s, $"/api/v1/sales/quotations/{quotationId}/convert", new { date = "2026-06-02" }));
        order.GetProperty("salespersonId").GetGuid().ShouldBe(rep);
        var orderId = order.GetProperty("id").GetGuid();
        await Ok(await Post(s, $"/api/v1/sales/orders/{orderId}/confirm"));
        var invoice = await PostInvoiceAsync(s, new { salesOrderId = orderId, date = "2026-06-03" });
        invoice.GetProperty("salespersonId").GetGuid().ShouldBe(rep);
        (await CreditNoteAsync(s, invoice, s.Plate, 1)).GetProperty("salespersonId").GetGuid().ShouldBe(rep);

        // An explicit salesperson wins; a non-salesperson is refused; drafts can be reassigned.
        var explicitOrder = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/sales/orders",
            new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-05", lines = new[] { Line(s.Plate, s.Pcs, 1) }, salespersonId = other }));
        explicitOrder.GetProperty("salespersonId").GetGuid().ShouldBe(other);
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/sales/orders",
            new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-05", lines = new[] { Line(s.Plate, s.Pcs, 1) }, salespersonId = clerk })).ErrorCodeAsync())
            .ShouldBe("sales.salesperson-invalid");
        (await Ok(await Put(s, $"/api/v1/sales/orders/{explicitOrder.GetProperty("id").GetGuid()}/salesperson", new { salespersonId = rep })))
            .GetProperty("salespersonId").GetGuid().ShouldBe(rep);
        (await (await Put(s, $"/api/v1/sales/orders/{orderId}/salesperson", new { salespersonId = other })).ErrorCodeAsync()).ShouldBe("sales.invalid-state-transition");

        // A deactivated salesperson is no longer used as the customer default.
        await Ok(await Put(s, $"/api/v1/people/employees/{rep}", new
        {
            details = new { nameEn = "Employee E1", nameAr = "موظف E1", isSalesperson = true, userId = s.T.AdminUserId }, isActive = false,
        }));
        var afterLeave = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/sales/orders",
            new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-06", lines = new[] { Line(s.Plate, s.Pcs, 1) } }));
        afterLeave.TryGetProperty("salespersonId", out var none).ShouldBeTrue();
        none.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Commission_runs_apply_tiers_category_rates_and_clawbacks_post_reverse_and_feed_the_reports()
    {
        var s = await NewShopAsync();
        await Ok(await Send(s.T, HttpMethod.Put, "/api/v1/configuration/modules/pos", new { enabled = true }));
        (await (await Send(s.T, HttpMethod.Get, "/api/v1/people/commission-plans")).ErrorCodeAsync()).ShouldBe("module-disabled");
        await Ok(await Send(s.T, HttpMethod.Put, "/api/v1/configuration/modules/commissions", new { enabled = true }));
        var plan = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/people/commission-plans", new
        {
            code = "STD",
            details = new
            {
                nameEn = "Standard", nameAr = "قياسية", basis = "NetSales", ratePercent = 5m, clawbackOnReturns = true,
                tiers = new[] { new { threshold = 300m, ratePercent = 8m } },
                categoryRates = new[] { new { categoryId = s.Services, ratePercent = 20m } },
            },
        }))).GetProperty("id").GetGuid();
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/people/commission-plans", new
        {
            code = "BAD", details = new { nameEn = "Bad", nameAr = "Bad", basis = "NetSales", ratePercent = 120m, clawbackOnReturns = false },
        })).ErrorCodeAsync()).ShouldBe("people.invalid-rate");

        var rep = await EmployeeAsync(s, "E1", salesperson: true, planId: plan, userId: s.T.AdminUserId);
        var noPlan = await EmployeeAsync(s, "E2", salesperson: true);

        // Invoice A (rep): 2 plates + 1 repair; credit note: 1 plate back. Invoice B (no plan): 1 plate.
        var a = await PostInvoiceAsync(s, new
        {
            customerId = s.Customer, warehouseId = s.Main, date = "2026-06-02", salespersonId = rep, lines = new[] { Line(s.Plate, s.Pcs, 2), Line(s.Repair, s.Pcs, 1) },
        });
        await CreditNoteAsync(s, a, s.Plate, 1);
        await PostInvoiceAsync(s, new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-04", salespersonId = noPlan, lines = new[] { Line(s.Plate, s.Pcs, 1) } });

        // POS: the cashier is linked to the rep, so the sale defaults to the rep: 2 plates.
        var cash = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/finance/cash-accounts",
            new { legalEntityId = s.LegalEntityId, code = "DRAWER1", name = "Drawer", currency = "EGP" }))).GetProperty("id").GetGuid();
        var terminal = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/pos/terminals",
            new { code = "T01", name = "Till", warehouseId = s.Main, priceListId = s.PriceList, cashAccountId = cash, maxDiscountPercent = 10m }))).GetProperty("id").GetGuid();
        await Ok(await Post(s, "/api/v1/pos/shifts", new { terminalId = terminal, openingFloat = 100m, businessDate = "2026-06-10" }));
        var sale = await Ok(await Post(s, "/api/v1/pos/receipts", new
        {
            terminalId = terminal, clientReceiptId = Guid.NewGuid(), lines = new[] { new { productId = s.Plate, quantity = 2m, discountPercent = 0m } },
            payments = new[] { new { method = "Cash", amount = 228m } },
        }));
        sale.GetProperty("salespersonId").GetGuid().ShouldBe(rep);

        // Rep basis = 200 + 50 − 100 + 200 = 350 ≥ 300 → tier 8 %; the repair earns its category rate of 20 %.
        var run = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/people/commission-runs", new { legalEntityId = s.LegalEntityId, period = "2026-06-15" }));
        run.GetProperty("status").GetString().ShouldBe("Draft");
        run.GetProperty("periodFrom").GetString().ShouldBe("2026-06-01");
        run.GetProperty("periodTo").GetString().ShouldBe("2026-06-30");
        var line = run.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("salespersonId").GetGuid().ShouldBe(rep);
        line.GetProperty("basisTotal").GetDecimal().ShouldBe(350m);
        line.GetProperty("tierRatePercent").GetDecimal().ShouldBe(8m);
        line.GetProperty("commission").GetDecimal().ShouldBe(16m + 10m - 8m + 16m);
        run.GetProperty("total").GetDecimal().ShouldBe(34m);
        run.GetProperty("skippedSalespersons").EnumerateArray().Select(x => x.GetGuid()).ShouldBe([noPlan]);
        run.GetProperty("details").EnumerateArray().Count().ShouldBe(4);
        var runId = run.GetProperty("id").GetGuid();

        // One active run per legal entity and month.
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/people/commission-runs", new { legalEntityId = s.LegalEntityId, period = "2026-06-01" })).ErrorCodeAsync())
            .ShouldBe("people.commission-run-exists");

        // Draft → (post refused) → approved → posted: Dr commission expense / Cr commissions payable.
        (await (await Post(s, $"/api/v1/people/commission-runs/{runId}/post")).ErrorCodeAsync()).ShouldBe("people.invalid-state-transition");
        await Ok(await Post(s, $"/api/v1/people/commission-runs/{runId}/recalculate"));
        (await Ok(await Post(s, $"/api/v1/people/commission-runs/{runId}/approve"))).GetProperty("status").GetString().ShouldBe("Approved");
        var posted = await Ok(await Post(s, $"/api/v1/people/commission-runs/{runId}/post"));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("number").GetString()!.ShouldStartWith("COM");
        (await RoleAsync(s, PostingRoles.CommissionExpense)).ShouldBe(34m);
        (await RoleAsync(s, PostingRoles.CommissionPayable)).ShouldBe(-34m);
        var journal = await Get(s, $"/api/v1/accounting/journals/{posted.GetProperty("journalId").GetGuid()}");
        journal.GetProperty("lines").EnumerateArray().ShouldAllBe(l => l.GetProperty("partyType").GetString() == "Employee" && l.GetProperty("partyId").GetGuid() == rep);
        (await (await Post(s, $"/api/v1/people/commission-runs/{runId}/recalculate")).ErrorCodeAsync()).ShouldBe("people.invalid-state-transition");

        // Reports: sales by salesperson (invoices, credit notes, POS) and the commission statement.
        var bySalesperson = await Get(s, "/api/v1/reports/sales/by-salesperson?from=2026-06-01&to=2026-06-30");
        var rows = bySalesperson.GetProperty("rows").EnumerateArray().ToList();
        rows.Single(r => r.GetProperty("salespersonCode").GetString() == "E1").GetProperty("netRevenue").GetDecimal().ShouldBe(350m);
        rows.Single(r => r.GetProperty("salespersonCode").GetString() == "E2").GetProperty("netRevenue").GetDecimal().ShouldBe(100m);
        var statement = await Get(s, $"/api/v1/reports/people/commission-statement?from=2026-06-01&to=2026-06-30&salespersonId={rep}");
        statement.GetProperty("rows").EnumerateArray().Count().ShouldBe(4);
        statement.GetProperty("totals").GetProperty("commission").GetDecimal().ShouldBe(34m);

        // Reversal clears the books and frees the month for a new calculation.
        (await Ok(await Post(s, $"/api/v1/people/commission-runs/{runId}/reverse"))).GetProperty("status").GetString().ShouldBe("Reversed");
        (await RoleAsync(s, PostingRoles.CommissionExpense)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.CommissionPayable)).ShouldBe(0m);
        (await Get(s, $"/api/v1/reports/people/commission-statement?from=2026-06-01&to=2026-06-30")).GetProperty("totals").GetProperty("commission").GetDecimal().ShouldBe(0m);
        var again = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/people/commission-runs", new { legalEntityId = s.LegalEntityId, period = "2026-06-01" }));
        (await Ok(await Post(s, $"/api/v1/people/commission-runs/{again.GetProperty("id").GetGuid()}/cancel"))).GetProperty("status").GetString().ShouldBe("Cancelled");
        (await Get(s, $"/api/v1/accounting/reports/trial-balance?legalEntityId={s.LegalEntityId}&from=2026-01-01&to=2026-12-31")).GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Profit_centers_default_from_the_branch_and_split_the_income_statement()
    {
        var s = await NewShopAsync();
        var hq = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/accounting/profit-centers", new { code = "PC-HQ", name = "Head office", branchId = s.Branch })))
            .GetProperty("id").GetGuid();
        var projects = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/accounting/profit-centers", new { code = "PC-PRJ", name = "Projects" }))).GetProperty("id").GetGuid();
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/accounting/profit-centers", new { code = "PC-2", name = "Second", branchId = s.Branch })).ErrorCodeAsync())
            .ShouldBe("accounting.branch-already-mapped");
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/accounting/profit-centers", new { code = "PC-3", name = "Third", branchId = Guid.NewGuid() })).ErrorCodeAsync())
            .ShouldBe("accounting.branch-not-found");

        // Invoice of the HQ warehouse: revenue (sales journal) and cost of sales (inventory journal) both default to PC-HQ.
        var invoice = await PostInvoiceAsync(s, new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-02", lines = new[] { Line(s.Plate, s.Pcs, 1) } });
        var journal = await Get(s, $"/api/v1/accounting/journals/{invoice.GetProperty("journalId").GetGuid()}");
        journal.GetProperty("lines").EnumerateArray().ShouldAllBe(l => l.GetProperty("profitCenterId").GetGuid() == hq);

        // Manual journal with an explicit profit center: expense 30 for projects.
        var accounts = (await Get(s, "/api/v1/accounting/accounts")).EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!, x => x.GetProperty("id").GetGuid());
        var manual = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/accounting/journals", new
        {
            legalEntityId = s.LegalEntityId, journalDate = "2026-06-10", description = "Project costs",
            lines = new object[]
            {
                new { accountId = accounts["5510"], debit = 30m, credit = 0m, profitCenterId = projects },
                new { accountId = accounts["3100"], debit = 0m, credit = 30m },
            },
        }));
        await Ok(await Send(s.T, HttpMethod.Post, $"/api/v1/accounting/journals/{manual.GetProperty("id").GetGuid()}/post"));
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/accounting/journals", new
        {
            legalEntityId = s.LegalEntityId, journalDate = "2026-06-10", description = "Bad",
            lines = new object[]
            {
                new { accountId = accounts["5510"], debit = 1m, credit = 0m, profitCenterId = Guid.NewGuid() },
                new { accountId = accounts["3100"], debit = 0m, credit = 1m },
            },
        })).ErrorCodeAsync()).ShouldBe("accounting.profit-center-invalid");

        var pnl = await Get(s, $"/api/v1/accounting/reports/profit-center-pnl?legalEntityId={s.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        var rows = pnl.GetProperty("rows").EnumerateArray().ToList();
        var hqRow = rows.Single(r => r.GetProperty("code").GetString() == "PC-HQ");
        hqRow.GetProperty("netRevenue").GetDecimal().ShouldBe(100m);
        hqRow.GetProperty("costOfSales").GetDecimal().ShouldBe(60m);
        hqRow.GetProperty("netProfit").GetDecimal().ShouldBe(40m);
        rows.Single(r => r.GetProperty("code").GetString() == "PC-PRJ").GetProperty("operatingExpenses").GetDecimal().ShouldBe(30m);
        pnl.GetProperty("total").GetProperty("netProfit").GetDecimal().ShouldBe(10m);

        var filtered = await Get(s, $"/api/v1/accounting/reports/income-statement?legalEntityId={s.LegalEntityId}&from=2026-01-01&to=2026-12-31&profitCenterId={hq}");
        filtered.GetProperty("netProfit").GetProperty("amount").GetDecimal().ShouldBe(40m);
        filtered.GetProperty("profitCenterId").GetGuid().ShouldBe(hq);

        // An inactive profit center is no longer defaulted.
        await Ok(await Put(s, $"/api/v1/accounting/profit-centers/{hq}", new { name = "Head office", branchId = s.Branch, isActive = false }));
        var later = await PostInvoiceAsync(s, new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-12", lines = new[] { Line(s.Plate, s.Pcs, 1) } });
        (await Get(s, $"/api/v1/accounting/journals/{later.GetProperty("journalId").GetGuid()}")).GetProperty("lines").EnumerateArray()
            .ShouldAllBe(l => l.GetProperty("profitCenterId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Invoices_close_when_settled_and_fully_returned_or_on_request_and_reopen_when_the_receipt_is_reversed()
    {
        var s = await NewShopAsync();
        var bank = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/finance/bank-accounts",
            new { legalEntityId = s.LegalEntityId, code = "CIB", name = "CIB current", currency = "EGP", bankName = "CIB" }))).GetProperty("id").GetGuid();

        // Paid in full, then closed on request: no more credit notes.
        var invoice = await PostInvoiceAsync(s, new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-02", lines = new[] { Line(s.Plate, s.Pcs, 1) } });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        (await (await Post(s, $"/api/v1/sales/invoices/{invoiceId}/close")).ErrorCodeAsync()).ShouldBe("sales.invalid-state-transition");
        var item = (await AsModuleAsync(s, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Customer, s.Customer, cancellationToken: Ct))).Single();
        var receipt = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/finance/receipts", new
        {
            legalEntityId = s.LegalEntityId, partyId = s.Customer, date = "2026-06-15", method = "BankTransfer", cashBankAccountId = bank, amount = 114m,
            allocations = new[] { new { openItemId = item.Id, amount = 114m } },
        }));
        var receiptId = receipt.GetProperty("id").GetGuid();
        await Ok(await Post(s, $"/api/v1/finance/receipts/{receiptId}/post"));
        (await InvoiceUntilAsync(s, invoiceId, "Paid")).GetProperty("status").GetString().ShouldBe("Paid");
        (await Ok(await Post(s, $"/api/v1/sales/invoices/{invoiceId}/close"))).GetProperty("status").GetString().ShouldBe("Closed");
        var line = invoice.GetProperty("lines").EnumerateArray().First().GetProperty("id").GetGuid();
        (await (await Send(s.T, HttpMethod.Post, "/api/v1/sales/returns", new
        {
            salesInvoiceId = invoiceId, date = "2026-06-20", reason = "Late", lines = new[] { new { invoiceLineId = line, quantity = 1m, restock = true } },
        })).ErrorCodeAsync()).ShouldBe("sales.invalid-state-transition");

        // Reversing the receipt reopens the receivable: the invoice is open again.
        await Ok(await Post(s, $"/api/v1/finance/receipts/{receiptId}/reverse", new { date = "2026-06-25", reason = "Cheque bounced" }));
        var reopened = await InvoiceUntilAsync(s, invoiceId, "Posted");
        reopened.GetProperty("status").GetString().ShouldBe("Posted");
        reopened.GetProperty("openAmount").GetDecimal().ShouldBe(114m);

        // An unpaid invoice that is fully credited closes by itself once the credit note settles it.
        var credited = await PostInvoiceAsync(s, new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-03", lines = new[] { Line(s.Plate, s.Pcs, 2) } });
        await CreditNoteAsync(s, credited, s.Plate, 2);
        var closed = await InvoiceUntilAsync(s, credited.GetProperty("id").GetGuid(), "Closed");
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("openAmount").GetDecimal().ShouldBe(0m);
    }
}
