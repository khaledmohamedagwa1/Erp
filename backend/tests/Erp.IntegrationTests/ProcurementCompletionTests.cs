using System.Net;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Receipts valued at the receipt-date rate (FX split at invoice) and returns of goods invoiced before receipt.</summary>
public class ProcurementCompletionTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Warehouse, Guid Pcs, Guid Product, Guid SupplierId);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Shop s, HttpMethod method, string url, object? body = null, bool idempotent = false) =>
        s.T.Client.SendAsCompanyAsync(method, url, s.T.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private async Task<Shop> NewShopAsync(string supplierCurrency)
    {
        var t = await fixture.NewTenantAsync();
        async Task<JsonElement> Post(string url, object? body = null) => await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, url, t.CompanyId, body ?? new { }));
        await Post("/api/v1/accounting/coa-templates/generic");
        var le = (await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId))).EnumerateArray().First().GetProperty("id").GetGuid();
        await Post("/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" });
        await Post("/api/v1/tax/templates/egypt");
        foreach (var (code, name) in new[] { ("EGP", "Egyptian pound"), ("USD", "US dollar") })
        {
            await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/currencies", t.CompanyId, new { code, nameEn = name, nameAr = name, minorUnits = 2 });
        }

        foreach (var (date, rate) in new[] { ("2026-04-01", 50m), ("2026-04-05", 52m), ("2026-04-15", 55m) })
        {
            await Post("/api/v1/tax/exchange-rates", new { fromCurrency = "USD", toCurrency = "EGP", type = "Spot", effectiveDate = date, rate });
        }

        var warehouses = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId));
        var main = warehouses.GetProperty("items").EnumerateArray().First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await Post("/api/v1/catalog/uoms/seed-standard")).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = (await Post("/api/v1/catalog/categories", new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .GetProperty("id").GetGuid();
        var product = (await Post("/api/v1/catalog/products", new { sku = "WIDGET", nameEn = "Widget", nameAr = "قطعة", type = "Stockable", baseUomId = uoms["PCS"], categoryId = category }))
            .GetProperty("id").GetGuid();
        var supplier = (await Post("/api/v1/parties/suppliers", new { code = "S1", nameEn = "Global Supplies", nameAr = "عالمية", currency = supplierCurrency })).GetProperty("id").GetGuid();
        return new Shop(t, le, main, uoms["PCS"], product, supplier);
    }

    private static async Task<JsonElement> OrderAsync(Shop s, decimal quantity, decimal price, string currency)
    {
        var order = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/orders", new
        {
            legalEntityId = s.LegalEntityId, warehouseId = s.Warehouse, supplierId = s.SupplierId, date = "2026-04-01", currency,
            lines = new[] { new { productId = s.Product, uomId = s.Pcs, quantity, unitPrice = price, discountPercent = 0m } },
        }));
        return await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/orders/{order.GetProperty("id").GetGuid()}/submit"));
    }

    private static async Task<JsonElement> ReceiveAsync(Shop s, JsonElement order, decimal quantity, string date)
    {
        var receipt = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/receipts", new
        {
            purchaseOrderId = order.GetProperty("id").GetGuid(), date,
            lines = new[] { new { purchaseOrderLineId = order.GetProperty("lines")[0].GetProperty("lineId").GetGuid(), receivedQuantity = quantity } },
        }));
        var id = receipt.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/receipts/{id}/approve"));
        return await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/receipts/{id}/post", idempotent: true));
    }

    private static async Task<JsonElement> InvoiceAsync(Shop s, string number, string currency, params object[] lines)
    {
        var invoice = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = number, invoiceDate = "2026-04-15", currency, lines,
        }));
        var id = invoice.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{id}/submit"));
        return await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{id}/post", idempotent: true));
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

    private Task<decimal> ValuationAsync(Shop s) => AsModuleAsync(s, sp => sp.GetRequiredService<IStockQueries>().GetValuationTotalAsync(s.LegalEntityId, Ct));

    [Fact]
    public async Task Goods_are_valued_at_the_receipt_date_rate_and_the_invoice_rate_difference_is_realised_fx()
    {
        var s = await NewShopAsync("USD");
        var order = await OrderAsync(s, 10, 10m, "USD");
        order.GetProperty("exchangeRate").GetDecimal().ShouldBe(50m);

        // Received when USD = 52 EGP: inventory at 10 × 10 × 52, not at the order's 50.
        var receipt = await ReceiveAsync(s, order, 10, "2026-04-05");
        receipt.GetProperty("exchangeRate").GetDecimal().ShouldBe(52m);
        receipt.GetProperty("totalBaseValue").GetDecimal().ShouldBe(5200m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(-5200m);

        // Invoiced when USD = 55 at 10.10 (+1 %): 101 USD = 5,555 EGP. Price difference at the receipt rate (52) goes to stock;
        // the rate movement 52 → 55 on 101 USD (303) is a realised FX loss.
        var invoice = await InvoiceAsync(s, "USD-1", "USD", new
        {
            kind = "Receipt", goodsReceiptId = receipt.GetProperty("id").GetGuid(), goodsReceiptLineId = receipt.GetProperty("lines")[0].GetProperty("lineId").GetGuid(),
            quantity = 10m, unitPrice = 10.10m,
        });
        invoice.GetProperty("status").GetString().ShouldBe("Posted");
        invoice.GetProperty("lines")[0].GetProperty("fxDifference").GetDecimal().ShouldBe(303m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.RealizedFxLoss)).ShouldBe(303m);
        (await RoleAsync(s, PostingRoles.PurchasePriceVariance)).ShouldBe(0m);
        (await ValuationAsync(s)).ShouldBe(5252m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(5252m);
    }

    [Fact]
    public async Task The_order_rate_can_be_kept_by_policy()
    {
        var s = await NewShopAsync("USD");
        var policy = await s.T.Client.GetAsCompanyAsync("/api/v1/configuration/policies/procurement", s.T.CompanyId);
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/procurement", s.T.CompanyId,
            new { receiptBeforeInvoice = true, priceTolerancePercent = 2m, quantityTolerancePercent = 0m, receiptValuationRate = "OrderRate" }, policy.Headers.ETag!.Tag))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var receipt = await ReceiveAsync(s, await OrderAsync(s, 10, 10m, "USD"), 10, "2026-04-05");
        receipt.GetProperty("totalBaseValue").GetDecimal().ShouldBe(5000m);
    }

    [Fact]
    public async Task Goods_invoiced_before_receipt_can_be_returned_with_a_debit_note_at_the_invoiced_terms()
    {
        var s = await NewShopAsync("EGP");
        var policy = await s.T.Client.GetAsCompanyAsync("/api/v1/configuration/policies/procurement", s.T.CompanyId);
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/procurement", s.T.CompanyId,
            new { receiptBeforeInvoice = false, priceTolerancePercent = 2m, quantityTolerancePercent = 0m }, policy.Headers.ETag!.Tag)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var order = await OrderAsync(s, 10, 10m, "EGP");
        var invoice = await InvoiceAsync(s, "B-1", "EGP", new
        {
            kind = "Order", purchaseOrderId = order.GetProperty("id").GetGuid(), purchaseOrderLineId = order.GetProperty("lines")[0].GetProperty("lineId").GetGuid(),
            quantity = 10m, unitPrice = 10.10m,
        });
        var payableAfterInvoice = await RoleAsync(s, PostingRoles.SupplierPayable);
        var receipt = await ReceiveAsync(s, order, 10, "2026-04-20");
        (await ValuationAsync(s)).ShouldBe(101m);

        var draft = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/returns", new
        {
            goodsReceiptId = receipt.GetProperty("id").GetGuid(), date = "2026-04-25", reason = "Damaged",
            lines = new[] { new { goodsReceiptLineId = receipt.GetProperty("lines")[0].GetProperty("lineId").GetGuid(), quantity = 4m } },
        }));
        var id = draft.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/returns/{id}/approve"));
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/returns/{id}/post", idempotent: true));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("lines")[0].GetProperty("invoicedQuantity").GetDecimal().ShouldBe(4m);
        posted.GetProperty("totalCost").GetDecimal().ShouldBe(40.40m); // the stock was received at the invoiced price

        var invoiceGross = invoice.GetProperty("grandTotal").GetDecimal();
        var debitNote = posted.GetProperty("debitNoteTotal").GetDecimal();
        debitNote.ShouldBe(Math.Round(invoiceGross * 4m / 10m, 2));
        (await ValuationAsync(s)).ShouldBe(60.60m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(60.60m);
        (await RoleAsync(s, PostingRoles.GoodsInvoicedNotReceived)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.PurchasePriceVariance)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.SupplierPayable)).ShouldBe(payableAfterInvoice + debitNote);
    }
}
