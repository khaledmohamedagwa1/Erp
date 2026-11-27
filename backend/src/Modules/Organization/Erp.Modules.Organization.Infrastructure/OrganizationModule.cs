using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Organization.Application;
using Erp.Modules.Organization.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Organization.Infrastructure;

public sealed class OrganizationModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "organization",
        "Organization",
        "الهيكل التنظيمي",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(OrganizationPermissions.CompanyRead, "organization", "View companies", "عرض الشركات"),
            new(OrganizationPermissions.CompanyManage, "organization", "Create and change companies", "إنشاء الشركات وتعديلها", IsSensitive: true),
            new(OrganizationPermissions.StructureRead, "organization", "View legal entities, branches and warehouses", "عرض الكيانات القانونية والفروع والمستودعات"),
            new(OrganizationPermissions.StructureManage, "organization", "Change legal entities, branches and warehouses", "تعديل الكيانات القانونية والفروع والمستودعات"),
        ],
        Menu:
        [
            new("organization.companies", "Companies", "الشركات", "/settings/companies", "administration", 10, OrganizationPermissions.CompanyRead),
            new("organization.branches", "Branches", "الفروع", "/settings/branches", "administration", 11, OrganizationPermissions.StructureRead),
            new("organization.warehouses", "Warehouses", "المستودعات", "/settings/warehouses", "administration", 12, OrganizationPermissions.StructureRead),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<OrganizationDbContext>(OrganizationDbContext.SchemaName, order: 10);
        services.AddScoped<IOrganizationDbContext>(sp => sp.GetRequiredService<OrganizationDbContext>());
        services.AddScoped<IOrganizationQueries, OrganizationQueries>();
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        services.AddHandlersFromAssembly(typeof(IOrganizationDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.Company).Assembly));
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.SectionName));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Platform operators (SaaS) provision tenants with a platform key; on-prem uses Erp.Migrator init-tenant.
        endpoints.MapPost("/api/platform/tenants", async (ProvisionTenantCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(r => Results.Created($"/api/platform/tenants/{r.TenantId}", r)))
            .WithTags("Platform")
            .AllowAnonymous()
            .RequirePlatformKey();

        var group = endpoints.MapGroup("/api/v1/organization").WithTags("Organization").RequireAuthorization();

        group.MapGet("/companies", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListMyCompaniesQuery(), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.CompanyRead);
        group.MapGet("/companies/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetCompanyQuery(id), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.CompanyRead);
        group.MapPost("/companies", async (CreateCompanyCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(c => Results.Created($"/api/v1/organization/companies/{c.Id}", c)))
            .RequirePermission(OrganizationPermissions.CompanyManage);

        var scoped = group.MapGroup(string.Empty).RequireCompany();
        scoped.MapGet("/legal-entities", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListLegalEntitiesQuery(), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.StructureRead);
        scoped.MapPost("/legal-entities", async (CreateLegalEntityCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(l => Results.Created($"/api/v1/organization/legal-entities/{l.Id}", l)))
            .RequirePermission(OrganizationPermissions.StructureManage);
        scoped.MapGet("/branches", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListBranchesQuery(), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.StructureRead);
        scoped.MapPost("/branches", async (CreateBranchCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(b => Results.Created($"/api/v1/organization/branches/{b.Id}", b)))
            .RequirePermission(OrganizationPermissions.StructureManage);
        scoped.MapGet("/warehouses", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListWarehousesQuery(new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(OrganizationPermissions.StructureRead);
        scoped.MapPost("/warehouses", async (CreateWarehouseCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(w => Results.Created($"/api/v1/organization/warehouses/{w.Id}", w)))
            .RequirePermission(OrganizationPermissions.StructureManage);
    }
}
