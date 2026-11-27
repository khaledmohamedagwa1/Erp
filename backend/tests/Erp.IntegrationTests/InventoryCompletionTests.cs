using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Recost of back-dated postings, exact FIFO adjustment reversal, direct transfers, residue-free receipts, serial counts, bins, returns by issue layer.</summary>
public class InventoryCompletionTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Store(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Second, Guid Pcs, Guid Plain, Guid Batched, Guid Serial, Guid FifoItem);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Store s, HttpMethod method, string url, object? body = null, bool idempotent = false, string? ifMatch = null) =>
        s.T.Client.SendAsCompanyAsync(method, url, s.T.CompanyId, body, ifMatch, idempotent ? Guid.NewGuid().ToString() : null);

    private static async Task<JsonElement> Get(Store s, string url) => await Json(await s.T.Client.GetAsCompanyAsync(url, s.T.CompanyId));

    private async Task<Store> NewStoreAsync()
    {
        var t = await fixture.NewTenantAsync();
        Task<HttpResponseMessage> Post(string url, object? body = null) => t.Client.SendAsCompanyAsync(HttpMethod.Post, url, t.CompanyId, body);
        (await Post("/api/v1/accounting/coa-templates/generic")).EnsureSuccessStatusCode();
        var legal = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId));
        var le = legal.EnumerateArray().First().GetProperty("id").GetGuid();
        (await Post("/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" })).EnsureSuccessStatusCode();
        var warehouses = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId));
        var main = warehouses.GetProperty("items").EnumerateArray().First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var branches = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/branches", t.CompanyId));
        var second = (await Json(await Post("/api/v1/organization/warehouses", new { branchId = branches.EnumerateArray().First().GetProperty("id").GetGuid(), code = "WH2", name = "Second", type = "Physical" })))
            .GetProperty("id").GetGuid();
        var uoms = (await Json(await Post("/api/v1/catalog/uoms/seed-standard"))).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var wac = (await Json(await Post("/api/v1/catalog/categories", new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))).GetProperty("id").GetGuid();
        var fifo = (await Json(await Post("/api/v1/catalog/categories", new { code = "FIFO", nameEn = "Fifo", nameAr = "فيفو", defaultCostingMethod = "FIFO", postingGroup = "GOODS" }))).GetProperty("id").GetGuid();

        async Task<Guid> Product(string sku, Guid category, string tracking = "None") =>
            (await Json(await Post("/api/v1/catalog/products", new { sku, nameEn = sku, nameAr = sku, type = "Stockable", baseUomId = uoms["PCS"], categoryId = category, tracking })))
            .GetProperty("id").GetGuid();

        return new Store(t, le, main, second, uoms["PCS"], await Product("PLAIN", wac), await Product("BATCHED", wac, "Batch"), await Product("SERIAL", wac, "Serial"),
            await Product("FIFOITEM", fifo));
    }

    private async Task OpeningStockAsync(Store s, Guid warehouse, params object[] lines)
    {
        var doc = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/opening-stock", new { warehouseId = warehouse, date = "2026-01-15", lines }));
        var id = doc.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/submit"));
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/approve"));
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/post", idempotent: true));
    }

    private async Task<T> AsModuleAsync<T>(Store s, Func<IServiceProvider, Task<T>> work, bool transaction = false)
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

        try
        {
            var result = await work(scope.ServiceProvider);
            if (transaction)
            {
                await session.CommitAsync();
            }

            return result;
        }
        catch
        {
            if (transaction)
            {
                await session.RollbackAsync();
            }

            throw;
        }
    }

    /// <summary>Posts through the contract on the given date; commits only on success.</summary>
    private async Task<Erp.SharedKernel.Results.Result<InventoryPostingResult>> PostAsync(Store s, string date, string docType, Guid docId, params InventoryPostingLine[] lines)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(s.T.TenantId);
        var session = scope.ServiceProvider.GetRequiredService<ErpDbSession>();
        await session.BeginTransactionAsync();
        var result = await scope.ServiceProvider.GetRequiredService<IInventoryPostingService>().PostAsync(
            new InventoryPostingRequest(null, DateOnly.Parse(date), "sales", docType, docId, "T-" + docId.ToString("N")[..6], lines), Ct);
        if (result.IsSuccess)
        {
            await session.CommitAsync();
        }
        else
        {
            await session.RollbackAsync();
        }

        return result;
    }

    private Task<decimal> RoleAsync(Store s, string role) =>
        AsModuleAsync(s, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(s.LegalEntityId, role, new DateOnly(2026, 12, 31), Ct));

    private Task<decimal> ValuationAsync(Store s) => AsModuleAsync(s, sp => sp.GetRequiredService<IStockQueries>().GetValuationTotalAsync(s.LegalEntityId, Ct));

    private static async Task<decimal> OnHandAsync(Store s, Guid product, Guid warehouse) =>
        (await Get(s, $"/api/v1/inventory/stock?productId={product}&warehouseId={warehouse}")).GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("onHand").GetDecimal());

    private static InventoryPostingLine Line(Store s, Guid product, decimal quantity, string type, decimal? unitCost = null, Guid? warehouse = null) =>
        new(Guid.NewGuid(), product, null, warehouse ?? s.Main, s.Pcs, quantity, type, unitCost);

    [Fact]
    public async Task A_return_restocks_each_batch_it_was_issued_from_in_proportion()
    {
        var s = await NewStoreAsync();
        await OpeningStockAsync(s, s.Main,
            new { productId = s.Batched, uomId = s.Pcs, quantity = 6, unitCost = 10m, batchNumber = "A", expiryDate = "2027-01-01" },
            new { productId = s.Batched, uomId = s.Pcs, quantity = 10, unitCost = 20m, batchNumber = "B", expiryDate = "2027-06-01" });
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var issue = await PostAsync(s, "2026-06-01", "SalesInvoice", invoiceId, new InventoryPostingLine(lineId, s.Batched, null, s.Main, s.Pcs, -10, InventoryMovementTypes.SalesIssue));
        issue.IsSuccess.ShouldBeTrue(issue.IsFailure ? issue.Error.Message : null);
        var first = issue.Value.Lines[0].Movements.Single(m => m.BatchNumber == "A");

        // Return 5 of 10 (6 from A, 4 from B) naming only the first issue movement: 3 back to A, 2 back to B.
        var returned = await PostAsync(s, "2026-06-05", "SalesReturn", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Batched, null, s.Main, s.Pcs, 5, InventoryMovementTypes.SalesReturn, Batch: new BatchInput("A"), CostSourceMovementId: first.MovementId));
        returned.IsSuccess.ShouldBeTrue(returned.IsFailure ? returned.Error.Message : null);
        returned.Value.Lines[0].Movements.Select(m => (m.BatchNumber, m.BaseQuantity)).ShouldBe([("A", 3m), ("B", 2m)], ignoreOrder: true);

        var issues = await AsModuleAsync(s, sp => sp.GetRequiredService<IStockQueries>().GetReturnableIssuesAsync("SalesInvoice", invoiceId, lineId, Ct));
        issues.Select(i => (i.BatchNumber, i.IssuedQuantity, i.ReturnedQuantity, i.ReturnableQuantity))
            .ShouldBe([("A", 6m, 3m, 3m), ("B", 4m, 2m, 2m)], ignoreOrder: true);

        // The rest comes back the same way; each part at its own issue cost, so valuation returns to the opening value.
        var rest = await PostAsync(s, "2026-06-06", "SalesReturn", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Batched, null, s.Main, s.Pcs, 5, InventoryMovementTypes.SalesReturn, CostSourceMovementId: first.MovementId));
        rest.Value.Lines[0].Movements.Select(m => (m.BatchNumber, m.BaseQuantity)).ShouldBe([("A", 3m), ("B", 2m)], ignoreOrder: true);
        (await ValuationAsync(s)).ShouldBe(260m);
        var batches = await Get(s, $"/api/v1/inventory/returnable-issues?sourceDocType=SalesInvoice&sourceDocId={invoiceId}&sourceLineId={lineId}");
        batches.EnumerateArray().Sum(b => b.GetProperty("returnableQuantity").GetDecimal()).ShouldBe(0m);
    }

    [Fact]
    public async Task Reversing_a_fifo_cost_adjustment_on_a_partly_consumed_layer_is_exact()
    {
        var s = await NewStoreAsync();
        var receipt = await PostAsync(s, "2026-06-01", "GoodsReceipt", Guid.NewGuid(), Line(s, s.FifoItem, 10, InventoryMovementTypes.PurchaseReceipt, 5m));
        var invoiceId = Guid.NewGuid();
        var adjust = await AsModuleAsync(s, sp => sp.GetRequiredService<IInventoryPostingService>().AdjustCostAsync(
            new CostAdjustmentRequest(receipt.Value.Lines[0].Movements[0].MovementId, 20m, new DateOnly(2026, 6, 2), "procurement", "PurchaseInvoice", invoiceId, Guid.NewGuid()), Ct),
            transaction: true);
        adjust.Value.AppliedToInventory.ShouldBe(20m); // the whole layer was on hand: unit cost 5 → 7

        var issue = await PostAsync(s, "2026-06-03", "SalesInvoice", Guid.NewGuid(), Line(s, s.FifoItem, -4, InventoryMovementTypes.SalesIssue));
        issue.Value.TotalCost.ShouldBe(-28m);
        (await ValuationAsync(s)).ShouldBe(42m);

        // Reversal: only 6/10 of the 20 is still in stock (12); the 8 consumed at 7 is credited to purchase price variance.
        var reversed = await AsModuleAsync(s, sp => sp.GetRequiredService<IInventoryPostingService>().ReverseAsync("PurchaseInvoice", invoiceId, new DateOnly(2026, 6, 4), Ct),
            transaction: true);
        reversed.IsSuccess.ShouldBeTrue(reversed.IsFailure ? reversed.Error.Message : null);
        (await ValuationAsync(s)).ShouldBe(30m); // 6 units back at 5
        (await RoleAsync(s, PostingRoles.PurchasePriceVariance)).ShouldBe(-8m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(8m); // the caller's own journal reversal takes the full 20 out

        var next = await PostAsync(s, "2026-06-05", "SalesInvoice", Guid.NewGuid(), Line(s, s.FifoItem, -6, InventoryMovementTypes.SalesIssue));
        next.Value.TotalCost.ShouldBe(-30m);
        (await ValuationAsync(s)).ShouldBe(0m);
    }

    [Fact]
    public async Task A_direct_transfer_moves_stock_in_one_posting_without_transit()
    {
        var s = await NewStoreAsync();
        await OpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 100, unitCost = 5m });
        var transfer = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/transfers", new
        {
            fromWarehouseId = s.Main, toWarehouseId = s.Second, date = "2026-03-01", direct = true, lines = new[] { new { productId = s.Plain, uomId = s.Pcs, quantity = 30 } },
        }));
        transfer.GetProperty("direct").GetBoolean().ShouldBeTrue();
        var id = transfer.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/approve"));
        var moved = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/issue", new { date = "2026-03-01" }, idempotent: true));
        moved.GetProperty("status").GetString().ShouldBe("Completed");
        moved.GetProperty("lines")[0].GetProperty("receivedBaseQuantity").GetDecimal().ShouldBe(30m);
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(70m);
        (await OnHandAsync(s, s.Plain, s.Second)).ShouldBe(30m);
        (await RoleAsync(s, PostingRoles.InventoryInTransit)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(500m);
        (await ValuationAsync(s)).ShouldBe(500m);
        var movements = await Get(s, $"/api/v1/inventory/movements?sourceDocId={id}");
        movements.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("warehouseId").GetGuid()).Distinct().ShouldBe([s.Main, s.Second], ignoreOrder: true);
    }

    [Fact]
    public async Task Partial_transfer_receipts_leave_no_residue_in_transit()
    {
        var s = await NewStoreAsync();
        await OpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 3, unitCost = 33.333333m });
        var transfer = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/transfers", new
        {
            fromWarehouseId = s.Main, toWarehouseId = s.Second, date = "2026-03-01", lines = new[] { new { productId = s.Plain, uomId = s.Pcs, quantity = 3 } },
        }));
        var id = transfer.GetProperty("id").GetGuid();
        var lineId = transfer.GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/approve"));
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/issue", new { date = "2026-03-01" }, idempotent: true));
        var inTransit = await RoleAsync(s, PostingRoles.InventoryInTransit);
        inTransit.ShouldBe(100m);

        for (var i = 0; i < 3; i++)
        {
            await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/receive", new { date = "2026-03-02", lines = new[] { new { lineId, quantity = 1 } } }, idempotent: true));
        }

        // 33.3333 + 33.3333 + the remainder 33.3334: exactly what left the source.
        (await RoleAsync(s, PostingRoles.InventoryInTransit)).ShouldBe(0m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(await ValuationAsync(s));
        var done = await Get(s, $"/api/v1/inventory/transfers/{id}");
        done.GetProperty("status").GetString().ShouldBe("Completed");
    }

    [Fact]
    public async Task Serial_tracked_items_are_counted_by_serial()
    {
        var s = await NewStoreAsync();
        await OpeningStockAsync(s, s.Main, new { productId = s.Serial, uomId = s.Pcs, quantity = 3, unitCost = 100m, serials = new[] { "SN-1", "SN-2", "SN-3" } });
        var count = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/counts", new { warehouseId = s.Main, date = "2026-04-01" }));
        var id = count.GetProperty("id").GetGuid();
        var snap = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/snapshot"));
        snap.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("serial").GetString()).ShouldBe(["SN-1", "SN-2", "SN-3"], ignoreOrder: true);

        // SN-2 is missing; SN-9 turned up.
        await Json(await Send(s, HttpMethod.Put, $"/api/v1/inventory/counts/{id}/counts",
            new { entries = new[] { new { productId = s.Serial, countedQuantity = 0, serials = new[] { "SN-1", "SN-3", "SN-9" } } } }));
        var finished = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/finish"));
        finished.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("serial").GetString()!, l => l.GetProperty("variance").GetDecimal())
            .ShouldBe(new Dictionary<string, decimal> { ["SN-1"] = 0m, ["SN-2"] = -1m, ["SN-3"] = 0m, ["SN-9"] = 1m }, ignoreOrder: true);
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/approve"));
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/post", idempotent: true));
        posted.GetProperty("status").GetString().ShouldBe("Posted");

        (await OnHandAsync(s, s.Serial, s.Main)).ShouldBe(3m);
        var serials = await Get(s, $"/api/v1/inventory/serials?productId={s.Serial}");
        serials.EnumerateArray().ToDictionary(x => x.GetProperty("serial").GetString()!, x => x.GetProperty("status").GetString())
            .ShouldBe(new Dictionary<string, string?> { ["SN-1"] = "InStock", ["SN-2"] = "Issued", ["SN-3"] = "InStock", ["SN-9"] = "InStock" }, ignoreOrder: true);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(await ValuationAsync(s));
    }

    [Fact]
    public async Task Bins_put_away_pick_transfer_and_reverse_with_the_stock()
    {
        var s = await NewStoreAsync();
        var a = (await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/warehouses/{s.Main}/bins", new { code = "a-01", name = "Aisle A", isDefaultReceiving = true })))
            .GetProperty("id").GetGuid();
        var b = (await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/warehouses/{s.Main}/bins", new { code = "B-01", isDefaultReceiving = false }))).GetProperty("id").GetGuid();
        (await (await Send(s, HttpMethod.Post, $"/api/v1/inventory/warehouses/{s.Main}/bins", new { code = "A-01" })).ErrorCodeAsync()).ShouldBe("inventory.bin-duplicate");

        await OpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 10, unitCost = 5m });
        async Task<Dictionary<string, decimal>> BinStock()
        {
            var rows = await Get(s, $"/api/v1/inventory/warehouses/{s.Main}/bin-stock?productId={s.Plain}");
            return rows.EnumerateArray().ToDictionary(r => r.GetProperty("binCode").GetString() ?? "-", r => r.GetProperty("quantity").GetDecimal());
        }

        (await BinStock()).ShouldBe(new Dictionary<string, decimal> { ["A-01"] = 10m }); // put away to the default receiving bin

        (await PostAsync(s, "2026-06-01", "SalesInvoice", Guid.NewGuid(), Line(s, s.Plain, -4, InventoryMovementTypes.SalesIssue))).IsSuccess.ShouldBeTrue();
        (await BinStock()).ShouldBe(new Dictionary<string, decimal> { ["A-01"] = 6m }); // picked automatically

        await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/bin-transfers",
            new { warehouseId = s.Main, productId = s.Plain, fromBinId = a, toBinId = b, quantity = 3m }, idempotent: true));
        (await BinStock()).ShouldBe(new Dictionary<string, decimal> { ["A-01"] = 3m, ["B-01"] = 3m }, ignoreOrder: true);
        (await (await Send(s, HttpMethod.Post, "/api/v1/inventory/bin-transfers",
            new { warehouseId = s.Main, productId = s.Plain, fromBinId = a, toBinId = b, quantity = 9m }, idempotent: true)).ErrorCodeAsync()).ShouldBe("inventory.bin-insufficient");

        var tooMuch = await PostAsync(s, "2026-06-02", "SalesInvoice", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -5, InventoryMovementTypes.SalesIssue, BinId: b));
        tooMuch.Error.Code.ShouldBe("inventory.bin-insufficient");

        var picked = Guid.NewGuid();
        (await PostAsync(s, "2026-06-02", "SalesInvoice", picked,
            new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -3, InventoryMovementTypes.SalesIssue, BinId: b))).IsSuccess.ShouldBeTrue();
        (await BinStock()).ShouldBe(new Dictionary<string, decimal> { ["A-01"] = 3m });

        var reversed = await AsModuleAsync(s, sp => sp.GetRequiredService<IInventoryPostingService>().ReverseAsync("SalesInvoice", picked, new DateOnly(2026, 6, 3), Ct), transaction: true);
        reversed.IsSuccess.ShouldBeTrue();
        (await BinStock()).ShouldBe(new Dictionary<string, decimal> { ["A-01"] = 3m, ["B-01"] = 3m }, ignoreOrder: true);
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(6m);
    }

    [Fact]
    public async Task Recost_replays_back_dated_postings_and_corrects_issue_costs()
    {
        var s = await NewStoreAsync();
        await PostAsync(s, "2026-06-01", "GoodsReceipt", Guid.NewGuid(), Line(s, s.Plain, 10, InventoryMovementTypes.PurchaseReceipt, 10m));
        var issue = await PostAsync(s, "2026-06-10", "SalesInvoice", Guid.NewGuid(), Line(s, s.Plain, -5, InventoryMovementTypes.SalesIssue));
        issue.Value.TotalCost.ShouldBe(-50m);
        await PostAsync(s, "2026-06-05", "GoodsReceipt", Guid.NewGuid(), Line(s, s.Plain, 10, InventoryMovementTypes.PurchaseReceipt, 16m)); // back-dated
        (await ValuationAsync(s)).ShouldBe(210m);

        // In date order the issue saw an average of 13: it should have cost 65.
        var preview = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/recost",
            new { productId = s.Plain, warehouseId = s.Main, fromDate = "2026-06-01", preview = true }, idempotent: true));
        preview.GetProperty("status").GetString().ShouldBe("Preview");
        preview.GetProperty("totalDifference").GetDecimal().ShouldBe(15m);
        (await ValuationAsync(s)).ShouldBe(210m);

        var run = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/recost", new { productId = s.Plain, warehouseId = s.Main, fromDate = "2026-06-01" }, idempotent: true));
        run.GetProperty("status").GetString().ShouldBe("Completed");
        var correction = run.GetProperty("corrections")[0];
        correction.GetProperty("recordedCost").GetDecimal().ShouldBe(50m);
        correction.GetProperty("recalculatedCost").GetDecimal().ShouldBe(65m);
        (await ValuationAsync(s)).ShouldBe(195m);
        (await RoleAsync(s, PostingRoles.CostOfGoodsSold)).ShouldBe(15m);
        (await RoleAsync(s, PostingRoles.Inventory)).ShouldBe(-15m); // only the correction journal (the contract postings above had none)

        // A second run finds nothing left to correct.
        var again = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/recost", new { productId = s.Plain, warehouseId = s.Main, fromDate = "2026-06-01" }, idempotent: true));
        again.GetProperty("status").GetString().ShouldBe("NothingToCorrect");
    }

    [Fact]
    public async Task With_the_policy_on_a_back_dated_posting_queues_a_recost_that_runs_after_commit()
    {
        var s = await NewStoreAsync();
        var policy = await s.T.Client.GetAsCompanyAsync("/api/v1/configuration/policies/inventory", s.T.CompanyId);
        await Json(await Send(s, HttpMethod.Put, "/api/v1/configuration/policies/inventory", new { autoRecostBackdatedPostings = true }, ifMatch: policy.Headers.ETag!.Tag));

        await PostAsync(s, "2026-06-01", "GoodsReceipt", Guid.NewGuid(), Line(s, s.Plain, 10, InventoryMovementTypes.PurchaseReceipt, 10m));
        await PostAsync(s, "2026-06-10", "SalesInvoice", Guid.NewGuid(), Line(s, s.Plain, -5, InventoryMovementTypes.SalesIssue));
        await PostAsync(s, "2026-06-05", "GoodsReceipt", Guid.NewGuid(), Line(s, s.Plain, 10, InventoryMovementTypes.PurchaseReceipt, 16m));

        var runs = await Get(s, $"/api/v1/inventory/recost-runs?productId={s.Plain}");
        var queued = runs.EnumerateArray().Single();
        queued.GetProperty("trigger").GetString().ShouldBe("BackdatedPosting");

        // Deliver the queued event the way the outbox does (without draining every tenant's outbox, which races other tests).
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(s.T.TenantId);
            scope.ServiceProvider.GetRequiredService<IOutboxMessageContext>().Set(Guid.NewGuid());
            var @event = new Erp.Modules.Inventory.Domain.RecostRequested(queued.GetProperty("id").GetGuid(), s.T.CompanyId);
            foreach (var handler in scope.ServiceProvider.GetServices<IIntegrationEventHandler<Erp.Modules.Inventory.Domain.RecostRequested>>())
            {
                await handler.HandleAsync(@event, Ct);
            }
        }

        var completed = (await Get(s, $"/api/v1/inventory/recost-runs?productId={s.Plain}")).EnumerateArray().Single();
        completed.GetProperty("status").GetString().ShouldBe("Completed");
        completed.GetProperty("totalDifference").GetDecimal().ShouldBe(15m);
        (await ValuationAsync(s)).ShouldBe(195m);
    }
}
