using System.Globalization;
using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Infrastructure;
using Erp.Web.Http;
using Erp.Web.Modules;
using Erp.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Web;

public static class DependencyInjection
{
    /// <summary>Registers the platform (application, infrastructure, web) plus every module.</summary>
    public static IServiceCollection AddErpPlatform(this IServiceCollection services, IConfiguration configuration, IReadOnlyCollection<IErpModule> modules)
    {
        services.AddErpApplication();
        services.AddErpInfrastructure(configuration);
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddProblemDetails();
        services.AddExceptionHandler<ErpExceptionHandler>();
        services.AddLocalization();

        foreach (var module in modules)
        {
            services.AddSingleton(module);
            module.AddServices(services, configuration);
        }

        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<IPermissionCatalog>(sp => sp.GetRequiredService<ModuleRegistry>());
        return services;
    }

    public static IApplicationBuilder UseErpRequestPipeline(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseIdempotentRequestBuffering();
        app.UseExceptionHandler();
        var cultures = new[] { new CultureInfo("en"), new CultureInfo("ar") };
        app.UseRequestLocalization(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture("en"),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
        });
        app.UseAuthentication();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthorization();
        return app;
    }
}
