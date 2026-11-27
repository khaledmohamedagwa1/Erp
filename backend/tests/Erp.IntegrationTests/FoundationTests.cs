using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Identity.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class IdempotencyTests(ErpApiFixture fixture)
{
    private async Task<(ErpApiFixture.TenantSession Tenant, Guid LegalEntityId)> SetupAsync()
    {
        var t = await fixture.NewTenantAsync();
        var legal = await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>();
        return (t, legal.EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Retrying_with_the_same_key_replays_the_first_response_without_a_second_effect()
    {
        var (t, legalEntityId) = await SetupAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { legalEntityId, code = "ALX", name = "Alexandria" };

        var missing = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, body);
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await missing.ErrorCodeAsync()).ShouldBe("idempotency.key-required");

        var first = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, body, idempotencyKey: key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains("Idempotency-Replayed").ShouldBeFalse();
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var replay = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, body, idempotencyKey: key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotency-Replayed").ShouldBe(["true"]);
        (await replay.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid().ShouldBe(firstId);

        var reused = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId,
            new { legalEntityId, code = "GIZ", name = "Giza" }, idempotencyKey: key);
        (await reused.ErrorCodeAsync()).ShouldBe("idempotency.key-reused");

        await AssertBranchCount(t, "ALX", 1);
    }

    [Fact]
    public async Task Failed_requests_are_not_stored_and_can_be_retried_with_the_same_key()
    {
        var (t, legalEntityId) = await SetupAsync();
        var key = Guid.NewGuid().ToString();
        var duplicateOfHq = new { legalEntityId, code = "HQ", name = "Duplicate" };
        (await (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, duplicateOfHq, idempotencyKey: key)).ErrorCodeAsync())
            .ShouldBe("organization.branch-code-taken");
        var again = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, duplicateOfHq, idempotencyKey: key);
        again.Headers.Contains("Idempotency-Replayed").ShouldBeFalse();
        (await again.ErrorCodeAsync()).ShouldBe("organization.branch-code-taken");
    }

    [Fact]
    public async Task Concurrent_duplicates_create_exactly_one_effect()
    {
        var (t, legalEntityId) = await SetupAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { legalEntityId, code = "PAR", name = "Parallel" };
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/organization/branches", t.CompanyId, body, idempotencyKey: key)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains("Idempotency-Replayed")).ShouldBe(1);
        await AssertBranchCount(t, "PAR", 1);
    }

    private static async Task AssertBranchCount(ErpApiFixture.TenantSession t, string code, int expected)
    {
        var branches = await (await t.Client.GetAsCompanyAsync("/api/v1/organization/branches", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>();
        branches.EnumerateArray().Count(b => b.GetProperty("code").GetString() == code).ShouldBe(expected);
    }
}

public class InboxTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task A_redelivered_event_is_handled_once_by_an_inbox_consumer()
    {
        var t = await fixture.NewTenantAsync();
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        for (var i = 0; i < 20 && !CompanyCreatedInboxCounter.Counts.ContainsKey(t.CompanyId); i++)
        {
            await processor.ProcessPendingAsync(TestContext.Current.CancellationToken);
        }

        CompanyCreatedInboxCounter.Counts[t.CompanyId].ShouldBe(1);
        var recordedBefore = CompanyCreatedRecorder.Received.Count(r => r.CompanyId == t.CompanyId);

        // Simulate redelivery (e.g. the worker crashed after handling but before marking the message processed).
        await using (var connection = await fixture.OpenAsTenantAsync(null))
        {
            await using var reset = connection.CreateCommand();
            reset.CommandText = "UPDATE organization.OutboxMessage SET ProcessedAtUtc = NULL WHERE Type LIKE '%CompanyCreated' AND Payload LIKE @id";
            reset.Parameters.AddWithValue("@id", $"%{t.CompanyId}%");
            (await reset.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        for (var i = 0; i < 20 && CompanyCreatedRecorder.Received.Count(r => r.CompanyId == t.CompanyId) == recordedBefore; i++)
        {
            await processor.ProcessPendingAsync(TestContext.Current.CancellationToken);
        }

        CompanyCreatedRecorder.Received.Count(r => r.CompanyId == t.CompanyId).ShouldBe(recordedBefore + 1, "the message was redelivered");
        CompanyCreatedInboxCounter.Counts[t.CompanyId].ShouldBe(1, "the inbox consumer ignored the duplicate");
    }
}

public class MfaTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Enrolled_users_need_a_second_factor_and_codes_cannot_be_replayed()
    {
        var t = await fixture.NewTenantAsync();
        var enroll = await (await t.Client.PostAsync("/api/v1/auth/mfa/enroll", null, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var secret = Base32.Decode(enroll.GetProperty("secret").GetString()!);
        enroll.GetProperty("otpAuthUri").GetString()!.ShouldStartWith("otpauth://totp/");
        var step = Totp.TimeStep(DateTime.UtcNow);

        (await (await t.Client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = "000000" }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.mfa-invalid-code");
        var confirm = await t.Client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = Totp.Compute(secret, step) }, TestContext.Current.CancellationToken);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var recovery = (await confirm.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        recovery.Count.ShouldBe(10);

        using var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = t.Email, password = t.Password }, TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var challenge = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        challenge.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        challenge.TryGetProperty("accessToken", out _).ShouldBeFalse();
        var mfaToken = challenge.GetProperty("mfaToken").GetString();

        // The step used at confirmation is spent; the next step's code is accepted once.
        (await (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken, code = Totp.Compute(secret, step) }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.mfa-invalid-code");
        var next = Totp.Compute(secret, step + 1);
        var verified = await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken, code = next }, TestContext.Current.CancellationToken);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verified.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
        (await (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken, code = next }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.mfa-invalid-code");

        // Recovery codes work once.
        (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken, recoveryCode = recovery[0] }, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken, recoveryCode = recovery[0] }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.mfa-invalid-code");

        (await (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken = "tampered", code = next }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.mfa-challenge-invalid");

        // Disabling needs the password and a second factor.
        (await t.Client.PostAsJsonAsync("/api/v1/auth/mfa/disable", new { password = t.Password, code = recovery[1] }, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var plain = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = t.Email, password = t.Password }, TestContext.Current.CancellationToken);
        (await plain.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
    }
}

public class PasswordAndActivationTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Changing_the_password_ends_every_other_session()
    {
        var t = await fixture.NewTenantAsync();
        using var other = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(other, t.TenantCode, t.Email, t.Password);

        (await (await t.Client.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "wrong-password!", newPassword = "Brand-New-Passw0rd" }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("identity.invalid-current-password");
        (await t.Client.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = t.Password, newPassword = "Brand-New-Passw0rd" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await t.Client.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var fresh = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(fresh, t.TenantCode, t.Email, "Brand-New-Passw0rd");
    }

    [Fact]
    public async Task Admin_reset_forces_a_password_change_before_sign_in()
    {
        var t = await fixture.NewTenantAsync();
        var userId = await CreateUserAsync(t, "clerk@test.example", "Clerk-Passw0rd!");
        using var clerk = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(clerk, t.TenantCode, "clerk@test.example", "Clerk-Passw0rd!");

        var reset = await t.Client.PostAsJsonAsync($"/api/v1/identity/users/{userId}/reset-password", new { }, TestContext.Current.CancellationToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        var temporary = (await reset.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("temporaryPassword").GetString()!;
        (await clerk.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = "clerk@test.example", password = temporary }, TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("auth.password-change-required");
        var token = problem.GetProperty("passwordChangeToken").GetString();

        (await client.PostAsJsonAsync("/api/v1/auth/password/complete-change", new { passwordChangeToken = token, newPassword = "Clerk-New-Passw0rd" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await client.PostAsJsonAsync("/api/v1/auth/password/complete-change", new { passwordChangeToken = token, newPassword = "Another-Passw0rd!" }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.password-change-token-invalid");
        await ErpApiFixture.SignInAsync(client, t.TenantCode, "clerk@test.example", "Clerk-New-Passw0rd");

        // The notification is sent after commit, from the outbox, once.
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        for (var i = 0; i < 20 && !RecordingNotificationSender.Sent.Any(m => m.To == "clerk@test.example"); i++)
        {
            await processor.ProcessPendingAsync(TestContext.Current.CancellationToken);
        }

        RecordingNotificationSender.Sent.Count(m => m.To == "clerk@test.example" && m.Subject == "Your password was reset").ShouldBe(1);
    }

    [Fact]
    public async Task Deactivation_revokes_access_immediately_and_reactivation_restores_it()
    {
        var t = await fixture.NewTenantAsync();
        var userId = await CreateUserAsync(t, "temp@test.example", "Temp-Passw0rd!");
        using var user = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(user, t.TenantCode, "temp@test.example", "Temp-Passw0rd!");

        (await (await t.Client.PostAsync($"/api/v1/identity/users/{t.AdminUserId}/deactivate", null, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("identity.cannot-deactivate-self");
        var deactivated = await t.Client.PostAsync($"/api/v1/identity/users/{userId}/deactivate", null, TestContext.Current.CancellationToken);
        (await deactivated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("isActive").GetBoolean().ShouldBeFalse();
        (await user.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        user.DefaultRequestHeaders.Authorization = null; // a revoked token is rejected even on anonymous endpoints
        (await (await user.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = "temp@test.example", password = "Temp-Passw0rd!" }, TestContext.Current.CancellationToken)).ErrorCodeAsync())
            .ShouldBe("auth.invalid-credentials");

        (await t.Client.PostAsync($"/api/v1/identity/users/{userId}/reactivate", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ErpApiFixture.SignInAsync(user, t.TenantCode, "temp@test.example", "Temp-Passw0rd!");
    }

    [Fact]
    public async Task Audit_log_records_password_changes_without_the_hash()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = t.Password, newPassword = "Audited-Passw0rd!" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var connection = await fixture.OpenAsTenantAsync(t.TenantId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Changes FROM audit.AuditLog WHERE EntityType = 'identity.User'";
        var rows = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add(reader.GetString(0));
            }
        }

        rows.ShouldContain(r => r.Contains("\"PasswordHash\":[\"***\",\"***\"]", StringComparison.Ordinal));
        rows.ShouldAllBe(r => !r.Contains("AQAAAA", StringComparison.Ordinal));
    }

    private static async Task<Guid> CreateUserAsync(ErpApiFixture.TenantSession t, string email, string password)
    {
        var created = await t.Client.PostAsJsonAsync("/api/v1/identity/users", new { email, displayName = email, password, companyIds = new[] { t.CompanyId } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

public class OpenApiAndReliabilityTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task OpenApi_document_declares_bearer_auth_and_custom_headers()
    {
        using var client = fixture.CreateClient();
        var doc = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString().ShouldBe("bearer");

        var post = doc.GetProperty("paths").GetProperty("/api/v1/organization/branches").GetProperty("post");
        var headers = post.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        headers.ShouldContain("Idempotency-Key");
        headers.ShouldContain("X-Company-Id");
        post.GetProperty("security").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_real_sql_server_deadlock_is_classified_as_transient()
    {
        var detector = fixture.Factory.Services.GetRequiredService<ITransientFailureDetector>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var a = new SqlConnection(fixture.ConnectionString);
        await using var b = new SqlConnection(fixture.ConnectionString);
        await a.OpenAsync(TestContext.Current.CancellationToken);
        await b.OpenAsync(TestContext.Current.CancellationToken);
        await Exec(a, $"CREATE TABLE ##dl_a_{suffix} (v int); CREATE TABLE ##dl_b_{suffix} (v int); INSERT ##dl_a_{suffix} VALUES (1); INSERT ##dl_b_{suffix} VALUES (1);");
        await Exec(b, "SET DEADLOCK_PRIORITY LOW;");

        await using var ta = a.BeginTransaction();
        await using var tb = b.BeginTransaction();
        await Exec(a, $"UPDATE ##dl_a_{suffix} SET v = 2", ta);
        await Exec(b, $"UPDATE ##dl_b_{suffix} SET v = 2", tb);
        var first = Exec(a, $"UPDATE ##dl_b_{suffix} SET v = 3", ta);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var victim = await Should.ThrowAsync<SqlException>(() => Exec(b, $"UPDATE ##dl_a_{suffix} SET v = 3", tb));
        await first;

        victim.Number.ShouldBe(1205);
        detector.IsTransient(victim).ShouldBeTrue();
        detector.IsTransient(new InvalidOperationException("not a database error")).ShouldBeFalse();
    }

    private static async Task Exec(SqlConnection connection, string sql, SqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }
}
