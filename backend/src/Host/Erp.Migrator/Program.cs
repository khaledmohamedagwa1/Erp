using Erp.Application.Messaging;
using Erp.Infrastructure.Database;
using Erp.Modules.Catalog.Infrastructure;
using Erp.Modules.Accounting.Infrastructure;
using Erp.Modules.Finance.Infrastructure;
using Erp.Modules.Inventory.Infrastructure;
using Erp.Modules.Configuration.Infrastructure;
using Erp.Modules.Tax.Infrastructure;
using Erp.Modules.Identity.Infrastructure;
using Erp.Modules.Organization.Application;
using Erp.Modules.Organization.Infrastructure;
using Erp.Modules.Parties.Infrastructure;
using Erp.Web;
using Erp.Web.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Erp.Migrator — explicit deployment step (ADR-015).
//   migrate [--allow-destructive]
//   init-tenant --code ACME --name "Acme" --company-code ACME --company-name-en "Acme" --company-name-ar "أكمي"
//               --currency EGP --preset trading --admin-email a@b.c --admin-name "Admin" --admin-password ...
var command = args.FirstOrDefault() ?? "migrate";
var options = ParseOptions(args.Skip(1).ToArray());

var builder = Host.CreateApplicationBuilder(args);
IErpModule[] modules = [new OrganizationModule(), new IdentityModule(), new ConfigurationModule(), new PartiesModule(), new TaxModule(), new CatalogModule(), new AccountingModule(), new FinanceModule(), new InventoryModule()];
builder.Services.AddErpPlatform(builder.Configuration, modules);
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Erp.Migrator");

try
{
    switch (command)
    {
        case "migrate":
            await host.Services.GetRequiredService<ErpDatabaseMigrator>().MigrateAsync(options.ContainsKey("allow-destructive"));
            logger.LogInformation("Migration completed");
            return 0;

        case "init-tenant":
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ProvisionTenantCommand(
                    Required(options, "code"), Required(options, "name"), Required(options, "company-code"),
                    Required(options, "company-name-en"), Required(options, "company-name-ar"), Required(options, "currency"),
                    options.GetValueOrDefault("preset", "trading"), Required(options, "admin-email"),
                    options.GetValueOrDefault("admin-name", "Administrator"), Required(options, "admin-password")));
                if (result.IsFailure)
                {
                    logger.LogError("Tenant was not created: {Code} {Message}", result.Error.Code, result.Error.Message);
                    return 2;
                }

                logger.LogInformation("Tenant {Code} created ({TenantId}); company {CompanyId}", result.Value.TenantCode, result.Value.TenantId, result.Value.CompanyId);
                return 0;
            }

        default:
            logger.LogError("Unknown command '{Command}'. Use 'migrate' or 'init-tenant'.", command);
            return 1;
    }
}
catch (DestructiveMigrationException ex)
{
    logger.LogError("{Message}", ex.Message);
    return 3;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? args[++i] : "true";
    }

    return result;
}

static string Required(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"--{key} is required.");
