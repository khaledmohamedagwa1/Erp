using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Procurement.Application;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Workflow.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Modules.Procurement.Infrastructure;

public sealed record OrderLinesRequest(IReadOnlyList<OrderLineInput> Lines);

public sealed record InspectRequest(IReadOnlyDictionary<Guid, decimal> AcceptedQuantities, string? Notes);

public sealed record DateRequest(DateOnly? Date);

public sealed record PostInvoiceRequest(DateOnly? PostingDate);

public sealed record CancelRequest(string? Reason, DateOnly? Date);

public sealed class ProcurementModule : IErpModule
{
    public const string Code = "procurement";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Procurement",
        "المشتريات",
        IsCore: false,
        DependsOn: ["inventory"],
        Permissions:
        [
            new(ProcurementPermissions.Read, Code, "View purchase requests, quotations, orders, receipts, invoices, returns and landed costs",
                "عرض طلبات الشراء وعروض الأسعار وأوامر الشراء والاستلامات والفواتير والمرتجعات والتكاليف الإضافية"),
            new(ProcurementPermissions.RequestManage, Code, "Create and submit purchase requests", "إنشاء وتقديم طلبات الشراء"),
            new(ProcurementPermissions.QuotationManage, Code, "Record and evaluate supplier quotations", "تسجيل وتقييم عروض أسعار الموردين"),
            new(ProcurementPermissions.OrderManage, Code, "Create, submit, send, cancel and close purchase orders", "إنشاء وتقديم وإرسال وإلغاء وإقفال أوامر الشراء"),
            new(ProcurementPermissions.ReceiptManage, Code, "Create, inspect and approve goods receipts", "إنشاء وفحص واعتماد استلام البضائع"),
            new(ProcurementPermissions.ReceiptPost, Code, "Post and reverse goods receipts (changes stock and the ledger)", "ترحيل وعكس استلام البضائع", IsSensitive: true),
            new(ProcurementPermissions.InvoiceManage, Code, "Enter, match and submit supplier invoices", "إدخال ومطابقة وتقديم فواتير الموردين"),
            new(ProcurementPermissions.InvoicePost, Code, "Post and cancel supplier invoices (changes payables and the ledger)", "ترحيل وإلغاء فواتير الموردين", IsSensitive: true),
            new(ProcurementPermissions.ReturnManage, Code, "Create and approve purchase returns", "إنشاء واعتماد مرتجعات المشتريات"),
            new(ProcurementPermissions.ReturnPost, Code, "Post purchase returns and debit notes", "ترحيل مرتجعات المشتريات وإشعارات الخصم", IsSensitive: true),
            new(ProcurementPermissions.LandedCostManage, Code, "Enter landed cost charges", "إدخال التكاليف الإضافية للشحنات"),
            new(ProcurementPermissions.LandedCostPost, Code, "Post landed costs (changes inventory value)", "ترحيل التكاليف الإضافية", IsSensitive: true),
        ],
        Menu:
        [
            new("procurement.requests", "Purchase requests", "طلبات الشراء", "/procurement/requests", "shopping-cart", 10, ProcurementPermissions.Read),
            new("procurement.quotations", "Quotations", "عروض الأسعار", "/procurement/quotations", "shopping-cart", 20, ProcurementPermissions.Read),
            new("procurement.orders", "Purchase orders", "أوامر الشراء", "/procurement/orders", "shopping-cart", 30, ProcurementPermissions.Read),
            new("procurement.receipts", "Goods receipts", "استلام البضائع", "/procurement/receipts", "shopping-cart", 40, ProcurementPermissions.Read),
            new("procurement.invoices", "Supplier invoices", "فواتير الموردين", "/procurement/invoices", "shopping-cart", 50, ProcurementPermissions.Read),
            new("procurement.returns", "Purchase returns", "مرتجعات المشتريات", "/procurement/returns", "shopping-cart", 60, ProcurementPermissions.Read),
            new("procurement.landed-costs", "Landed costs", "التكاليف الإضافية", "/procurement/landed-costs", "shopping-cart", 70, ProcurementPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<ProcurementDbContext>(ProcurementDbContext.SchemaName, order: 100);
        services.AddScoped<IProcurementDbContext>(sp => sp.GetRequiredService<ProcurementDbContext>());
        services.AddScoped<ProcurementScope>();
        services.AddScoped<ProductLookup>();
        services.AddScoped<PurchaseTax>();
        services.AddScoped<ProcurementPolicy>();
        services.AddScoped<ProcurementApprovals>();
        services.AddScoped<InvoiceLineBuilder>();
        services.AddScoped<InvoiceMatcher>();
        services.AddScoped<IProcurementQueries, ProcurementQueries>();
        services.AddScoped<IPurchaseInvoiceQueries, PurchaseInvoiceQueries>();
        services.AddScoped<IProcurementReportQueries, ProcurementReportQueries>();

        // Default until the Workflow module is installed: nothing requires approval.
        services.TryAddScoped<IApprovalService, NoApprovalRequired>();
        services.AddHandlersFromAssembly(typeof(IProcurementDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.PurchaseOrder).Assembly));
        services.AddSingleton(new EventAssembly(typeof(ApprovalCompleted).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/procurement").WithTags("Procurement").RequireAuthorization().RequireCompany().RequireModule(Code);
        MapRequests(g);
        MapOrders(g);
        MapReceipts(g);
        MapInvoices(g);
        MapReturnsAndLandedCosts(g);
    }

    private static PageRequest Page(int? page, int? pageSize) => new(page ?? 1, pageSize ?? 50);

    private static void MapRequests(RouteGroupBuilder g)
    {
        g.MapGet("/requests", async (string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPurchaseRequestsQuery(status, Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/requests/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetPurchaseRequestQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/requests", async (CreatePurchaseRequestCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/requests/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.RequestManage);
        foreach (var (path, action) in new[] { ("submit", RequestAction.Submit), ("revise", RequestAction.Revise), ("close", RequestAction.Close) })
        {
            g.MapPost($"/requests/{{id:guid}}/{path}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new PurchaseRequestActionCommand(id, action), ct)).ToHttpResult())
                .RequirePermission(ProcurementPermissions.RequestManage);
        }

        g.MapGet("/quotations", async (Guid? requestId, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListQuotationsQuery(requestId, Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/quotations/compare", async (Guid requestId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CompareQuotationsQuery(requestId), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/quotations", async (CreateQuotationCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/quotations/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.QuotationManage);
        foreach (var (path, action) in new[] { ("receive", QuotationAction.Receive), ("accept", QuotationAction.Accept), ("reject", QuotationAction.Reject) })
        {
            g.MapPost($"/quotations/{{id:guid}}/{path}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new QuotationActionCommand(id, action), ct)).ToHttpResult())
                .RequirePermission(ProcurementPermissions.QuotationManage);
        }
    }

    private static void MapOrders(RouteGroupBuilder g)
    {
        g.MapGet("/orders", async (Guid? supplierId, string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPurchaseOrdersQuery(supplierId, status, Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/orders/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetPurchaseOrderQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/orders", async (CreatePurchaseOrderCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/orders/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.OrderManage);
        g.MapPut("/orders/{id:guid}/lines", async (Guid id, OrderLinesRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdatePurchaseOrderLinesCommand(id, body.Lines), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.OrderManage);
        foreach (var (path, action) in new[] { ("submit", OrderAction.Submit), ("send", OrderAction.Send), ("cancel", OrderAction.Cancel), ("close", OrderAction.Close) })
        {
            g.MapPost($"/orders/{{id:guid}}/{path}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new PurchaseOrderActionCommand(id, action), ct)).ToHttpResult())
                .RequirePermission(ProcurementPermissions.OrderManage);
        }
    }

    private static void MapReceipts(RouteGroupBuilder g)
    {
        g.MapGet("/receipts", async (Guid? purchaseOrderId, Guid? supplierId, string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListGoodsReceiptsQuery(purchaseOrderId, supplierId, status, Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/receipts/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetGoodsReceiptQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/receipts", async (CreateGoodsReceiptCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/receipts/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.ReceiptManage);
        g.MapPost("/receipts/{id:guid}/inspect", async (Guid id, InspectRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new InspectGoodsReceiptCommand(id, body.AcceptedQuantities, body.Notes), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReceiptManage);
        g.MapPost("/receipts/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GoodsReceiptActionCommand(id, ReceiptAction.Approve), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReceiptManage);
        g.MapPost("/receipts/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GoodsReceiptActionCommand(id, ReceiptAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReceiptManage);
        g.MapPost("/receipts/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostGoodsReceiptCommand(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReceiptPost).RequireIdempotency();
        g.MapPost("/receipts/{id:guid}/reverse", async (Guid id, DateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseGoodsReceiptCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReceiptPost).RequireIdempotency();
    }

    private static void MapInvoices(RouteGroupBuilder g)
    {
        g.MapGet("/invoices", async (Guid? supplierId, string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPurchaseInvoicesQuery(supplierId, status, Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/invoices/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetPurchaseInvoiceQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/invoices/{id:guid}/match", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PreviewInvoiceMatchQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/invoices", async (CreatePurchaseInvoiceCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/invoices/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.InvoiceManage);
        g.MapPost("/invoices/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SubmitPurchaseInvoiceCommand(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.InvoiceManage);
        g.MapPost("/invoices/{id:guid}/post", async (Guid id, PostInvoiceRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostPurchaseInvoiceCommand(id, body?.PostingDate), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.InvoicePost).RequireIdempotency();
        g.MapPost("/invoices/{id:guid}/cancel", async (Guid id, CancelRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelPurchaseInvoiceCommand(id, body?.Reason, body?.Date), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.InvoicePost).RequireIdempotency();
    }

    private static void MapReturnsAndLandedCosts(RouteGroupBuilder g)
    {
        g.MapGet("/returns", async (Guid? supplierId, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListPurchaseReturnsQuery(supplierId, Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/returns/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetPurchaseReturnQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/returns", async (CreatePurchaseReturnCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/returns/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.ReturnManage);
        g.MapPost("/returns/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PurchaseReturnActionCommand(id, ReturnAction.Approve), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReturnManage);
        g.MapPost("/returns/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PurchaseReturnActionCommand(id, ReturnAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReturnManage);
        g.MapPost("/returns/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostPurchaseReturnCommand(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.ReturnPost).RequireIdempotency();

        g.MapGet("/landed-costs", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListLandedCostsQuery(Page(page, pageSize)), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/landed-costs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetLandedCostQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapGet("/landed-costs/{id:guid}/allocation", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PreviewLandedCostQuery(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.Read);
        g.MapPost("/landed-costs", async (CreateLandedCostCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/procurement/landed-costs/{x.Id}", x)))
            .RequirePermission(ProcurementPermissions.LandedCostManage);
        g.MapPost("/landed-costs/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelLandedCostCommand(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.LandedCostManage);
        g.MapPost("/landed-costs/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostLandedCostCommand(id), ct)).ToHttpResult())
            .RequirePermission(ProcurementPermissions.LandedCostPost).RequireIdempotency();
    }
}
