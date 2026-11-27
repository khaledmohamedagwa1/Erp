using Erp.Application.Abstractions;
using Erp.Infrastructure.Tenancy;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Erp.Web;

public sealed record NavigationItemDto(string Key, string LabelEn, string LabelAr, string Route, int Order);

public sealed record NavigationGroupDto(string Key, IReadOnlyList<NavigationItemDto> Items);

/// <summary>Checks the database answers. Used by the readiness probe.</summary>
public sealed class DatabaseHealthCheck(IConnectionStringProvider connectionStrings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionStrings.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapErpPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

        // The sidebar is built here, never hard-coded in the client: enabled modules ∩ the user's permissions (Part 19).
        endpoints.MapGet("/api/v1/me/navigation", async (
                ModuleRegistry registry,
                ICurrentUser user,
                ITenantContext tenant,
                IPermissionService permissions,
                IModuleActivationService modules,
                CancellationToken ct) =>
            {
                var granted = await permissions.GetPermissionsAsync(user.UserId!.Value, ct);
                var enabled = tenant.CompanyId is { } companyId ? await modules.GetEnabledModulesAsync(companyId, ct) : null;
                var groups = registry.Modules
                    .Where(m => m.Manifest.IsCore || (enabled?.Contains(m.Manifest.Code) ?? false))
                    .SelectMany(m => m.Manifest.Menu)
                    .Where(i => i.RequiredPermission is null || granted.Contains(i.RequiredPermission))
                    .GroupBy(i => i.Group)
                    .Select(g => new NavigationGroupDto(g.Key, g.OrderBy(i => i.Order)
                        .Select(i => new NavigationItemDto(i.Key, i.LabelEn, i.LabelAr, i.Route, i.Order)).ToList()))
                    .ToList();
                return Results.Ok(groups);
            })
            .WithTags("Me")
            .RequireAuthorization();
        return endpoints;
    }
}
