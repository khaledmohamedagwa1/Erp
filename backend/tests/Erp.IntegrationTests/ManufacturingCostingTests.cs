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

/// <summary>Standard costing, absorption clearing, substitution, purchase conversion, capacity, variants and rework (wave 6).</summary>
public class ManufacturingCostingTests(ErpApiFixture fixture)
{
    private const string Api = "/api/v1/manufacturing";

    private sealed record Plant(ErpApiFixture.TenantSession T, Guid LegalEntityId, Guid Main, Guid Pcs, Guid Category, Guid X, Guid Y, Guid Z, Guid Fg, Guid WorkCenter,
        Guid RoutingId, Guid BomId, Guid VersionId);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
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

    private static async Task<JsonElement> PostAsync(Plant p, string url, object? body = null, bool idempotent = false) =>
        await Json(await Send(p.T, HttpMethod.Post, url, body, idempotent));

    private static async Task<JsonElement> GetAsync(Plant p, string url) => await Json(await p.T.Client.GetAsCompanyAsync(url, p.T.CompanyId));

    /// <summary>FG = 2 X + 3 Y + 1 Z; assembly at labour 30 / machine 50 / overhead 25 per hour, setup 1 h + 0.1 h per unit; stock X 200 @5, Y 300 @2, Z 100 @4.</summary>
    private async Task<Plant> NewPlantAsync(decimal capacityHoursPerDay = 8m)
    {
        var t = await fixture.NewTenantAsync(preset: "manufacturing");
        async Task<JsonElement> P(string url, object? body = null, bool idempotent = false) => await Json(await Send(t, HttpMethod.Post, url, body, idempotent));
        await P("/api/v1/accounting/coa-templates/generic");
        var legal = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId));
        var le = legal.EnumerateArray().First().GetProperty("id").GetGuid();
        await P("/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" });
        var warehouses = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId));
        var main = warehouses.GetProperty("items").EnumerateArray().First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await P("/api/v1/catalog/uoms/seed-standard")).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var pcs = uoms["PCS"];
        var category = (await P("/api/v1/catalog/categories", new { code = "MAT", nameEn = "Materials", nameAr = "خامات", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .GetProperty("id").GetGuid();
        async Task<Guid> Product(string sku) =>
            (await P("/api/v1/catalog/products", new { sku, nameEn = sku, nameAr = sku, type = "Stockable", baseUomId = pcs, categoryId = category, tracking = "None" })).GetProperty("id").GetGuid();
        var (x, y, z, fg) = (await Product("X"), await Product("Y"), await Product("Z"), await Product("FG"));
        await OpeningAsync(t, main, pcs, (x, 200, 5m), (y, 300, 2m), (z, 100, 4m));

        var wc = (await P($"{Api}/work-centers", new
        {
            code = "ASM", nameEn = "Assembly", nameAr = "تجميع", capacityHoursPerDay, laborRatePerHour = 30m, machineRatePerHour = 50m, overheadRatePerHour = 25m, overheadBasis = "MachineHours",
        })).GetProperty("id").GetGuid();
        var routing = (await P($"{Api}/routings", new
        {
            productId = fg, code = "R-FG", name = "FG routing", operations = new[] { new { sequence = 10, name = "Assemble", workCenterId = wc, setupHours = 1m, runHoursPerUnit = 0.1m } },
        })).GetProperty("id").GetGuid();
        var bom = (await P($"{Api}/boms", new { productId = fg, code = "BOM-FG", name = "FG" })).GetProperty("id").GetGuid();
        var version = (await P($"{Api}/boms/{bom}/versions", new
        {
            effectiveFrom = "2026-01-01", outputQuantity = 1m,
            lines = new object[]
            {
                new { componentProductId = x, quantityPerOutput = 2m, uomId = pcs, scrapPercent = 0m },
                new { componentProductId = y, quantityPerOutput = 3m, uomId = pcs, scrapPercent = 0m },
                new { componentProductId = z, quantityPerOutput = 1m, uomId = pcs, scrapPercent = 0m },
            },
        })).GetProperty("id").GetGuid();
        await P($"{Api}/boms/{bom}/versions/{version}/activate");
        return new Plant(t, le, main, pcs, category, x, y, z, fg, wc, routing, bom, version);
    }

    private static async Task OpeningAsync(ErpApiFixture.TenantSession t, Guid warehouse, Guid uom, params (Guid Product, decimal Quantity, decimal Cost)[] lines)
    {
        var opening = await Json(await Send(t, HttpMethod.Post, "/api/v1/inventory/opening-stock", new
        {
            warehouseId = warehouse, date = "2026-01-15",
            lines = lines.Select(l => new { productId = l.Product, uomId = uom, quantity = l.Quantity, unitCost = l.Cost }).ToArray(),
        }));
        var id = opening.GetProperty("id").GetGuid();
        await Json(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/submit"));
        await Json(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/approve"));
        await Json(await Send(t, HttpMethod.Post, $"/api/v1/inventory/opening-stock/{id}/post", idempotent: true));
    }

    private async Task<Guid> NewProductAsync(Plant p, string sku) =>
        (await PostAsync(p, "/api/v1/catalog/products", new { sku, nameEn = sku, nameAr = sku, type = "Stockable", baseUomId = p.Pcs, categoryId = p.Category, tracking = "None" }))
        .GetProperty("id").GetGuid();

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

    private async Task<decimal> ValueOfAsync(Plant p, Guid product)
    {
        var rows = await GetAsync(p, $"/api/v1/inventory/valuation?productId={product}");
        var items = rows.ValueKind == JsonValueKind.Array ? rows : rows.GetProperty("items");
        return items.EnumerateArray().Sum(r => r.GetProperty("value").GetDecimal());
    }

    private static async Task<JsonElement> CreatePlannedOrderAsync(Plant p, Guid product, decimal quantity, string start = "2026-03-02", string due = "2026-03-10", Guid? variantId = null)
    {
        var order = await PostAsync(p, $"{Api}/production-orders", new { productId = product, quantity, componentWarehouseId = p.Main, startDate = start, dueDate = due, variantId });
        return await PostAsync(p, $"{Api}/production-orders/{order.GetProperty("id").GetGuid()}/plan", new { });
    }

    [Fact]
    public async Task Standard_cost_roll_up_activation_revalues_stock_and_close_posts_split_variances()
    {
        var p = await NewPlantAsync();
        await Json(await Send(p.T, HttpMethod.Put, $"{Api}/settings", new { defaultCostingMethod = "Standard" }));
        // X stays at actual cost in stock (so its price variance shows at issue); FG costs over lots of 100.
        await Json(await Send(p.T, HttpMethod.Put, $"{Api}/planning-parameters", new { productId = p.X, safetyStock = 0m, leadTimeDays = 0, lotSize = 0m, costingMethod = "Actual" }));
        await Json(await Send(p.T, HttpMethod.Put, $"{Api}/planning-parameters", new { productId = p.Fg, safetyStock = 0m, leadTimeDays = 0, lotSize = 100m }));

        var version = await PostAsync(p, $"{Api}/cost-versions", new { code = "STD-26", name = "Standards 2026", effectiveFrom = "2026-01-01" });
        var versionId = version.GetProperty("id").GetGuid();
        await Json(await Send(p.T, HttpMethod.Put, $"{Api}/cost-versions/{versionId}/items", new
        {
            items = new[]
            {
                new { productId = p.X, material = 4.8m, labor = 0m, machine = 0m, overhead = 0m, note = "quote" },
                new { productId = p.Z, material = 4.5m, labor = 0m, machine = 0m, overhead = 0m, note = "quote" },
            },
        }));
        var rolled = await PostAsync(p, $"{Api}/cost-versions/{versionId}/roll-up", new { costWarehouseId = p.Main });
        var items = rolled.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("productId").GetGuid());
        items[p.Y].GetProperty("unitCost").GetDecimal().ShouldBe(2m);            // current cost
        items[p.Y].GetProperty("source").GetString().ShouldBe("CurrentCost");
        items[p.X].GetProperty("source").GetString().ShouldBe("Manual");          // kept
        var fg = items[p.Fg];
        fg.GetProperty("material").GetDecimal().ShouldBe(20.1m);                  // 2×4.8 + 3×2 + 4.5
        fg.GetProperty("labor").GetDecimal().ShouldBe(3.3m);                      // (1 + 0.1×100) h × 30 ÷ 100
        fg.GetProperty("machine").GetDecimal().ShouldBe(5.5m);
        fg.GetProperty("overhead").GetDecimal().ShouldBe(2.75m);
        fg.GetProperty("unitCost").GetDecimal().ShouldBe(31.65m);

        await PostAsync(p, $"{Api}/cost-versions/{versionId}/approve");
        (await Send(p.T, HttpMethod.Post, $"{Api}/cost-versions/{versionId}/activate", new { legalEntityId = p.LegalEntityId })).IsSuccessStatusCode.ShouldBeFalse(); // idempotency key required
        var activated = await PostAsync(p, $"{Api}/cost-versions/{versionId}/activate", new { legalEntityId = p.LegalEntityId }, idempotent: true);
        activated.GetProperty("version").GetProperty("status").GetString().ShouldBe("Active");
        var revaluations = activated.GetProperty("revaluations").EnumerateArray().ToList();
        revaluations.ShouldNotContain(r => r.GetProperty("productId").GetGuid() == p.X); // actual-costed: untouched
        revaluations.Single(r => r.GetProperty("productId").GetGuid() == p.Z).GetProperty("change").GetDecimal().ShouldBe(50m);
        (await ValueOfAsync(p, p.Z)).ShouldBe(450m);
        (await ValueOfAsync(p, p.X)).ShouldBe(1000m);
        (await RoleBalanceAsync(p, PostingRoles.InventoryAdjustmentGain)).ShouldBe(-50m);

        // Production at standard: 100 planned, 5 scrapped, 95 good.
        var order = await CreatePlannedOrderAsync(p, p.Fg, 100);
        order.GetProperty("costingMethod").GetString().ShouldBe("Standard");
        order.GetProperty("standardUnitCost").GetDecimal().ShouldBe(31.65m);
        var id = order.GetProperty("id").GetGuid();
        order = await PostAsync(p, $"{Api}/production-orders/{id}/release");
        await PostAsync(p, $"{Api}/production-orders/{id}/issues", new
        {
            date = "2026-03-02",
            lines = new[] { new { lineId = LineOf(order, p.X), quantity = 200m }, new { lineId = LineOf(order, p.Y), quantity = 300m }, new { lineId = LineOf(order, p.Z), quantity = 100m } },
        }, idempotent: true);
        await PostAsync(p, $"{Api}/production-orders/{id}/operations", new { sequence = 10, laborHours = 12m, machineHours = 10m, date = "2026-03-03" }, idempotent: true);
        await PostAsync(p, $"{Api}/production-orders/{id}/scrap", new { quantity = 5m, date = "2026-03-04" }, idempotent: true);
        var received = await PostAsync(p, $"{Api}/production-orders/{id}/receipts", new { quantity = 95m, date = "2026-03-04" }, idempotent: true);
        received.GetProperty("receivedCost").GetDecimal().ShouldBe(3006.75m); // 95 × 31.65
        received.GetProperty("wipBalance").GetDecimal().ShouldBe(153.25m);   // 3160 actual − 3006.75

        var closed = await PostAsync(p, $"{Api}/production-orders/{id}/close", new { date = "2026-03-05" }, idempotent: true);
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("materialPriceVariance").GetDecimal().ShouldBe(40m);     // X: 1000 − 200 × 4.8
        closed.GetProperty("materialUsageVariance").GetDecimal().ShouldBe(100.5m);  // (200−190)×4.8 + (300−285)×2 + (100−95)×4.5
        closed.GetProperty("conversionVariance").GetDecimal().ShouldBe(12.75m);     // 1110 − 95 × 11.55
        closed.GetProperty("otherVariance").GetDecimal().ShouldBe(0m);
        closed.GetProperty("wipBalance").GetDecimal().ShouldBe(0m);

        (await RoleBalanceAsync(p, PostingRoles.WorkInProgress)).ShouldBe(0m);
        (await RoleBalanceAsync(p, PostingRoles.PurchasePriceVariance)).ShouldBe(40m);
        (await RoleBalanceAsync(p, PostingRoles.MaterialUsageVariance)).ShouldBe(100.5m);
        (await RoleBalanceAsync(p, PostingRoles.ConversionCostVariance)).ShouldBe(12.75m);
        (await RoleBalanceAsync(p, PostingRoles.ProductionVariance)).ShouldBe(0m);
        (await ValueOfAsync(p, p.Fg)).ShouldBe(3006.75m);
    }

    [Fact]
    public async Task Absorption_clearing_moves_applied_cost_against_actual_spending_and_posts_the_difference()
    {
        var p = await NewPlantAsync();
        var accounts = (await GetAsync(p, "/api/v1/accounting/accounts")).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
        var pool = await PostAsync(p, $"{Api}/cost-pools", new { code = "LAB", name = "Direct labour", kind = "Labor", workCenterIds = new[] { p.WorkCenter }, expenseAccountIds = new[] { accounts["5320"] } });
        (await (await Send(p.T, HttpMethod.Post, $"{Api}/cost-pools", new { code = "LAB2", name = "Again", kind = "Labor", workCenterIds = new[] { p.WorkCenter }, expenseAccountIds = new[] { accounts["5320"] } }))
            .ErrorCodeAsync()).ShouldBe("manufacturing.cost-pool-account-taken");

        // Actual wages of 400 in March; 10 h × 30 = 300 absorbed into WIP.
        var journal = await Json(await p.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/journals", p.T.CompanyId, new
        {
            legalEntityId = p.LegalEntityId, journalDate = "2026-03-15", description = "March wages",
            lines = new[] { new { accountId = accounts["5320"], debit = 400m, credit = 0m }, new { accountId = accounts["3100"], debit = 0m, credit = 400m } },
        }));
        await Json(await p.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/accounting/journals/{journal.GetProperty("id").GetGuid()}/post", p.T.CompanyId));
        var order = await CreatePlannedOrderAsync(p, p.Fg, 10);
        var id = order.GetProperty("id").GetGuid();
        await PostAsync(p, $"{Api}/production-orders/{id}/release");
        await PostAsync(p, $"{Api}/production-orders/{id}/operations", new { sequence = 10, laborHours = 10m, machineHours = 0m, date = "2026-03-03" }, idempotent: true);

        var preview = await GetAsync(p, $"{Api}/absorption-runs/preview?legalEntityId={p.LegalEntityId}&from=2026-03-01&to=2026-03-31");
        var line = preview.GetProperty("lines").EnumerateArray().Single();
        (line.GetProperty("applied").GetDecimal(), line.GetProperty("actual").GetDecimal(), line.GetProperty("variance").GetDecimal()).ShouldBe((300m, 400m, 100m));
        preview.GetProperty("posted").GetBoolean().ShouldBeFalse();

        var posted = await PostAsync(p, $"{Api}/absorption-runs", new { legalEntityId = p.LegalEntityId, from = "2026-03-01", to = "2026-03-31" }, idempotent: true);
        posted.GetProperty("journalId").GetGuid().ShouldNotBe(Guid.Empty);
        (await RoleBalanceAsync(p, PostingRoles.AbsorptionVariance)).ShouldBe(100m);
        (await RoleBalanceAsync(p, PostingRoles.AppliedLabor)).ShouldBe(0m);
        var wages = await QueryAsync(p, sp => sp.GetRequiredService<IAccountLedgerQueries>().GetAccountLinesAsync(p.LegalEntityId, accounts["5320"], new DateOnly(2026, 12, 31), null));
        wages.Sum(l => l.BaseDebit - l.BaseCredit).ShouldBe(0m);

        var again = await Send(p.T, HttpMethod.Post, $"{Api}/absorption-runs", new { legalEntityId = p.LegalEntityId, from = "2026-03-15", to = "2026-04-15" }, idempotent: true);
        (await again.ErrorCodeAsync()).ShouldBe("manufacturing.absorption-already-posted");
        pool.GetProperty("kind").GetString().ShouldBe("Labor");
    }

    [Fact]
    public async Task Release_substitutes_a_short_component_with_its_alternative_and_reserves_it()
    {
        var p = await NewPlantAsync();
        var w = await NewProductAsync(p, "W");
        await OpeningAsync(p.T, p.Main, p.Pcs, (w, 50, 6m));
        var fg2 = await NewProductAsync(p, "FG2");
        var bom = (await PostAsync(p, $"{Api}/boms", new { productId = fg2, code = "BOM-FG2", name = "FG2" })).GetProperty("id").GetGuid();
        var version = (await PostAsync(p, $"{Api}/boms/{bom}/versions", new
        {
            effectiveFrom = "2026-01-01", outputQuantity = 1m,
            lines = new object[] { new { componentProductId = p.X, quantityPerOutput = 2m, uomId = p.Pcs, scrapPercent = 0m, alternatives = new[] { w } } },
        })).GetProperty("id").GetGuid();
        await PostAsync(p, $"{Api}/boms/{bom}/versions/{version}/activate");

        // 120 FG2 need 240 X; only 200 on hand → 40 move to W at release.
        var order = await CreatePlannedOrderAsync(p, fg2, 120);
        var id = order.GetProperty("id").GetGuid();
        var released = await PostAsync(p, $"{Api}/production-orders/{id}/release");
        var components = released.GetProperty("components").EnumerateArray().ToList();
        components.Single(c => c.GetProperty("productId").GetGuid() == p.X).GetProperty("requiredQuantity").GetDecimal().ShouldBe(200m);
        var substitute = components.Single(c => c.GetProperty("productId").GetGuid() == w);
        substitute.GetProperty("requiredQuantity").GetDecimal().ShouldBe(40m);
        substitute.GetProperty("substituteForLineId").GetGuid().ShouldBe(LineOf(released, p.X));
        released.GetProperty("substitutions").EnumerateArray().Single().GetProperty("automatic").GetBoolean().ShouldBeTrue();

        var reservations = await GetAsync(p, $"/api/v1/inventory/reservations?demandType=ProductionOrder&demandId={id}");
        reservations.EnumerateArray().ToDictionary(r => r.GetProperty("productId").GetGuid(), r => r.GetProperty("open").GetDecimal())
            .ShouldBe(new Dictionary<Guid, decimal> { [p.X] = 200, [w] = 40 }, ignoreOrder: true);

        // A product that is not an alternative cannot substitute.
        var refused = await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{id}/substitute", new { lineId = LineOf(released, p.X), alternativeProductId = p.Y, baseQuantity = 1m });
        (await refused.ErrorCodeAsync()).ShouldBe("manufacturing.not-an-alternative");
    }

    [Fact]
    public async Task Purchase_suggestions_convert_into_draft_purchase_orders_per_preferred_supplier()
    {
        var p = await NewPlantAsync();
        var supplier = (await PostAsync(p, "/api/v1/parties/suppliers", new { code = "S1", nameEn = "Nile Supplies", nameAr = "النيل", currency = "EGP" })).GetProperty("id").GetGuid();
        await Json(await Send(p.T, HttpMethod.Put, $"{Api}/planning-parameters", new
        {
            productId = p.Y, safetyStock = 300m, leadTimeDays = 5, lotSizeRule = "Multiple", lotSize = 50m, procurement = "Buy", preferredSupplierId = supplier,
        }));
        TestMrpDemandSource.Demand[p.T.CompanyId] = [new MrpDemand(p.Fg, 50, new DateOnly(2026, 4, 21), "SalesOrder", "SO-PO-1")];
        try
        {
            var run = await PostAsync(p, $"{Api}/mrp-runs", new { runDate = "2026-04-01", horizonDays = 56 });
            var buyY = run.GetProperty("suggestions").EnumerateArray().Single(s => s.GetProperty("productId").GetGuid() == p.Y);
            var runId = run.GetProperty("id").GetGuid();

            var converted = await PostAsync(p, $"{Api}/mrp-runs/{runId}/convert-purchases", new { suggestionIds = new[] { buyY.GetProperty("suggestionId").GetGuid() }, warehouseId = p.Main });
            var after = converted.GetProperty("suggestions").EnumerateArray().Single(s => s.GetProperty("productId").GetGuid() == p.Y);
            after.GetProperty("status").GetString().ShouldBe("Converted");
            var poId = after.GetProperty("purchaseOrderId").GetGuid();

            var po = await GetAsync(p, $"/api/v1/procurement/orders/{poId}");
            po.GetProperty("status").GetString().ShouldBe("Draft");
            po.GetProperty("supplierId").GetGuid().ShouldBe(supplier);
            var line = po.GetProperty("lines").EnumerateArray().Single();
            line.GetProperty("productId").GetGuid().ShouldBe(p.Y);
            line.GetProperty("quantity").GetDecimal().ShouldBe(150m);
            line.GetProperty("unitPrice").GetDecimal().ShouldBe(2m); // last cost in the warehouse

            // Produce suggestions are not purchases; converted ones cannot convert again.
            var produce = converted.GetProperty("suggestions").EnumerateArray().Single(s => s.GetProperty("productId").GetGuid() == p.Fg).GetProperty("suggestionId").GetGuid();
            (await (await Send(p.T, HttpMethod.Post, $"{Api}/mrp-runs/{runId}/convert-purchases", new { suggestionIds = new[] { produce }, warehouseId = p.Main })).ErrorCodeAsync())
                .ShouldBe("manufacturing.suggestion-not-convertible");
            (await (await Send(p.T, HttpMethod.Post, $"{Api}/mrp-runs/{runId}/convert-purchases", new { suggestionIds = new[] { buyY.GetProperty("suggestionId").GetGuid() }, warehouseId = p.Main }))
                .ErrorCodeAsync()).ShouldBe("manufacturing.suggestion-not-convertible");
        }
        finally
        {
            TestMrpDemandSource.Demand.TryRemove(p.T.CompanyId, out _);
        }
    }

    [Fact]
    public async Task Capacity_load_flags_overloaded_days_and_proposes_a_finish_date()
    {
        var p = await NewPlantAsync();
        // 100 FG need 1 + 10 = 11 hours on a work center with 8 hours a day, all on 2 March.
        var order = await CreatePlannedOrderAsync(p, p.Fg, 100, start: "2026-03-02", due: "2026-03-02");
        var load = await GetAsync(p, $"{Api}/capacity-load?from=2026-03-01&to=2026-03-07");
        var wc = load.GetProperty("workCenters").EnumerateArray().Single(w => w.GetProperty("workCenterId").GetGuid() == p.WorkCenter);
        var day = wc.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-03-02");
        day.GetProperty("load").GetDecimal().ShouldBe(11m);
        day.GetProperty("capacity").GetDecimal().ShouldBe(8m);
        day.GetProperty("overloaded").GetBoolean().ShouldBeTrue();
        wc.GetProperty("overloadedDays").GetInt32().ShouldBe(1);

        var proposal = load.GetProperty("proposals").EnumerateArray().Single(x => x.GetProperty("sourceId").GetGuid() == order.GetProperty("id").GetGuid());
        proposal.GetProperty("late").GetBoolean().ShouldBeTrue();
        proposal.GetProperty("proposedFinish").GetString().ShouldBe("2026-03-03");
    }

    [Fact]
    public async Task Variant_bom_lines_explode_per_variant_and_rework_orders_consume_the_defective_item()
    {
        var p = await NewPlantAsync();
        var shirt = await NewProductAsync(p, "SHIRT");
        var variants = await PostAsync(p, $"/api/v1/catalog/products/{shirt}/variants/generate", new { attributes = new Dictionary<string, string[]> { ["Size"] = ["S", "L"] } });
        var bySize = variants.EnumerateArray().ToDictionary(v => v.GetProperty("attributes").GetProperty("Size").GetString()!, v => v.GetProperty("id").GetGuid());
        variants.EnumerateArray().Select(v => v.GetProperty("sku").GetString()).ShouldBe(["SHIRT-L", "SHIRT-S"], ignoreOrder: true);

        var bom = (await PostAsync(p, $"{Api}/boms", new { productId = shirt, code = "BOM-SHIRT", name = "Shirt" })).GetProperty("id").GetGuid();
        var version = (await PostAsync(p, $"{Api}/boms/{bom}/versions", new
        {
            effectiveFrom = "2026-01-01", outputQuantity = 1m,
            lines = new object[]
            {
                new { componentProductId = p.X, quantityPerOutput = 1m, uomId = p.Pcs, scrapPercent = 0m },
                new { componentProductId = p.Y, quantityPerOutput = 1m, uomId = p.Pcs, scrapPercent = 0m, appliesTo = new Dictionary<string, string> { ["Size"] = "L" } },
            },
        })).GetProperty("id").GetGuid();
        await PostAsync(p, $"{Api}/boms/{bom}/versions/{version}/activate");

        var large = await CreatePlannedOrderAsync(p, shirt, 10, variantId: bySize["L"]);
        large.GetProperty("variantId").GetGuid().ShouldBe(bySize["L"]);
        large.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("productId").GetGuid()).ShouldBe([p.X, p.Y], ignoreOrder: true);
        var small = await CreatePlannedOrderAsync(p, shirt, 10, variantId: bySize["S"]);
        small.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("productId").GetGuid()).ShouldBe([p.X]);
        var foreign = await Send(p.T, HttpMethod.Post, $"{Api}/production-orders", new { productId = p.Fg, quantity = 1m, componentWarehouseId = p.Main, variantId = bySize["L"] });
        (await foreign.ErrorCodeAsync()).ShouldBe("manufacturing.variant-invalid");

        // Rework: 3 defective FG are issued back into production with 1 extra Z each and received again.
        var rework = await PostAsync(p, $"{Api}/rework-orders", new
        {
            productId = p.Fg, quantity = 3m, componentWarehouseId = p.Main, routingId = p.RoutingId, startDate = "2026-03-02",
            extraComponents = new[] { new { productId = p.Z, quantity = 3m, uomId = p.Pcs } },
        });
        rework.GetProperty("orderType").GetString().ShouldBe("Rework");
        rework.GetProperty("status").GetString().ShouldBe("Planned");
        rework.GetProperty("costingMethod").GetString().ShouldBe("Actual");
        var components = rework.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("productId").GetGuid(), c => c.GetProperty("requiredQuantity").GetDecimal());
        components.ShouldBe(new Dictionary<Guid, decimal> { [p.Fg] = 3m, [p.Z] = 3m }, ignoreOrder: true);
        rework.GetProperty("operations").GetArrayLength().ShouldBe(1);
        ManufacturingPermissions.CostingManage.ShouldBe("manufacturing.costing.manage");
        (await Send(p.T, HttpMethod.Post, $"{Api}/production-orders/{rework.GetProperty("id").GetGuid()}/plan", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
