using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class TaxTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Task<HttpResponseMessage> ApplyTemplate(ErpApiFixture.TenantSession t, decimal? tableTaxRate = null) =>
        t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/templates/egypt", t.CompanyId, new { tableTaxRate });

    [Fact]
    public async Task Egypt_template_seeds_once_and_is_idempotent()
    {
        var t = await fixture.NewTenantAsync();
        var first = await Json(await ApplyTemplate(t));
        first.GetProperty("currenciesCreated").GetInt32().ShouldBe(1);
        first.GetProperty("taxCodesCreated").GetInt32().ShouldBe(9);
        first.GetProperty("taxGroupsCreated").GetInt32().ShouldBe(7);
        first.GetProperty("mappingsCreated").GetInt32().ShouldBe(6);

        var second = await Json(await ApplyTemplate(t));
        second.EnumerateObject().Select(p => p.Value.GetInt32()).ShouldAllBe(v => v == 0);

        var codes = await Json(await t.Client.GetAsCompanyAsync("/api/v1/tax/tax-codes", t.CompanyId));
        codes.GetArrayLength().ShouldBe(9);
        var vat = codes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "VAT14-IN");
        vat.GetProperty("recoverable").GetBoolean().ShouldBeTrue();
        vat.GetProperty("rates")[0].GetProperty("rate").GetDecimal().ShouldBe(14m);

        var currencies = await Json(await t.Client.GetAsync("/api/v1/tax/currencies", Ct));
        currencies.EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldBe(["EGP"]);
    }

    [Fact]
    public async Task Calculate_endpoint_resolves_categories_groups_and_dates()
    {
        var t = await fixture.NewTenantAsync();
        await Json(await ApplyTemplate(t, tableTaxRate: 10m));

        var standard = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { categoryCode = "standard", direction = "Sales", amount = 1000m, taxInclusive = false, currency = "EGP", date = "2026-11-27" }));
        standard.GetProperty("gross").GetDecimal().ShouldBe(1140m);
        standard.GetProperty("lines")[0].GetProperty("code").GetString().ShouldBe("VAT14-OUT");

        var inclusive = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { categoryCode = "STANDARD", direction = "Purchase", amount = 100m, taxInclusive = true, currency = "EGP", date = "2026-11-27" }));
        inclusive.GetProperty("net").GetDecimal().ShouldBe(87.72m);
        inclusive.GetProperty("totalTax").GetDecimal().ShouldBe(12.28m);

        var groups = await Json(await t.Client.GetAsCompanyAsync("/api/v1/tax/tax-groups", t.CompanyId));
        var compoundId = groups.EnumerateArray().Single(g => g.GetProperty("code").GetString() == "TABLE-VAT-SALES").GetProperty("id").GetGuid();
        var compound = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { taxGroupId = compoundId, amount = 1000m, taxInclusive = false, currency = "EGP", date = "2026-11-27" }));
        compound.GetProperty("gross").GetDecimal().ShouldBe(1254m);

        var unmapped = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { categoryCode = "LUXURY", direction = "Sales", amount = 10m, taxInclusive = false, currency = "EGP" });
        (await unmapped.ErrorCodeAsync()).ShouldBe("tax.category-not-mapped");

        var beforeVat = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { categoryCode = "STANDARD", direction = "Sales", amount = 10m, taxInclusive = false, currency = "EGP", date = "2017-06-30" });
        (await beforeVat.ErrorCodeAsync()).ShouldBe("tax.rate-missing");

        var unknownCurrency = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { categoryCode = "STANDARD", direction = "Sales", amount = 10m, taxInclusive = false, currency = "USD" });
        (await unknownCurrency.ErrorCodeAsync()).ShouldBe("tax.currency-not-found");
    }

    [Fact]
    public async Task Tax_rates_change_over_time_without_overlaps()
    {
        var t = await fixture.NewTenantAsync();
        await Json(await ApplyTemplate(t));
        var codes = await Json(await t.Client.GetAsCompanyAsync("/api/v1/tax/tax-codes", t.CompanyId));
        var vatId = codes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "VAT14-OUT").GetProperty("id").GetGuid();

        var changed = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/tax/tax-codes/{vatId}/rates", t.CompanyId,
            new { rate = 15m, validFrom = "2027-01-01" }));
        changed.GetProperty("rates").GetArrayLength().ShouldBe(2);

        var overlap = await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/tax/tax-codes/{vatId}/rates", t.CompanyId,
            new { rate = 10m, validFrom = "2020-01-01", validTo = "2020-12-31" });
        overlap.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var future = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", t.CompanyId,
            new { categoryCode = "STANDARD", direction = "Sales", amount = 1000m, taxInclusive = false, currency = "EGP", date = "2027-03-01" }));
        future.GetProperty("totalTax").GetDecimal().ShouldBe(150m);
    }

    [Fact]
    public async Task Exchange_rates_use_the_latest_rate_on_or_before_the_date()
    {
        var t = await fixture.NewTenantAsync();
        await Json(await ApplyTemplate(t));
        (await t.Client.PostAsJsonAsync("/api/v1/tax/currencies", new { code = "USD", nameEn = "US dollar", nameAr = "دولار أمريكي", minorUnits = 2 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await Json(await t.Client.PostAsJsonAsync("/api/v1/tax/exchange-rates", new { fromCurrency = "USD", toCurrency = "EGP", effectiveDate = "2026-11-01", rate = 48.5m }, Ct));
        await Json(await t.Client.PostAsJsonAsync("/api/v1/tax/exchange-rates", new { fromCurrency = "USD", toCurrency = "EGP", effectiveDate = "2026-11-20", rate = 49m }, Ct));
        // Recording the same pair/type/date again replaces the rate.
        await Json(await t.Client.PostAsJsonAsync("/api/v1/tax/exchange-rates", new { fromCurrency = "usd", toCurrency = "egp", effectiveDate = "2026-11-20", rate = 49.1m }, Ct));

        async Task<decimal?> Lookup(string from, string to, string date)
        {
            var response = await t.Client.GetAsync($"/api/v1/tax/exchange-rates/lookup?from={from}&to={to}&date={date}", Ct);
            return response.StatusCode == HttpStatusCode.NotFound ? null : (await Json(response)).GetProperty("rate").GetDecimal();
        }

        (await Lookup("USD", "EGP", "2026-11-10")).ShouldBe(48.5m);
        (await Lookup("USD", "EGP", "2026-11-25")).ShouldBe(49.1m);
        (await Lookup("USD", "EGP", "2026-10-01")).ShouldBeNull();
        (await Lookup("EGP", "USD", "2026-11-25")).ShouldBe(Math.Round(1m / 49.1m, 8, MidpointRounding.AwayFromZero));
        (await Lookup("EGP", "EGP", "2000-01-01")).ShouldBe(1m);

        var list = await Json(await t.Client.GetAsync("/api/v1/tax/exchange-rates?from=USD&to=EGP", Ct));
        list.GetArrayLength().ShouldBe(2);

        var unknown = await t.Client.PostAsJsonAsync("/api/v1/tax/exchange-rates", new { fromCurrency = "GBP", toCurrency = "EGP", effectiveDate = "2026-11-01", rate = 60m }, Ct);
        (await unknown.ErrorCodeAsync()).ShouldBe("tax.currency-not-found");
    }

    [Fact]
    public async Task Tax_data_is_isolated_by_tenant_and_company()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        await Json(await ApplyTemplate(a));

        // Tenant B sees neither A's currencies nor A's tax groups.
        (await Json(await b.Client.GetAsync("/api/v1/tax/currencies", Ct))).GetArrayLength().ShouldBe(0);
        var groupsA = await Json(await a.Client.GetAsCompanyAsync("/api/v1/tax/tax-groups", a.CompanyId));
        var groupId = groupsA[0].GetProperty("id").GetGuid();
        await Json(await ApplyTemplate(b));
        var foreign = await b.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", b.CompanyId,
            new { taxGroupId = groupId, amount = 10m, taxInclusive = false, currency = "EGP" });
        (await foreign.ErrorCodeAsync()).ShouldBe("tax.group-not-found");

        // A second company in tenant A has its own (empty) tax setup but shares the tenant's currencies.
        var second = await Json(await a.Client.PostAsJsonAsync("/api/v1/organization/companies", new
        {
            code = "SECOND", nameEn = "Second", nameAr = "الثانية", baseCurrency = "EGP", businessPreset = "trading",
        }, Ct));
        var secondId = second.GetProperty("id").GetGuid();
        (await Json(await a.Client.GetAsCompanyAsync("/api/v1/tax/tax-codes", secondId))).GetArrayLength().ShouldBe(0);
        var crossCompany = await a.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/calculate", secondId,
            new { taxGroupId = groupId, amount = 10m, taxInclusive = false, currency = "EGP" });
        (await crossCompany.ErrorCodeAsync()).ShouldBe("tax.group-not-found");
    }

    [Fact]
    public async Task Tax_setup_requires_company_and_permission()
    {
        var t = await fixture.NewTenantAsync();
        var noCompany = await t.Client.GetAsync("/api/v1/tax/tax-codes", Ct);
        (await noCompany.ErrorCodeAsync()).ShouldBe("company.required");

        var role = await Json(await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "TaxViewer", permissions = new[] { "tax.read" } }, Ct));
        await Json(await t.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email = "taxviewer@test.example", displayName = "Viewer", password = "Viewer-Passw0rd!", roleIds = new[] { role.GetProperty("id").GetGuid() }, companyIds = new[] { t.CompanyId },
        }, Ct));
        using var viewer = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(viewer, t.TenantCode, "taxviewer@test.example", "Viewer-Passw0rd!");
        (await viewer.GetAsCompanyAsync("/api/v1/tax/tax-codes", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/templates/egypt", t.CompanyId, new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
