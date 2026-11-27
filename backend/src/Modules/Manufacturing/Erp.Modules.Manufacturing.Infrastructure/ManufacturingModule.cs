using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Manufacturing.Application;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Manufacturing.Infrastructure;

public sealed record CreateBomRequest(Guid ProductId, string Code, string Name);

public sealed record BomVersionRequest(DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal OutputQuantity, decimal? ExpectedYieldPercent, IReadOnlyList<BomLineInput> Lines);

public sealed record PlanRequest(Guid? BomVersionId, Guid? RoutingId);

public sealed record MaterialRequest(DateOnly? Date, IReadOnlyList<MaterialLineInput> Lines);

public sealed record OperationRequest(int Sequence, decimal LaborHours, decimal MachineHours, DateOnly? Date);

public sealed record ReceiptRequest(decimal Quantity, DateOnly? Date, string? BatchNumber, DateOnly? ExpiryDate, IReadOnlyList<string>? Serials);

public sealed record ScrapRequest(decimal Quantity, DateOnly? Date, string? Reason);

public sealed record CloseRequest(DateOnly? Date, bool AbnormalScrap);

public sealed record ConvertRequest(IReadOnlyList<Guid> SuggestionIds, Guid ComponentWarehouseId, Guid? OutputWarehouseId);

public sealed record RunRequest(DateOnly? RunDate, int? HorizonDays);

public sealed class ManufacturingModule : IErpModule
{
    public const string Code = "manufacturing";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Manufacturing",
        "التصنيع",
        IsCore: false,
        DependsOn: ["inventory", "procurement"],
        Permissions:
        [
            new(ManufacturingPermissions.Read, Code, "View work centers, routings, BOMs, production orders and MRP results", "عرض مراكز العمل ومسارات التصنيع وقوائم المواد وأوامر الإنتاج ونتائج تخطيط المواد"),
            new(ManufacturingPermissions.SetupManage, Code, "Maintain work centers, routings, BOMs and planning parameters", "إدارة مراكز العمل ومسارات التصنيع وقوائم المواد ومعايير التخطيط"),
            new(ManufacturingPermissions.OrderManage, Code, "Create, plan, release and cancel production orders", "إنشاء أوامر الإنتاج وتخطيطها وإطلاقها وإلغاؤها"),
            new(ManufacturingPermissions.OrderPost, Code, "Issue materials, report operations, receive finished goods and close orders (posts stock and journals)", "صرف المواد وتسجيل العمليات واستلام الإنتاج التام وإقفال الأوامر", IsSensitive: true),
            new(ManufacturingPermissions.PlanningRun, Code, "Run MRP and convert suggestions", "تشغيل تخطيط الاحتياجات وتحويل الاقتراحات"),
        ],
        Menu:
        [
            new("manufacturing.orders", "Production orders", "أوامر الإنتاج", "/manufacturing/production-orders", "manufacturing", 10, ManufacturingPermissions.Read),
            new("manufacturing.boms", "Bills of materials", "قوائم المواد", "/manufacturing/boms", "manufacturing", 20, ManufacturingPermissions.Read),
            new("manufacturing.routings", "Routings", "مسارات التصنيع", "/manufacturing/routings", "manufacturing", 30, ManufacturingPermissions.Read),
            new("manufacturing.work-centers", "Work centers", "مراكز العمل", "/manufacturing/work-centers", "manufacturing", 40, ManufacturingPermissions.Read),
            new("manufacturing.mrp", "MRP", "تخطيط الاحتياجات", "/manufacturing/mrp", "manufacturing", 50, ManufacturingPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<ManufacturingDbContext>(ManufacturingDbContext.SchemaName, order: 130);
        services.AddScoped<IManufacturingDbContext>(sp => sp.GetRequiredService<ManufacturingDbContext>());
        services.AddScoped<ManufacturingSupport>();
        services.AddScoped<ProductionPoster>();
        services.AddScoped<IManufacturingQueries, ManufacturingQueries>();

        // MRP inputs: Manufacturing's own production orders, open purchase orders from Procurement, and confirmed
        // sales-order demand from Sales. IEnumerable resolves every registration.
        services.AddScoped<ProductionOrderMrpSource>();
        services.AddScoped<IMrpDemandSource>(sp => sp.GetRequiredService<ProductionOrderMrpSource>());
        services.AddScoped<IMrpSupplySource>(sp => sp.GetRequiredService<ProductionOrderMrpSource>());
        services.AddScoped<IMrpDemandSource, SalesOrderMrpSource>();
        services.AddScoped<IMrpSupplySource, PurchaseOrderMrpSource>();

        services.AddHandlersFromAssembly(typeof(IManufacturingDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.ProductionOrder).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/manufacturing").WithTags("Manufacturing").RequireAuthorization().RequireCompany().RequireModule(Code);

        // Setup
        g.MapGet("/work-centers", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListWorkCentersQuery(), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapPost("/work-centers", async (SaveWorkCenterCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body with { Id = null }, ct)).ToHttpResult(w => Results.Created($"/api/v1/manufacturing/work-centers/{w.Id}", w)))
            .RequirePermission(ManufacturingPermissions.SetupManage);
        g.MapPut("/work-centers/{id:guid}", async (Guid id, SaveWorkCenterCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body with { Id = id }, ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.SetupManage);

        g.MapGet("/routings", async (Guid? productId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListRoutingsQuery(productId), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapPost("/routings", async (SaveRoutingCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body with { Id = null }, ct)).ToHttpResult(r => Results.Created($"/api/v1/manufacturing/routings/{r.Id}", r)))
            .RequirePermission(ManufacturingPermissions.SetupManage);
        g.MapPut("/routings/{id:guid}", async (Guid id, SaveRoutingCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body with { Id = id }, ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.SetupManage);

        g.MapGet("/boms", async (Guid? productId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListBomsQuery(productId), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapGet("/boms/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetBomQuery(id), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapPost("/boms", async (CreateBomRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateBomCommand(body.ProductId, body.Code, body.Name), ct)).ToHttpResult(b => Results.Created($"/api/v1/manufacturing/boms/{b.Id}", b)))
            .RequirePermission(ManufacturingPermissions.SetupManage);
        g.MapPost("/boms/{id:guid}/versions", async (Guid id, BomVersionRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveBomVersionCommand(id, null, body.EffectiveFrom, body.EffectiveTo, body.OutputQuantity, body.ExpectedYieldPercent, body.Lines), ct))
                .ToHttpResult(v => Results.Created($"/api/v1/manufacturing/boms/{id}/versions/{v.Id}", v)))
            .RequirePermission(ManufacturingPermissions.SetupManage);
        g.MapPut("/boms/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, BomVersionRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveBomVersionCommand(id, versionId, body.EffectiveFrom, body.EffectiveTo, body.OutputQuantity, body.ExpectedYieldPercent, body.Lines), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.SetupManage);
        g.MapPost("/boms/{id:guid}/versions/{versionId:guid}/activate", async (Guid id, Guid versionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BomVersionActionCommand(id, versionId, Activate: true), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.SetupManage);
        g.MapPost("/boms/{id:guid}/versions/{versionId:guid}/obsolete", async (Guid id, Guid versionId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BomVersionActionCommand(id, versionId, Activate: false), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.SetupManage);

        g.MapGet("/planning-parameters", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPlanningParametersQuery(), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapPut("/planning-parameters", async (SavePlanningParametersCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.SetupManage);

        // Production orders
        g.MapGet("/production-orders", async (int? page, int? pageSize, string? status, Guid? productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListProductionOrdersQuery(new PageRequest(page ?? 1, pageSize ?? 50), status, productId), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapGet("/production-orders/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetProductionOrderQuery(id), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapPost("/production-orders", async (CreateProductionOrderCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(o => Results.Created($"/api/v1/manufacturing/production-orders/{o.Id}", o)))
            .RequirePermission(ManufacturingPermissions.OrderManage);
        g.MapPost("/production-orders/{id:guid}/plan", async (Guid id, PlanRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PlanProductionOrderCommand(id, body?.BomVersionId, body?.RoutingId), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderManage);
        g.MapPost("/production-orders/{id:guid}/release", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReleaseProductionOrderCommand(id), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderManage);
        g.MapPost("/production-orders/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelProductionOrderCommand(id), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderManage);
        g.MapPost("/production-orders/{id:guid}/issues", async (Guid id, MaterialRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostMaterialCommand(id, IsReturn: false, body.Date, body.Lines), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderPost).RequireIdempotency();
        g.MapPost("/production-orders/{id:guid}/returns", async (Guid id, MaterialRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostMaterialCommand(id, IsReturn: true, body.Date, body.Lines), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderPost).RequireIdempotency();
        g.MapPost("/production-orders/{id:guid}/operations", async (Guid id, OperationRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReportOperationCommand(id, body.Sequence, body.LaborHours, body.MachineHours, body.Date), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderPost).RequireIdempotency();
        g.MapPost("/production-orders/{id:guid}/receipts", async (Guid id, ReceiptRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReceiveFinishedGoodsCommand(id, body.Quantity, body.Date, body.BatchNumber, body.ExpiryDate, body.Serials), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderPost).RequireIdempotency();
        g.MapPost("/production-orders/{id:guid}/scrap", async (Guid id, ScrapRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ScrapCommand(id, body.Quantity, body.Date, body.Reason), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderPost).RequireIdempotency();
        g.MapPost("/production-orders/{id:guid}/close", async (Guid id, CloseRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseProductionOrderCommand(id, body?.Date, body?.AbnormalScrap ?? false), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.OrderPost).RequireIdempotency();

        // MRP
        g.MapGet("/mrp-runs", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListMrpRunsQuery(), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapGet("/mrp-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetMrpRunQuery(id), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.Read);
        g.MapPost("/mrp-runs", async (RunRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RunMrpCommand(body?.RunDate, body?.HorizonDays), ct)).ToHttpResult(r => Results.Created($"/api/v1/manufacturing/mrp-runs/{r.Id}", r)))
            .RequirePermission(ManufacturingPermissions.PlanningRun);
        g.MapPost("/mrp-runs/{id:guid}/convert", async (Guid id, ConvertRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConvertSuggestionsCommand(id, body.SuggestionIds, body.ComponentWarehouseId, body.OutputWarehouseId), ct)).ToHttpResult())
            .RequirePermission(ManufacturingPermissions.PlanningRun);
    }
}
