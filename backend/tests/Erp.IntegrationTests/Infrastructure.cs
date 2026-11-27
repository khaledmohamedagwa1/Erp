using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Database;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Organization.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Erp.IntegrationTests.ErpApiFixture))]

namespace Erp.IntegrationTests;

/// <summary>MRP demand injected by tests, keyed by company (stands in for the not-yet-wired sales source).</summary>
public sealed class TestMrpDemandSource : IMrpDemandSource
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<MrpDemand>> Demand = new();

    public string Name => "test-demand";

    public Task<IReadOnlyList<MrpDemand>> GetDemandAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default) =>
        Task.FromResult(Demand.TryGetValue(companyId, out var demand) ? demand : (IReadOnlyList<MrpDemand>)[]);
}

/// <summary>Records CompanyCreated events delivered from the outbox.</summary>
public sealed class CompanyCreatedRecorder : IIntegrationEventHandler<CompanyCreated>
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<(Guid TenantId, Guid CompanyId)> Received = [];

    private readonly ITenantContext _tenant;

    public CompanyCreatedRecorder(ITenantContext tenant) => _tenant = tenant;

    public Task HandleAsync(CompanyCreated integrationEvent, CancellationToken cancellationToken)
    {
        Received.Add((_tenant.TenantId!.Value, integrationEvent.CompanyId));
        return Task.CompletedTask;
    }
}

public sealed class RecordingNotificationSender : INotificationSender
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<EmailMessage> Sent = [];

    public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Counts CompanyCreated deliveries through the inbox: must stay at one per company even if the event is redelivered.</summary>
public sealed class CompanyCreatedInboxCounter(IUnitOfWork unitOfWork, IInbox inbox, IOutboxMessageContext message)
    : InboxIntegrationEventHandler<CompanyCreated>(unitOfWork, inbox, message)
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> Counts = new();

    protected override Task HandleOnceAsync(CompanyCreated integrationEvent, CancellationToken cancellationToken)
    {
        Counts.AddOrUpdate(integrationEvent.CompanyId, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }
}

/// <summary>
/// One API instance and one fresh database for the whole test run. Uses ERP_TEST_SQL (a SQL Server connection string
/// without a database name) when set — e.g. in CI with a service container — otherwise starts SQL Server with Testcontainers.
/// </summary>
public sealed class ErpApiFixture : IAsyncLifetime
{
    public const string PlatformKey = "test-platform-key";

    /// <summary>The browser origin allowed by CORS in tests (the Angular app's origin in a real deployment).</summary>
    public const string AllowedOrigin = "https://app.erp.test";

    private MsSqlContainer? _container;
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString { get; private set; } = string.Empty;

    public WebApplicationFactory<Program> Factory => _factory!;

    /// <summary>Local attachment store for this run (outside the repository); deleted on dispose.</summary>
    public string AttachmentRoot { get; } = Path.Combine(Path.GetTempPath(), "erp-test-attachments-" + Guid.NewGuid().ToString("N")[..12]);

    public async ValueTask InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("ERP_TEST_SQL");
        if (string.IsNullOrWhiteSpace(server))
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            server = _container.GetConnectionString();
        }

        ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "ErpTest_" + Guid.NewGuid().ToString("N")[..12], TrustServerCertificate = true }.ConnectionString;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Erp", ConnectionString);
            builder.UseSetting("Erp:Role", "api");
            builder.UseSetting("Platform:ApiKey", PlatformKey);
            builder.UseSetting("Auth:SecureCookies", "false");
            builder.UseSetting("RateLimiting:AuthPerMinute", "10000");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
            builder.UseSetting("OpenApi:Enabled", "true");
            builder.UseSetting("Documents:Attachments:LocalRoot", AttachmentRoot);
            builder.UseSetting("Hosting:CorsAllowedOrigins:0", AllowedOrigin);            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IIntegrationEventHandler<CompanyCreated>, CompanyCreatedRecorder>();
                services.AddScoped<IIntegrationEventHandler<CompanyCreated>, CompanyCreatedInboxCounter>();
                services.AddSingleton<INotificationSender, RecordingNotificationSender>();
                services.AddScoped<IIntegrationEventHandler<Erp.Modules.Workflow.Contracts.ApprovalCompleted>, ApprovalCompletedRecorder>();
                services.AddScoped<IMrpDemandSource, TestMrpDemandSource>();
            });
        });

        await _factory.Services.GetRequiredService<ErpDatabaseMigrator>().MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (Directory.Exists(AttachmentRoot))
        {
            Directory.Delete(AttachmentRoot, recursive: true);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else if (!string.IsNullOrEmpty(ConnectionString))
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString);
            var database = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];";
            await command.ExecuteNonQueryAsync();
        }
    }

    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    public sealed record TenantSession(HttpClient Client, Guid TenantId, Guid CompanyId, Guid AdminUserId, string TenantCode, string Email, string Password);

    /// <summary>Provisions a brand-new tenant and returns a client signed in as its administrator.</summary>
    public async Task<TenantSession> NewTenantAsync(string companyCode = "MAIN", string preset = "trading")
    {
        var code = "T" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var email = $"admin@{code.ToLowerInvariant()}.example";
        const string password = "Str0ng-Passw0rd!";
        using var platform = CreateClient();
        platform.DefaultRequestHeaders.Add("X-Platform-Key", PlatformKey);
        var provisioned = await platform.PostAsJsonAsync("/api/platform/tenants", new
        {
            tenantCode = code, tenantName = "Tenant " + code, companyCode, companyNameEn = "Company", companyNameAr = "شركة",
            baseCurrency = "EGP", businessPreset = preset, adminEmail = email, adminDisplayName = "Admin", adminPassword = password,
        });
        provisioned.EnsureSuccessStatusCode();
        var body = await provisioned.Content.ReadFromJsonAsync<JsonElement>();

        var client = CreateClient();
        await SignInAsync(client, code, email, password);
        return new TenantSession(client, body.GetProperty("tenantId").GetGuid(), body.GetProperty("companyId").GetGuid(),
            body.GetProperty("administratorUserId").GetGuid(), code, email, password);
    }

    public static async Task SignInAsync(HttpClient client, string tenantCode, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantCode, email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Opens a raw connection with the tenant in SESSION_CONTEXT, exactly as the application does.</summary>
    public async Task<SqlConnection> OpenAsTenantAsync(Guid? tenantId)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        if (tenantId is not null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sys.sp_set_session_context @key = N'TenantId', @value = @t, @read_only = 1;";
            command.Parameters.AddWithValue("@t", tenantId.Value);
            await command.ExecuteNonQueryAsync();
        }

        return connection;
    }
}

public static class HttpExtensions
{
    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static HttpRequestMessage WithCompany(this HttpRequestMessage request, Guid companyId)
    {
        request.Headers.Add("X-Company-Id", companyId.ToString());
        return request;
    }

    public static Task<HttpResponseMessage> GetAsCompanyAsync(this HttpClient client, string url, Guid companyId) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithCompany(companyId));

    public static Task<HttpResponseMessage> SendAsCompanyAsync(this HttpClient client, HttpMethod method, string url, Guid companyId, object? body = null, string? ifMatch = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url).WithCompany(companyId);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }
}
