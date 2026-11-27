using Erp.Application;
using Erp.Application.Messaging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.People.Application;
using Erp.Modules.People.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.People.Infrastructure;

public sealed record UpdateEmployeeRequest(EmployeeInput Details, bool IsActive);

public sealed record UpdateCommissionPlanRequest(CommissionPlanInput Details, bool IsActive);

public sealed record CommissionRunRequest(Guid LegalEntityId, DateOnly Period);

public sealed record CommissionRunDateRequest(DateOnly? Date);

/// <summary>Employees and salespersons, commission plans and commission runs (schema <c>people</c>, migration order 190).</summary>
public sealed class PeopleModule : IErpModule
{
    public const string Code = "people";

    /// <summary>Optional module (configuration catalog) that switches commission plans and runs on; employees are always available.</summary>
    public const string CommissionsModule = "commissions";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "People & commissions",
        "الموظفون والعمولات",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(PeoplePermissions.EmployeeRead, Code, "View employees and salespersons", "عرض الموظفين ومندوبي المبيعات"),
            new(PeoplePermissions.EmployeeManage, Code, "Maintain employees and salespersons", "إدارة الموظفين ومندوبي المبيعات"),
            new(PeoplePermissions.CommissionRead, Code, "View commission plans and runs", "عرض خطط العمولات ودوراتها"),
            new(PeoplePermissions.CommissionManage, Code, "Maintain commission plans and calculate commission runs", "إدارة خطط العمولات واحتساب دوراتها"),
            new(PeoplePermissions.CommissionApprove, Code, "Approve commission runs", "اعتماد دورات العمولات", IsSensitive: true),
            new(PeoplePermissions.CommissionPost, Code, "Post and reverse commission runs (posts journals)", "ترحيل دورات العمولات وعكسها (يرحّل قيوداً)", IsSensitive: true),
        ],
        Menu:
        [
            new("people.employees", "Employees", "الموظفون", "/people/employees", "people", 10, PeoplePermissions.EmployeeRead),
            new("people.commission-plans", "Commission plans", "خطط العمولات", "/people/commission-plans", "people", 20, PeoplePermissions.CommissionRead),
            new("people.commission-runs", "Commission runs", "دورات العمولات", "/people/commission-runs", "people", 30, PeoplePermissions.CommissionRead),
            new("people.commission-statement", "Commission statement", "كشف العمولات", "/reports/people/commission-statement", "people", 40, PeoplePermissions.CommissionRead),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<PeopleDbContext>(PeopleDbContext.SchemaName, order: 190);
        services.AddScoped<IPeopleDbContext>(sp => sp.GetRequiredService<PeopleDbContext>());
        services.AddScoped<EmployeeReferences>();
        services.AddScoped<CommissionSourceLoader>();
        services.AddScoped<CommissionEngine>();
        services.AddScoped<IEmployeeQueries, EmployeeQueries>();
        services.AddScoped<ICommissionReportQueries, CommissionReportQueries>();
        services.AddHandlersFromAssembly(typeof(IPeopleDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.Employee).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/people").WithTags("People").RequireAuthorization().RequireCompany();

        // Employees
        g.MapGet("/employees", async (bool? salespersons, bool? active, Guid? branchId, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListEmployeesQuery(salespersons, active, branchId, search), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.EmployeeRead);
        g.MapGet("/employees/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetEmployeeQuery(id), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.EmployeeRead);
        g.MapPost("/employees", async (CreateEmployeeCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(e => Results.Created($"/api/v1/people/employees/{e.Id}", e)))
            .RequirePermission(PeoplePermissions.EmployeeManage);
        g.MapPut("/employees/{id:guid}", async (Guid id, UpdateEmployeeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateEmployeeCommand(id, body.Details, body.IsActive), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.EmployeeManage);

        // Commission plans and runs (optional "commissions" module)
        var c = g.MapGroup(string.Empty).RequireModule(CommissionsModule);
        c.MapGet("/commission-plans", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCommissionPlansQuery(), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionRead);
        c.MapPost("/commission-plans", async (CreateCommissionPlanCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(p => Results.Created($"/api/v1/people/commission-plans/{p.Id}", p)))
            .RequirePermission(PeoplePermissions.CommissionManage);
        c.MapPut("/commission-plans/{id:guid}", async (Guid id, UpdateCommissionPlanRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateCommissionPlanCommand(id, body.Details, body.IsActive), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionManage);

        // Commission runs
        c.MapGet("/commission-runs", async (Guid? legalEntityId, string? status, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListCommissionRunsQuery(legalEntityId, status), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionRead);
        c.MapGet("/commission-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetCommissionRunQuery(id), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionRead);
        c.MapPost("/commission-runs", async (CommissionRunRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CalculateCommissionRunCommand(body.LegalEntityId, body.Period), ct))
                .ToHttpResult(r => Results.Created($"/api/v1/people/commission-runs/{r.Id}", r)))
            .RequirePermission(PeoplePermissions.CommissionManage);
        c.MapPost("/commission-runs/{id:guid}/recalculate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecalculateCommissionRunCommand(id), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionManage);
        c.MapPost("/commission-runs/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveCommissionRunCommand(id), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionApprove);
        c.MapPost("/commission-runs/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelCommissionRunCommand(id), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionManage);
        c.MapPost("/commission-runs/{id:guid}/post", async (Guid id, CommissionRunDateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostCommissionRunCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionPost).RequireIdempotency();
        c.MapPost("/commission-runs/{id:guid}/reverse", async (Guid id, CommissionRunDateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseCommissionRunCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(PeoplePermissions.CommissionPost).RequireIdempotency();
    }
}
