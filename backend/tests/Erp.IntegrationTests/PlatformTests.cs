using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class AuthenticationTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Provisioned_administrator_can_sign_in_and_read_their_profile()
    {
        var t = await fixture.NewTenantAsync();
        var me = await t.Client.GetFromJsonAsync<JsonElement>("/api/v1/me", TestContext.Current.CancellationToken);
        me.GetProperty("tenantCode").GetString().ShouldBe(t.TenantCode);
        me.GetProperty("companies").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(t.CompanyId);
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("identity.role.manage");
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_five_failures_lock_the_account()
    {
        var t = await fixture.NewTenantAsync();
        using var client = fixture.CreateClient();
        for (var i = 0; i < 4; i++)
        {
            var failed = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = t.Email, password = "wrong-password" }, TestContext.Current.CancellationToken);
            failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await failed.ErrorCodeAsync()).ShouldBe("auth.invalid-credentials");
        }

        var fifth = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = t.Email, password = "wrong-password" }, TestContext.Current.CancellationToken);
        (await fifth.ErrorCodeAsync()).ShouldBe("auth.locked-out");
        var correctButLocked = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = t.Email, password = t.Password }, TestContext.Current.CancellationToken);
        (await correctButLocked.ErrorCodeAsync()).ShouldBe("auth.locked-out");
    }

    [Fact]
    public async Task Unknown_tenant_and_unknown_user_look_identical()
    {
        using var client = fixture.CreateClient();
        var noTenant = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = "NOPE", email = "x@y.z", password = "whatever-123" }, TestContext.Current.CancellationToken);
        (await noTenant.ErrorCodeAsync()).ShouldBe("auth.invalid-credentials");
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reusing_an_old_token_revokes_the_session()
    {
        var t = await fixture.NewTenantAsync();
        var cookies = fixture.Factory.Server.BaseAddress;
        // Capture the first refresh token from the cookie jar by calling refresh through a raw request.
        var first = await t.Client.SendAsync(Refresh(), TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oldCookie = first.RequestMessage!.Headers.TryGetValues("Cookie", out var sent) ? sent.Single() : null;
        oldCookie.ShouldNotBeNull();

        var second = await t.Client.SendAsync(Refresh(), TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var attacker = fixture.CreateClient();
        var replay = Refresh();
        replay.Headers.Add("Cookie", oldCookie);
        var reused = await attacker.SendAsync(replay, TestContext.Current.CancellationToken);
        reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await reused.ErrorCodeAsync()).ShouldBe("auth.refresh-token-reused");

        // The legitimate session is gone too: its access token no longer works.
        (await t.Client.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _ = cookies;
    }

    [Fact]
    public async Task Refresh_requires_the_anti_forgery_header()
    {
        var t = await fixture.NewTenantAsync();
        var response = await t.Client.PostAsync("/api/v1/auth/refresh", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logout_revokes_the_access_token_immediately()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.PostAsync("/api/v1/auth/logout", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await t.Client.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpRequestMessage Refresh()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("X-ERP-Request", "1");
        return request;
    }
}

public class TenantIsolationTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Tenants_with_identical_codes_never_see_each_other()
    {
        var a = await fixture.NewTenantAsync("SAME");
        var b = await fixture.NewTenantAsync("SAME");

        var companies = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/companies", TestContext.Current.CancellationToken);
        companies.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([a.CompanyId]);

        // Another tenant's company by id: not found (no existence disclosure).
        (await a.Client.GetAsync($"/api/v1/organization/companies/{b.CompanyId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Selecting another tenant's company in the header: refused.
        var foreign = await a.Client.GetAsCompanyAsync("/api/v1/organization/warehouses", b.CompanyId);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await foreign.ErrorCodeAsync()).ShouldBe("company.access-denied");
    }

    [Fact]
    public async Task Row_level_security_filters_raw_sql_by_session_tenant()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();

        await using (var asA = await fixture.OpenAsTenantAsync(a.TenantId))
        {
            var ids = await ReadIds(asA, "SELECT Id FROM organization.Company");
            ids.ShouldContain(a.CompanyId);
            ids.ShouldNotContain(b.CompanyId);
        }

        // No tenant in the session: tenant tables return nothing at all.
        await using var anonymous = await fixture.OpenAsTenantAsync(null);
        (await ReadIds(anonymous, "SELECT Id FROM [identity].[User]")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Row_level_security_blocks_writing_rows_for_another_tenant()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        await using var asA = await fixture.OpenAsTenantAsync(a.TenantId);
        await using var insert = asA.CreateCommand();
        insert.CommandText = """
            INSERT INTO configuration.CompanyPolicy (Id, TenantId, CompanyId, Section, Json, Version, CreatedAtUtc)
            VALUES (NEWID(), @b, @company, 'x', '{}', 1, SYSUTCDATETIME());
            """;
        insert.Parameters.AddWithValue("@b", b.TenantId);
        insert.Parameters.AddWithValue("@company", b.CompanyId);
        var error = await Should.ThrowAsync<SqlException>(() => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        error.Message.ShouldContain("block predicate");
    }

    [Fact]
    public async Task Session_tenant_cannot_be_switched_on_an_open_connection()
    {
        var a = await fixture.NewTenantAsync();
        await using var asA = await fixture.OpenAsTenantAsync(a.TenantId);
        await using var change = asA.CreateCommand();
        change.CommandText = "EXEC sys.sp_set_session_context @key = N'TenantId', @value = @t;";
        change.Parameters.AddWithValue("@t", Guid.NewGuid());
        await Should.ThrowAsync<SqlException>(() => change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<List<Guid>> ReadIds(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}

public class AuthorizationTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Users_without_a_permission_are_refused_by_the_server()
    {
        var t = await fixture.NewTenantAsync();
        var role = await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Viewer", permissions = new[] { "organization.company.read" } }, TestContext.Current.CancellationToken);
        role.StatusCode.ShouldBe(HttpStatusCode.Created);
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var created = await t.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email = "viewer@test.example", displayName = "Viewer", password = "Viewer-Passw0rd!", roleIds = new[] { roleId }, companyIds = new[] { t.CompanyId },
        }, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var viewer = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(viewer, t.TenantCode, "viewer@test.example", "Viewer-Passw0rd!");
        (await viewer.GetAsync("/api/v1/organization/companies", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var denied = await viewer.GetAsync("/api/v1/identity/users", TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.ErrorCodeAsync()).ShouldBe("forbidden");

        // Navigation only shows what the user may open.
        var nav = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/me/navigation", TestContext.Current.CancellationToken);
        nav.EnumerateArray().SelectMany(g => g.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("key").GetString())
            .ShouldBe(["organization.companies"]);
    }

    [Fact]
    public async Task Unknown_permissions_are_rejected_when_creating_roles()
    {
        var t = await fixture.NewTenantAsync();
        var response = await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Bad", permissions = new[] { "sales.invoice.post-anything" } }, TestContext.Current.CancellationToken);
        (await response.ErrorCodeAsync()).ShouldBe("identity.unknown-permission");
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected_and_platform_key_is_required()
    {
        using var client = fixture.CreateClient();
        (await client.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/platform/tenants", new { }, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

public class ConfigurationTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Preset_enables_modules_and_dependencies_are_enforced()
    {
        var t = await fixture.NewTenantAsync(preset: "retail");
        var modules = await (await t.Client.GetAsCompanyAsync("/api/v1/configuration/modules", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        modules.EnumerateArray().Where(m => m.GetProperty("isEnabled").GetBoolean()).Select(m => m.GetProperty("code").GetString())
            .ShouldContain("pos");

        var inUse = await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/modules/inventory", t.CompanyId, new { enabled = false });
        inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await inUse.ErrorCodeAsync()).ShouldBe("configuration.module-in-use");

        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/modules/pos", t.CompanyId, new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/modules/inventory", t.CompanyId, new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Policy_updates_require_the_current_version()
    {
        var t = await fixture.NewTenantAsync();
        var get = await t.Client.GetAsCompanyAsync("/api/v1/configuration/policies/sales", t.CompanyId);
        var etag = get.Headers.ETag!.Tag;
        var values = new { stockIssueTiming = "AtDelivery", enableCreditLimits = true, creditLimitAction = "Block", allowCreatorToApprove = false };

        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/sales", t.CompanyId, values)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionRequired);

        var ok = await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/sales", t.CompanyId, values, etag);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("version").GetInt32().ShouldBe(2);

        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/sales", t.CompanyId, values, etag)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed);

        var invalid = await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/policies/sales", t.CompanyId,
            new { stockIssueTiming = "Whenever" }, ok.Headers.ETag!.Tag);
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}

public class OrganizationTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Company_structure_is_created_and_scoped_to_the_selected_company()
    {
        var t = await fixture.NewTenantAsync();
        var second = await t.Client.PostAsJsonAsync("/api/v1/organization/companies", new
        {
            code = "SECOND", nameEn = "Second Co", nameAr = "الشركة الثانية", baseCurrency = "SAR", businessPreset = "wholesale",
        }, TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var legal = await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", secondId)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var legalEntityId = legal.EnumerateArray().Single().GetProperty("id").GetGuid();

        var branch = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", secondId, new { legalEntityId, code = "JED", name = "Jeddah" });
        branch.StatusCode.ShouldBe(HttpStatusCode.Created);
        var duplicate = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", secondId, new { legalEntityId, code = "jed", name = "Again" });
        (await duplicate.ErrorCodeAsync()).ShouldBe("organization.branch-code-taken");

        // The first company does not see the second company's branches.
        var firstBranches = await (await t.Client.GetAsCompanyAsync("/api/v1/organization/branches", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        firstBranches.EnumerateArray().Select(b => b.GetProperty("code").GetString()).ShouldBe(["HQ"]);

        // A branch from another company cannot be used for a warehouse.
        var foreignLegal = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, new { legalEntityId, code = "X1", name = "X" });
        (await foreignLegal.ErrorCodeAsync()).ShouldBe("organization.legal-entity-not-found");
    }

    [Fact]
    public async Task Validation_errors_name_the_offending_fields()
    {
        var t = await fixture.NewTenantAsync();
        var response = await t.Client.PostAsJsonAsync("/api/v1/organization/companies", new { code = "", nameEn = "", nameAr = "", baseCurrency = "EG", businessPreset = "x" }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldContain("baseCurrency");
    }

    [Fact]
    public async Task Error_titles_are_localised_to_arabic()
    {
        using var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { tenantCode = "NOPE", email = "x@y.z", password = "whatever-123" }) };
        request.Headers.AcceptLanguage.ParseAdd("ar");
        var body = await (await client.SendAsync(request, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("title").GetString().ShouldBe("البريد الإلكتروني أو كلمة المرور غير صحيحة.");
    }
}

public class PlatformServicesTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Document_numbers_are_unique_and_gapless_under_concurrency_and_rollback()
    {
        var t = await fixture.NewTenantAsync();
        var scope = new NumberScope(t.CompanyId, "TEST", "TST", FiscalYear: 2026);

        async Task<string> Allocate(bool commit)
        {
            await using var di = fixture.Factory.Services.CreateAsyncScope();
            di.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(t.TenantId);
            var session = di.ServiceProvider.GetRequiredService<ErpDbSession>();
            await session.BeginTransactionAsync();
            var number = await di.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>().NextAsync(scope);
            if (commit)
            {
                await session.CommitAsync();
            }
            else
            {
                await session.RollbackAsync();
            }

            return number;
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Allocate(true)));
        numbers.Distinct().Count().ShouldBe(40);
        numbers.Order().ShouldBe(Enumerable.Range(1, 40).Select(i => $"TST-2026-{i:000000}"));

        var rolledBack = await Allocate(false);
        rolledBack.ShouldBe("TST-2026-000041");
        (await Allocate(true)).ShouldBe("TST-2026-000041");
    }

    [Fact]
    public async Task Audit_log_is_hash_chained_and_tampering_is_detected()
    {
        var t = await fixture.NewTenantAsync();
        await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Auditor", permissions = new[] { "organization.company.read" } }, TestContext.Current.CancellationToken);

        async Task<long?> Verify()
        {
            await using var di = fixture.Factory.Services.CreateAsyncScope();
            di.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(t.TenantId);
            return await di.ServiceProvider.GetRequiredService<AuditChainVerifier>().FindFirstBrokenRecordAsync();
        }

        (await Verify()).ShouldBeNull();

        await using (var asTenant = await fixture.OpenAsTenantAsync(t.TenantId))
        {
            await using var count = asTenant.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM audit.AuditLog";
            ((int)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBeGreaterThan(5);

            await using var tamper = asTenant.CreateCommand();
            tamper.CommandText = "UPDATE audit.AuditLog SET Changes = N'{}' WHERE Sequence = 2";
            await tamper.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await Verify()).ShouldBe(2);
    }

    [Fact]
    public async Task Domain_events_are_delivered_from_the_outbox_with_the_right_tenant()
    {
        var t = await fixture.NewTenantAsync();
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        for (var i = 0; i < 20 && !CompanyCreatedRecorder.Received.Contains((t.TenantId, t.CompanyId)); i++)
        {
            await processor.ProcessPendingAsync(TestContext.Current.CancellationToken);
        }

        CompanyCreatedRecorder.Received.ShouldContain((t.TenantId, t.CompanyId));
    }

    [Fact]
    public async Task Readiness_probe_checks_the_database()
    {
        using var client = fixture.CreateClient();
        (await client.GetStringAsync("/health/ready", TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }
}
