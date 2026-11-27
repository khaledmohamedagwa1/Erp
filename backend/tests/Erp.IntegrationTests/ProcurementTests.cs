using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Workflow.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Every document needs approval (stands in for the Workflow module).</summary>
public sealed class AlwaysRequiredApprovals : IApprovalService
{
    public Task<ApprovalDecision> SubmitAsync(ApprovalRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApprovalDecision(true, Guid.NewGuid()));

    public Task WithdrawAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class ProcurementTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Warehouse, Guid Pcs, Guid Product, Guid SupplierId, Guid BankId, Guid WhtGroupId);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, Shop s, HttpMethod method, string url, object? body = null, bool idempotent = false, string? key = null)
    {
        var request = new HttpRequestMessage(method, url).WithCompany(s.T.CompanyId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotent || key is not null)
        {
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        }

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Send(Shop s, HttpMethod method, string url, object? body = null, bool idempotent = false, string? key = null) =>
        Send(s.T.Client, s, method, url, body, idempotent, key);

    private async Task<Shop> NewShopAsync()
    {
        var t = await fixture.NewTenantAsync();
        async Task<JsonElement> Post(string url, object? body = null) => await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, url, t.CompanyId, body ?? new { }));
        await Post("/api/v1/accounting/coa-templates/generic");
        var le = (await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId))).EnumerateArray().First().GetProperty("id").GetGuid();
        await Post("/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" });
        await Post("/api/v1/tax/templates/egypt");
        var codes = await Json(await t.Client.GetAsCompanyAsync("/api/v1/tax/tax-codes", t.CompanyId));
        var wht1 = codes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "WHT-1").GetProperty("id").GetGuid();
        var wht = (await Post("/api/v1/tax/tax-groups", new
        {
            code = "WHT1P", nameEn = "Withholding 1%", nameAr = "خصم 1%", components = new[] { new { taxCodeId = wht1, sequence = 1, compoundsOnPrevious = false } },
        })).GetProperty("id").GetGuid();

        var warehouses = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId));
        var main = warehouses.GetProperty("items").EnumerateArray().First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await Post("/api/v1/catalog/uoms/seed-standard")).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = (await Post("/api/v1/catalog/categories", new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .GetProperty("id").GetGuid();
        var product = (await Post("/api/v1/catalog/products", new { sku = "WIDGET", nameEn = "Widget", nameAr = "قطعة", type = "Stockable", baseUomId = uoms["PCS"], categoryId = category }))
            .GetProperty("id").GetGuid();
        var supplier = (await Post("/api/v1/parties/suppliers", new { code = "S1", nameEn = "Nile Supplies", nameAr = "النيل", currency = "EGP" })).GetProperty("id").GetGuid();
        var bank = (await Post("/api/v1/finance/bank-accounts", new { legalEntityId = le, code = "CIB", name = "CIB current", currency = "EGP", bankName = "CIB" })).GetProperty("id").GetGuid();
        return new Shop(t, le, main, uoms["PCS"], product, supplier, bank, wht);
    }

    private static async Task<JsonElement> OrderAsync(Shop s, decimal quantity, decimal price, HttpClient? client = null)
    {
        client ??= s.T.Client;
        var order = await Json(await Send(client, s, HttpMethod.Post, "/api/v1/procurement/orders", new
        {
            legalEntityId = s.LegalEntityId, warehouseId = s.Warehouse, supplierId = s.SupplierId, date = "2026-04-01",
            lines = new[] { new { productId = s.Product, uomId = s.Pcs, quantity, unitPrice = price, discountPercent = 0m } },
        }));
        return await Json(await Send(client, s, HttpMethod.Post, $"/api/v1/procurement/orders/{order.GetProperty("id").GetGuid()}/submit"));
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

    private static object ReceiptLine(JsonElement receipt, decimal quantity, decimal price) => new
    {
        kind = "Receipt", goodsReceiptId = receipt.GetProperty("id").GetGuid(), goodsReceiptLineId = receipt.GetProperty("lines")[0].GetProperty("lineId").GetGuid(),
        quantity, unitPrice = price,
    };

    private static async Task<JsonElement> InvoiceAsync(Shop s, string number, params object[] lines)
    {
        var invoice = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = number, invoiceDate = "2026-04-15", lines,
        }));
        var id = invoice.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{id}/submit"));
        return await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{id}/post", idempotent: true));
    }

    private async Task<T> AsModuleAsync<T>(Shop s, Func<IServiceProvider, Task<T>> work, bool transaction = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(s.T.TenantId);
        tenant.SetCompany(s.T.CompanyId);
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

    private Task<decimal> RoleAsync(Shop s, string role) =>
        AsModuleAsync(s, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(s.LegalEntityId, role, new DateOnly(2026, 12, 31), Ct));

    private Task<decimal> ValuationAsync(Shop s) => AsModuleAsync(s, sp => sp.GetRequiredService<IStockQueries>().GetValuationTotalAsync(s.LegalEntityId, Ct));

    private async Task IssueAsync(Shop s, decimal quantity, string date)
    {
        var result = await AsModuleAsync(s, sp => sp.GetRequiredService<IInventoryPostingService>().PostAsync(new InventoryPostingRequest(
            s.LegalEntityId, DateOnly.Parse(date), "sales", "SalesInvoice", Guid.NewGuid(), "SI-1",
            [new InventoryPostingLine(Guid.NewGuid(), s.Product, null, s.Warehouse, s.Pcs, -quantity, InventoryMovementTypes.SalesIssue, CounterRole: PostingRoles.CostOfGoodsSold)],
            PostJournal: true), Ct), transaction: true);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);
    }

    private async Task DeliverApprovalAsync(Shop s, string documentType, Guid documentId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(s.T.TenantId);
        scope.ServiceProvider.GetRequiredService<IOutboxMessageContext>().Set(Guid.NewGuid());
        foreach (var handler in scope.ServiceProvider.GetServices<IIntegrationEventHandler<ApprovalCompleted>>())
        {
            await handler.HandleAsync(new ApprovalCompleted(documentType, documentId, true, s.T.AdminUserId, null), Ct);
        }
    }

    private static async Task<JsonElement> JournalAsync(Shop s, Guid journalId) =>
        await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/accounting/journals/{journalId}", s.T.CompanyId));

    [Fact]
    public async Task Procure_to_pay_S2_partial_receipts_price_variance_after_issue_withholding_payment()
    {
        var s = await NewShopAsync();
        var order = await OrderAsync(s, 100, 10m);
        order.GetProperty("status").GetString().ShouldBe("Approved");
        order.GetProperty("grandTotal").GetDecimal().ShouldBe(1140m);
        var orderId = order.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/orders/{orderId}/send"));

        var grn1 = await ReceiveAsync(s, order, 60, "2026-04-05");
        grn1.GetProperty("status").GetString().ShouldBe("Posted");
        grn1.GetProperty("totalBaseValue").GetDecimal().ShouldBe(600m);
        (await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/orders/{orderId}", s.T.CompanyId))).GetProperty("status").GetString().ShouldBe("PartiallyReceived");
        var grn2 = await ReceiveAsync(s, order, 40, "2026-04-10");
        (await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/orders/{orderId}", s.T.CompanyId))).GetProperty("status").GetString().ShouldBe("FullyReceived");
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(-1000m);

        // 80 of the 100 units are sold before the supplier invoice arrives at 10.10 (+1 %, inside the 2 % tolerance).
        await IssueAsync(s, 80, "2026-04-12");
        var invoice = await InvoiceAsync(s, "SUP-INV-1", ReceiptLine(grn1, 60, 10.10m), ReceiptLine(grn2, 40, 10.10m));
        invoice.GetProperty("status").GetString().ShouldBe("Posted");
        invoice.GetProperty("matchStatus").GetString().ShouldBe("Matched");
        invoice.GetProperty("grandTotal").GetDecimal().ShouldBe(1151.40m);
        invoice.GetProperty("number").GetString().ShouldBe("PI-2026-000001");
        var lines = invoice.GetProperty("lines");
        lines[0].GetProperty("varianceToInventory").GetDecimal().ShouldBe(2m); // 20 of 60 still on hand
        lines[0].GetProperty("varianceToPpv").GetDecimal().ShouldBe(4m);
        lines[1].GetProperty("varianceToInventory").GetDecimal().ShouldBe(2m); // 20 of 40
        lines[1].GetProperty("varianceToPpv").GetDecimal().ShouldBe(2m);

        var journal = await JournalAsync(s, invoice.GetProperty("journalId").GetGuid());
        journal.GetProperty("totalBaseDebit").GetDecimal().ShouldBe(1151.40m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.PurchasePriceVariance)).ShouldBe(6m);
        (await RoleAsync(s, PostingRoles.InputTax)).ShouldBe(141.40m);
        (await RoleAsync(s, PostingRoles.SupplierPayable)).ShouldBe(-1151.40m);
        (await ValuationAsync(s)).ShouldBe(204m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(204m);

        // Posting again (new key) returns the posted invoice; nothing is booked twice.
        var again = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoice.GetProperty("id").GetGuid()}/post", idempotent: true));
        again.GetProperty("journalId").GetGuid().ShouldBe(invoice.GetProperty("journalId").GetGuid());
        (await RoleAsync(s, PostingRoles.SupplierPayable)).ShouldBe(-1151.40m);

        // AP open item with the supplier, paid by Finance with 1 % withholding on the net.
        var items = await AsModuleAsync(s, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Supplier, s.SupplierId, cancellationToken: Ct));
        var item = items.Single();
        item.OpenAmount.ShouldBe(1151.40m);
        var payment = await Json(await Send(s, HttpMethod.Post, "/api/v1/finance/payments", new
        {
            legalEntityId = s.LegalEntityId, partyId = s.SupplierId, date = "2026-04-20", method = "BankTransfer", cashBankAccountId = s.BankId, amount = 1141.30m,
            withholdingTaxGroupId = s.WhtGroupId, withholdingBase = 1010m, allocations = new[] { new { openItemId = item.Id, amount = 1151.40m } },
        }));
        var paid = await Json(await Send(s, HttpMethod.Post, $"/api/v1/finance/payments/{payment.GetProperty("id").GetGuid()}/post", idempotent: true));
        paid.GetProperty("withholdingAmount").GetDecimal().ShouldBe(10.10m);
        (await RoleAsync(s, PostingRoles.SupplierPayable)).ShouldBe(0m);

        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        var url = $"/api/v1/procurement/invoices/{invoice.GetProperty("id").GetGuid()}";
        for (var i = 0; i < 20 && (await Json(await s.T.Client.GetAsCompanyAsync(url, s.T.CompanyId))).GetProperty("status").GetString() != "Paid"; i++)
        {
            await processor.ProcessPendingAsync(Ct);
        }

        var settled = await Json(await s.T.Client.GetAsCompanyAsync(url, s.T.CompanyId));
        settled.GetProperty("status").GetString().ShouldBe("Paid");
        settled.GetProperty("openAmount").GetDecimal().ShouldBe(0m);

        // Fully invoiced receipts can no longer be reversed or invoiced again.
        (await Send(s, HttpMethod.Post, $"/api/v1/procurement/receipts/{grn1.GetProperty("id").GetGuid()}/reverse", idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var extra = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = "SUP-INV-2", invoiceDate = "2026-04-16", lines = new[] { ReceiptLine(grn1, 1, 10m) },
        }));
        var match = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/invoices/{extra.GetProperty("id").GetGuid()}/match", s.T.CompanyId));
        match.GetProperty("lines")[0].GetProperty("quantityException").GetBoolean().ShouldBeTrue();
        var duplicate = await Send(s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = "SUP-INV-1", invoiceDate = "2026-04-16", lines = new[] { ReceiptLine(grn1, 1, 10m) },
        });
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ErrorCodeAsync()).ShouldBe("procurement.duplicate-supplier-invoice");
    }

    [Fact]
    public async Task Return_leaves_at_original_receipt_cost_with_a_debit_note_for_the_invoiced_part()
    {
        var s = await NewShopAsync();
        var grn1 = await ReceiveAsync(s, await OrderAsync(s, 10, 10m), 10, "2026-04-05");
        var invoice = await InvoiceAsync(s, "R-1", ReceiptLine(grn1, 6, 10m));
        invoice.GetProperty("grandTotal").GetDecimal().ShouldBe(68.40m);
        await ReceiveAsync(s, await OrderAsync(s, 10, 20m), 10, "2026-04-06"); // average cost is now 15

        var draft = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/returns", new
        {
            goodsReceiptId = grn1.GetProperty("id").GetGuid(), date = "2026-04-20", reason = "Damaged",
            lines = new[] { new { goodsReceiptLineId = grn1.GetProperty("lines")[0].GetProperty("lineId").GetGuid(), quantity = 8m } },
        }));
        var id = draft.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/returns/{id}/approve"));
        var key = Guid.NewGuid().ToString();
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/returns/{id}/post", key: key));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("totalCost").GetDecimal().ShouldBe(80m); // original 10/unit, not the current average of 15
        posted.GetProperty("debitNoteTotal").GetDecimal().ShouldBe(45.60m);
        posted.GetProperty("lines")[0].GetProperty("uninvoicedQuantity").GetDecimal().ShouldBe(4m);
        posted.GetProperty("lines")[0].GetProperty("invoicedQuantity").GetDecimal().ShouldBe(4m);

        // Same key replays; a new key finds the return already posted.
        (await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/returns/{id}/post", key: key))).GetProperty("journalId").GetGuid()
            .ShouldBe(posted.GetProperty("journalId").GetGuid());
        (await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/returns/{id}/post", idempotent: true))).GetProperty("number").GetString()
            .ShouldBe(posted.GetProperty("number").GetString());

        (await ValuationAsync(s)).ShouldBe(220m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(-200m); // only the second receipt is still uninvoiced
        (await RoleAsync(s, PostingRoles.InputTax)).ShouldBe(2.80m);
        (await RoleAsync(s, PostingRoles.SupplierPayable)).ShouldBe(-22.80m);
        (await RoleAsync(s, PostingRoles.PurchasePriceVariance)).ShouldBe(0m);
        var items = await AsModuleAsync(s, sp => sp.GetRequiredService<IOpenItemQueries>().GetOpenItemsAsync(PartyKind.Supplier, s.SupplierId, cancellationToken: Ct));
        items.Sum(i => i.Sign * i.OpenAmount).ShouldBe(22.80m);
    }

    [Fact]
    public async Task Approval_required_path_completes_on_ApprovalCompleted_and_exceptions_need_approval()
    {
        var s = await NewShopAsync();
        await using var withApprovals = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddScoped<IApprovalService, AlwaysRequiredApprovals>()));
        var client = withApprovals.CreateClient();
        await ErpApiFixture.SignInAsync(client, s.T.TenantCode, s.T.Email, s.T.Password); // the second host has its own signing key

        var order = await OrderAsync(s, 10, 10m, client);
        order.GetProperty("status").GetString().ShouldBe("PendingApproval");
        var orderId = order.GetProperty("id").GetGuid();
        (await Send(s, HttpMethod.Post, "/api/v1/procurement/receipts", new
        {
            purchaseOrderId = orderId, date = "2026-04-05", lines = new[] { new { purchaseOrderLineId = order.GetProperty("lines")[0].GetProperty("lineId").GetGuid(), receivedQuantity = 1m } },
        })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await DeliverApprovalAsync(s, ApprovalDocumentTypes.PurchaseOrder, orderId);
        order = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/orders/{orderId}", s.T.CompanyId));
        order.GetProperty("status").GetString().ShouldBe("Approved");
        var grn = await ReceiveAsync(s, order, 10, "2026-04-05");

        // 10.50 is 5 % above the order price: refused without an approval rule…
        var noRule = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = "A-1", invoiceDate = "2026-04-15", lines = new[] { ReceiptLine(grn, 10, 10.5m) },
        }));
        var refused = await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{noRule.GetProperty("id").GetGuid()}/submit");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ErrorCodeAsync()).ShouldBe("procurement.match-exceptions");
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{noRule.GetProperty("id").GetGuid()}/cancel", new { reason = "Re-entered" }, idempotent: true));

        // …and goes to approval when a rule applies.
        var draft = await Json(await Send(client, s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = "A-1", invoiceDate = "2026-04-15", lines = new[] { ReceiptLine(grn, 10, 10.5m) },
        }));
        var invoiceId = draft.GetProperty("id").GetGuid();
        var submitted = await Json(await Send(client, s, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoiceId}/submit"));
        submitted.GetProperty("status").GetString().ShouldBe("PendingApproval");
        submitted.GetProperty("matchStatus").GetString().ShouldBe("Exceptions");
        (await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoiceId}/post", idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await DeliverApprovalAsync(s, ApprovalDocumentTypes.PurchaseInvoice, invoiceId);
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoiceId}/post", idempotent: true));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("lines")[0].GetProperty("varianceToInventory").GetDecimal().ShouldBe(5m); // everything still on hand
        (await ValuationAsync(s)).ShouldBe(105m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(0m);

        // Cancelling the posted invoice reverses the journal, the cost adjustment and the open item.
        var cancelled = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoiceId}/cancel", new { reason = "Wrong supplier invoice" }, idempotent: true));
        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        (await ValuationAsync(s)).ShouldBe(100m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(-100m);
        (await RoleAsync(s, PostingRoles.SupplierPayable)).ShouldBe(0m);
        var receipt = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/receipts/{grn.GetProperty("id").GetGuid()}", s.T.CompanyId));
        receipt.GetProperty("lines")[0].GetProperty("invoiceableQuantity").GetDecimal().ShouldBe(10m);
    }

    [Fact]
    public async Task Landed_cost_capitalises_the_on_hand_share_and_receipt_reversal_restores_the_order()
    {
        var s = await NewShopAsync();
        var order = await OrderAsync(s, 20, 10m);
        var grn = await ReceiveAsync(s, order, 10, "2026-04-05");
        await IssueAsync(s, 5, "2026-04-06");

        var charge = await Json(await Send(s, HttpMethod.Post, "/api/v1/procurement/landed-costs", new
        {
            legalEntityId = s.LegalEntityId, date = "2026-04-07", basis = "Value",
            charges = new[] { new { description = "Freight", amount = 30m } }, goodsReceiptIds = new[] { grn.GetProperty("id").GetGuid() },
        }));
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/landed-costs/{charge.GetProperty("id").GetGuid()}/post", idempotent: true));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        var allocation = posted.GetProperty("allocations")[0];
        allocation.GetProperty("appliedToInventory").GetDecimal().ShouldBe(15m);
        allocation.GetProperty("toVariance").GetDecimal().ShouldBe(15m);
        (await RoleAsync(s, PostingRoles.LandedCostClearing)).ShouldBe(-30m);
        (await ValuationAsync(s)).ShouldBe(65m);

        // A second, uninvoiced receipt can be reversed; the order goes back to partially received.
        var grn2 = await ReceiveAsync(s, order, 10, "2026-04-08");
        (await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/orders/{order.GetProperty("id").GetGuid()}", s.T.CompanyId)))
            .GetProperty("status").GetString().ShouldBe("FullyReceived");
        var reversed = await Json(await Send(s, HttpMethod.Post, $"/api/v1/procurement/receipts/{grn2.GetProperty("id").GetGuid()}/reverse", new { date = "2026-04-09" }, idempotent: true));
        reversed.GetProperty("status").GetString().ShouldBe("Reversed");
        (await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/procurement/orders/{order.GetProperty("id").GetGuid()}", s.T.CompanyId)))
            .GetProperty("status").GetString().ShouldBe("PartiallyReceived");
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(-100m);

        var supply = await AsModuleAsync(s, sp => sp.GetRequiredService<Erp.Modules.Procurement.Contracts.IProcurementQueries>().GetOpenSupplyAsync([s.Product], cancellationToken: Ct));
        supply.Single().OpenBaseQuantity.ShouldBe(10m);
    }

    [Fact]
    public async Task Invoice_before_receipt_books_goods_invoiced_not_received_and_the_receipt_clears_it()
    {
        var s = await NewShopAsync();
        var order = await OrderAsync(s, 10, 10m);
        var orderLine = new
        {
            kind = "Order", purchaseOrderId = order.GetProperty("id").GetGuid(), purchaseOrderLineId = order.GetProperty("lines")[0].GetProperty("lineId").GetGuid(),
            quantity = 10m, unitPrice = 10.10m,
        };
        var disabled = await Send(s, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = s.LegalEntityId, supplierId = s.SupplierId, supplierInvoiceNumber = "B-1", invoiceDate = "2026-04-15", lines = new[] { orderLine },
        });
        (await disabled.ErrorCodeAsync()).ShouldBe("procurement.invoice-before-receipt-disabled");

        var policy = await s.T.Client.GetAsCompanyAsync("/api/v1/configuration/policies/procurement", s.T.CompanyId);
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/procurement", s.T.CompanyId,
            new { receiptBeforeInvoice = false, priceTolerancePercent = 2m, quantityTolerancePercent = 0m }, policy.Headers.ETag!.Tag)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var invoice = await InvoiceAsync(s, "B-1", orderLine);
        invoice.GetProperty("grandTotal").GetDecimal().ShouldBe(115.14m);
        (await RoleAsync(s, PostingRoles.GoodsInvoicedNotReceived)).ShouldBe(101m);

        await ReceiveAsync(s, order, 10, "2026-04-20");
        (await RoleAsync(s, PostingRoles.GoodsInvoicedNotReceived)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(0m);
        (await ValuationAsync(s)).ShouldBe(101m); // received at the invoiced price
        (await Send(s, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoice.GetProperty("id").GetGuid()}/cancel", new { reason = "x" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict); // goods since received: reverse the receipt first
    }

    [Fact]
    public async Task Procurement_documents_are_tenant_isolated_and_need_the_module()
    {
        var a = await NewShopAsync();
        var order = await OrderAsync(a, 5, 10m);
        var b = await fixture.NewTenantAsync();
        (await b.Client.GetAsCompanyAsync($"/api/v1/procurement/orders/{order.GetProperty("id").GetGuid()}", b.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var list = await Json(await b.Client.GetAsCompanyAsync("/api/v1/procurement/orders", b.CompanyId));
        list.GetProperty("totalCount").GetInt32().ShouldBe(0);

        (await b.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/modules/procurement", b.CompanyId, new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await b.Client.GetAsCompanyAsync("/api/v1/procurement/orders", b.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
