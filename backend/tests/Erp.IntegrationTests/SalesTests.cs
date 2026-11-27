using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class SalesTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Pcs, Guid Plate, Guid Repair, Guid Customer);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Shop s, HttpMethod method, string url, object? body = null, string? key = null, bool idempotent = false) =>
        s.T.Client.SendAsCompanyAsync(method, url, s.T.CompanyId, body, idempotencyKey: key ?? (idempotent ? Guid.NewGuid().ToString() : null));

    private static Task<HttpResponseMessage> Post(Shop s, string url, object? body = null, string? key = null) => Send(s, HttpMethod.Post, url, body, key, idempotent: true);

    private static async Task<JsonElement> Get(Shop s, string url) => await Ok(await s.T.Client.GetAsCompanyAsync(url, s.T.CompanyId));

    /// <summary>
    /// Tenant with the generic chart, FY2026, Egyptian taxes (14 % VAT on "standard"), a stockable plate (20 in stock at 60),
    /// a repair service, an exclusive EGP price list (plate 100, repair 50) and a customer using it.
    /// </summary>
    private async Task<Shop> NewShopAsync()
    {
        var t = await fixture.NewTenantAsync();
        var s0 = new Shop(t, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
        await Ok(await Send(s0, HttpMethod.Post, "/api/v1/accounting/coa-templates/generic"));
        var le = (await Get(s0, "/api/v1/organization/legal-entities")).EnumerateArray().First().GetProperty("id").GetGuid();
        await Ok(await Send(s0, HttpMethod.Post, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" }));
        await Ok(await Send(s0, HttpMethod.Post, "/api/v1/tax/templates/egypt", new { }));
        var main = (await Get(s0, "/api/v1/organization/warehouses")).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await Ok(await Send(s0, HttpMethod.Post, "/api/v1/catalog/uoms/seed-standard"))).EnumerateArray()
            .ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = (await Ok(await Send(s0, HttpMethod.Post, "/api/v1/catalog/categories",
            new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))).GetProperty("id").GetGuid();

        async Task<Guid> Product(string sku, string type) => (await Ok(await Send(s0, HttpMethod.Post, "/api/v1/catalog/products", new
        {
            sku, nameEn = sku, nameAr = sku, type, baseUomId = uoms["PCS"], categoryId = category, tracking = "None", expiryTracked = false, taxCategory = "standard",
        }))).GetProperty("id").GetGuid();

        var plate = await Product("PLATE", "Stockable");
        var repair = await Product("REPAIR", "Service");
        var list = (await Ok(await Send(s0, HttpMethod.Post, "/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })))
            .GetProperty("id").GetGuid();
        await Ok(await Send(s0, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = plate, uomId = uoms["PCS"], minQuantity = 0m, price = 100m }));
        await Ok(await Send(s0, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = repair, uomId = uoms["PCS"], minQuantity = 0m, price = 50m }));
        var customer = (await Ok(await Send(s0, HttpMethod.Post, "/api/v1/parties/customers",
            new { code = "C1", kind = "Business", nameEn = "Delta Retail", nameAr = "دلتا", currency = "EGP", priceListId = list }))).GetProperty("id").GetGuid();

        var s = new Shop(t, le, main, uoms["PCS"], plate, repair, customer);
        await OpeningStockAsync(s, 20, 60m);
        return s;
    }

    private static async Task OpeningStockAsync(Shop s, decimal quantity, decimal unitCost)
    {
        var doc = await Ok(await Send(s, HttpMethod.Post, "/api/v1/inventory/opening-stock",
            new { warehouseId = s.Main, date = "2026-01-15", lines = new[] { new { productId = s.Plate, uomId = s.Pcs, quantity, unitCost } } }));
        var id = doc.GetProperty("id").GetGuid();
        await Ok(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/submit"));
        await Ok(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/approve"));
        await Ok(await Post(s, $"/api/v1/inventory/opening-stock/{id}/post"));
    }

    private static async Task SetPolicyAsync(Shop s, string section, object values)
    {
        var current = await s.T.Client.GetAsCompanyAsync($"/api/v1/configuration/policies/{section}", s.T.CompanyId);
        await Ok(await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/configuration/policies/{section}", s.T.CompanyId, values, current.Headers.ETag!.Tag));
    }

    private static Task SalesPolicyAsync(Shop s, string timing = "AtInvoice", string action = "RequireApproval") =>
        SetPolicyAsync(s, "sales", new { stockIssueTiming = timing, enableCreditLimits = true, creditLimitAction = action, allowCreatorToApprove = false });

    private static async Task CreditProfileAsync(Shop s, decimal limit, string action)
    {
        var current = await s.T.Client.GetAsCompanyAsync($"/api/v1/parties/customers/{s.Customer}", s.T.CompanyId);
        await Ok(await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/parties/customers/{s.Customer}/credit-profile", s.T.CompanyId,
            new { creditLimit = limit, creditAction = action, onHold = false }, current.Headers.ETag!.Tag));
    }

    private static object Line(Guid product, Guid uom, decimal quantity) => new { productId = product, uomId = uom, quantity };

    private static async Task<JsonElement> OrderAsync(Shop s, params object[] lines)
    {
        var order = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/orders", new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-01", lines }));
        return await Ok(await Post(s, $"/api/v1/sales/orders/{order.GetProperty("id").GetGuid()}/confirm"));
    }

    private static async Task<JsonElement> PostInvoiceAsync(Shop s, object request)
    {
        var draft = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/invoices", request));
        var id = draft.GetProperty("id").GetGuid();
        (await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/submit"))).GetProperty("status").GetString().ShouldBe("Approved");
        return await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/post"));
    }

    private static object FreeInvoice(Shop s, params object[] lines) => new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-02", lines };

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

    private Task<decimal> ValuationAsync(Shop s) => AsModuleAsync(s, sp => sp.GetRequiredService<IStockQueries>().GetValuationTotalAsync(s.LegalEntityId, Ct));

    private async Task<decimal> OpenItemsAsync(Shop s) =>
        (await AsModuleAsync(s, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Customer, s.Customer, cancellationToken: Ct))).Sum(i => i.Sign * i.OpenBase);

    private static async Task<decimal> OnHandAsync(Shop s) =>
        (await Get(s, $"/api/v1/inventory/stock?productId={s.Plate}&warehouseId={s.Main}")).GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("onHand").GetDecimal());

    /// <summary>The three reconciliations every sales flow must keep: balanced books, AR control = open items, inventory GL = valuation.</summary>
    private async Task BooksReconcileAsync(Shop s)
    {
        (await Get(s, $"/api/v1/accounting/reports/trial-balance?legalEntityId={s.LegalEntityId}&from=2026-01-01&to=2026-12-31")).GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        (await RoleAsync(s, PostingRoles.CustomerReceivable)).ShouldBe(await OpenItemsAsync(s));
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(await ValuationAsync(s));
    }

    [Fact]
    public async Task Order_to_cash_posts_revenue_tax_cogs_and_receivables_that_reconcile()
    {
        var s = await NewShopAsync();
        var order = await OrderAsync(s, Line(s.Plate, s.Pcs, 10), Line(s.Repair, s.Pcs, 1));
        order.GetProperty("status").GetString().ShouldBe("Confirmed");
        order.GetProperty("number").GetString().ShouldBe("SO-2026-000001");
        order.GetProperty("grossTotal").GetDecimal().ShouldBe(1197m);

        var invoice = await PostInvoiceAsync(s, new { date = "2026-06-03", salesOrderId = order.GetProperty("id").GetGuid() });
        invoice.GetProperty("status").GetString().ShouldBe("Posted");
        invoice.GetProperty("number").GetString().ShouldBe("INV-2026-000001");
        invoice.GetProperty("netTotal").GetDecimal().ShouldBe(1050m);
        invoice.GetProperty("taxTotal").GetDecimal().ShouldBe(147m);
        invoice.GetProperty("openAmount").GetDecimal().ShouldBe(1197m);

        (await RoleAsync(s, PostingRoles.CustomerReceivable)).ShouldBe(1197m);
        (await RoleAsync(s, PostingRoles.SalesRevenue)).ShouldBe(-1000m);
        (await RoleAsync(s, PostingRoles.ServiceRevenue)).ShouldBe(-50m);
        (await RoleAsync(s, PostingRoles.OutputTax)).ShouldBe(-147m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(600m);
        (await OnHandAsync(s)).ShouldBe(10m);
        await BooksReconcileAsync(s);

        var closed = await Get(s, $"/api/v1/sales/orders/{order.GetProperty("id").GetGuid()}");
        closed.GetProperty("status").GetString().ShouldBe("Closed");

        // The reservation made at confirmation was consumed by the issue: everything left is available.
        var stock = (await Get(s, $"/api/v1/inventory/stock?productId={s.Plate}&warehouseId={s.Main}")).GetProperty("items").EnumerateArray().Single();
        stock.GetProperty("reserved").GetDecimal().ShouldBe(0m);

        var list = await Get(s, $"/api/v1/sales/invoices?status=Posted&customerId={s.Customer}&from=2026-06-01&to=2026-06-30");
        list.GetProperty("totalCount").GetInt32().ShouldBe(1);
        list.GetProperty("grossTotal").GetDecimal().ShouldBe(1197m);
    }

    [Fact]
    public async Task Delivery_driven_policy_issues_stock_at_delivery_and_invoices_revenue_only()
    {
        var s = await NewShopAsync();
        await SalesPolicyAsync(s, timing: "AtDelivery");
        var order = await OrderAsync(s, Line(s.Plate, s.Pcs, 10));
        var orderId = order.GetProperty("id").GetGuid();

        var dn = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/deliveries",
            new { salesOrderId = orderId, date = "2026-06-02", lines = new[] { new { lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 6m } } }));
        var dnId = dn.GetProperty("id").GetGuid();
        await Ok(await Send(s, HttpMethod.Post, $"/api/v1/sales/deliveries/{dnId}/pick", new { lines = Array.Empty<object>() }));
        var posted = await Ok(await Post(s, $"/api/v1/sales/deliveries/{dnId}/post"));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        (await OnHandAsync(s)).ShouldBe(14m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(360m);
        (await Get(s, $"/api/v1/sales/orders/{orderId}")).GetProperty("status").GetString().ShouldBe("PartiallyDelivered");

        // Stock lines cannot be invoiced without a delivery in this mode.
        (await (await Send(s, HttpMethod.Post, "/api/v1/sales/invoices", FreeInvoice(s, Line(s.Plate, s.Pcs, 1)))).ErrorCodeAsync()).ShouldBe("sales.invoice-requires-delivery");

        var invoice = await PostInvoiceAsync(s, new { date = "2026-06-03", deliveryNoteId = dnId });
        invoice.GetProperty("grossTotal").GetDecimal().ShouldBe(684m);
        (await OnHandAsync(s)).ShouldBe(14m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(360m);
        (await RoleAsync(s, PostingRoles.SalesRevenue)).ShouldBe(-600m);
        await BooksReconcileAsync(s);

        // An invoiced delivery cannot be reversed.
        (await (await Post(s, $"/api/v1/sales/deliveries/{dnId}/reverse")).ErrorCodeAsync()).ShouldBe("sales.invalid-state-transition");
    }

    [Fact]
    public async Task Returns_restock_at_the_original_cost_and_credit_the_invoice()
    {
        var s = await NewShopAsync();
        var invoice = await PostInvoiceAsync(s, FreeInvoice(s, Line(s.Plate, s.Pcs, 5)));
        var invoiceId = invoice.GetProperty("id").GetGuid();
        var lineId = invoice.GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(300m);

        // New stock at a higher cost moves the average; the return must still come back at the cost it left with (60).
        await OpeningStockAsync(s, 15, 100m);

        (await (await Send(s, HttpMethod.Post, "/api/v1/sales/returns", new { salesInvoiceId = invoiceId, date = "2026-06-10", reason = "Broken", lines = new[] { new { invoiceLineId = lineId, quantity = 6m } } }))
            .ErrorCodeAsync()).ShouldBe("sales.return-exceeds-invoiced");

        var draft = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/returns",
            new { salesInvoiceId = invoiceId, date = "2026-06-10", reason = "Broken", lines = new[] { new { invoiceLineId = lineId, quantity = 2m } } }));
        var credit = await Ok(await Post(s, $"/api/v1/sales/returns/{draft.GetProperty("id").GetGuid()}/post"));
        credit.GetProperty("number").GetString().ShouldBe("CN-2026-000001");
        credit.GetProperty("grossTotal").GetDecimal().ShouldBe(228m);

        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(180m);
        (await RoleAsync(s, PostingRoles.SalesReturns)).ShouldBe(200m);
        (await RoleAsync(s, PostingRoles.OutputTax)).ShouldBe(-42m);
        (await RoleAsync(s, PostingRoles.CustomerReceivable)).ShouldBe(342m);
        (await OnHandAsync(s)).ShouldBe(32m);
        (await ValuationAsync(s)).ShouldBe(15 * 60m + 1500m + 120m);
        await BooksReconcileAsync(s);

        // Only three remain returnable; an invoice with returns cannot be cancelled.
        (await (await Send(s, HttpMethod.Post, "/api/v1/sales/returns", new { salesInvoiceId = invoiceId, date = "2026-06-10", reason = "More", lines = new[] { new { invoiceLineId = lineId, quantity = 4m } } }))
            .ErrorCodeAsync()).ShouldBe("sales.return-exceeds-invoiced");
        (await (await Post(s, $"/api/v1/sales/invoices/{invoiceId}/cancel")).ErrorCodeAsync()).ShouldBe("sales.invoice-has-returns");
    }

    [Fact]
    public async Task Cancelling_a_posted_invoice_reverses_journal_stock_and_receivable()
    {
        var s = await NewShopAsync();
        var order = await OrderAsync(s, Line(s.Plate, s.Pcs, 4), Line(s.Repair, s.Pcs, 2));
        var orderId = order.GetProperty("id").GetGuid();
        var invoice = await PostInvoiceAsync(s, new { date = "2026-06-03", salesOrderId = orderId });
        (await OnHandAsync(s)).ShouldBe(16m);

        var cancelled = await Ok(await Post(s, $"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/cancel", new { date = "2026-06-04" }));
        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        (await OnHandAsync(s)).ShouldBe(20m);
        foreach (var role in new[] { PostingRoles.CustomerReceivable, PostingRoles.SalesRevenue, PostingRoles.ServiceRevenue, PostingRoles.OutputTax, PostingRoles.CostOfGoodsSold })
        {
            (await RoleAsync(s, role)).ShouldBe(0m, role);
        }

        (await OpenItemsAsync(s)).ShouldBe(0m);
        await BooksReconcileAsync(s);

        // The order is open again and can be invoiced anew.
        (await Get(s, $"/api/v1/sales/orders/{orderId}")).GetProperty("status").GetString().ShouldBe("Confirmed");
        (await PostInvoiceAsync(s, new { date = "2026-06-05", salesOrderId = orderId })).GetProperty("number").GetString().ShouldBe("INV-2026-000002");
    }

    [Fact]
    public async Task Credit_limits_block_warn_or_need_an_approval_engine()
    {
        var s = await NewShopAsync();
        await CreditProfileAsync(s, 500m, "Block");
        var draft = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/orders",
            new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-01", lines = new[] { Line(s.Plate, s.Pcs, 10) } }));
        var id = draft.GetProperty("id").GetGuid();
        (await (await Post(s, $"/api/v1/sales/orders/{id}/confirm")).ErrorCodeAsync()).ShouldBe("sales.credit-limit-exceeded");

        // RequireApproval without the workflow engine cannot be satisfied (documented decision).
        await CreditProfileAsync(s, 500m, "RequireApproval");
        (await (await Post(s, $"/api/v1/sales/orders/{id}/confirm")).ErrorCodeAsync()).ShouldBe("sales.credit-approval-unavailable");

        await CreditProfileAsync(s, 500m, "Warn");
        var confirmed = await Ok(await Post(s, $"/api/v1/sales/orders/{id}/confirm"));
        confirmed.GetProperty("status").GetString().ShouldBe("Confirmed");
        confirmed.GetProperty("warnings").GetArrayLength().ShouldBe(1);

        // Invoices are checked again at posting: the confirmed order already counts towards the exposure.
        await CreditProfileAsync(s, 500m, "Block");
        var free = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/invoices", FreeInvoice(s, Line(s.Repair, s.Pcs, 1))));
        (await (await Post(s, $"/api/v1/sales/invoices/{free.GetProperty("id").GetGuid()}/submit")).ErrorCodeAsync()).ShouldBe("sales.credit-limit-exceeded");
    }

    [Fact]
    public async Task Posting_is_idempotent_per_key_and_refused_twice_otherwise()
    {
        var s = await NewShopAsync();
        var draft = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/invoices", FreeInvoice(s, Line(s.Plate, s.Pcs, 2))));
        var id = draft.GetProperty("id").GetGuid();
        await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/submit"));
        var key = Guid.NewGuid().ToString();
        var first = await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/post", key: key));
        var replay = await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/post", key: key));
        replay.GetProperty("number").GetString().ShouldBe(first.GetProperty("number").GetString());
        (await (await Post(s, $"/api/v1/sales/invoices/{id}/post")).ErrorCodeAsync()).ShouldBe("sales.invalid-state-transition");
        (await OnHandAsync(s)).ShouldBe(18m);
        (await RoleAsync(s, PostingRoles.CustomerReceivable)).ShouldBe(228m);
    }

    [Fact]
    public async Task Receipts_mark_invoices_paid_through_the_settlement_event_and_paid_invoices_cannot_be_cancelled()
    {
        var s = await NewShopAsync();
        var invoice = await PostInvoiceAsync(s, FreeInvoice(s, Line(s.Plate, s.Pcs, 2)));
        var invoiceId = invoice.GetProperty("id").GetGuid();
        var bank = (await Ok(await Send(s, HttpMethod.Post, "/api/v1/finance/bank-accounts",
            new { legalEntityId = s.LegalEntityId, code = "CIB", name = "CIB current", currency = "EGP", bankName = "CIB" }))).GetProperty("id").GetGuid();
        var item = (await AsModuleAsync(s, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Customer, s.Customer, cancellationToken: Ct))).Single();

        async Task<JsonElement> PayAsync(decimal amount)
        {
            var receipt = await Ok(await Send(s, HttpMethod.Post, "/api/v1/finance/receipts", new
            {
                legalEntityId = s.LegalEntityId, partyId = s.Customer, date = "2026-06-15", method = "BankTransfer", cashBankAccountId = bank, amount,
                allocations = new[] { new { openItemId = item.Id, amount } },
            }));
            await Ok(await Post(s, $"/api/v1/finance/receipts/{receipt.GetProperty("id").GetGuid()}/post"));
            var processor = fixture.Factory.Services.GetRequiredService<Erp.Infrastructure.Outbox.IOutboxProcessor>();
            var current = await Get(s, $"/api/v1/sales/invoices/{invoiceId}");
            for (var i = 0; i < 20 && current.GetProperty("openAmount").GetDecimal() == invoice.GetProperty("openAmount").GetDecimal(); i++)
            {
                await processor.ProcessPendingAsync(Ct);
                current = await Get(s, $"/api/v1/sales/invoices/{invoiceId}");
            }

            return current;
        }

        var partial = await PayAsync(100m);
        partial.GetProperty("status").GetString().ShouldBe("PartiallyPaid");
        partial.GetProperty("openAmount").GetDecimal().ShouldBe(128m);
        (await (await Post(s, $"/api/v1/sales/invoices/{invoiceId}/cancel")).ErrorCodeAsync()).ShouldBe("sales.invalid-state-transition");

        invoice = partial;
        var paid = await PayAsync(128m);
        paid.GetProperty("status").GetString().ShouldBe("Paid");
        paid.GetProperty("openAmount").GetDecimal().ShouldBe(0m);
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task Concurrent_invoices_for_the_last_units_never_oversell()
    {
        var s = await NewShopAsync();
        var ids = new List<Guid>();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var draft = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/invoices", FreeInvoice(s, Line(s.Plate, s.Pcs, 15))));
            var id = draft.GetProperty("id").GetGuid();
            await Ok(await Post(s, $"/api/v1/sales/invoices/{id}/submit"));
            ids.Add(id);
        }

        var results = await Task.WhenAll(ids.Select(id => Post(s, $"/api/v1/sales/invoices/{id}/post")));
        results.Count(r => r.IsSuccessStatusCode).ShouldBe(1);
        (await results.Single(r => !r.IsSuccessStatusCode).ErrorCodeAsync()).ShouldBe("inventory.insufficient-stock");
        (await OnHandAsync(s)).ShouldBe(5m);
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task Sales_documents_are_isolated_per_tenant_and_need_the_module()
    {
        var s = await NewShopAsync();
        var invoice = await PostInvoiceAsync(s, FreeInvoice(s, Line(s.Repair, s.Pcs, 1)));
        var other = await fixture.NewTenantAsync();
        (await other.Client.GetAsCompanyAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Ok(await other.Client.GetAsCompanyAsync("/api/v1/sales/invoices", other.CompanyId))).GetProperty("totalCount").GetInt32().ShouldBe(0);

        (await Send(s, HttpMethod.Put, "/api/v1/configuration/modules/sales", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.T.Client.GetAsCompanyAsync("/api/v1/sales/invoices", s.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Quotation_converts_to_an_order_at_the_quoted_prices()
    {
        var s = await NewShopAsync();
        var quote = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/quotations",
            new { customerId = s.Customer, warehouseId = s.Main, date = "2026-06-01", validUntil = "2099-12-31", lines = new[] { Line(s.Plate, s.Pcs, 3) } }));
        var id = quote.GetProperty("id").GetGuid();
        (await Ok(await Post(s, $"/api/v1/sales/quotations/{id}/send"))).GetProperty("number").GetString().ShouldBe("QUO-2026-000001");
        await Ok(await Post(s, $"/api/v1/sales/quotations/{id}/accept"));
        var order = await Ok(await Post(s, $"/api/v1/sales/quotations/{id}/convert", new { date = "2026-06-05" }));
        order.GetProperty("status").GetString().ShouldBe("Draft");
        order.GetProperty("grossTotal").GetDecimal().ShouldBe(342m);
        (await Get(s, $"/api/v1/sales/quotations/{id}")).GetProperty("status").GetString().ShouldBe("Converted");
    }
}
