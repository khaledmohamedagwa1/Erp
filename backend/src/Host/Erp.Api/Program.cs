using Erp.Infrastructure.Database;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Tenancy;
using Erp.Modules.Catalog.Infrastructure;
using Erp.Modules.Accounting.Infrastructure;
using Erp.Modules.Finance.Infrastructure;
using Erp.Modules.Pos.Infrastructure;
using Erp.Modules.Sales.Infrastructure;
using Erp.Modules.Inventory.Infrastructure;
using Erp.Modules.FixedAssets.Infrastructure;
using Erp.Modules.Manufacturing.Infrastructure;
using Erp.Modules.Procurement.Infrastructure;
using Erp.Modules.Configuration.Infrastructure;
using Erp.Modules.Tax.Infrastructure;
using Erp.Modules.Identity.Infrastructure;
using Erp.Modules.Organization.Infrastructure;
using Erp.Modules.Parties.Infrastructure;
using Erp.Modules.Import.Infrastructure;
using Erp.Modules.People.Infrastructure;
using Erp.Modules.Reporting.Infrastructure;
using Erp.Modules.Workflow.Infrastructure;
using Erp.Modules.Documents.Infrastructure;
using Erp.Application.Observability;
using Erp.Web;
using Erp.Web.Hosting;
using Erp.Web.Modules;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

// Container health probe: `dotnet Erp.Api.dll --healthcheck` checks the running instance and exits 0/1.
if (args.Contains(ErpHealthProbe.Argument))
{
    return await ErpHealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// Secret files (Docker/Kubernetes secrets): /run/secrets/Auth__SigningKeyPem becomes Auth:SigningKeyPem.
builder.Configuration.AddKeyPerFile(Environment.GetEnvironmentVariable("ERP_SECRETS_DIR") ?? "/run/secrets", optional: true);

// No-op unless the process is started by the Windows Service Control Manager (on-prem Windows installs).
builder.Host.UseWindowsService();

// ---- Logging: structured JSON outside Development ----
builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext();
    if (builder.Environment.IsDevelopment())
    {
        logger.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
    }
    else
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

// ---- Modules: the platform is composed from these (order = migration order is set per module) ----
IErpModule[] modules = [new OrganizationModule(), new IdentityModule(), new ConfigurationModule(), new PartiesModule(), new TaxModule(), new CatalogModule(), new AccountingModule(), new FinanceModule(), new InventoryModule(), new WorkflowModule(), new ManufacturingModule(), new ProcurementModule(), new SalesModule(), new FixedAssetsModule(), new PosModule(), new ReportingModule(), new DocumentsModule(), new ImportModule(), new PeopleModule()];
builder.Services.AddErpPlatform(builder.Configuration, modules);

// ---- Process role: api | worker | all (same image, different responsibilities) ----
var role = builder.Configuration["Erp:Role"] ?? "all";
if (role is "worker" or "all")
{
    builder.Services.AddHostedService<OutboxBackgroundService>();
}

builder.Services.AddErpHosting(builder.Configuration, role);

builder.Services.AddErpOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlp))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("erp-api"))
        .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
        .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter(ErpMetrics.MeterName).AddOtlpExporter());
}

var app = builder.Build();
ErpProductionChecks.ThrowIfInvalid(app.Configuration, app.Environment);

app.UseErpHosting();
app.UseErpRequestLogging();
app.UseErpRequestPipeline();
app.UseRateLimiter();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ERP API v1"));
}

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

app.MapErpPlatformEndpoints();

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.AutoMigrate)
{
    // Development convenience only. Production deploys run Erp.Migrator as an explicit, approval-gated step (ADR-015).
    await app.Services.GetRequiredService<ErpDatabaseMigrator>().MigrateAsync();
}

await app.RunAsync();
return 0;

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
