using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class PosTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Pcs, Guid Plate, Guid Repair, Guid PriceList, Guid Terminal, Guid Shift);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(ErpApiFixture.TenantSession t, HttpMethod method, string url, object? body = null, bool idempotent = true) =>
        t.Client.SendAsCompanyAsync(method, url, t.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static Task<HttpResponseMessage> Post(Shop s, string url, object? body = null) => Send(s.T, HttpMethod.Post, url, body);

    private static async Task<JsonElement> Get(ErpApiFixture.TenantSession t, string url) => await Ok(await t.Client.GetAsCompanyAsync(url, t.CompanyId));

    /// <summary>
    /// Tenant with POS enabled, the generic chart, FY2026, Egyptian VAT (14 %), a stockable plate (20 in stock at 60), a repair
    /// service, an exclusive EGP price list (plate 100, repair 50), terminal T01 on MAIN with a drawer and an open shift (float 500).
    /// </summary>
    private async Task<Shop> NewShopAsync()
    {
        var t = await fixture.NewTenantAsync();
        await Ok(await Send(t, HttpMethod.Put, "/api/v1/configuration/modules/pos", new { enabled = true }, idempotent: false));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/accounting/coa-templates/generic"));
        var le = (await Get(t, "/api/v1/organization/legal-entities")).EnumerateArray().First().GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" }));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/tax/templates/egypt", new { }));
        var main = (await Get(t, "/api/v1/organization/warehouses")).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/uoms/seed-standard"))).EnumerateArray()
            .ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/categories",
            new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))).GetProperty("id").GetGuid();

        async Task<Guid> Product(string sku, string type) => (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/products", new
        {
            sku, nameEn = sku, nameAr = sku, type, baseUomId = uoms["PCS"], categoryId = category, tracking = "None", expiryTracked = false, taxCategory = "standard",
        }))).GetProperty("id").GetGuid();

        var plate = await Product("PLATE", "Stockable");
        var repair = await Product("REPAIR", "Service");
        var list = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })))
            .GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = plate, uomId = uoms["PCS"], minQuantity = 0m, price = 100m }));
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = repair, uomId = uoms["PCS"], minQuantity = 0m, price = 50m }));

        var s = new Shop(t, le, main, uoms["PCS"], plate, repair, list, Guid.Empty, Guid.Empty);
        await OpeningStockAsync(s, 20, 60m);
        var terminal = await TerminalAsync(s, "T01", "DRAWER1");
        var shift = await OpenShiftAsync(s, terminal);
        return s with { Terminal = terminal, Shift = shift };
    }

    private static async Task<Guid> TerminalAsync(Shop s, string code, string drawer, decimal maxDiscount = 10m)
    {
        var cash = (await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/finance/cash-accounts",
            new { legalEntityId = s.LegalEntityId, code = drawer, name = drawer, currency = "EGP" }))).GetProperty("id").GetGuid();
        var terminal = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/pos/terminals",
            new { code, name = $"Till {code}", warehouseId = s.Main, priceListId = s.PriceList, cashAccountId = cash, maxDiscountPercent = maxDiscount }));
        terminal.GetProperty("currency").GetString().ShouldBe("EGP");
        return terminal.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> OpenShiftAsync(Shop s, Guid terminal, decimal openingFloat = 500m) =>
        (await Ok(await Post(s, "/api/v1/pos/shifts", new { terminalId = terminal, openingFloat, businessDate = "2026-06-01" }))).GetProperty("id").GetGuid();

    private static async Task OpeningStockAsync(Shop s, decimal quantity, decimal unitCost)
    {
        var doc = await Ok(await Send(s.T, HttpMethod.Post, "/api/v1/inventory/opening-stock",
            new { warehouseId = s.Main, date = "2026-01-15", lines = new[] { new { productId = s.Plate, uomId = s.Pcs, quantity, unitCost } } }));
        var id = doc.GetProperty("id").GetGuid();
        await Ok(await Send(s.T, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/submit"));
        await Ok(await Send(s.T, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/approve"));
        await Ok(await Post(s, $"/api/v1/inventory/opening-stock/{id}/post"));
    }

    private static object Line(Guid product, decimal quantity, decimal discountPercent = 0) => new { productId = product, quantity, discountPercent };

    private static object Pay(string method, decimal amount) => new { method, amount };

    private static Task<HttpResponseMessage> SubmitAsync(Shop s, Guid terminal, Guid clientReceiptId, object[] lines, object[] payments, string? priceVersion = null,
        decimal? expectedGross = null) =>
        Post(s, "/api/v1/pos/receipts", new { terminalId = terminal, clientReceiptId, priceVersion, expectedGross, lines, payments });

    private static async Task<JsonElement> SellAsync(Shop s, object[] lines, object[] payments, Guid? terminal = null) =>
        await Ok(await SubmitAsync(s, terminal ?? s.Terminal, Guid.NewGuid(), lines, payments));

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

    private static async Task<decimal> OnHandAsync(Shop s) =>
        (await Get(s.T, $"/api/v1/inventory/stock?productId={s.Plate}&warehouseId={s.Main}")).GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("onHand").GetDecimal());

    /// <summary>Balanced books, no receivables from POS, inventory GL = valuation.</summary>
    private async Task BooksReconcileAsync(Shop s)
    {
        (await Get(s.T, $"/api/v1/accounting/reports/trial-balance?legalEntityId={s.LegalEntityId}&from=2026-01-01&to=2026-12-31")).GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        (await RoleAsync(s, PostingRoles.CustomerReceivable)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(await ValuationAsync(s));
    }

    [Fact]
    public async Task A_cash_sale_posts_stock_revenue_tax_and_cash_atomically_without_receivables()
    {
        var s = await NewShopAsync();
        var receipt = await SellAsync(s, [Line(s.Plate, 2), Line(s.Repair, 1)], [Pay("Cash", 300m)]);
        receipt.GetProperty("number").GetString().ShouldBe("POS-T01-2026-000001");
        receipt.GetProperty("kind").GetString().ShouldBe("Sale");
        receipt.GetProperty("status").GetString().ShouldBe("Completed");
        receipt.GetProperty("grossTotal").GetDecimal().ShouldBe(285m);
        receipt.GetProperty("change").GetDecimal().ShouldBe(15m);
        receipt.GetProperty("businessDate").GetString().ShouldBe("2026-06-01");
        receipt.GetProperty("lines")[0].GetProperty("costAmount").GetDecimal().ShouldBe(120m);

        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(285m);
        (await RoleAsync(s, PostingRoles.SalesRevenue)).ShouldBe(-200m);
        (await RoleAsync(s, PostingRoles.ServiceRevenue)).ShouldBe(-50m);
        (await RoleAsync(s, PostingRoles.OutputTax)).ShouldBe(-35m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(120m);
        (await OnHandAsync(s)).ShouldBe(18m);
        await BooksReconcileAsync(s);

        // Split payment: change only from cash; non-cash may not exceed the amount due; a card goes to the bank clearing role.
        var split = await SellAsync(s, [Line(s.Plate, 1)], [Pay("Card", 100m), Pay("Cash", 20m)]);
        split.GetProperty("change").GetDecimal().ShouldBe(6m);
        (await RoleAsync(s, PostingRoles.BankAccount)).ShouldBe(100m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(299m);
        (await (await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 1)], [Pay("Card", 120m)])).ErrorCodeAsync()).ShouldBe("pos.overpaid-non-cash");
        (await (await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 1)], [Pay("Cash", 100m)])).ErrorCodeAsync()).ShouldBe("pos.underpaid");

        // Discounts: within the terminal maximum (10 %) only.
        var discounted = await SellAsync(s, [Line(s.Plate, 1, 10m)], [Pay("Cash", 102.60m)]);
        discounted.GetProperty("netTotal").GetDecimal().ShouldBe(90m);
        (await (await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 1, 15m)], [Pay("Cash", 200m)])).ErrorCodeAsync()).ShouldBe("pos.discount-exceeds-limit");

        var x = await Get(s.T, $"/api/v1/pos/shifts/{s.Shift}/x-report");
        x.GetProperty("reportType").GetString().ShouldBe("X");
        x.GetProperty("salesCount").GetInt32().ShouldBe(3);
        x.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("method").GetString() == "Cash").GetProperty("expected").GetDecimal().ShouldBe(500m + 285m + 14m + 102.60m);
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task S9_resubmitting_the_same_client_receipt_id_posts_once()
    {
        var s = await NewShopAsync();
        var clientId = Guid.NewGuid();
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            // A different Idempotency-Key each time: the terminal's ClientReceiptId alone guarantees a single posting.
            responses.Add(await SubmitAsync(s, s.Terminal, clientId, [Line(s.Plate, 1)], [Pay("Cash", 114m)]));
        }

        responses[0].StatusCode.ShouldBe(HttpStatusCode.Created);
        responses[1].StatusCode.ShouldBe(HttpStatusCode.OK);
        responses[2].StatusCode.ShouldBe(HttpStatusCode.OK);
        var bodies = new List<JsonElement>();
        foreach (var response in responses)
        {
            bodies.Add(await Ok(response));
        }

        bodies.Select(b => b.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
        bodies.Select(b => b.GetProperty("number").GetString()).Distinct().Single().ShouldBe("POS-T01-2026-000001");
        bodies[2].GetProperty("replayed").GetBoolean().ShouldBeTrue();

        (await Get(s.T, $"/api/v1/pos/shifts/{s.Shift}/receipts")).GetArrayLength().ShouldBe(1);
        var movements = await AsModuleAsync(s, sp => sp.GetRequiredService<IStockQueries>().GetMovementsBySourceAsync("pos.receipt", bodies[0].GetProperty("id").GetGuid(), Ct));
        movements.Count.ShouldBe(1);
        (await OnHandAsync(s)).ShouldBe(19m);
        (await RoleAsync(s, PostingRoles.SalesRevenue)).ShouldBe(-100m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(114m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(60m);

        // The next receipt takes the next number: nothing was consumed by the replays.
        (await SellAsync(s, [Line(s.Repair, 1)], [Pay("Cash", 57m)])).GetProperty("number").GetString().ShouldBe("POS-T01-2026-000002");
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task Refunds_restock_at_the_original_cost_and_reverse_proportionally()
    {
        var s = await NewShopAsync();
        var sale = await SellAsync(s, [Line(s.Plate, 5)], [Pay("Cash", 570m)]);
        var saleId = sale.GetProperty("id").GetGuid();
        var lineId = sale.GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(300m);

        // New stock at a higher cost moves the average; the refund must still come back at the cost it left with (60).
        await OpeningStockAsync(s, 15, 100m);

        (await (await Post(s, $"/api/v1/pos/receipts/{saleId}/refund", new { clientReceiptId = Guid.NewGuid(), lines = new[] { new { lineId, quantity = 6m } } }))
            .ErrorCodeAsync()).ShouldBe("pos.refund-exceeds-sold");

        var refundClientId = Guid.NewGuid();
        var refund = await Ok(await Post(s, $"/api/v1/pos/receipts/{saleId}/refund", new { clientReceiptId = refundClientId, lines = new[] { new { lineId, quantity = 2m } } }));
        refund.GetProperty("kind").GetString().ShouldBe("Refund");
        refund.GetProperty("number").GetString().ShouldBe("PRF-T01-2026-000001");
        refund.GetProperty("grossTotal").GetDecimal().ShouldBe(228m);
        refund.GetProperty("originalReceiptId").GetGuid().ShouldBe(saleId);
        refund.GetProperty("lines")[0].GetProperty("costAmount").GetDecimal().ShouldBe(120m);

        // Resending the refund is idempotent too.
        var again = await Ok(await Post(s, $"/api/v1/pos/receipts/{saleId}/refund", new { clientReceiptId = refundClientId, lines = new[] { new { lineId, quantity = 2m } } }));
        again.GetProperty("id").GetGuid().ShouldBe(refund.GetProperty("id").GetGuid());

        (await Get(s.T, $"/api/v1/pos/receipts/{saleId}")).GetProperty("status").GetString().ShouldBe("PartiallyRefunded");
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(180m);
        (await RoleAsync(s, PostingRoles.SalesReturns)).ShouldBe(200m);
        (await RoleAsync(s, PostingRoles.OutputTax)).ShouldBe(-42m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(342m);
        (await OnHandAsync(s)).ShouldBe(32m);
        (await ValuationAsync(s)).ShouldBe(15 * 60m + 1500m + 120m);
        await BooksReconcileAsync(s);

        // Only three remain refundable; refunding them all marks the sale Refunded.
        (await (await Post(s, $"/api/v1/pos/receipts/{saleId}/refund", new { clientReceiptId = Guid.NewGuid(), lines = new[] { new { lineId, quantity = 4m } } }))
            .ErrorCodeAsync()).ShouldBe("pos.refund-exceeds-sold");
        await Ok(await Post(s, $"/api/v1/pos/receipts/{saleId}/refund", new { clientReceiptId = Guid.NewGuid(), lines = new[] { new { lineId, quantity = 3m } } }));
        (await Get(s.T, $"/api/v1/pos/receipts/{saleId}")).GetProperty("status").GetString().ShouldBe("Refunded");
        (await RoleAsync(s, PostingRoles.SalesReturns)).ShouldBe(500m);
        (await RoleAsync(s, PostingRoles.OutputTax)).ShouldBe(0m);
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task J11_closing_a_shift_20_short_posts_the_over_short_journal_and_the_z_report()
    {
        var s = await NewShopAsync();
        await SellAsync(s, [Line(s.Plate, 1)], [Pay("Cash", 200m)]);
        await SellAsync(s, [Line(s.Repair, 2)], [Pay("Card", 114m)]);
        await Ok(await Post(s, $"/api/v1/pos/shifts/{s.Shift}/cash-movements", new { kind = "PayOut", amount = 100m, reason = "Safe drop" }));
        await Ok(await Post(s, $"/api/v1/pos/shifts/{s.Shift}/cash-movements", new { kind = "PayIn", amount = 50m, reason = "Coins" }));

        (await (await s.T.Client.GetAsCompanyAsync($"/api/v1/pos/shifts/{s.Shift}/z-report", s.T.CompanyId)).ErrorCodeAsync()).ShouldBe("pos.shift-not-closed");
        (await Ok(await Post(s, $"/api/v1/pos/shifts/{s.Shift}/start-close"))).GetProperty("status").GetString().ShouldBe("Closing");
        (await (await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 1)], [Pay("Cash", 114m)])).ErrorCodeAsync()).ShouldBe("pos.no-open-shift");

        // Expected cash: 500 float + 114 sale − 100 pay-out + 50 pay-in = 564; counted 544 → 20 short.
        var closed = await Ok(await Post(s, $"/api/v1/pos/shifts/{s.Shift}/close", new { counts = new[] { Pay("Cash", 544m), Pay("Card", 114m) } }));
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("cashVariance").GetDecimal().ShouldBe(-20m);
        closed.GetProperty("varianceJournalId").ValueKind.ShouldBe(JsonValueKind.String);

        (await RoleAsync(s, PostingRoles.CashOverShort)).ShouldBe(20m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(114m - 20m);
        (await RoleAsync(s, PostingRoles.BankAccount)).ShouldBe(114m);

        var z = await Get(s.T, $"/api/v1/pos/shifts/{s.Shift}/z-report");
        z.GetProperty("reportType").GetString().ShouldBe("Z");
        z.GetProperty("salesCount").GetInt32().ShouldBe(2);
        z.GetProperty("grossSales").GetDecimal().ShouldBe(228m);
        var cash = z.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("method").GetString() == "Cash");
        cash.GetProperty("expected").GetDecimal().ShouldBe(564m);
        cash.GetProperty("counted").GetDecimal().ShouldBe(544m);
        cash.GetProperty("variance").GetDecimal().ShouldBe(-20m);
        z.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("method").GetString() == "Card").GetProperty("variance").GetDecimal().ShouldBe(0m);

        (await (await Post(s, $"/api/v1/pos/shifts/{s.Shift}/close", new { counts = Array.Empty<object>() })).ErrorCodeAsync()).ShouldBe("pos.invalid-state-transition");

        // A new shift can open once the previous one is closed; only one non-closed shift per terminal.
        var next = await OpenShiftAsync(s, s.Terminal, 300m);
        (await (await Post(s, "/api/v1/pos/shifts", new { terminalId = s.Terminal, openingFloat = 0m })).ErrorCodeAsync()).ShouldBe("pos.shift-already-open");
        (await Get(s.T, $"/api/v1/pos/shifts/{next}")).GetProperty("number").GetString().ShouldBe("SH-T01-2026-000002");
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task Receipts_on_two_terminals_post_concurrently_with_numbers_per_terminal()
    {
        var s = await NewShopAsync();
        var t2 = await TerminalAsync(s, "T02", "DRAWER2");
        await OpenShiftAsync(s, t2);

        var tasks = Enumerable.Range(0, 4).SelectMany(_ => new[] { s.Terminal, t2 })
            .Select(terminal => SubmitAsync(s, terminal, Guid.NewGuid(), [Line(s.Plate, 1)], [Pay("Cash", 114m)])).ToList();
        var responses = await Task.WhenAll(tasks);
        var receipts = new List<JsonElement>();
        foreach (var response in responses)
        {
            receipts.Add(await Ok(response));
        }

        receipts.Where(r => r.GetProperty("terminalId").GetGuid() == s.Terminal).Select(r => r.GetProperty("number").GetString()).Order()
            .ShouldBe(["POS-T01-2026-000001", "POS-T01-2026-000002", "POS-T01-2026-000003", "POS-T01-2026-000004"]);
        receipts.Where(r => r.GetProperty("terminalId").GetGuid() == t2).Select(r => r.GetProperty("number").GetString()).Order()
            .ShouldBe(["POS-T02-2026-000001", "POS-T02-2026-000002", "POS-T02-2026-000003", "POS-T02-2026-000004"]);
        (await OnHandAsync(s)).ShouldBe(12m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(480m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(912m);
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task A_stale_price_version_returns_the_repriced_cart_unless_the_totals_still_match()
    {
        var s = await NewShopAsync();
        var snapshot = await Get(s.T, $"/api/v1/pos/terminals/{s.Terminal}/catalog-snapshot?date=2026-06-01");
        var version = snapshot.GetProperty("priceVersion").GetString();
        snapshot.GetProperty("prices").GetArrayLength().ShouldBe(2);
        snapshot.GetProperty("products").GetArrayLength().ShouldBe(2);

        // A quantity break at 5 changes the price list (and its version).
        await Ok(await Send(s.T, HttpMethod.Post, $"/api/v1/catalog/price-lists/{s.PriceList}/items", new { productId = s.Plate, uomId = s.Pcs, minQuantity = 5m, price = 90m }));
        var newVersion = (await Get(s.T, $"/api/v1/pos/terminals/{s.Terminal}/catalog-snapshot?date=2026-06-01")).GetProperty("priceVersion").GetString();
        newVersion.ShouldNotBe(version);

        // Totals unaffected by the change: accepted with the server prices.
        (await Ok(await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 1)], [Pay("Cash", 114m)], version, 114m))).GetProperty("grossTotal").GetDecimal().ShouldBe(114m);

        var stale = await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 5)], [Pay("Cash", 570m)], version, 570m);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("pos.price-version-stale");
        problem.GetProperty("repriced").GetProperty("grossTotal").GetDecimal().ShouldBe(513m);
        problem.GetProperty("repriced").GetProperty("priceVersion").GetString().ShouldBe(newVersion);
        (await OnHandAsync(s)).ShouldBe(19m);

        (await Ok(await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Plate, 5)], [Pay("Cash", 513m)], newVersion, 513m))).GetProperty("grossTotal").GetDecimal().ShouldBe(513m);
        await BooksReconcileAsync(s);
    }

    [Fact]
    public async Task A_registered_device_must_present_its_key()
    {
        var s = await NewShopAsync();
        var registration = await Ok(await Post(s, $"/api/v1/pos/terminals/{s.Terminal}/register-device"));
        var key = registration.GetProperty("deviceKey").GetString()!;
        (await Get(s.T, $"/api/v1/pos/terminals/{s.Terminal}")).GetProperty("deviceRegistered").GetBoolean().ShouldBeTrue();

        var refused = await SubmitAsync(s, s.Terminal, Guid.NewGuid(), [Line(s.Repair, 1)], [Pay("Cash", 57m)]);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ErrorCodeAsync()).ShouldBe("pos.device-key-invalid");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/receipts").WithCompany(s.T.CompanyId);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Pos-Device-Key", key);
        request.Content = JsonContent.Create(new { terminalId = s.Terminal, clientReceiptId = Guid.NewGuid(), lines = new[] { Line(s.Repair, 1) }, payments = new[] { Pay("Cash", 57m) } });
        (await s.T.Client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Pos_data_is_isolated_per_tenant_and_needs_the_module()
    {
        var s = await NewShopAsync();
        var receipt = await SellAsync(s, [Line(s.Repair, 1)], [Pay("Cash", 57m)]);
        var other = await fixture.NewTenantAsync();
        await Ok(await Send(other, HttpMethod.Put, "/api/v1/configuration/modules/pos", new { enabled = true }, idempotent: false));
        (await other.Client.GetAsCompanyAsync($"/api/v1/pos/receipts/{receipt.GetProperty("id").GetGuid()}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Client.GetAsCompanyAsync($"/api/v1/pos/shifts/{s.Shift}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Get(other, "/api/v1/pos/terminals")).GetArrayLength().ShouldBe(0);
        (await (await SubmitAsync(s with { T = other }, s.Terminal, Guid.NewGuid(), [Line(s.Repair, 1)], [Pay("Cash", 57m)])).ErrorCodeAsync()).ShouldBe("pos.terminal-not-found");

        (await Send(s.T, HttpMethod.Put, "/api/v1/configuration/modules/pos", new { enabled = false }, idempotent: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.T.Client.GetAsCompanyAsync("/api/v1/pos/terminals", s.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
