using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Modules.Identity.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class StepUpTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sensitive_actions_require_a_recent_second_factor_once_mfa_is_enrolled()
    {
        var t = await fixture.NewTenantAsync();
        var enroll = await (await t.Client.PostAsync("/api/v1/auth/mfa/enroll", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secret = Base32.Decode(enroll.GetProperty("secret").GetString()!);
        var step = Totp.TimeStep(DateTime.UtcNow);
        (await t.Client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = Totp.Compute(secret, step) }, Ct)).EnsureSuccessStatusCode();

        // A refreshed token of the original (password-only) session now says "enrolled" but carries no recent MFA.
        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refresh.Headers.Add("X-ERP-Request", "1");
        var refreshed = await (await t.Client.SendAsync(refresh, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        t.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.GetProperty("accessToken").GetString());

        var blocked = await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Clerk", permissions = Array.Empty<string>() }, Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("identity.step-up-required");
        problem.GetProperty("mfaEnrollmentRequired").GetBoolean().ShouldBeFalse();

        // Reading is not sensitive.
        (await t.Client.GetAsync("/api/v1/identity/roles", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await (await t.Client.PostAsJsonAsync("/api/v1/auth/step-up", new { code = "000000" }, Ct)).ErrorCodeAsync()).ShouldBe("auth.mfa-invalid-code");
        var stepUp = await t.Client.PostAsJsonAsync("/api/v1/auth/step-up", new { code = Totp.Compute(secret, step + 1) }, Ct);
        stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await stepUp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();
        t.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Clerk", permissions = Array.Empty<string>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // The verification is remembered by the session: a refreshed token still allows sensitive actions.
        var again = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        again.Headers.Add("X-ERP-Request", "1");
        var second = await (await t.Client.SendAsync(again, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        t.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.GetProperty("accessToken").GetString());
        (await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Auditor", permissions = Array.Empty<string>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Step_up_without_mfa_enrolled_is_refused()
    {
        var t = await fixture.NewTenantAsync();
        (await (await t.Client.PostAsJsonAsync("/api/v1/auth/step-up", new { code = "123456" }, Ct)).ErrorCodeAsync()).ShouldBe("identity.mfa-not-enabled");
    }
}

public class PlatformOperatorTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Operators_sign_in_with_password_and_totp_and_the_static_key_only_bootstraps()
    {
        var token = await fixture.OperatorTokenAsync();
        using var client = fixture.CreateClient();

        // The static key cannot create more operators, nor provision tenants.
        client.DefaultRequestHeaders.Add("X-Platform-Key", ErpApiFixture.PlatformKey);
        var second = await client.PostAsJsonAsync("/api/platform/operators/bootstrap",
            new { email = "late@platform.test", displayName = "Late", password = "An0ther-Passw0rd!" }, Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ErrorCodeAsync()).ShouldBe("platform.bootstrap-closed");
        (await client.PostAsJsonAsync("/api/platform/tenants", new { tenantCode = "NOPE" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Wrong code or password is refused with one generic error.
        (await (await client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ErpApiFixture.OperatorEmail, password = ErpApiFixture.OperatorPassword, code = "000000" }, Ct)).ErrorCodeAsync())
            .ShouldBe("platform.invalid-credentials");
        (await (await client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ErpApiFixture.OperatorEmail, password = "wrong-password!", code = "123456" }, Ct)).ErrorCodeAsync())
            .ShouldBe("platform.invalid-credentials");

        // Operator tokens are for the platform audience only.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var created = await client.PostAsJsonAsync("/api/platform/operators",
            new { email = "second@platform.test", displayName = "Second", password = "Sec0nd-Passw0rd!" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("otpAuthUri").GetString().ShouldStartWith("otpauth://totp/");

        using var other = fixture.CreateClient();
        var secondToken = await ErpApiFixture.OperatorLoginAsync(other, "second@platform.test", "Sec0nd-Passw0rd!", body.GetProperty("mfaSecret").GetString()!, 0);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken);
        var list = await (await other.GetAsync("/api/platform/operators", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        list.EnumerateArray().Select(o => o.GetProperty("email").GetString()).ShouldContain(ErpApiFixture.OperatorEmail);

        // A deactivated operator's token stops working immediately.
        (await client.PostAsync($"/api/platform/operators/{body.GetProperty("id").GetGuid()}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync("/api/platform/operators", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var connection = await fixture.OpenAsTenantAsync(null);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [identity].PlatformAuditEntry WHERE Action IN ('operator.bootstrapped', 'operator.created', 'operator.deactivated', 'operator.sign-in-failed', 'tenant.provisioned')";
        ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task Tenant_user_tokens_cannot_call_platform_endpoints()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.GetAsync("/api/platform/operators", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

public class ExternalSignInTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Browser() =>
        fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static async Task ConfigureProviderAsync(ErpApiFixture.TenantSession t, bool requireVerifiedEmail = true)
    {
        var response = await t.Client.PostAsJsonAsync("/api/v1/identity/identity-providers", new
        {
            key = "fake", displayName = "Fake IdP", authority = FakeOidcProvider.Issuer, clientId = FakeOidcProvider.ClientId,
            clientSecretName = FakeOidcProvider.SecretName, requireVerifiedEmail,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> StartAsync(HttpClient browser, string tenantCode, string returnUrl = "/home")
    {
        var start = await browser.GetAsync($"/api/v1/auth/oidc/fake/start?tenantCode={tenantCode}&returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect, await start.Content.ReadAsStringAsync(Ct));
        var location = start.Headers.Location!.ToString();
        location.ShouldStartWith(FakeOidcProvider.Issuer + "/authorize?");
        return location;
    }

    private static Task<HttpResponseMessage> CallbackAsync(HttpClient browser, string authorizationUrl, string code)
    {
        var state = FakeOidcProvider.ParseQuery(new Uri(authorizationUrl).Query)["state"];
        return browser.GetAsync($"/api/v1/auth/oidc/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", Ct);
    }

    [Fact]
    public async Task Users_sign_in_with_the_tenants_provider_and_are_matched_by_verified_email()
    {
        var t = await fixture.NewTenantAsync();
        await ConfigureProviderAsync(t);

        using var anonymous = fixture.CreateClient();
        var offered = await (await anonymous.GetAsync($"/api/v1/auth/oidc/providers?tenantCode={t.TenantCode}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        offered.EnumerateArray().Single().GetProperty("key").GetString().ShouldBe("fake");

        using var browser = Browser();
        var authorize = await StartAsync(browser, t.TenantCode);
        var callback = await CallbackAsync(browser, authorize, FakeOidcProvider.Instance.IssueCode(authorize, t.Email.ToUpperInvariant()));
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync(Ct));
        callback.Headers.Location!.ToString().ShouldBe("/home");

        // The SPA exchanges the refresh cookie for an access token.
        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refresh.Headers.Add("X-ERP-Request", "1");
        var session = await browser.SendAsync(refresh, Ct);
        session.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await session.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await (await browser.GetAsync("/api/v1/me", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        me.GetProperty("userId").GetGuid().ShouldBe(t.AdminUserId);
    }

    [Fact]
    public async Task Unknown_or_unverified_emails_and_foreign_browsers_are_refused()
    {
        var t = await fixture.NewTenantAsync();
        await ConfigureProviderAsync(t);

        using var browser = Browser();
        var authorize = await StartAsync(browser, t.TenantCode);
        var unknown = await CallbackAsync(browser, authorize, FakeOidcProvider.Instance.IssueCode(authorize, "nobody@example.com"));
        unknown.Headers.Location!.ToString().ShouldBe("/home?error=auth.external-user-not-found");

        authorize = await StartAsync(browser, t.TenantCode);
        var unverified = await CallbackAsync(browser, authorize, FakeOidcProvider.Instance.IssueCode(authorize, t.Email, emailVerified: false));
        unverified.Headers.Location!.ToString().ShouldBe("/home?error=auth.external-email-not-verified");

        // The state is bound to the browser that started the flow (login CSRF): another browser cannot use it.
        authorize = await StartAsync(browser, t.TenantCode);
        using var attacker = Browser();
        var foreign = await CallbackAsync(attacker, authorize, FakeOidcProvider.Instance.IssueCode(authorize, t.Email));
        foreign.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await foreign.ErrorCodeAsync()).ShouldBe("auth.external-state-invalid");

        // Open redirects are refused before anything starts.
        var evil = await browser.GetAsync($"/api/v1/auth/oidc/fake/start?tenantCode={t.TenantCode}&returnUrl={Uri.EscapeDataString("https://evil.example/")}", Ct);
        evil.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await evil.ErrorCodeAsync()).ShouldBe("auth.invalid-return-url");
    }

    [Fact]
    public async Task Users_with_mfa_still_complete_the_erp_second_factor()
    {
        var t = await fixture.NewTenantAsync();
        await ConfigureProviderAsync(t);
        var enroll = await (await t.Client.PostAsync("/api/v1/auth/mfa/enroll", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secret = Base32.Decode(enroll.GetProperty("secret").GetString()!);
        (await t.Client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = Totp.Compute(secret, Totp.TimeStep(DateTime.UtcNow)) }, Ct)).EnsureSuccessStatusCode();

        using var browser = Browser();
        var authorize = await StartAsync(browser, t.TenantCode, ErpApiFixture.AllowedOrigin + "/signed-in");
        var callback = await CallbackAsync(browser, authorize, FakeOidcProvider.Instance.IssueCode(authorize, t.Email));
        var location = callback.Headers.Location!.ToString();
        location.ShouldStartWith(ErpApiFixture.AllowedOrigin + "/signed-in?mfaToken=");
        var mfaToken = FakeOidcProvider.ParseQuery(new Uri(location).Query)["mfaToken"];

        var verified = await browser.PostAsJsonAsync("/api/v1/auth/mfa/verify",
            new { mfaToken, code = Totp.Compute(secret, Totp.TimeStep(DateTime.UtcNow) + 1) }, Ct);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public class DataScopeTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Users_limited_to_a_warehouse_only_see_and_create_its_documents()
    {
        var t = await fixture.NewTenantAsync();
        Task<HttpResponseMessage> Post(string url, object? body = null) => t.Client.SendAsCompanyAsync(HttpMethod.Post, url, t.CompanyId, body);
        (await Post("/api/v1/accounting/coa-templates/generic")).EnsureSuccessStatusCode();
        var warehouses = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", t.CompanyId));
        var main = warehouses.GetProperty("items").EnumerateArray().First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var branches = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/branches", t.CompanyId));
        var other = (await Json(await Post("/api/v1/organization/warehouses",
            new { branchId = branches.EnumerateArray().First().GetProperty("id").GetGuid(), code = "WH2", name = "Second", type = "Physical" }))).GetProperty("id").GetGuid();
        var uoms = (await Json(await Post("/api/v1/catalog/uoms/seed-standard"))).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = (await Json(await Post("/api/v1/catalog/categories",
            new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))).GetProperty("id").GetGuid();
        var product = (await Json(await Post("/api/v1/catalog/products",
            new { sku = "P1", nameEn = "P1", nameAr = "P1", type = "Stockable", baseUomId = uoms["PCS"], categoryId = category }))).GetProperty("id").GetGuid();

        object Adjustment(Guid warehouseId) => new
        {
            warehouseId, date = "2026-02-01", reason = "Damage",
            lines = new object[] { new { productId = product, uomId = uoms["PCS"], quantity = 1 } },
        };

        var inMain = (await Json(await Post("/api/v1/inventory/adjustments", Adjustment(main)))).GetProperty("id").GetGuid();
        var inOther = (await Json(await Post("/api/v1/inventory/adjustments", Adjustment(other)))).GetProperty("id").GetGuid();

        // A storekeeper limited to the main warehouse.
        var role = (await Json(await t.Client.PostAsJsonAsync("/api/v1/identity/roles",
            new { name = "Storekeeper", permissions = new[] { "inventory.read", "inventory.document.manage" } }, Ct))).GetProperty("id").GetGuid();
        var email = $"keeper@{t.TenantCode.ToLowerInvariant()}.example";
        const string password = "Keep3r-Passw0rd!";
        var user = (await Json(await t.Client.PostAsJsonAsync("/api/v1/identity/users",
            new { email, displayName = "Keeper", password, roleIds = new[] { role }, companyIds = new[] { t.CompanyId } }, Ct))).GetProperty("id").GetGuid();
        (await t.Client.PutAsJsonAsync($"/api/v1/identity/users/{user}/companies/{t.CompanyId}",
            new { granted = true, warehouseIds = new[] { main } }, Ct)).EnsureSuccessStatusCode();

        using var keeper = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(keeper, t.TenantCode, email, password);
        var list = await Json(await keeper.GetAsCompanyAsync("/api/v1/inventory/adjustments", t.CompanyId));
        list.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ShouldBe([inMain]);
        (await keeper.GetAsCompanyAsync($"/api/v1/inventory/adjustments/{inOther}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var denied = await keeper.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/inventory/adjustments", t.CompanyId, Adjustment(other));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.ErrorCodeAsync()).ShouldBe("data-scope.denied");
        (await keeper.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/inventory/adjustments", t.CompanyId, Adjustment(main))).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The administrator (all data scopes) still sees both warehouses.
        var all = await Json(await t.Client.GetAsCompanyAsync("/api/v1/inventory/adjustments", t.CompanyId));
        all.GetProperty("items").GetArrayLength().ShouldBe(3);
    }
}
