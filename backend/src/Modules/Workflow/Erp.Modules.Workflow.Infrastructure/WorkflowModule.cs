using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Workflow.Application;
using Erp.Modules.Workflow.Contracts;
using Erp.Modules.Workflow.Domain;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Modules.Workflow.Infrastructure;

public sealed record DefinitionRequest(string? DocumentType, string Name, int Priority, IReadOnlyList<ConditionInput>? Conditions, IReadOnlyList<StepInput> Steps);

public sealed record DecisionRequest(string? Comment);

public sealed record RejectRequest(string? Reason);

public sealed record DelegateRequest(Guid ToUserId, string? Comment);

public sealed class WorkflowOptions
{
    public const string SectionName = "Workflow";

    /// <summary>How often the worker looks for overdue approval steps.</summary>
    public int EscalationIntervalMinutes { get; set; } = 15;
}

/// <summary>Escalates overdue approval steps for every active tenant (worker/all role).</summary>
public sealed class EscalationBackgroundService(
    IServiceScopeFactory scopes,
    ITenantDirectory tenants,
    IOptions<WorkflowOptions> options,
    ILogger<EscalationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.EscalationIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenant in await tenants.ListActiveAsync(stoppingToken))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id);
                    var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new EscalateOverdueCommand(), stoppingToken);
                    if (result.IsSuccess && result.Value > 0)
                    {
                        logger.LogInformation("Escalated {Count} approval steps for tenant {TenantCode}", result.Value, tenant.Code);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Approval escalation run failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

public sealed class WorkflowModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "workflow",
        "Approvals",
        "الموافقات",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(WorkflowPermissions.DefinitionRead, "workflow", "View approval rules and approval history", "عرض قواعد الموافقة وسجلها"),
            new(WorkflowPermissions.DefinitionManage, "workflow", "Change approval rules, delegate for others and run escalations", "تعديل قواعد الموافقة والتفويض عن الآخرين وتشغيل التصعيد", IsSensitive: true),
            new(WorkflowPermissions.Approve, "workflow", "Decide approvals assigned to me and delegate my approvals", "البت في الموافقات المسندة إليّ وتفويض موافقاتي"),
        ],
        Menu:
        [
            new("workflow.inbox", "Approvals", "الموافقات", "/approvals", "overview", 2, WorkflowPermissions.Approve),
            new("workflow.definitions", "Approval rules", "قواعد الموافقة", "/settings/approval-rules", "administration", 40, WorkflowPermissions.DefinitionRead),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<WorkflowDbContext>(WorkflowDbContext.SchemaName, order: 140);
        services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<WorkflowDbContext>());

        // Registered with AddScoped (not TryAdd): wins over modules' TryAddScoped<IApprovalService, NoApprovalRequired>.
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<EligibilityResolver>();
        services.AddScoped<IIntegrationEventHandler<ApprovalStepAssigned>, ApprovalStepAssignedNotifier>();
        services.AddHandlersFromAssembly(typeof(IWorkflowDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(ApprovalInstance).Assembly));
        services.AddSingleton(new EventAssembly(typeof(ApprovalCompleted).Assembly));
        services.AddOptions<WorkflowOptions>().Bind(configuration.GetSection(WorkflowOptions.SectionName));

        var role = configuration["Erp:Role"] ?? "all";
        if (role is "worker" or "all")
        {
            services.AddHostedService<EscalationBackgroundService>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/workflow").WithTags("Workflow").RequireAuthorization().RequireCompany();

        // ---- Definitions ----
        group.MapGet("/definitions", async (string? documentType, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDefinitionsQuery(documentType), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.DefinitionRead);
        group.MapGet("/definitions/{id:guid}", async (Guid id, HttpContext http, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetDefinitionQuery(id), ct)).ToHttpResult(x => WithETag(http, x)))
            .RequirePermission(WorkflowPermissions.DefinitionRead);
        group.MapPost("/definitions", async (DefinitionRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateDefinitionCommand(b.DocumentType ?? string.Empty, b.Name, b.Priority, b.Conditions, b.Steps ?? []), ct))
                .ToHttpResult(x => Results.Created($"/api/v1/workflow/definitions/{x.Id}", x)))
            .RequirePermission(WorkflowPermissions.DefinitionManage);
        group.MapPut("/definitions/{id:guid}", async (Guid id, DefinitionRequest b, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var ifMatch = ConcurrencyHeaders.ReadIfMatch(http.Request);
                if (ifMatch.IsFailure)
                {
                    return ifMatch.Error.ToProblem();
                }

                return (await d.Send(new UpdateDefinitionCommand(id, b.Name, b.Priority, b.Conditions, b.Steps ?? [], ifMatch.Value), ct))
                    .ToHttpResult(x => WithETag(http, x));
            })
            .RequirePermission(WorkflowPermissions.DefinitionManage);
        group.MapPost("/definitions/{id:guid}/activate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetDefinitionActiveCommand(id, true), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.DefinitionManage);
        group.MapPost("/definitions/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetDefinitionActiveCommand(id, false), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.DefinitionManage);
        group.MapDelete("/definitions/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteDefinitionCommand(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(WorkflowPermissions.DefinitionManage);

        // ---- Inbox & instances ----
        group.MapGet("/inbox", async (IDispatcher d, CancellationToken ct) => (await d.Send(new InboxQuery(), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.Approve);
        group.MapGet("/instances", async (int? page, int? pageSize, string? status, string? documentType, Guid? documentId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListInstancesQuery(new PageRequest(page ?? 1, pageSize ?? 50), status, documentType, documentId), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.DefinitionRead);
        group.MapGet("/instances/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetInstanceQuery(id), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.Approve);
        group.MapPost("/instances/{id:guid}/approve", async (Guid id, DecisionRequest? b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideCommand(id, DecisionKind.Approve, b?.Comment), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.Approve);
        group.MapPost("/instances/{id:guid}/reject", async (Guid id, RejectRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideCommand(id, DecisionKind.Reject, b.Reason), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.Approve);
        group.MapPost("/instances/{id:guid}/delegate", async (Guid id, DelegateRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideCommand(id, DecisionKind.Delegate, b.Comment, b.ToUserId), ct)).ToHttpResult())
            .RequirePermission(WorkflowPermissions.Approve);

        // ---- Delegations ----
        group.MapGet("/delegations", async (bool? all, ICurrentUser user, IPermissionService permissions, IDispatcher d, CancellationToken ct) =>
            {
                var manage = all == true && await permissions.HasPermissionAsync(user.UserId!.Value, WorkflowPermissions.DefinitionManage, ct);
                return (await d.Send(new ListDelegationsQuery(manage), ct)).ToHttpResult();
            })
            .RequirePermission(WorkflowPermissions.Approve);
        group.MapPost("/delegations", async (CreateDelegationCommand b, ICurrentUser user, IPermissionService permissions, IDispatcher d, CancellationToken ct) =>
            {
                if (b.FromUserId is { } from && from != user.UserId &&
                    !await permissions.HasPermissionAsync(user.UserId!.Value, WorkflowPermissions.DefinitionManage, ct))
                {
                    return Error.Forbidden("forbidden", "Delegating on behalf of another user requires workflow.definition.manage.").ToProblem();
                }

                return (await d.Send(b, ct)).ToHttpResult(x => Results.Created($"/api/v1/workflow/delegations/{x.Id}", x));
            })
            .RequirePermission(WorkflowPermissions.Approve);
        group.MapDelete("/delegations/{id:guid}", async (Guid id, ICurrentUser user, IPermissionService permissions, IDispatcher d, CancellationToken ct) =>
            {
                var manage = await permissions.HasPermissionAsync(user.UserId!.Value, WorkflowPermissions.DefinitionManage, ct);
                return (await d.Send(new RevokeDelegationCommand(id, manage), ct)).ToHttpResult(_ => Results.NoContent());
            })
            .RequirePermission(WorkflowPermissions.Approve);

        // ---- Escalation (admin; the background job does this on the worker) ----
        group.MapPost("/escalations/run", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new EscalateOverdueCommand(), ct)).ToHttpResult(n => Results.Ok(new { escalated = n })))
            .RequirePermission(WorkflowPermissions.DefinitionManage);
    }

    private static IResult WithETag(HttpContext http, DefinitionDto definition)
    {
        http.Response.Headers.ETag = ConcurrencyHeaders.ToETag(definition.RowVersion);
        return Results.Ok(definition);
    }
}
