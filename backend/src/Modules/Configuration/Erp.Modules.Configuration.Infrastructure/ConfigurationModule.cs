using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Configuration.Application;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Configuration.Domain;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Erp.Web.Security;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Configuration.Infrastructure;

public sealed record ModuleChangeRequest(bool Enabled);

public sealed class ModuleActivationService(ConfigurationDbContext db, IMemoryCache cache, ITenantContext tenant)
    : IModuleActivationService, IModuleActivationCache
{
    private string Key(Guid companyId) => $"t:{tenant.TenantId}:modules:{companyId}";

    public async Task<bool> IsEnabledAsync(Guid companyId, string moduleCode, CancellationToken cancellationToken = default) =>
        ModuleCatalog.IsOn(moduleCode, await GetEnabledModulesAsync(companyId, cancellationToken));

    public async Task<IReadOnlySet<string>> GetEnabledModulesAsync(Guid companyId, CancellationToken cancellationToken = default) =>
        (await cache.GetOrCreateAsync(Key(companyId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            // IgnoreQueryFilters only lifts the company filter so any company can be checked; the tenant is still enforced here and by RLS.
            var tenantId = tenant.TenantId!.Value;
            var enabled = await db.CompanyModules.IgnoreQueryFilters().AsNoTracking()
                .Where(m => EF.Property<Guid>(m, "TenantId") == tenantId && m.CompanyId == companyId && m.IsEnabled)
                .Select(m => m.ModuleCode).ToListAsync(cancellationToken);
            return (IReadOnlySet<string>)ModuleCatalog.All.Where(m => m.IsCore).Select(m => m.Code).Concat(enabled).ToHashSet(StringComparer.Ordinal);
        }))!;

    public void Invalidate(Guid companyId) => cache.Remove(Key(companyId));
}

public sealed class ConfigurationModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "configuration",
        "Configuration",
        "الإعدادات",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(ConfigurationPermissions.Read, "configuration", "View modules and business policies", "عرض الوحدات وسياسات العمل"),
            new(ConfigurationPermissions.ModuleManage, "configuration", "Switch modules on or off", "تفعيل الوحدات وإيقافها", IsSensitive: true),
            new(ConfigurationPermissions.PolicyManage, "configuration", "Change business policies", "تعديل سياسات العمل", IsSensitive: true),
        ],
        Menu:
        [
            new("configuration.modules", "Modules & policies", "الوحدات والسياسات", "/settings/modules", "administration", 30, ConfigurationPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<ConfigurationDbContext>(ConfigurationDbContext.SchemaName, order: 30);
        services.AddScoped<IConfigurationDbContext>(sp => sp.GetRequiredService<ConfigurationDbContext>());
        services.AddScoped<ModuleActivationService>();
        services.AddScoped<IModuleActivationService>(sp => sp.GetRequiredService<ModuleActivationService>());
        services.AddScoped<IModuleActivationCache>(sp => sp.GetRequiredService<ModuleActivationService>());
        services.AddScoped<IConfigurationProvisioning, ConfigurationProvisioning>();
        services.AddScoped<IPolicyReader, PolicyReader>();
        services.AddHandlersFromAssembly(typeof(IConfigurationDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(CompanyModule).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/configuration").WithTags("Configuration").RequireAuthorization().RequireCompany();

        group.MapGet("/modules", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListModulesQuery(), ct)).ToHttpResult())
            .RequirePermission(ConfigurationPermissions.Read);
        group.MapPut("/modules/{code}", async (string code, ModuleChangeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetModuleEnabledCommand(code, body.Enabled), ct)).ToHttpResult())
            .RequirePermission(ConfigurationPermissions.ModuleManage)
            .RequireStepUp();

        group.MapGet("/policies/{section}", async (string section, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new GetPolicyQuery(section), ct);
                return result.ToHttpResult(p =>
                {
                    http.Response.Headers.ETag = ConcurrencyHeaders.ToETag(p.RowVersion);
                    return Results.Ok(new { p.Section, p.Version, p.Values });
                });
            })
            .RequirePermission(ConfigurationPermissions.Read);

        group.MapPut("/policies/{section}", async (string section, System.Text.Json.JsonElement body, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var ifMatch = ConcurrencyHeaders.ReadIfMatch(http.Request);
                if (ifMatch.IsFailure)
                {
                    return ifMatch.Error.ToProblem();
                }

                var result = await d.Send(new UpdatePolicyCommand(section, body, ifMatch.Value), ct);
                return result.ToHttpResult(p =>
                {
                    http.Response.Headers.ETag = ConcurrencyHeaders.ToETag(p.RowVersion);
                    return Results.Ok(new { p.Section, p.Version, p.Values });
                });
            })
            .RequirePermission(ConfigurationPermissions.PolicyManage)
            .RequireStepUp();
    }
}
