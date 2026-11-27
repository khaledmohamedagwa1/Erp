using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class InventoryTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Store(
        ErpApiFixture.TenantSession T,
        Guid LegalEntityId,
        Guid Main,
        Guid Second,
        Guid Pcs,
        Guid Box,
        Guid Plain,
        Guid Batched,
        Guid Serial,
        Guid FifoItem);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Store s, HttpMethod method, string url, object? body = null, bool idempotent = false)
    {
        var request = new HttpRequestMessage(method, url).WithCompany(s.T.CompanyId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotent)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return s.T.Client.SendAsync(request);
    }

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

        async Task<Guid> Product(string sku, Guid category, string tracking = "None", bool expiry = false) =>
            (await Json(await Post("/api/v1/catalog/products", new
            {
                sku, nameEn = sku, nameAr = sku, type = "Stockable", baseUomId = uoms["PCS"], categoryId = category, tracking, expiryTracked = expiry,
                units = new[] { new { uomId = uoms["BOX"], factorToBase = 10m } },
            }))).GetProperty("id").GetGuid();

        return new Store(t, le, main, second, uoms["PCS"], uoms["BOX"],
            await Product("PLAIN", wac), await Product("BATCHED", wac, "Batch", expiry: true), await Product("SERIAL", wac, "Serial"), await Product("FIFOITEM", fifo));
    }

    private async Task<JsonElement> PostOpeningStockAsync(Store s, Guid warehouse, params object[] lines)
    {
        var doc = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/opening-stock", new { warehouseId = warehouse, date = "2026-01-15", lines }));
        var id = doc.GetProperty("id").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/submit"));
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/approve"));
        return await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/post", idempotent: true));
    }

    private async Task<decimal> OnHandAsync(Store s, Guid product, Guid warehouse)
    {
        var page = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/inventory/stock?productId={product}&warehouseId={warehouse}", s.T.CompanyId));
        return page.GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("onHand").GetDecimal());
    }

    private async Task<T> AsModuleAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work, bool transaction = true, bool commit = true, Guid? companyId = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        if (companyId is { } company)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetCompany(company);
        }

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
                if (commit)
                {
                    await session.CommitAsync();
                }
                else
                {
                    await session.RollbackAsync();
                }
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

    private Task<decimal> RoleBalanceAsync(Store s, string role) =>
        AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(s.LegalEntityId, role, new DateOnly(2026, 12, 31)), transaction: false, companyId: s.T.CompanyId);

    private Task<decimal> ValuationAsync(Store s) =>
        AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<IStockQueries>().GetValuationTotalAsync(s.LegalEntityId), transaction: false);

    /// <summary>Posts through the contract; commits only when the posting succeeded (as a calling module must).</summary>
    private async Task<Erp.SharedKernel.Results.Result<InventoryPostingResult>> PostAsync(Store s, string docType, Guid docId, params InventoryPostingLine[] lines)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(s.T.TenantId);
        var session = scope.ServiceProvider.GetRequiredService<ErpDbSession>();
        await session.BeginTransactionAsync();
        var result = await scope.ServiceProvider.GetRequiredService<IInventoryPostingService>().PostAsync(
            new InventoryPostingRequest(null, new DateOnly(2026, 6, 1), "sales", docType, docId, "T-" + docId.ToString("N")[..6], lines), Ct);
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

    [Fact]
    public async Task Opening_stock_posts_the_ledger_valuation_and_a_balanced_journal()
    {
        var s = await NewStoreAsync();
        var posted = await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Box, quantity = 10, unitCost = 5m });
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("totalCost").GetDecimal().ShouldBe(500m);
        posted.GetProperty("journalId").GetGuid().ShouldNotBe(Guid.Empty);

        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(100m);
        (await RoleBalanceAsync(s, PostingRoles.Inventory)).ShouldBe(500m);
        (await RoleBalanceAsync(s, PostingRoles.OpeningBalanceEquity)).ShouldBe(-500m);
        (await ValuationAsync(s)).ShouldBe(500m);
    }

    [Fact]
    public async Task Adjustments_post_gains_at_current_cost_and_losses_to_the_gl()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 100, unitCost = 5m });
        var adj = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/adjustments", new
        {
            warehouseId = s.Main, date = "2026-02-01", reason = "Damage",
            lines = new object[] { new { productId = s.Plain, uomId = s.Pcs, quantity = -10 }, new { productId = s.Plain, uomId = s.Pcs, quantity = 4 } },
        }));
        var id = adj.GetProperty("id").GetGuid();
        (await Send(s, HttpMethod.Post, $"/api/v1/inventory/adjustments/{id}/post", idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // not approved yet
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/adjustments/{id}/approve"));
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/adjustments/{id}/post", idempotent: true));
        posted.GetProperty("netCost").GetDecimal().ShouldBe(-30m);

        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(94m);
        (await RoleBalanceAsync(s, PostingRoles.InventoryAdjustmentLoss)).ShouldBe(50m);
        (await RoleBalanceAsync(s, PostingRoles.InventoryAdjustmentGain)).ShouldBe(-20m);
        (await RoleBalanceAsync(s, PostingRoles.Inventory)).ShouldBe(470m);
        (await ValuationAsync(s)).ShouldBe(470m);
    }

    [Fact]
    public async Task Transfer_goes_through_transit_receives_partially_and_closes_with_a_loss()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 100, unitCost = 5m });
        var transfer = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/transfers", new
        {
            fromWarehouseId = s.Main, toWarehouseId = s.Second, date = "2026-03-01", lines = new[] { new { productId = s.Plain, uomId = s.Box, quantity = 3 } },
        }));
        var id = transfer.GetProperty("id").GetGuid();
        var lineId = transfer.GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/approve"));
        var issued = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/issue", new { date = "2026-03-01" }, idempotent: true));
        issued.GetProperty("status").GetString().ShouldBe("InTransit");
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(70m);
        (await RoleBalanceAsync(s, PostingRoles.InventoryInTransit)).ShouldBe(150m);

        var received = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/receive",
            new { date = "2026-03-03", lines = new[] { new { lineId, quantity = 2 } } }, idempotent: true));
        received.GetProperty("status").GetString().ShouldBe("PartiallyReceived");
        (await OnHandAsync(s, s.Plain, s.Second)).ShouldBe(20m);

        var tooMuch = await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/receive",
            new { date = "2026-03-03", lines = new[] { new { lineId, quantity = 2 } } }, idempotent: true);
        (await tooMuch.ErrorCodeAsync()).ShouldBe("inventory.receipt-exceeds-issued");

        var closed = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/transfers/{id}/close", new { date = "2026-03-05" }, idempotent: true));
        closed.GetProperty("status").GetString().ShouldBe("Completed");
        closed.GetProperty("lines")[0].GetProperty("lostBaseQuantity").GetDecimal().ShouldBe(10m);

        (await RoleBalanceAsync(s, PostingRoles.InventoryInTransit)).ShouldBe(0m);
        (await RoleBalanceAsync(s, PostingRoles.InventoryAdjustmentLoss)).ShouldBe(50m);
        var gl = await RoleBalanceAsync(s, PostingRoles.Inventory) + await RoleBalanceAsync(s, PostingRoles.InventoryInTransit);
        gl.ShouldBe(450m);
        (await ValuationAsync(s)).ShouldBe(gl);
    }

    [Fact]
    public async Task Stock_count_counts_movements_posted_after_the_snapshot()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 100, unitCost = 5m });
        var count = await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/counts", new { warehouseId = s.Main, date = "2026-04-01" }));
        var id = count.GetProperty("id").GetGuid();
        var snap = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/snapshot"));
        snap.GetProperty("lines")[0].GetProperty("snapshotQuantity").GetDecimal().ShouldBe(100m);

        // 7 units leave after the snapshot.
        (await PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -7, InventoryMovementTypes.SalesIssue))).IsSuccess.ShouldBeTrue();

        await Json(await Send(s, HttpMethod.Put, $"/api/v1/inventory/counts/{id}/counts", new { entries = new[] { new { productId = s.Plain, countedQuantity = 95 } } }));
        var finished = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/finish"));
        var line = finished.GetProperty("lines")[0];
        line.GetProperty("movementsSinceSnapshot").GetDecimal().ShouldBe(-7m);
        line.GetProperty("variance").GetDecimal().ShouldBe(2m);
        await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/approve"));
        var posted = await Json(await Send(s, HttpMethod.Post, $"/api/v1/inventory/counts/{id}/post", idempotent: true));
        posted.GetProperty("status").GetString().ShouldBe("Posted");
        posted.GetProperty("adjustmentId").GetGuid().ShouldNotBe(Guid.Empty);
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(95m);
        (await ValuationAsync(s)).ShouldBe(475m);
    }

    [Fact]
    public async Task Twenty_parallel_issues_for_ten_units_never_go_negative()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 10, unitCost = 5m });
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -1, InventoryMovementTypes.SalesIssue))));
        results.Count(r => r.IsSuccess).ShouldBe(10);
        results.Where(r => r.IsFailure).ShouldAllBe(r => r.Error.Code == "inventory.insufficient-stock");
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(0m);
        (await ValuationAsync(s)).ShouldBe(0m);
    }

    [Fact]
    public async Task Posting_the_same_source_twice_returns_the_first_result_and_reversal_restores_stock()
    {
        var s = await NewStoreAsync();
        var doc = Guid.NewGuid();
        var line = new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, 20, InventoryMovementTypes.PurchaseReceipt, UnitCost: 6m);
        var first = await PostAsync(s, "GoodsReceipt", doc, line);
        first.Value.AlreadyPosted.ShouldBeFalse();
        first.Value.TotalCost.ShouldBe(120m);
        var again = await PostAsync(s, "GoodsReceipt", doc, line);
        again.Value.AlreadyPosted.ShouldBeTrue();
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(20m);

        var reversed = await AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<IInventoryPostingService>().ReverseAsync("GoodsReceipt", doc, new DateOnly(2026, 6, 2), Ct));
        reversed.IsSuccess.ShouldBeTrue();
        reversed.Value.TotalCost.ShouldBe(-120m);
        (await OnHandAsync(s, s.Plain, s.Main)).ShouldBe(0m);
        (await ValuationAsync(s)).ShouldBe(0m);
    }

    [Fact]
    public async Task Posting_with_a_journal_uses_the_counter_role()
    {
        var s = await NewStoreAsync();
        var result = await AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<IInventoryPostingService>().PostAsync(new InventoryPostingRequest(
            s.LegalEntityId, new DateOnly(2026, 6, 1), SourceModules.Procurement, "GoodsReceipt", Guid.NewGuid(), "GRN-1",
            [new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Box, 2, InventoryMovementTypes.PurchaseReceipt, UnitCost: 3m, CounterRole: PostingRoles.GoodsReceivedNotInvoiced)],
            PostJournal: true), Ct));
        result.Value.JournalId.ShouldNotBeNull();
        result.Value.TotalCost.ShouldBe(60m);
        (await RoleBalanceAsync(s, PostingRoles.Inventory)).ShouldBe(60m);
        (await RoleBalanceAsync(s, PostingRoles.GoodsReceivedNotInvoiced)).ShouldBe(-60m);
    }

    [Fact]
    public async Task Batches_are_issued_first_expiry_first_and_expired_batches_are_blocked()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main,
            new { productId = s.Batched, uomId = s.Pcs, quantity = 10, unitCost = 2m, batchNumber = "OLD", expiryDate = "2026-02-01" },
            new { productId = s.Batched, uomId = s.Pcs, quantity = 10, unitCost = 2m, batchNumber = "LATE", expiryDate = "2027-12-31" },
            new { productId = s.Batched, uomId = s.Pcs, quantity = 10, unitCost = 2m, batchNumber = "SOON", expiryDate = "2027-01-31" });

        var issue = await PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Batched, null, s.Main, s.Pcs, -12, InventoryMovementTypes.SalesIssue));
        issue.IsSuccess.ShouldBeTrue(issue.IsFailure ? issue.Error.Message : null);
        // FEFO decides how much comes from each batch; movements are posted in lock order (batch id), so compare without order.
        issue.Value.Lines[0].Movements.Select(m => (m.BatchNumber, m.BaseQuantity)).ShouldBe([("SOON", -10m), ("LATE", -2m)], ignoreOrder: true);

        var expired = await PostAsync(s, "SalesInvoice", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Batched, null, s.Main, s.Pcs, -1, InventoryMovementTypes.SalesIssue, Batch: new BatchInput("OLD")));
        expired.Error.Code.ShouldBe("inventory.batch-expired");

        var missingBatch = await PostAsync(s, "GoodsReceipt", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Batched, null, s.Main, s.Pcs, 1, InventoryMovementTypes.PurchaseReceipt, UnitCost: 1m));
        missingBatch.Error.Code.ShouldBe("inventory.batch-required");
    }

    [Fact]
    public async Task Serial_numbers_follow_the_stock()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Serial, uomId = s.Pcs, quantity = 2, unitCost = 100m, serials = new[] { "SN-1", "SN-2" } });
        (await PostAsync(s, "SalesInvoice", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Serial, null, s.Main, s.Pcs, -1, InventoryMovementTypes.SalesIssue, Serials: ["SN-1"]))).IsSuccess.ShouldBeTrue();
        var again = await PostAsync(s, "SalesInvoice", Guid.NewGuid(),
            new InventoryPostingLine(Guid.NewGuid(), s.Serial, null, s.Main, s.Pcs, -1, InventoryMovementTypes.SalesIssue, Serials: ["SN-1"]));
        again.Error.Code.ShouldBe("inventory.serial-not-in-stock");

        var serials = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/inventory/serials?productId={s.Serial}", s.T.CompanyId));
        serials.EnumerateArray().ToDictionary(x => x.GetProperty("serial").GetString()!, x => x.GetProperty("status").GetString())
            .ShouldBe(new Dictionary<string, string?> { ["SN-1"] = "Issued", ["SN-2"] = "InStock" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Reserved_stock_is_not_available_except_to_its_demand()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 10, unitCost = 5m });
        var order = Guid.NewGuid();
        var orderLine = Guid.NewGuid();
        await Json(await Send(s, HttpMethod.Post, "/api/v1/inventory/reservations",
            new { demandType = "SalesOrder", demandId = order, demandLineId = orderLine, productId = s.Plain, warehouseId = s.Main, baseQuantity = 8 }, idempotent: true));

        var other = await PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -5, InventoryMovementTypes.SalesIssue));
        other.Error.Code.ShouldBe("inventory.insufficient-stock");

        var fulfil = await PostAsync(s, "Delivery", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -5, InventoryMovementTypes.SalesIssue,
            ConsumeReservation: new ReservationRef("SalesOrder", order, orderLine)));
        fulfil.IsSuccess.ShouldBeTrue();
        var reservations = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/inventory/reservations?demandType=SalesOrder&demandId={order}", s.T.CompanyId));
        reservations[0].GetProperty("open").GetDecimal().ShouldBe(3m);
        var stock = await Json(await s.T.Client.GetAsCompanyAsync($"/api/v1/inventory/stock?productId={s.Plain}", s.T.CompanyId));
        stock.GetProperty("items")[0].GetProperty("available").GetDecimal().ShouldBe(2m);
    }

    [Fact]
    public async Task Fifo_and_purchase_price_variance_adjustment()
    {
        var s = await NewStoreAsync();
        var r1 = await PostAsync(s, "GoodsReceipt", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.FifoItem, null, s.Main, s.Pcs, 10, InventoryMovementTypes.PurchaseReceipt, UnitCost: 5m));
        await PostAsync(s, "GoodsReceipt", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.FifoItem, null, s.Main, s.Pcs, 10, InventoryMovementTypes.PurchaseReceipt, UnitCost: 7m));
        var issue = await PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.FifoItem, null, s.Main, s.Pcs, -12, InventoryMovementTypes.SalesIssue));
        issue.Value.TotalCost.ShouldBe(-64m);

        // Supplier invoice adds 20 to the first receipt; only 0 of its 10 units remain (all consumed) → all variance.
        var receiptMovement = r1.Value.Lines[0].Movements[0].MovementId;
        var adjust = await AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<IInventoryPostingService>().AdjustCostAsync(
            new CostAdjustmentRequest(receiptMovement, 20m, new DateOnly(2026, 6, 5), "procurement", "PurchaseInvoice", Guid.NewGuid(), Guid.NewGuid()), Ct));
        adjust.Value.AppliedToInventory.ShouldBe(0m);
        adjust.Value.ToVariance.ShouldBe(20m);
        (await ValuationAsync(s)).ShouldBe(56m);

        // FIFO items never go negative even if the policy allowed it.
        var tooMuch = await PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.FifoItem, null, s.Main, s.Pcs, -9, InventoryMovementTypes.SalesIssue));
        tooMuch.Error.Code.ShouldBe("inventory.insufficient-stock");
    }

    [Fact]
    public async Task Weighted_average_cost_adjustment_applies_the_on_hand_share()
    {
        var s = await NewStoreAsync();
        var r = await PostAsync(s, "GoodsReceipt", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, 10, InventoryMovementTypes.PurchaseReceipt, UnitCost: 10m));
        await PostAsync(s, "SalesInvoice", Guid.NewGuid(), new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, -4, InventoryMovementTypes.SalesIssue));
        var adjust = await AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<IInventoryPostingService>().AdjustCostAsync(
            new CostAdjustmentRequest(r.Value.Lines[0].Movements[0].MovementId, 20m, new DateOnly(2026, 6, 5), "procurement", "PurchaseInvoice", Guid.NewGuid(), Guid.NewGuid()), Ct));
        adjust.Value.AppliedToInventory.ShouldBe(12m);
        adjust.Value.ToVariance.ShouldBe(8m);
        (await ValuationAsync(s)).ShouldBe(72m);
    }

    [Fact]
    public async Task Inventory_ledger_rows_cannot_be_changed_or_deleted()
    {
        var s = await NewStoreAsync();
        await PostOpeningStockAsync(s, s.Main, new { productId = s.Plain, uomId = s.Pcs, quantity = 1, unitCost = 5m });
        await using var connection = await fixture.OpenAsTenantAsync(s.T.TenantId);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE inventory.InventoryMovement SET UnitCost = 0";
        (await Should.ThrowAsync<SqlException>(() => update.ExecuteNonQueryAsync(Ct))).Message.ShouldContain("immutable");
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM inventory.InventoryMovement";
        (await Should.ThrowAsync<SqlException>(() => delete.ExecuteNonQueryAsync(Ct))).Message.ShouldContain("immutable");
    }

    [Fact]
    public async Task Inventory_requires_the_module_and_an_open_period()
    {
        var s = await NewStoreAsync();
        var outside = await AsModuleAsync(s.T.TenantId, sp => sp.GetRequiredService<IInventoryPostingService>().PostAsync(new InventoryPostingRequest(
            null, new DateOnly(2025, 6, 1), "sales", "X", Guid.NewGuid(), null,
            [new InventoryPostingLine(Guid.NewGuid(), s.Plain, null, s.Main, s.Pcs, 1, InventoryMovementTypes.PurchaseReceipt, UnitCost: 1m)]), Ct), commit: false);
        outside.IsFailure.ShouldBeTrue();
        outside.Error.Code.ShouldStartWith("accounting.period");

        (await Send(s, HttpMethod.Put, "/api/v1/configuration/modules/inventory", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.T.Client.GetAsCompanyAsync("/api/v1/inventory/stock", s.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
