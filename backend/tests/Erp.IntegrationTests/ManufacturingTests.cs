using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Manufacturing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class ManufacturingTests(ErpApiFixture fixture)
{
    private const string Api = "/api/v1/manufacturing";

    private sealed record Plant(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Pcs, Guid X, Guid Y, Guid Z, Guid Fg, Guid BomId, Guid VersionId, Guid RoutingId);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(ErpApiFixture.TenantSession t, HttpMethod method, string url, object? body = null, bool idempotent = false)
    {
        var request = new HttpRequestMessage(method, url).WithCompany(t.CompanyId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotent)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return t.Client.SendAsync(request);
    }

    /// <summary>J-9 plant: FG = 2 X + 3 Y + 1 Z, one assembly operation at labour 30 / machine 50 / overhead 25 per machine hour; opening stock X 200 @5, Y 300 @2, Z 100 @4.</summary>
    private async Task<Plant> NewPlantAsync(string zIssueMethod = "Manual")
    {
        var t = await fixture.NewTenantAsync(preset: "manufacturing");
        Task<HttpResponseMessage> Post(string url, object? body = null) => Send(t, HttpMethod.Post, url, body);
        (await Post("/api/v1/accounting/coa-templates/generic")).EnsureSuccessStatusCode();
        var legal = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId));
        var le = legal.EnumerateArray().First().GetProperty("id").GetGuid();
        (await Post("/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" })).EnsureSuccessStatusCode();
        var warehouses = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId));
        var main = warehouses.GetProperty("items").EnumerateArray().First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();

        var uoms = (await Json(await Post("/api/v1/catalog/uoms/seed-standard"))).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var pcs = uoms["PCS"];
        var category = (await Json(await Post("/api/v1/catalog/categories", new { code = "MAT", nameEn = "Materials", nameAr = "خامات", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" })))
            .GetProperty("id").GetGuid();
        async Task<Guid> Product(string sku) =>
            (await Json(await Post("/api/v1/catalog/products", new { sku, nameEn = sku, nameAr = sku, type = "Stockable", baseUomId = pcs, categoryId = category, tracking = "None" })))
            .GetProperty("id").GetGuid();
        var (x, y, z, fg) = (await Product("X"), await Product("Y"), await Product("Z"), await Product("FG"));

        var opening = await Json(await Post("/api/v1/inventory/opening-stock", new
        {
            warehouseId = main, date = "2026-01-15",
            lines = new object[]
            {
                new { productId = x, uomId = pcs, quantity = 200, unitCost = 5m },
                new { productId = y, uomId = pcs, quantity = 300, unitCost = 2m },
                new { productId = z, uomId = pcs, quantity = 100, unitCost = 4m },
            },
        }));
        var openingId = opening.GetProperty("id").GetGuid();
        await Json(await Post($"/api/v1/inventory/opening-stock/{openingId}/submit"));
        await Json(await Post($"/api/v1/inventory/opening-stock/{openingId}/approve"));
        await Json(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{openingId}/post", idempotent: true));

        var wc = (await Json(await Post($"{Api}/work-centers", new
        {
            code = "ASM", nameEn = "Assembly", nameAr = "تجميع", capacityHoursPerDay = 8m, laborRatePerHour = 30m, machineRatePerHour = 50m, overheadRatePerHour = 25m, overheadBasis = "MachineHours",
        }))).GetProperty("id").GetGuid();
        var routing = (await Json(await Post($"{Api}/routings", new
        {
            productId = fg, code = "R-FG", name = "FG routing",
            operations = new[] { new { sequence = 10, name = "Assemble", workCenterId = wc, setupHours = 1m, runHoursPerUnit = 0.1m } },
        }))).GetProperty("id").GetGuid();

        var bom = (await Json(await Post($"{Api}/boms", new { productId = fg, code = "BOM-FG", name = "FG" }))).GetProperty("id").GetGuid();
        var version = (await Json(await Post($"{Api}/boms/{bom}/versions", new
        {
            effectiveFrom = "2026-01-01", outputQuantity = 1m,
            lines = new object[]
            {
                new { componentProductId = x, quantityPerOutput = 2m, uomId = pcs, scrapPercent = 0m, issueMethod = "Manual" },
                new { componentProductId = y, quantityPerOutput = 3m, uomId = pcs, scrapPercent = 0m, issueMethod = "Manual" },
                new { componentProductId = z, quantityPerOutput = 1m, uomId = pcs, scrapPercent = 0m, issueMethod = zIssueMethod },
            },
        }))).GetProperty("id").GetGuid();
        (await Json(await Post($"{Api}/boms/{bom}/versions/{version}/activate"))).GetProperty("status").GetString().ShouldBe("Active");

        return new Plant(t, le, main, pcs, x, y, z, fg, bom, version, routing);
    }

    private static async Task<JsonElement> NewReleasedOrderAsync(Plant p, decimal quantity)
    {
        var order = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders", new
        {
            productId = p.Fg, quantity, componentWarehouseId = p.Main, startDate = "2026-03-01", dueDate = "2026-03-10",
        }));
        order.GetProperty("status").GetString().ShouldBe("Draft");
        var id = order.GetProperty("id").GetGuid();
        var planned = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/plan", new { }));
        planned.GetProperty("status").GetString().ShouldBe("Planned");
        planned.GetProperty("bomVersionId").GetGuid().ShouldBe(p.VersionId);
        planned.GetProperty("routingId").GetGuid().ShouldBe(p.RoutingId);
        return await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/release"));
    }

    private static Guid LineOf(JsonElement order, Guid product) =>
        order.GetProperty("components").EnumerateArray().First(c => c.GetProperty("productId").GetGuid() == product).GetProperty("lineId").GetGuid();

    private async Task<T> QueryAsync<T>(Plant p, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(p.T.TenantId);
        tenant.SetCompany(p.T.CompanyId);
        return await work(scope.ServiceProvider);
    }

    private Task<decimal> RoleBalanceAsync(Plant p, string role) =>
        QueryAsync(p, sp => sp.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(p.LegalEntityId, role, new DateOnly(2026, 12, 31)));

    private async Task<decimal> OnHandAsync(Plant p, Guid product)
    {
        var page = await Json(await p.T.Client.GetAsCompanyAsync($"/api/v1/inventory/stock?productId={product}&warehouseId={p.Main}", p.T.CompanyId));
        return page.GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("onHand").GetDecimal());
    }

    private async Task<decimal> ValueOfAsync(Plant p, Guid product)
    {
        var rows = await Json(await p.T.Client.GetAsCompanyAsync($"/api/v1/inventory/valuation?productId={product}", p.T.CompanyId));
        var items = rows.ValueKind == JsonValueKind.Array ? rows : rows.GetProperty("items");
        return items.EnumerateArray().Sum(r => r.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task S3_production_order_end_to_end_relieves_wip_to_zero_and_values_fg_at_materials_plus_conversion()
    {
        var p = await NewPlantAsync();
        var order = await NewReleasedOrderAsync(p, 100);
        order.GetProperty("status").GetString().ShouldBe("Released");
        var id = order.GetProperty("id").GetGuid();

        var reservations = await Json(await p.T.Client.GetAsCompanyAsync($"/api/v1/inventory/reservations?demandType=ProductionOrder&demandId={id}", p.T.CompanyId));
        reservations.EnumerateArray().ToDictionary(r => r.GetProperty("productId").GetGuid(), r => r.GetProperty("open").GetDecimal())
            .ShouldBe(new Dictionary<Guid, decimal> { [p.X] = 200, [p.Y] = 300, [p.Z] = 100 }, ignoreOrder: true);

        // Issue everything (one request, all lines).
        var issued = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/issues", new
        {
            date = "2026-03-02",
            lines = new[] { new { lineId = LineOf(order, p.X), quantity = 200m }, new { lineId = LineOf(order, p.Y), quantity = 300m }, new { lineId = LineOf(order, p.Z), quantity = 100m } },
        }, idempotent: true));
        issued.GetProperty("status").GetString().ShouldBe("InProgress");
        issued.GetProperty("materialCost").GetDecimal().ShouldBe(2000m);
        (await RoleBalanceAsync(p, PostingRoles.WorkInProgress)).ShouldBe(2000m);
        reservations = await Json(await p.T.Client.GetAsCompanyAsync($"/api/v1/inventory/reservations?demandType=ProductionOrder&demandId={id}", p.T.CompanyId));
        reservations.EnumerateArray().Sum(r => r.GetProperty("open").GetDecimal()).ShouldBe(0m);

        // Posting without an idempotency key is refused.
        (await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/operations", new { sequence = 10, laborHours = 10m, machineHours = 8m })).IsSuccessStatusCode.ShouldBeFalse();

        var reported = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/operations", new { sequence = 10, laborHours = 10m, machineHours = 8m, date = "2026-03-03" }, idempotent: true));
        reported.GetProperty("laborCost").GetDecimal().ShouldBe(300m);
        reported.GetProperty("machineCost").GetDecimal().ShouldBe(400m);
        reported.GetProperty("overheadCost").GetDecimal().ShouldBe(200m);
        reported.GetProperty("wipBalance").GetDecimal().ShouldBe(2900m);
        (await RoleBalanceAsync(p, PostingRoles.AppliedLabor)).ShouldBe(-300m);
        (await RoleBalanceAsync(p, PostingRoles.AppliedMachine)).ShouldBe(-400m);
        (await RoleBalanceAsync(p, PostingRoles.AppliedOverhead)).ShouldBe(-200m);

        var first = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/receipts", new { quantity = 60m, date = "2026-03-04" }, idempotent: true));
        first.GetProperty("status").GetString().ShouldBe("PartiallyCompleted");
        first.GetProperty("receivedCost").GetDecimal().ShouldBe(1740m);
        first.GetProperty("wipBalance").GetDecimal().ShouldBe(1160m);

        var second = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/receipts", new { quantity = 40m, date = "2026-03-05" }, idempotent: true));
        second.GetProperty("status").GetString().ShouldBe("Completed");
        second.GetProperty("receivedCost").GetDecimal().ShouldBe(2900m);
        second.GetProperty("wipBalance").GetDecimal().ShouldBe(0m);

        var closed = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/close", new { date = "2026-03-06" }, idempotent: true));
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("varianceAmount").GetDecimal().ShouldBe(0m);

        // The order's WIP history equals the WIP GL movements, and both end at zero.
        closed.GetProperty("transactions").EnumerateArray().Sum(tx => tx.GetProperty("wipAmount").GetDecimal()).ShouldBe(0m);
        (await RoleBalanceAsync(p, PostingRoles.WorkInProgress)).ShouldBe(0m);
        (await RoleBalanceAsync(p, PostingRoles.ProductionVariance)).ShouldBe(0m);
        (await OnHandAsync(p, p.Fg)).ShouldBe(100m);
        (await ValueOfAsync(p, p.Fg)).ShouldBe(2900m);
        (await OnHandAsync(p, p.X)).ShouldBe(0m);
        (await OnHandAsync(p, p.Y)).ShouldBe(0m);
        (await OnHandAsync(p, p.Z)).ShouldBe(0m);
        (await RoleBalanceAsync(p, PostingRoles.Inventory)).ShouldBe(2900m);

        // Closed orders take no more work.
        (await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/scrap", new { quantity = 1m }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Short_close_releases_unused_material_and_posts_residual_wip_to_production_variance()
    {
        var p = await NewPlantAsync(zIssueMethod: "Backflush");
        var order = await NewReleasedOrderAsync(p, 10);
        var id = order.GetProperty("id").GetGuid();
        await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/issues", new
        {
            lines = new[] { new { lineId = LineOf(order, p.X), quantity = 22m }, new { lineId = LineOf(order, p.Y), quantity = 30m } },
        }, idempotent: true));
        var returned = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/returns", new
        {
            lines = new[] { new { lineId = LineOf(order, p.X), quantity = 2m } },
        }, idempotent: true));
        returned.GetProperty("wipBalance").GetDecimal().ShouldBe(160m); // X 20 @5 + Y 30 @2
        (await OnHandAsync(p, p.X)).ShouldBe(180m);

        await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/operations", new { sequence = 10, laborHours = 1m, machineHours = 0m }, idempotent: true));

        // Receipt backflushes 8 Z (32) first: WIP 160 + 30 + 32 = 222, spread over the 10 still expected → 22.2 each.
        var received = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/receipts", new { quantity = 8m }, idempotent: true));
        received.GetProperty("receivedCost").GetDecimal().ShouldBe(177.6m);
        received.GetProperty("wipBalance").GetDecimal().ShouldBe(44.4m);
        (await OnHandAsync(p, p.Z)).ShouldBe(92m);

        // Close backflushes only what the reported output consumed (nothing more here), releases the unused
        // reservations and writes the residual 44.4 off to production variance.
        var closed = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/close", new { }, idempotent: true));
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("varianceAmount").GetDecimal().ShouldBe(44.4m);
        closed.GetProperty("wipBalance").GetDecimal().ShouldBe(0m);
        (await OnHandAsync(p, p.Z)).ShouldBe(92m);
        var reservations = await Json(await p.T.Client.GetAsCompanyAsync($"/api/v1/inventory/reservations?demandType=ProductionOrder&demandId={id}", p.T.CompanyId));
        reservations.EnumerateArray().Sum(r => r.GetProperty("open").GetDecimal()).ShouldBe(0m);
        (await RoleBalanceAsync(p, PostingRoles.WorkInProgress)).ShouldBe(0m);
        (await RoleBalanceAsync(p, PostingRoles.ProductionVariance)).ShouldBe(44.4m);
        (await ValueOfAsync(p, p.Fg)).ShouldBe(177.6m);
    }

    [Fact]
    public async Task Mrp_run_plans_production_for_injected_demand_nets_stock_and_converts_to_a_draft_order()
    {
        var p = await NewPlantAsync();
        // Y keeps a safety stock of 300: 300 on hand − 150 needed leaves 150 → buy 150 (multiples of 50), five days ahead.
        await Json(await Send(p.T, HttpMethod.Put, $"{Api}/planning-parameters", new { productId = p.Y, safetyStock = 300m, leadTimeDays = 5, lotSizeRule = "Multiple", lotSize = 50m, procurement = "Buy" }));
        var runDate = new DateOnly(2026, 4, 1);
        TestMrpDemandSource.Demand[p.T.CompanyId] = [new MrpDemand(p.Fg, 50, runDate.AddDays(20), "SalesOrder", "SO-TEST-1")];

        var run = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/mrp-runs", new { runDate = "2026-04-01", horizonDays = 56 }));
        run.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ShouldContain("test-demand");
        var suggestions = run.GetProperty("suggestions").EnumerateArray().ToList();

        var produce = suggestions.Single(s => s.GetProperty("productId").GetGuid() == p.Fg);
        produce.GetProperty("action").GetString().ShouldBe("Produce");
        produce.GetProperty("quantity").GetDecimal().ShouldBe(50m);
        produce.GetProperty("dueDate").GetString().ShouldBe("2026-04-21");
        produce.GetProperty("pegging").EnumerateArray().Single().GetProperty("sourceReference").GetString().ShouldBe("SO-TEST-1");

        var buyY = suggestions.Single(s => s.GetProperty("productId").GetGuid() == p.Y);
        buyY.GetProperty("action").GetString().ShouldBe("Purchase");
        buyY.GetProperty("quantity").GetDecimal().ShouldBe(150m);
        buyY.GetProperty("orderByDate").GetString().ShouldBe("2026-04-16");
        suggestions.ShouldNotContain(s => s.GetProperty("productId").GetGuid() == p.X || s.GetProperty("productId").GetGuid() == p.Z); // covered by stock

        var runId = run.GetProperty("id").GetGuid();
        object Convert(JsonElement suggestion) => new { suggestionIds = new[] { suggestion.GetProperty("suggestionId").GetGuid() }, componentWarehouseId = p.Main };

        // Purchase suggestions stay proposals until procurement is connected.
        var refused = await Send(p.T, HttpMethod.Post, $"{Api}/mrp-runs/{runId}/convert", Convert(buyY));
        (await refused.ErrorCodeAsync()).ShouldBe("manufacturing.suggestion-not-convertible");

        var converted = await Json(await Send(p.T, HttpMethod.Post, $"{Api}/mrp-runs/{runId}/convert", Convert(produce)));
        var after = converted.GetProperty("suggestions").EnumerateArray().ToList();
        after.Single(s => s.GetProperty("productId").GetGuid() == p.Y).GetProperty("status").GetString().ShouldBe("Open");
        var fgSuggestion = after.Single(s => s.GetProperty("productId").GetGuid() == p.Fg);
        fgSuggestion.GetProperty("status").GetString().ShouldBe("Converted");
        var orderId = fgSuggestion.GetProperty("productionOrderId").GetGuid();

        var order = await Json(await p.T.Client.GetAsCompanyAsync($"{Api}/production-orders/{orderId}", p.T.CompanyId));
        order.GetProperty("status").GetString().ShouldBe("Draft");
        order.GetProperty("plannedQuantity").GetDecimal().ShouldBe(50m);
        order.GetProperty("sourceReference").GetString()!.ShouldStartWith("MRP");

        // A converted suggestion cannot be converted twice.
        (await (await Send(p.T, HttpMethod.Post, $"{Api}/mrp-runs/{runId}/convert", Convert(produce))).ErrorCodeAsync()).ShouldBe("manufacturing.suggestion-not-convertible");
        TestMrpDemandSource.Demand.TryRemove(p.T.CompanyId, out _);
    }

    [Fact]
    public async Task A_bom_cycle_cannot_be_activated()
    {
        var p = await NewPlantAsync();
        // X made from FG closes the loop FG → X → FG.
        var bom = (await Json(await Send(p.T, HttpMethod.Post, $"{Api}/boms", new { productId = p.X, code = "BOM-X", name = "X" }))).GetProperty("id").GetGuid();
        var version = (await Json(await Send(p.T, HttpMethod.Post, $"{Api}/boms/{bom}/versions", new
        {
            effectiveFrom = "2026-01-01", outputQuantity = 1m, lines = new[] { new { componentProductId = p.Fg, quantityPerOutput = 1m, uomId = p.Pcs, scrapPercent = 0m } },
        }))).GetProperty("id").GetGuid();
        var response = await Send(p.T, HttpMethod.Post, $"{Api}/boms/{bom}/versions/{version}/activate");
        (await response.ErrorCodeAsync()).ShouldBe("manufacturing.bom-cycle");
    }

    [Fact]
    public async Task Production_orders_are_isolated_between_tenants()
    {
        var p = await NewPlantAsync();
        var order = await NewReleasedOrderAsync(p, 5);
        var id = order.GetProperty("id").GetGuid();

        var other = await fixture.NewTenantAsync(preset: "manufacturing");
        (await other.Client.GetAsCompanyAsync($"{Api}/production-orders/{id}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var list = await Json(await other.Client.GetAsCompanyAsync($"{Api}/production-orders", other.CompanyId));
        list.GetProperty("items").GetArrayLength().ShouldBe(0);
        (await Send(other, HttpMethod.Post, $"{Api}/production-orders/{id}/release")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var connection = await fixture.OpenAsTenantAsync(other.TenantId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM manufacturing.ProductionOrder";
        ((int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0);
    }

    [Fact]
    public async Task Manufacturing_endpoints_require_the_module()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.GetAsCompanyAsync($"{Api}/production-orders", t.CompanyId)).IsSuccessStatusCode.ShouldBeFalse();
        ManufacturingPermissions.OrderPost.ShouldBe("manufacturing.order.post");
    }
}
