using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Card/wallet variances posted, cash rounding to its own account, foreign cash tender, offline number ranges and batch sync.</summary>
public class PosCompletionTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Plate, Guid Cup, Guid Terminal, Guid Shift);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(ErpApiFixture.TenantSession t, HttpMethod method, string url, object? body = null, bool idempotent = true) =>
        t.Client.SendAsCompanyAsync(method, url, t.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static Task<HttpResponseMessage> Post(Shop s, string url, object? body = null, bool idempotent = true) => Send(s.T, HttpMethod.Post, url, body, idempotent);

    private static async Task<JsonElement> Get(ErpApiFixture.TenantSession t, string url) => await Ok(await t.Client.GetAsCompanyAsync(url, t.CompanyId));

    /// <summary>EGP with a 0.05 cash increment, Egyptian VAT, a plate (100, 20 in stock at 60), a cup (10.01), terminal T01 with an open shift (float 500).</summary>
    private async Task<Shop> NewShopAsync()
    {
        var t = await fixture.NewTenantAsync();
        await Ok(await Send(t, HttpMethod.Put, "/api/v1/configuration/modules/pos", new { enabled = true }, idempotent: false));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/accounting/coa-templates/generic"));
        var le = (await Get(t, "/api/v1/organization/legal-entities")).EnumerateArray().First().GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" }));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/tax/currencies", new { code = "EGP", nameEn = "Egyptian pound", nameAr = "جنيه", minorUnits = 2, cashRoundingIncrement = 0.05m }));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/tax/currencies", new { code = "USD", nameEn = "US dollar", nameAr = "دولار", minorUnits = 2 }));
        await Ok(await Send(t, HttpMethod.Post, "/api/v1/tax/exchange-rates", new { fromCurrency = "USD", toCurrency = "EGP", type = "Spot", effectiveDate = "2026-05-01", rate = 50m }));
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
        var cup = await Product("CUP", "Service");
        var list = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })))
            .GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = plate, uomId = uoms["PCS"], minQuantity = 0m, price = 100m }));
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = cup, uomId = uoms["PCS"], minQuantity = 0m, price = 10.01m }));

        var doc = await Ok(await Send(t, HttpMethod.Post, "/api/v1/inventory/opening-stock",
            new { warehouseId = main, date = "2026-01-15", lines = new[] { new { productId = plate, uomId = uoms["PCS"], quantity = 20m, unitCost = 60m } } }));
        var docId = doc.GetProperty("id").GetGuid();
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{docId}/submit"));
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{docId}/approve"));
        await Ok(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{docId}/post"));

        var cash = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/finance/cash-accounts", new { legalEntityId = le, code = "DRAWER1", name = "Drawer", currency = "EGP" })))
            .GetProperty("id").GetGuid();
        var terminal = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/pos/terminals",
            new { code = "T01", name = "Till", warehouseId = main, priceListId = list, cashAccountId = cash, maxDiscountPercent = 10m }))).GetProperty("id").GetGuid();
        var shift = (await Ok(await Send(t, HttpMethod.Post, "/api/v1/pos/shifts", new { terminalId = terminal, openingFloat = 500m, businessDate = "2026-06-01" })))
            .GetProperty("id").GetGuid();
        return new Shop(t, le, main, plate, cup, terminal, shift);
    }

    private static object Line(Guid product, decimal quantity) => new { productId = product, quantity };

    private static object Pay(string method, decimal amount, string? currency = null) => new { method, amount, currency };

    private static Task<HttpResponseMessage> SubmitAsync(Shop s, object[] lines, object[] payments) =>
        Post(s, "/api/v1/pos/receipts", new { terminalId = s.Terminal, clientReceiptId = Guid.NewGuid(), lines, payments });

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

    private async Task BooksBalanceAsync(Shop s)
    {
        (await Get(s.T, $"/api/v1/accounting/reports/trial-balance?legalEntityId={s.LegalEntityId}&from=2026-01-01&to=2026-12-31")).GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(await ValuationAsync(s));
    }

    [Fact]
    public async Task Card_and_wallet_variances_are_posted_and_cash_rounding_has_its_own_account()
    {
        var s = await NewShopAsync();

        // 10.01 + 14 % = 11.41, collected in cash as 11.40: the 0.01 goes to the cash rounding account, not to sales discounts.
        var cupSale = await Ok(await SubmitAsync(s, [Line(s.Cup, 1)], [Pay("Cash", 11.40m)]));
        cupSale.GetProperty("roundingAdjustment").GetDecimal().ShouldBe(-0.01m);
        (await RoleAsync(s, PostingRoles.CashRounding)).ShouldBe(0.01m);
        (await RoleAsync(s, PostingRoles.SalesDiscount)).ShouldBe(0m);

        await Ok(await SubmitAsync(s, [Line(s.Plate, 1)], [Pay("Card", 114m)]));
        await Ok(await SubmitAsync(s, [Line(s.Plate, 1)], [Pay("Wallet", 114m)]));
        (await RoleAsync(s, PostingRoles.CardClearing)).ShouldBe(228m);

        // The card settlement slip shows 110 (4 short); the wallet is not counted, so its variance is not posted.
        var closed = await Ok(await Post(s, $"/api/v1/pos/shifts/{s.Shift}/close", new { counts = new[] { Pay("Cash", 511.40m), Pay("Card", 110m) } }));
        closed.GetProperty("cashVariance").GetDecimal().ShouldBe(0m);
        closed.GetProperty("counts").EnumerateArray().Single(c => c.GetProperty("method").GetString() == "Card").GetProperty("variance").GetDecimal().ShouldBe(-4m);
        closed.GetProperty("varianceJournalId").ValueKind.ShouldBe(JsonValueKind.String);
        (await RoleAsync(s, PostingRoles.CashOverShort)).ShouldBe(4m);
        (await RoleAsync(s, PostingRoles.CardClearing)).ShouldBe(224m);
        await BooksBalanceAsync(s);
    }

    [Fact]
    public async Task Foreign_cash_is_accepted_at_the_day_rate_with_change_in_the_terminal_currency()
    {
        var s = await NewShopAsync();
        var receipt = await Ok(await SubmitAsync(s, [Line(s.Plate, 1)], [Pay("Cash", 3m, "USD")]));
        receipt.GetProperty("tendered").GetDecimal().ShouldBe(150m);
        receipt.GetProperty("change").GetDecimal().ShouldBe(36m);
        var payment = receipt.GetProperty("payments")[0];
        payment.GetProperty("foreignCurrency").GetString().ShouldBe("USD");
        payment.GetProperty("foreignAmount").GetDecimal().ShouldBe(3m);
        payment.GetProperty("exchangeRate").GetDecimal().ShouldBe(50m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(114m);

        (await (await SubmitAsync(s, [Line(s.Plate, 1)], [Pay("Card", 3m, "USD")])).ErrorCodeAsync()).ShouldBe("pos.foreign-currency-cash-only");
        (await (await SubmitAsync(s, [Line(s.Plate, 1)], [Pay("Cash", 3m, "EUR")])).ErrorCodeAsync()).ShouldBe("pos.rate-missing");

        var x = await Get(s.T, $"/api/v1/pos/shifts/{s.Shift}/x-report");
        var usd = x.GetProperty("foreignCash").EnumerateArray().Single();
        usd.GetProperty("currency").GetString().ShouldBe("USD");
        usd.GetProperty("amount").GetDecimal().ShouldBe(3m);
        usd.GetProperty("terminalCurrencyAmount").GetDecimal().ShouldBe(150m);
        await BooksBalanceAsync(s);
    }

    [Fact]
    public async Task Offline_receipts_sync_in_a_batch_with_per_receipt_results()
    {
        var s = await NewShopAsync();
        var range = await Ok(await Post(s, $"/api/v1/pos/terminals/{s.Terminal}/offline-range", new { size = 5 }));
        range.GetProperty("prefix").GetString().ShouldBe("OFF-T01-");
        range.GetProperty("from").GetInt64().ShouldBe(1);
        range.GetProperty("to").GetInt64().ShouldBe(5);

        var first = Guid.NewGuid();
        object Offline(Guid id, string number, decimal quantity, decimal? expectedGross, decimal cash) => new
        {
            clientReceiptId = id, offlineNumber = number, createdAtUtc = "2026-06-01T10:00:00Z", expectedGross,
            lines = new[] { Line(s.Plate, quantity) }, payments = new[] { Pay("Cash", cash) },
        };

        var sync = await Ok(await Post(s, $"/api/v1/pos/terminals/{s.Terminal}/offline-sync", new
        {
            receipts = new[]
            {
                Offline(first, "OFF-T01-1", 1, 114m, 114m),
                Offline(Guid.NewGuid(), "OFF-T01-2", 1, 100m, 120m),        // the device had an old price
                Offline(Guid.NewGuid(), "OFF-T01-3", 100, 11400m, 11400m),  // more than the 20 in stock (negative stock not allowed)
                Offline(Guid.NewGuid(), "OFF-T01-99", 1, 114m, 114m),       // not an allocated number
            },
        }, idempotent: false));
        sync.GetProperty("accepted").GetInt32().ShouldBe(2);
        sync.GetProperty("rejected").GetInt32().ShouldBe(2);
        var results = sync.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("offlineNumber").GetString()!);
        results["OFF-T01-1"].GetProperty("receipt").GetProperty("number").GetString().ShouldBe("OFF-T01-1");
        results["OFF-T01-1"].GetProperty("receipt").GetProperty("isOffline").GetBoolean().ShouldBeTrue();
        results["OFF-T01-2"].GetProperty("status").GetString().ShouldBe("Accepted");
        results["OFF-T01-2"].GetProperty("conflicts")[0].GetProperty("code").GetString().ShouldBe("pos.price-changed");
        results["OFF-T01-3"].GetProperty("status").GetString().ShouldBe("Rejected");
        results["OFF-T01-3"].GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldContain("inventory.insufficient-stock");
        results["OFF-T01-99"].GetProperty("conflicts")[0].GetProperty("code").GetString().ShouldBe("pos.offline-number-invalid");
        (await OnHandAsync(s)).ShouldBe(18m);

        // Resending is safe: the receipt that was accepted replays, nothing is posted twice.
        var again = await Ok(await Post(s, $"/api/v1/pos/terminals/{s.Terminal}/offline-sync", new { receipts = new[] { Offline(first, "OFF-T01-1", 1, 114m, 114m) } },
            idempotent: false));
        again.GetProperty("replayed").GetInt32().ShouldBe(1);
        (await OnHandAsync(s)).ShouldBe(18m);
        (await RoleAsync(s, PostingRoles.CashAccount)).ShouldBe(228m);
        await BooksBalanceAsync(s);
    }
}
