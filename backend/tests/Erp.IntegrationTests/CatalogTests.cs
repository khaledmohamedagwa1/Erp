using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class CatalogTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Setup(ErpApiFixture.TenantSession T, Dictionary<string, Guid> Uoms, Guid CategoryId);

    private async Task<Setup> SetupAsync()
    {
        var t = await fixture.NewTenantAsync();
        var seeded = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/uoms/seed-standard", t.CompanyId));
        var uoms = seeded.EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/categories", t.CompanyId,
            new { code = "HOME", nameEn = "Home", nameAr = "منزلي", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }));
        return new Setup(t, uoms, category.GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> CreatePlateAsync(Setup s, string sku = "SKU-2051", string barcode = "6221000000017") =>
        s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/products", s.T.CompanyId, new
        {
            sku, nameEn = "Ceramic Plate 27 cm", nameAr = "طبق سيراميك 27 سم", type = "Stockable", baseUomId = s.Uoms["PCS"], categoryId = s.CategoryId,
            tracking = "None", expiryTracked = false,
            units = new[] { new { uomId = s.Uoms["BOX"], factorToBase = 24m }, new { uomId = s.Uoms["CTN"], factorToBase = 288m } },
            barcodes = new object[] { new { barcode, uomId = (Guid?)null }, new { barcode = barcode + "9", uomId = (Guid?)s.Uoms["CTN"] } },
        });

    [Fact]
    public async Task Products_are_created_with_units_and_barcodes_and_found_by_barcode()
    {
        var s = await SetupAsync();
        var created = await CreatePlateAsync(s);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var product = await Json(created);
        product.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("factorToBase").GetDecimal()).ShouldBe([1m, 24m, 288m]);
        product.GetProperty("isStockable").GetBoolean().ShouldBeTrue();

        var carton = await Json(await s.T.Client.GetAsCompanyAsync("/api/v1/catalog/barcodes/62210000000179", s.T.CompanyId));
        carton.GetProperty("sku").GetString().ShouldBe("SKU-2051");
        carton.GetProperty("uomId").GetGuid().ShouldBe(s.Uoms["CTN"]);
        carton.GetProperty("factorToBase").GetDecimal().ShouldBe(288m);

        var search = await Json(await s.T.Client.GetAsCompanyAsync("/api/v1/catalog/products?search=6221000000017", s.T.CompanyId));
        search.GetProperty("totalCount").GetInt32().ShouldBe(1);

        (await s.T.Client.GetAsCompanyAsync("/api/v1/catalog/barcodes/0000", s.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Skus_and_barcodes_are_unique_within_a_company()
    {
        var s = await SetupAsync();
        (await CreatePlateAsync(s)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await CreatePlateAsync(s, barcode: "999")).ErrorCodeAsync()).ShouldBe("catalog.sku-taken");
        (await (await CreatePlateAsync(s, sku: "SKU-OTHER")).ErrorCodeAsync()).ShouldBe("catalog.barcode-taken");
    }

    [Fact]
    public async Task Price_quotes_pick_the_volume_tier_and_convert_units()
    {
        var s = await SetupAsync();
        var productId = (await Json(await CreatePlateAsync(s))).GetProperty("id").GetGuid();
        var list = await Json(await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/price-lists", s.T.CompanyId,
            new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = true }));
        var listId = list.GetProperty("id").GetGuid();

        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/catalog/price-lists/{listId}/items", s.T.CompanyId,
            new { productId, uomId = s.Uoms["PCS"], minQuantity = 0m, price = 72m })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/catalog/price-lists/{listId}/items", s.T.CompanyId,
            new { productId, uomId = s.Uoms["PCS"], minQuantity = 100m, price = 65m })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var overlap = await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/catalog/price-lists/{listId}/items", s.T.CompanyId,
            new { productId, uomId = s.Uoms["PCS"], minQuantity = 0m, price = 70m });
        (await overlap.ErrorCodeAsync()).ShouldBe("catalog.price-overlap");

        async Task<JsonElement> Quote(string uom, decimal qty) => await Json(await s.T.Client.GetAsCompanyAsync(
            $"/api/v1/catalog/price-quote?priceListId={listId}&productId={productId}&uomId={s.Uoms[uom]}&quantity={qty}&date=2026-11-27", s.T.CompanyId));

        (await Quote("PCS", 10)).GetProperty("unitPrice").GetDecimal().ShouldBe(72m);
        (await Quote("PCS", 150)).GetProperty("unitPrice").GetDecimal().ShouldBe(65m);

        // One box = 24 pieces; 5 boxes = 120 pieces reaches the 100-piece tier: 24 × 65.
        var box = await Quote("BOX", 5);
        box.GetProperty("unitPrice").GetDecimal().ShouldBe(1560m);
        box.GetProperty("convertedFromOtherUnit").GetBoolean().ShouldBeTrue();
        box.GetProperty("taxInclusive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Category_moves_that_create_a_cycle_are_rejected()
    {
        var s = await SetupAsync();
        var child = await Json(await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/categories", s.T.CompanyId,
            new { code = "KITCHEN", nameEn = "Kitchen", nameAr = "مطبخ", parentId = s.CategoryId }));
        var move = await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/catalog/categories/{s.CategoryId}", s.T.CompanyId,
            new { nameEn = "Home", nameAr = "منزلي", parentId = child.GetProperty("id").GetGuid(), defaultCostingMethod = "FIFO", postingGroup = "GOODS" });
        (await move.ErrorCodeAsync()).ShouldBe("catalog.category-cycle");
    }

    [Fact]
    public async Task Catalog_data_is_isolated_between_companies_and_tenants()
    {
        var s = await SetupAsync();
        var productId = (await Json(await CreatePlateAsync(s))).GetProperty("id").GetGuid();

        // Second company of the same tenant does not see the first company's products or barcodes.
        var second = await Json(await s.T.Client.PostAsJsonAsync("/api/v1/organization/companies", new
        {
            code = "SECOND", nameEn = "Second", nameAr = "الثانية", baseCurrency = "EGP", businessPreset = "retail",
        }, Ct));
        var secondId = second.GetProperty("id").GetGuid();
        (await s.T.Client.GetAsCompanyAsync($"/api/v1/catalog/products/{productId}", secondId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.T.Client.GetAsCompanyAsync("/api/v1/catalog/barcodes/6221000000017", secondId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The same SKU and barcode may be reused in another company.
        var secondUoms = (await Json(await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/uoms/seed-standard", secondId)))
            .EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var secondCategory = (await Json(await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/categories", secondId,
            new { code = "HOME", nameEn = "Home", nameAr = "منزلي" }))).GetProperty("id").GetGuid();
        (await CreatePlateAsync(new Setup(s.T with { CompanyId = secondId }, secondUoms, secondCategory))).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Another tenant cannot read it either.
        var other = await fixture.NewTenantAsync();
        (await other.Client.GetAsCompanyAsync($"/api/v1/catalog/products/{productId}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Catalog_endpoints_require_a_company_and_permissions()
    {
        var s = await SetupAsync();
        (await (await s.T.Client.GetAsync("/api/v1/catalog/products", Ct)).ErrorCodeAsync()).ShouldBe("company.required");

        var role = await Json(await s.T.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Catalog viewer", permissions = new[] { "catalog.read" } }, Ct));
        await Json(await s.T.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email = "viewer@catalog.example", displayName = "Viewer", password = "Viewer-Passw0rd!",
            roleIds = new[] { role.GetProperty("id").GetGuid() }, companyIds = new[] { s.T.CompanyId },
        }, Ct));
        using var viewer = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(viewer, s.T.TenantCode, "viewer@catalog.example", "Viewer-Passw0rd!");
        (await viewer.GetAsCompanyAsync("/api/v1/catalog/products", s.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/catalog/price-lists", s.T.CompanyId,
            new { code = "X", name = "X", currency = "EGP", taxInclusive = false })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Units_variants_and_activation_can_be_changed_and_are_audited()
    {
        var s = await SetupAsync();
        var productId = (await Json(await CreatePlateAsync(s))).GetProperty("id").GetGuid();

        var withVariant = await Json(await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/catalog/products/{productId}/variants", s.T.CompanyId,
            new { sku = "SKU-2051-BLUE", attributes = new Dictionary<string, string> { ["Colour"] = "Blue" } }));
        withVariant.GetProperty("variants").EnumerateArray().Single().GetProperty("attributes").GetProperty("Colour").GetString().ShouldBe("Blue");

        var dup = await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/catalog/products/{productId}/units", s.T.CompanyId,
            new { uomId = s.Uoms["BOX"], factorToBase = 12m });
        (await dup.ErrorCodeAsync()).ShouldBe("catalog.unit-duplicate");

        var inactive = await Json(await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/catalog/products/{productId}/deactivate", s.T.CompanyId));
        inactive.GetProperty("isActive").GetBoolean().ShouldBeFalse();

        await using var connection = await fixture.OpenAsTenantAsync(s.T.TenantId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit.AuditLog WHERE EntityType = 'catalog.Product' AND EntityId = @id";
        command.Parameters.AddWithValue("@id", productId.ToString());
        ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBeGreaterThanOrEqualTo(3);
    }
}
