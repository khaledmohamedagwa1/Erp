using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Erp.Application.Observability;
using Erp.Infrastructure.Database;
using Erp.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class HostingTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        using var client = fixture.CreateClient();
        foreach (var url in new[] { "/health/live", "/api/v1/me" })
        {
            var response = await client.GetAsync(url, Ct);
            response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
            response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
            response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
            response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe(SecurityHeadersMiddleware.ApiContentSecurityPolicy);
            response.Headers.Contains("Server").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Swagger_ui_gets_a_policy_that_allows_its_own_scripts()
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync("/swagger/index.html", Ct);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("script-src 'self'");
    }

    [Fact]
    public async Task Cors_allows_only_the_configured_origin_with_credentials()
    {
        using var client = fixture.CreateClient();

        var allowed = await client.SendAsync(Preflight(ErpApiFixture.AllowedOrigin), Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        allowed.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([ErpApiFixture.AllowedOrigin]);
        allowed.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);

        var other = await client.SendAsync(Preflight("https://evil.example"), Ct);
        other.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        var simple = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        simple.Headers.Add("Origin", ErpApiFixture.AllowedOrigin);
        var response = await client.SendAsync(simple, Ct);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([ErpApiFixture.AllowedOrigin]);
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/organization/companies");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-company-id,idempotency-key");
        return request;
    }

    [Fact]
    public async Task Readiness_includes_the_migration_check_and_the_schema_is_current()
    {
        var pending = await fixture.Factory.Services.GetRequiredService<IPendingMigrationsReader>().GetPendingAsync(Ct);
        pending.ShouldBeEmpty();
        using var client = fixture.CreateClient();
        (await client.GetStringAsync("/health/ready", Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Pending_migrations_make_readiness_unhealthy()
    {
        var check = new MigrationStatusHealthCheck(new FakePending(["IdentityDbContext: 20990101000000_Future"]));
        (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Outbox_lag_degrades_then_fails_readiness_and_the_real_query_runs()
    {
        var options = Options.Create(new HostingOptions { OutboxLagDegradedSeconds = 60, OutboxLagUnhealthySeconds = 600 });
        (await new OutboxLagHealthCheck(new FakeLag(TimeSpan.FromSeconds(5)), options).CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Healthy);
        (await new OutboxLagHealthCheck(new FakeLag(TimeSpan.FromMinutes(2)), options).CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Degraded);
        (await new OutboxLagHealthCheck(new FakeLag(TimeSpan.FromHours(1)), options).CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);

        // The SQL across every module outbox is valid (the test API runs as role=api, so messages accumulate undelivered).
        var lag = await fixture.Factory.Services.GetRequiredService<IOutboxLagReader>().GetOldestPendingAgeAsync(Ct);
        lag.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Failed_sign_ins_are_counted()
    {
        long failed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ErpMetrics.MeterName && instrument.Name == "erp.auth.failed_logins")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failed, value));
        listener.Start();

        var t = await fixture.NewTenantAsync();
        using var client = fixture.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode = t.TenantCode, email = t.Email, password = "wrong-password" }, Ct);
        Interlocked.Read(ref failed).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Production_refuses_development_grade_settings()
    {
        var bad = Config(new()
        {
            ["Auth:SecureCookies"] = "false",
            ["Platform:ApiKey"] = "short",
            ["Database:AutoMigrate"] = "true",
            ["Hosting:CorsAllowedOrigins:0"] = "*",
            ["Hosting:CorsAllowedOrigins:1"] = "http://app.example.com",
        });
        var problems = ErpProductionChecks.Validate(bad, new FakeEnvironment("Production"));
        problems.Count.ShouldBe(6);
        problems.ShouldContain(p => p.Contains("Auth:SigningKeyPem", StringComparison.Ordinal));
        Should.Throw<InvalidOperationException>(() => ErpProductionChecks.ThrowIfInvalid(bad, new FakeEnvironment("Production")));

        // Development and Testing are exempt; an explicit skip is honoured.
        ErpProductionChecks.Validate(bad, new FakeEnvironment("Development")).ShouldBeEmpty();
        Should.NotThrow(() => ErpProductionChecks.ThrowIfInvalid(Config(new() { ["Hosting:SkipProductionChecks"] = "true" }), new FakeEnvironment("Production")));
    }

    [Fact]
    public void Production_accepts_hardened_settings()
    {
        var good = Config(new()
        {
            ["Auth:SigningKeyPem"] = "-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----",
            ["Auth:SecureCookies"] = "true",
            ["Platform:ApiKey"] = new string('k', 48),
            ["Database:AutoMigrate"] = "false",
            ["Hosting:CorsAllowedOrigins:0"] = "https://erp.customer.example",
        });
        ErpProductionChecks.Validate(good, new FakeEnvironment("Production")).ShouldBeEmpty();
    }

    [Fact]
    public void Health_probe_targets_the_configured_http_port()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "8081;8082");
            ErpHealthProbe.DefaultUrl().ShouldBe("http://127.0.0.1:8081/health/ready");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", previous);
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class FakePending(IReadOnlyList<string> pending) : IPendingMigrationsReader
    {
        public Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(pending);
    }

    private sealed class FakeLag(TimeSpan lag) : IOutboxLagReader
    {
        public Task<TimeSpan> GetOldestPendingAgeAsync(CancellationToken cancellationToken = default) => Task.FromResult(lag);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Erp.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
