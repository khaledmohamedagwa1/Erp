using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class ReportingTests(ErpApiFixture fixture)
{
    private const string Api = "/api/v1/reports";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Company(
        ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Pcs, Guid Goods, Guid Plate, Guid X, Guid Y, Guid Fg, Guid Customer, Guid Supplier, Guid Bank);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(ErpApiFixture.TenantSession t, HttpMethod method, string url, object? body = null, bool idempotent = false) =>
        t.Client.SendAsCompanyAsync(method, url, t.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static async Task<JsonElement> Post(ErpApiFixture.TenantSession t, string url, object? body = null, bool idempotent = false) =>
        await Ok(await Send(t, HttpMethod.Post, url, body ?? new { }, idempotent));

    private static async Task<JsonElement> Get(ErpApiFixture.TenantSession t, string url) => await Ok(await t.Client.GetAsCompanyAsync(url, t.CompanyId));

    private static decimal Total(JsonElement report, string column) => report.GetProperty("totals").GetProperty(column).GetDecimal();

    private static IEnumerable<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray();

    /// <summary>
    /// Company with stock (PLATE 20 @ 60, X 100 @ 5, Y 100 @ 2), a purchase (order 10 PLATE @ 70, 6 received, 4 invoiced), a sale
    /// (5 PLATE @ 100 + 14 % VAT) with a restocked return of 2, an open sales order for 3 and a closed production order (10 FG = 2 X + 1 Y).
    /// </summary>
    private async Task<(Company C, Guid Invoice, Guid CreditNote, Guid Order, Guid Receipt, Guid Voucher, Guid Production)> SeedAsync()
    {
        var t = await fixture.NewTenantAsync(preset: "manufacturing");
        await Post(t, "/api/v1/accounting/coa-templates/generic");
        var le = (await Get(t, "/api/v1/organization/legal-entities")).EnumerateArray().First().GetProperty("id").GetGuid();
        await Post(t, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" });
        await Post(t, "/api/v1/tax/templates/egypt");
        var main = (await Get(t, "/api/v1/organization/warehouses")).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await Post(t, "/api/v1/catalog/uoms/seed-standard")).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var pcs = uoms["PCS"];
        var goods = (await Post(t, "/api/v1/catalog/categories", new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .GetProperty("id").GetGuid();
        var materials = (await Post(t, "/api/v1/catalog/categories", new { code = "MAT", nameEn = "Materials", nameAr = "خامات", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .GetProperty("id").GetGuid();
        async Task<Guid> Product(string sku, Guid category) => (await Post(t, "/api/v1/catalog/products", new
        {
            sku, nameEn = sku, nameAr = "صنف " + sku, type = "Stockable", baseUomId = pcs, categoryId = category, tracking = "None", taxCategory = "standard",
        })).GetProperty("id").GetGuid();
        var plate = await Product("PLATE", goods);
        var x = await Product("X", materials);
        var y = await Product("Y", materials);
        var fg = await Product("FG", goods);

        var list = (await Post(t, "/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })).GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/catalog/price-lists/{list}/items", new { productId = plate, uomId = pcs, minQuantity = 0m, price = 100m });
        var customer = (await Post(t, "/api/v1/parties/customers", new { code = "C1", kind = "Business", nameEn = "Delta Retail", nameAr = "دلتا للتجزئة", currency = "EGP", priceListId = list }))
            .GetProperty("id").GetGuid();
        var supplier = (await Post(t, "/api/v1/parties/suppliers", new { code = "S1", nameEn = "Nile Supplies", nameAr = "النيل للتوريدات", currency = "EGP" })).GetProperty("id").GetGuid();
        var bank = (await Post(t, "/api/v1/finance/bank-accounts", new { legalEntityId = le, code = "CIB", name = "CIB current", currency = "EGP", bankName = "CIB" })).GetProperty("id").GetGuid();
        var c = new Company(t, le, main, pcs, goods, plate, x, y, fg, customer, supplier, bank);

        // Opening stock.
        var opening = await Post(t, "/api/v1/inventory/opening-stock", new
        {
            warehouseId = main, date = "2026-01-15",
            lines = new object[]
            {
                new { productId = plate, uomId = pcs, quantity = 20, unitCost = 60m },
                new { productId = x, uomId = pcs, quantity = 100, unitCost = 5m },
                new { productId = y, uomId = pcs, quantity = 100, unitCost = 2m },
            },
        });
        var openingId = opening.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/inventory/opening-stock/{openingId}/submit");
        await Post(t, $"/api/v1/inventory/opening-stock/{openingId}/approve");
        await Post(t, $"/api/v1/inventory/opening-stock/{openingId}/post", idempotent: true);

        // Purchasing: order 10, receive 6, invoice 4.
        var po = await Post(t, "/api/v1/procurement/orders", new
        {
            legalEntityId = le, warehouseId = main, supplierId = supplier, date = "2026-04-01",
            lines = new[] { new { productId = plate, uomId = pcs, quantity = 10m, unitPrice = 70m, discountPercent = 0m } },
        });
        po = await Post(t, $"/api/v1/procurement/orders/{po.GetProperty("id").GetGuid()}/submit");
        var receipt = await Post(t, "/api/v1/procurement/receipts", new
        {
            purchaseOrderId = po.GetProperty("id").GetGuid(), date = "2026-04-05",
            lines = new[] { new { purchaseOrderLineId = po.GetProperty("lines")[0].GetProperty("lineId").GetGuid(), receivedQuantity = 6m } },
        });
        var receiptId = receipt.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/procurement/receipts/{receiptId}/approve");
        receipt = await Post(t, $"/api/v1/procurement/receipts/{receiptId}/post", idempotent: true);
        var purchaseInvoice = await Post(t, "/api/v1/procurement/invoices", new
        {
            legalEntityId = le, supplierId = supplier, supplierInvoiceNumber = "NS-501", invoiceDate = "2026-04-15",
            lines = new[]
            {
                new { kind = "Receipt", goodsReceiptId = receiptId, goodsReceiptLineId = receipt.GetProperty("lines")[0].GetProperty("lineId").GetGuid(), quantity = 4m, unitPrice = 70m },
            },
        });
        await Post(t, $"/api/v1/procurement/invoices/{purchaseInvoice.GetProperty("id").GetGuid()}/submit");
        await Post(t, $"/api/v1/procurement/invoices/{purchaseInvoice.GetProperty("id").GetGuid()}/post", idempotent: true);

        // Sales: invoice 5, return 2 (restocked), an open order for 3.
        var draft = await Post(t, "/api/v1/sales/invoices", new { customerId = customer, warehouseId = main, date = "2026-06-02", lines = new[] { new { productId = plate, uomId = pcs, quantity = 5m } } });
        var invoiceId = draft.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/sales/invoices/{invoiceId}/submit", idempotent: true);
        var invoice = await Post(t, $"/api/v1/sales/invoices/{invoiceId}/post", idempotent: true);
        var returnDraft = await Post(t, "/api/v1/sales/returns", new
        {
            salesInvoiceId = invoiceId, date = "2026-06-10", reason = "Broken",
            lines = new[] { new { invoiceLineId = invoice.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 2m } },
        });
        var creditNoteId = returnDraft.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/sales/returns/{creditNoteId}/post", idempotent: true);
        var order = await Post(t, "/api/v1/sales/orders", new { customerId = customer, warehouseId = main, date = "2026-06-20", lines = new[] { new { productId = plate, uomId = pcs, quantity = 3m } } });
        await Post(t, $"/api/v1/sales/orders/{order.GetProperty("id").GetGuid()}/confirm", idempotent: true);

        // Customer receipt against the invoice.
        var openItem = (await AsCompanyAsync(c, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Customer, customer, cancellationToken: Ct)))
            .First(i => i.OpenAmount > 0);
        var voucher = await Post(t, "/api/v1/finance/receipts", new
        {
            legalEntityId = le, partyId = customer, date = "2026-06-15", method = "BankTransfer", cashBankAccountId = bank, amount = 100m,
            allocations = new[] { new { openItemId = openItem.Id, amount = 100m } },
        });
        var voucherId = voucher.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/finance/receipts/{voucherId}/post", idempotent: true);

        // Manufacturing: BOM FG = 2 X + 1 Y, order 10, issue all, receive 10, close.
        var bom = (await Post(t, "/api/v1/manufacturing/boms", new { productId = fg, code = "BOM-FG", name = "FG" })).GetProperty("id").GetGuid();
        var version = (await Post(t, $"/api/v1/manufacturing/boms/{bom}/versions", new
        {
            effectiveFrom = "2026-01-01", outputQuantity = 1m,
            lines = new object[]
            {
                new { componentProductId = x, quantityPerOutput = 2m, uomId = pcs, scrapPercent = 0m, issueMethod = "Manual" },
                new { componentProductId = y, quantityPerOutput = 1m, uomId = pcs, scrapPercent = 0m, issueMethod = "Manual" },
            },
        })).GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/manufacturing/boms/{bom}/versions/{version}/activate");
        var production = await Post(t, "/api/v1/manufacturing/production-orders", new { productId = fg, quantity = 10m, componentWarehouseId = main, startDate = "2026-03-01", dueDate = "2026-03-10" });
        var productionId = production.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/manufacturing/production-orders/{productionId}/plan");
        production = await Post(t, $"/api/v1/manufacturing/production-orders/{productionId}/release");
        Guid Line(Guid product) => production.GetProperty("components").EnumerateArray().First(l => l.GetProperty("productId").GetGuid() == product).GetProperty("lineId").GetGuid();
        await Post(t, $"/api/v1/manufacturing/production-orders/{productionId}/issues", new
        {
            date = "2026-03-02", lines = new[] { new { lineId = Line(x), quantity = 21m }, new { lineId = Line(y), quantity = 10m } },
        }, idempotent: true);
        await Post(t, $"/api/v1/manufacturing/production-orders/{productionId}/receipts", new { quantity = 10m, date = "2026-03-04" }, idempotent: true);
        await Post(t, $"/api/v1/manufacturing/production-orders/{productionId}/close", new { date = "2026-03-06" }, idempotent: true);

        return (c, invoiceId, creditNoteId, po.GetProperty("id").GetGuid(), receiptId, voucherId, productionId);
    }

    private async Task<T> AsCompanyAsync<T>(Company c, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(c.T.TenantId);
        tenant.SetCompany(c.T.CompanyId);
        return await work(scope.ServiceProvider);
    }

    private Task<decimal> RoleAsync(Company c, string role) =>
        AsCompanyAsync(c, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(c.LegalEntityId, role, new DateOnly(2026, 12, 31), Ct));

    [Fact]
    public async Task Report_totals_equal_the_source_documents_and_the_general_ledger()
    {
        var (c, invoiceId, creditNoteId, orderId, receiptId, voucherId, productionId) = await SeedAsync();
        var t = c.T;
        const string Year = "from=2026-01-01&to=2026-12-31";

        // ---- Catalogue: every area is available to the administrator.
        var catalogue = (await Get(t, Api)).EnumerateArray().ToList();
        catalogue.Select(r => r.GetProperty("area").GetString()).Distinct().ShouldBe(["sales", "purchasing", "inventory", "manufacturing", "financial", "tax"], ignoreOrder: true);
        catalogue.Single(r => r.GetProperty("key").GetString() == "trial-balance").GetProperty("href").GetString().ShouldBe("/api/v1/accounting/reports/trial-balance");

        // ---- Sales: net revenue = invoice − credit note; COGS = the GL cost of goods sold.
        var cogs = await RoleAsync(c, PostingRoles.CostOfGoodsSold);
        var revenue = -(await RoleAsync(c, PostingRoles.SalesRevenue)) - await RoleAsync(c, PostingRoles.SalesReturns);
        revenue.ShouldBe(300m);
        var summary = await Get(t, $"{Api}/sales/summary?{Year}&groupBy=month");
        Total(summary, "netRevenue").ShouldBe(revenue);
        Total(summary, "grossSales").ShouldBe(500m);
        Total(summary, "returns").ShouldBe(-200m);
        Total(summary, "tax").ShouldBe(42m);
        Total(summary, "cogs").ShouldBe(cogs);
        Total(summary, "grossProfit").ShouldBe(revenue - cogs);
        Rows(summary).Single().GetProperty("period").GetString().ShouldBe("2026-06");

        var grossProfit = await Get(t, $"{Api}/sales/gross-profit?{Year}");
        Rows(grossProfit).Count().ShouldBe(2);
        Total(grossProfit, "cogs").ShouldBe(cogs);

        var byCustomer = await Get(t, $"{Api}/sales/by-customer?{Year}&lang=ar");
        var customerRow = Rows(byCustomer).Single();
        customerRow.GetProperty("customer").GetString().ShouldBe("دلتا للتجزئة");
        customerRow.GetProperty("gross").GetDecimal().ShouldBe(342m);

        var byProduct = await Get(t, $"{Api}/sales/by-product?{Year}&categoryId={c.Goods}");
        Rows(byProduct).Single().GetProperty("quantity").GetDecimal().ShouldBe(3m);
        Rows(await Get(t, $"{Api}/sales/by-product?{Year}&productId={c.X}")).ShouldBeEmpty();
        var returns = await Get(t, $"{Api}/sales/returns?{Year}");
        Rows(returns).Single().GetProperty("quantity").GetDecimal().ShouldBe(2m);
        Total(await Get(t, $"{Api}/sales/by-branch?{Year}"), "netRevenue").ShouldBe(revenue);

        // ---- Purchasing.
        var bySupplier = await Get(t, $"{Api}/purchasing/by-supplier?{Year}");
        Total(bySupplier, "net").ShouldBe(280m);
        Total(bySupplier, "tax").ShouldBe(39.2m);
        var priceHistory = await Get(t, $"{Api}/purchasing/price-history?{Year}&productId={c.Plate}");
        Rows(priceHistory).Single().GetProperty("baseUnitCost").GetDecimal().ShouldBe(70m);
        var receiptRow = Rows(await Get(t, $"{Api}/purchasing/receipt-analysis?{Year}")).Single();
        receiptRow.GetProperty("received").GetDecimal().ShouldBe(6m);
        receiptRow.GetProperty("invoiced").GetDecimal().ShouldBe(4m);
        receiptRow.GetProperty("uninvoiced").GetDecimal().ShouldBe(2m);
        receiptRow.GetProperty("uninvoicedValue").GetDecimal().ShouldBe(140m);
        Total(await Get(t, $"{Api}/purchasing/by-product?{Year}"), "quantity").ShouldBe(4m);

        // ---- Inventory: stock value = the GL inventory balance (per-row rounding aside).
        var inventoryGl = await RoleAsync(c, PostingRoles.Inventory);
        var stock = await Get(t, $"{Api}/inventory/stock-on-hand");
        Math.Abs(Total(stock, "value") - inventoryGl).ShouldBeLessThan(0.05m);
        Rows(stock).Single(r => r.GetProperty("sku").GetString() == "PLATE").GetProperty("onHand").GetDecimal().ShouldBe(23m);
        Math.Abs(Total(await Get(t, $"{Api}/inventory/valuation-by-category"), "value") - inventoryGl).ShouldBeLessThan(0.05m);
        var aging = await Get(t, $"{Api}/inventory/aging?productId={c.Plate}");
        Total(aging, "onHand").ShouldBe(23m);
        var ledger = await Get(t, $"{Api}/inventory/ledger?from=2026-04-01&to=2026-12-31&productId={c.Plate}&pageSize=2");
        ledger.GetProperty("totalCount").GetInt32().ShouldBe(3); // receipt, sale, return
        Rows(ledger).Count().ShouldBe(2);
        var parameters = ledger.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("labelEn").GetString()!, p => p.GetProperty("value").GetString());
        parameters["Opening quantity"].ShouldBe("20");
        parameters["Closing quantity"].ShouldBe("23");

        // ---- Manufacturing: the production order's figures.
        var productionRow = Rows(await Get(t, $"{Api}/manufacturing/production-summary?{Year}")).Single();
        productionRow.GetProperty("completed").GetDecimal().ShouldBe(10m);
        productionRow.GetProperty("material").GetDecimal().ShouldBe(125m);
        productionRow.GetProperty("received").GetDecimal().ShouldBe(125m);
        var consumption = Rows(await Get(t, $"{Api}/manufacturing/material-consumption?{Year}")).ToList();
        var xRow = consumption.Single(r => r.GetProperty("componentSku").GetString() == "X");
        xRow.GetProperty("standard").GetDecimal().ShouldBe(20m);
        xRow.GetProperty("actual").GetDecimal().ShouldBe(21m);
        xRow.GetProperty("quantityVariance").GetDecimal().ShouldBe(1m);
        xRow.GetProperty("costVariance").GetDecimal().ShouldBe(5m);
        Rows(await Get(t, $"{Api}/manufacturing/wip")).ShouldBeEmpty();
        Total(await Get(t, $"{Api}/manufacturing/variance?{Year}"), "wip").ShouldBe(0m);
        Rows(await Get(t, $"{Api}/manufacturing/scrap?{Year}")).ShouldBeEmpty();

        // ---- Incoming / outgoing / projected on the stock inquiry.
        var plateStock = (await Get(t, $"/api/v1/inventory/stock?productId={c.Plate}&warehouseId={c.Main}")).GetProperty("items").EnumerateArray().Single();
        plateStock.GetProperty("onHand").GetDecimal().ShouldBe(23m);
        plateStock.GetProperty("incoming").GetDecimal().ShouldBe(4m);
        plateStock.GetProperty("outgoing").GetDecimal().ShouldBe(3m);
        plateStock.GetProperty("projected").GetDecimal().ShouldBe(plateStock.GetProperty("available").GetDecimal() + 4m - 3m);

        // ---- Exports.
        var csv = await t.Client.GetAsCompanyAsync($"{Api}/sales/by-customer?{Year}&format=csv&lang=ar", t.CompanyId);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var csvBytes = await csv.Content.ReadAsByteArrayAsync(Ct);
        csvBytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(csvBytes).ShouldContain("دلتا للتجزئة");

        var xlsx = await t.Client.GetAsCompanyAsync($"{Api}/inventory/stock-on-hand?format=xlsx", t.CompanyId);
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using (var workbook = new XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync(Ct))))
        {
            workbook.Worksheets.Single().Cell(1, 1).GetString().ShouldBe("Stock on hand");
        }

        var html = await t.Client.GetAsCompanyAsync($"{Api}/sales/summary?{Year}&format=html&lang=ar", t.CompanyId);
        html.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var page = await html.Content.ReadAsStringAsync(Ct);
        page.ShouldContain("dir=\"rtl\"");
        page.ShouldContain("ملخص المبيعات حسب الفترة");
        (await (await t.Client.GetAsCompanyAsync($"{Api}/sales/summary?format=docx", t.CompanyId)).ErrorCodeAsync()).ShouldBe("reporting.invalid-format");
        (await (await t.Client.GetAsCompanyAsync($"{Api}/sales/summary?format=pdf", t.CompanyId)).ErrorCodeAsync()).ShouldBe("reporting.pdf-not-configured");
        (await (await t.Client.GetAsCompanyAsync($"{Api}/sales/summary?from=2026-05-01&to=2026-04-01", t.CompanyId)).ErrorCodeAsync()).ShouldBe("reporting.invalid-range");

        // ---- Financial statements are links.
        using var noRedirect = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization = t.Client.DefaultRequestHeaders.Authorization;
        var redirect = await noRedirect.GetAsCompanyAsync($"{Api}/financial/trial-balance?legalEntityId={c.LegalEntityId}&{Year}", t.CompanyId);
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        redirect.Headers.Location!.ToString().ShouldStartWith("/api/v1/accounting/reports/trial-balance?");

        // ---- Printable documents.
        async Task<string> Print(string type, Guid id, string lang = "en")
        {
            var response = await t.Client.GetAsCompanyAsync($"{Api}/documents/{type}/{id}?lang={lang}", t.CompanyId);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
            return text;
        }

        var invoiceHtml = await Print("sales-invoice", invoiceId, "ar");
        invoiceHtml.ShouldContain("dir=\"rtl\"");
        invoiceHtml.ShouldContain("INV-2026-000001");
        invoiceHtml.ShouldContain("دلتا للتجزئة");
        invoiceHtml.ShouldContain("570.00");
        (await Print("credit-note", creditNoteId)).ShouldContain("CN-2026-000001");
        (await Print("purchase-order", orderId)).ShouldContain("Nile Supplies");
        (await Print("goods-receipt", receiptId)).ShouldContain("PLATE");
        (await Print("voucher", voucherId)).ShouldContain("Receipt voucher");
        (await (await t.Client.GetAsCompanyAsync($"{Api}/documents/sales-invoice/{Guid.NewGuid()}", t.CompanyId)).ErrorCodeAsync()).ShouldBe("reporting.document-not-found");
        (await (await t.Client.GetAsCompanyAsync($"{Api}/documents/unknown/{invoiceId}", t.CompanyId)).ErrorCodeAsync()).ShouldBe("reporting.document-type-not-found");

        // ---- Tenant isolation: another tenant sees none of this and cannot print it.
        var other = await fixture.NewTenantAsync(preset: "manufacturing");
        Rows(await Get(other, $"{Api}/sales/gross-profit?{Year}")).ShouldBeEmpty();
        Rows(await Get(other, $"{Api}/inventory/stock-on-hand")).ShouldBeEmpty();
        Rows(await Get(other, $"{Api}/manufacturing/production-summary?{Year}")).ShouldBeEmpty();
        (await other.Client.GetAsCompanyAsync($"{Api}/documents/sales-invoice/{invoiceId}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _ = productionId;
    }

    [Fact]
    public async Task Exports_over_the_row_cap_fail_with_a_clear_error()
    {
        var t = await fixture.NewTenantAsync();
        await Post(t, "/api/v1/accounting/coa-templates/generic");
        var le = (await Get(t, "/api/v1/organization/legal-entities")).EnumerateArray().First().GetProperty("id").GetGuid();
        await Post(t, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" });
        var main = (await Get(t, "/api/v1/organization/warehouses")).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var pcs = (await Post(t, "/api/v1/catalog/uoms/seed-standard")).EnumerateArray().First(u => u.GetProperty("code").GetString() == "PCS").GetProperty("id").GetGuid();
        var category = (await Post(t, "/api/v1/catalog/categories", new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .GetProperty("id").GetGuid();
        async Task<Guid> Product(string sku) => (await Post(t, "/api/v1/catalog/products", new { sku, nameEn = sku, nameAr = sku, type = "Stockable", baseUomId = pcs, categoryId = category }))
            .GetProperty("id").GetGuid();
        var a = await Product("A");
        var b = await Product("B");
        var opening = await Post(t, "/api/v1/inventory/opening-stock", new
        {
            warehouseId = main, date = "2026-01-15",
            lines = new object[] { new { productId = a, uomId = pcs, quantity = 1, unitCost = 1m }, new { productId = b, uomId = pcs, quantity = 2, unitCost = 3m } },
        });
        var id = opening.GetProperty("id").GetGuid();
        await Post(t, $"/api/v1/inventory/opening-stock/{id}/submit");
        await Post(t, $"/api/v1/inventory/opening-stock/{id}/approve");
        await Post(t, $"/api/v1/inventory/opening-stock/{id}/post", idempotent: true);

        using var capped = fixture.Factory.WithWebHostBuilder(builder => builder.UseSetting("Reporting:MaxExportRows", "1"));
        using var client = capped.CreateClient();
        await ErpApiFixture.SignInAsync(client, t.TenantCode, t.Email, t.Password);

        // JSON is paged and never capped; every export format refuses more rows than the cap, including the database-paged ledger.
        var json = await Ok(await client.GetAsCompanyAsync($"{Api}/inventory/stock-on-hand", t.CompanyId));
        json.GetProperty("totalCount").GetInt32().ShouldBe(2);
        foreach (var format in new[] { "csv", "xlsx", "html" })
        {
            var refused = await client.GetAsCompanyAsync($"{Api}/inventory/stock-on-hand?format={format}", t.CompanyId);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.ErrorCodeAsync()).ShouldBe("reporting.export-too-large");
        }

        (await (await client.GetAsCompanyAsync($"{Api}/inventory/ledger?from=2026-01-01&to=2026-12-31&format=csv", t.CompanyId)).ErrorCodeAsync())
            .ShouldBe("reporting.export-too-large");
        (await client.GetAsCompanyAsync($"{Api}/inventory/stock-on-hand?format=csv&productId={a}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Catalogue_follows_permissions_and_enabled_modules()
    {
        // The services preset has sales and purchasing but neither inventory nor manufacturing.
        var t = await fixture.NewTenantAsync(preset: "services");
        var areas = (await Get(t, Api)).EnumerateArray().Select(r => r.GetProperty("area").GetString()).Distinct().ToList();
        areas.ShouldBe(["sales", "purchasing", "financial", "tax"], ignoreOrder: true);
        (await (await t.Client.GetAsCompanyAsync($"{Api}/inventory/stock-on-hand", t.CompanyId)).ErrorCodeAsync()).ShouldBe("module-disabled");
        (await (await t.Client.GetAsCompanyAsync($"{Api}/sales/nothing", t.CompanyId)).ErrorCodeAsync()).ShouldBe("reporting.report-not-found");
        (await (await t.Client.GetAsync(Api, Ct)).ErrorCodeAsync()).ShouldBe("company.required");

        // A user who may only run sales reports sees only those and is refused the rest.
        var role = await Ok(await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Sales analyst", permissions = new[] { "reporting.sales.read" } }, Ct));
        await Ok(await t.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email = "analyst@reports.example", displayName = "Analyst", password = "Analyst-Passw0rd!",
            roleIds = new[] { role.GetProperty("id").GetGuid() }, companyIds = new[] { t.CompanyId },
        }, Ct));
        using var analyst = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(analyst, t.TenantCode, "analyst@reports.example", "Analyst-Passw0rd!");
        var visible = await Ok(await analyst.GetAsCompanyAsync(Api, t.CompanyId));
        visible.EnumerateArray().ShouldAllBe(r => r.GetProperty("area").GetString() == "sales");
        visible.GetArrayLength().ShouldBe(8);
        (await analyst.GetAsCompanyAsync($"{Api}/sales/summary", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await analyst.GetAsCompanyAsync($"{Api}/purchasing/by-supplier", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await analyst.GetAsCompanyAsync($"{Api}/documents/sales-invoice/{Guid.NewGuid()}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await analyst.GetAsCompanyAsync($"{Api}/views", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Saved_views_are_private_to_their_owner()
    {
        var t = await fixture.NewTenantAsync(preset: "services");
        var created = await Post(t, $"{Api}/views", new { name = "Monthly", reportKey = "sales/summary", filters = new { groupBy = "month", from = "2026-01-01" } });
        created.GetProperty("filters").GetProperty("groupBy").GetString().ShouldBe("month");
        (await (await Send(t, HttpMethod.Post, $"{Api}/views", new { name = "Monthly", reportKey = "sales/summary" })).ErrorCodeAsync()).ShouldBe("reporting.view-name-taken");
        (await (await Send(t, HttpMethod.Post, $"{Api}/views", new { name = "X", reportKey = "sales/unknown" })).ErrorCodeAsync()).ShouldBe("reporting.report-not-found");
        (await Get(t, $"{Api}/views?reportKey=sales/summary")).GetArrayLength().ShouldBe(1);

        var role = await Ok(await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Report user", permissions = new[] { "reporting.view.manage", "reporting.sales.read" } }, Ct));
        await Ok(await t.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email = "second@reports.example", displayName = "Second", password = "Second-Passw0rd!",
            roleIds = new[] { role.GetProperty("id").GetGuid() }, companyIds = new[] { t.CompanyId },
        }, Ct));
        using var second = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(second, t.TenantCode, "second@reports.example", "Second-Passw0rd!");
        (await Ok(await second.GetAsCompanyAsync($"{Api}/views", t.CompanyId))).GetArrayLength().ShouldBe(0);
        (await second.SendAsCompanyAsync(HttpMethod.Delete, $"{Api}/views/{created.GetProperty("id").GetGuid()}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await Send(t, HttpMethod.Delete, $"{Api}/views/{created.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Get(t, $"{Api}/views")).GetArrayLength().ShouldBe(0);
    }

    private async Task<(Guid RoleId, HttpClient Client)> NewUserAsync(ErpApiFixture.TenantSession t, string name, params string[] permissions)
    {
        var role = await Ok(await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name, permissions }, Ct));
        var email = $"{name.ToLowerInvariant().Replace(' ', '-')}@reports.example";
        await Ok(await t.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email, displayName = name, password = "Report-Passw0rd!", roleIds = new[] { role.GetProperty("id").GetGuid() }, companyIds = new[] { t.CompanyId },
        }, Ct));
        var client = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(client, t.TenantCode, email, "Report-Passw0rd!");
        return (role.GetProperty("id").GetGuid(), client);
    }

    [Fact]
    public async Task Dashboards_are_shared_read_only_and_widgets_run_with_the_viewers_permissions()
    {
        var t = await fixture.NewTenantAsync(preset: "services");
        var (salesRole, salesViewer) = await NewUserAsync(t, "Dash sales", "reporting.dashboard.read", "reporting.sales.read");
        var (otherRole, otherViewer) = await NewUserAsync(t, "Dash other", "reporting.dashboard.read");
        using var s = salesViewer;
        using var o = otherViewer;

        (await (await Send(t, HttpMethod.Post, $"{Api}/dashboards", new
        {
            name = "Bad", widgets = new[] { new { title = "X", reportKey = "sales/unknown", visualization = "Table", x = 0, y = 0, width = 4, height = 3 } },
        })).ErrorCodeAsync()).ShouldBe("reporting.report-not-found");
        (await (await Send(t, HttpMethod.Post, $"{Api}/dashboards", new
        {
            name = "Bad", widgets = new[]
            {
                new { title = "X", reportKey = "sales/summary", visualization = "Table", parameters = new Dictionary<string, string> { ["from"] = "last week" }, x = 0, y = 0, width = 4, height = 3 },
            },
        })).ErrorCodeAsync()).ShouldBe("reporting.parameter-invalid");

        var created = await Post(t, $"{Api}/dashboards", new
        {
            name = "Sales overview", sharedWithRoleIds = new[] { salesRole, otherRole },
            widgets = new object[]
            {
                new
                {
                    title = "Revenue this month", reportKey = "sales/summary", visualization = "Kpi", valueColumn = "netRevenue",
                    parameters = new Dictionary<string, string> { ["from"] = "month-start", ["to"] = "today", ["groupBy"] = "day" }, x = 0, y = 0, width = 3, height = 2,
                },
                new
                {
                    title = "Top products", reportKey = "sales/by-product", visualization = "Bar", labelColumn = "product", valueColumn = "netRevenue", rowLimit = 5,
                    parameters = new Dictionary<string, string> { ["from"] = "-3m" }, x = 3, y = 0, width = 9, height = 4,
                },
            },
        });
        var id = created.GetProperty("id").GetGuid();
        var kpi = created.GetProperty("widgets")[0].GetProperty("widgetId").GetGuid();
        created.GetProperty("isOwner").GetBoolean().ShouldBeTrue();

        // A viewer with sales rights sees the shared dashboard and its data, but cannot change it.
        (await Ok(await s.GetAsCompanyAsync($"{Api}/dashboards", t.CompanyId))).EnumerateArray().Single().GetProperty("isOwner").GetBoolean().ShouldBeFalse();
        var data = await Ok(await s.GetAsCompanyAsync($"{Api}/dashboards/{id}/widgets/{kpi}/data", t.CompanyId));
        data.GetProperty("visualization").GetString().ShouldBe("Kpi");
        data.GetProperty("result").GetProperty("key").GetString().ShouldBe("summary");
        data.GetProperty("value").GetDecimal().ShouldBe(0m);
        (await s.SendAsCompanyAsync(HttpMethod.Delete, $"{Api}/dashboards/{id}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Sharing never widens access: the other viewer sees the dashboard but not the sales data behind it.
        (await Ok(await o.GetAsCompanyAsync($"{Api}/dashboards/{id}", t.CompanyId))).GetProperty("name").GetString().ShouldBe("Sales overview");
        (await o.GetAsCompanyAsync($"{Api}/dashboards/{id}/widgets/{kpi}/data", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Unshared: gone for the viewers.
        var updated = await Ok(await Send(t, HttpMethod.Put, $"{Api}/dashboards/{id}", new { name = "Mine only", widgets = Array.Empty<object>() }));
        updated.GetProperty("widgets").GetArrayLength().ShouldBe(0);
        (await s.GetAsCompanyAsync($"{Api}/dashboards/{id}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(t, HttpMethod.Delete, $"{Api}/dashboards/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Scheduled_reports_run_as_the_owner_keep_the_file_and_email_the_recipients()
    {
        var t = await fixture.NewTenantAsync(preset: "services");
        (await (await Send(t, HttpMethod.Post, $"{Api}/schedules", new
        {
            name = "Bad", reportKey = "sales/summary", format = "csv", cron = "every day", isActive = true,
        })).ErrorCodeAsync()).ShouldBe("validation-failed");

        var schedule = await Post(t, $"{Api}/schedules", new
        {
            name = "Daily sales", reportKey = "sales/summary", format = "csv", cron = "0 6 * * *", isActive = true,
            parameters = new Dictionary<string, string> { ["from"] = "-7d", ["groupBy"] = "day" }, recipients = new[] { "boss@reports.example" },
        });
        var id = schedule.GetProperty("id").GetGuid();
        var next = schedule.GetProperty("nextRunAtUtc").GetDateTime();
        next.Hour.ShouldBe(6);
        next.Minute.ShouldBe(0);

        // Run now (manual trigger): stored with its file and the recipients get a link.
        var run = await Post(t, $"{Api}/schedules/{id}/run");
        run.GetProperty("status").GetString().ShouldBe("Succeeded");
        var download = await t.Client.GetAsCompanyAsync(run.GetProperty("downloadUrl").GetString()!, t.CompanyId);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        RecordingNotificationSender.Sent.ShouldContain(m => m.To == "boss@reports.example" && m.Body.Contains(run.GetProperty("id").GetGuid().ToString()));

        // The worker path: a due occurrence is claimed once (the next run moves on) and runs without a signed-in user.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(t.TenantId);
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var db = scope.ServiceProvider.GetRequiredService<Erp.Modules.Reporting.Application.IReportingDbContext>();
            (await Erp.Modules.Reporting.Application.ReportScheduleExecutor.DueAsync(db, t.TenantId, next.AddMinutes(1), Ct)).ShouldContain(d => d.ScheduleId == id);
        }

        await AsScheduleWorkerAsync(t, id, next.AddMinutes(1), "Succeeded");
        var runs = await Get(t, $"{Api}/schedules/{id}/runs");
        runs.GetArrayLength().ShouldBe(2);
        runs.EnumerateArray().Select(r => r.GetProperty("trigger").GetString()).ShouldBe(["Schedule", "Manual"], ignoreOrder: true);
        (await Get(t, $"{Api}/schedules")).EnumerateArray().Single().GetProperty("nextRunAtUtc").GetDateTime().ShouldBe(next.AddDays(1));

        // Other users never see someone else's schedules.
        var (_, otherClient) = await NewUserAsync(t, "Sched other", "reporting.schedule.manage", "reporting.sales.read");
        using var other = otherClient;
        (await Ok(await other.GetAsCompanyAsync($"{Api}/schedules", t.CompanyId))).GetArrayLength().ShouldBe(0);
        (await other.SendAsCompanyAsync(HttpMethod.Post, $"{Api}/schedules/{id}/run", t.CompanyId, new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(t, HttpMethod.Delete, $"{Api}/schedules/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>Runs a schedule the way the worker does, with a clock set to <paramref name="now"/>.</summary>
    private async Task AsScheduleWorkerAsync(ErpApiFixture.TenantSession t, Guid scheduleId, DateTime now, string expectedStatus)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(t.TenantId);
        tenant.SetCompany(t.CompanyId);
        var executor = ActivatorUtilities.CreateInstance<Erp.Modules.Reporting.Application.ReportScheduleExecutor>(scope.ServiceProvider, new FixedClock(now));
        var result = await executor.RunAsync(scheduleId, Erp.Modules.Reporting.Application.ReportScheduleExecutor.ScheduledTrigger, Ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Status.ShouldBe(expectedStatus);
        var again = await executor.RunAsync(scheduleId, Erp.Modules.Reporting.Application.ReportScheduleExecutor.ScheduledTrigger, Ct);
        again.IsFailure.ShouldBeTrue();
        again.Error.Code.ShouldBe("reporting.schedule-not-due");
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    [Fact]
    public async Task Read_models_match_the_live_reports_and_can_be_rebuilt()
    {
        var (c, _, _, _, _, _, _) = await SeedAsync();
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        for (var i = 0; i < 10 && await processor.ProcessPendingAsync(Ct) > 0; i++)
        {
        }

        var live = await Get(c.T, $"{Api}/sales/summary?from=2026-01-01&to=2026-12-31&groupBy=day");
        var model = await Get(c.T, $"{Api}/sales/daily-summary?from=2026-01-01&to=2026-12-31");
        Rows(model).Count().ShouldBe(Rows(live).Count());
        Total(model, "netRevenue").ShouldBe(Total(live, "netRevenue"));
        Total(model, "cogs").ShouldBe(Total(live, "cogs"));
        Total(model, "invoices").ShouldBe(1m);
        Total(model, "creditNotes").ShouldBe(1m);

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var history = await Get(c.T, $"{Api}/inventory/value-history?from={today}&to={today}");
        var stock = await Get(c.T, $"{Api}/inventory/stock-on-hand");
        Rows(history).Sum(r => r.GetProperty("value").GetDecimal()).ShouldBe(Total(stock, "value"));

        // Rebuild recomputes the same figures.
        var rebuilt = await Post(c.T, $"{Api}/read-models/rebuild", new { from = "2026-01-01", to = "2026-12-31" });
        rebuilt.GetProperty("salesSummaryRows").GetInt32().ShouldBe(Rows(live).Count());
        Total(await Get(c.T, $"{Api}/sales/daily-summary?from=2026-01-01&to=2026-12-31"), "netRevenue").ShouldBe(Total(live, "netRevenue"));
        (await (await Send(c.T, HttpMethod.Post, $"{Api}/read-models/rebuild", new { from = "2026-12-31", to = "2026-01-01" })).ErrorCodeAsync())
            .ShouldBe("reporting.invalid-range");
    }

    [Fact]
    public async Task Navigation_hides_report_entries_of_disabled_modules()
    {
        // The services preset has sales and purchasing but neither inventory nor manufacturing.
        var t = await fixture.NewTenantAsync(preset: "services");
        var nav = await Get(t, "/api/v1/me/navigation");
        var keys = nav.EnumerateArray().SelectMany(g => g.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("key").GetString()).ToList();
        keys.ShouldContain("reporting.sales");
        keys.ShouldContain("reporting.dashboards");
        keys.ShouldNotContain("reporting.inventory");
        keys.ShouldNotContain("reporting.manufacturing");

        (await Send(t, HttpMethod.Put, "/api/v1/configuration/modules/inventory", new { enabled = true })).IsSuccessStatusCode.ShouldBeTrue();
        nav = await Get(t, "/api/v1/me/navigation");
        nav.EnumerateArray().SelectMany(g => g.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("key").GetString()).ShouldContain("reporting.inventory");
    }
}
