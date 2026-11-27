using System.Threading.RateLimiting;
using Erp.Infrastructure.Database;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Tenancy;
using Erp.Modules.Configuration.Infrastructure;
using Erp.Modules.Identity.Infrastructure;
using Erp.Modules.Organization.Infrastructure;
using Erp.Modules.Parties.Infrastructure;
using Erp.Web;
using Erp.Web.Modules;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

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
IErpModule[] modules = [new OrganizationModule(), new IdentityModule(), new ConfigurationModule(), new PartiesModule()];
builder.Services.AddErpPlatform(builder.Configuration, modules);

// ---- Process role: api | worker | all (same image, different responsibilities) ----
var role = builder.Configuration["Erp:Role"] ?? "all";
if (role is "worker" or "all")
{
    builder.Services.AddHostedService<OutboxBackgroundService>();
}

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
});

var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlp))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("erp-api"))
        .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
        .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddOtlpExporter());
}

var app = builder.Build();

app.UseErpRequestPipeline();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
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

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
