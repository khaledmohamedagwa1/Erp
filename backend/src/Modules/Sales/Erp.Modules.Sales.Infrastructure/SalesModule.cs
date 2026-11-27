using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Sales.Application;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Workflow.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Modules.Sales.Infrastructure;

public sealed record LinesBody(IReadOnlyList<SalesLineRequest> Lines);

public sealed record DateBody(DateOnly? Date);

public sealed record PickBody(IReadOnlyList<PickLine> Lines);

public sealed class SalesModule : IErpModule
{
    public const string Code = "sales";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Sales",
        "المبيعات",
        IsCore: false,
        DependsOn: ["accounting", "finance"],
        Permissions:
        [
            new(SalesPermissions.Read, Code, "View quotations, orders, deliveries, invoices and returns", "عرض عروض الأسعار وأوامر البيع والتسليم والفواتير والمرتجعات"),
            new(SalesPermissions.QuotationManage, Code, "Create, send and convert quotations", "إنشاء وإرسال وتحويل عروض الأسعار"),
            new(SalesPermissions.OrderManage, Code, "Create, confirm, cancel and short-close sales orders", "إنشاء وتأكيد وإلغاء وإقفال أوامر البيع"),
            new(SalesPermissions.DeliveryManage, Code, "Create and pick delivery notes", "إنشاء وتجهيز أذون التسليم"),
            new(SalesPermissions.DeliveryPost, Code, "Post and reverse delivery notes (issues stock)", "ترحيل وعكس أذون التسليم", IsSensitive: true),
            new(SalesPermissions.InvoiceManage, Code, "Create and submit sales invoices", "إنشاء وتقديم فواتير المبيعات"),
            new(SalesPermissions.InvoicePost, Code, "Post sales invoices", "ترحيل فواتير المبيعات", IsSensitive: true),
            new(SalesPermissions.InvoiceCancel, Code, "Cancel posted sales invoices by reversal", "إلغاء فواتير المبيعات المرحلة بالعكس", IsSensitive: true),
            new(SalesPermissions.ReturnManage, Code, "Create sales returns", "إنشاء مرتجعات المبيعات"),
            new(SalesPermissions.ReturnPost, Code, "Post sales returns (credit notes)", "ترحيل مرتجعات المبيعات (إشعارات دائنة)", IsSensitive: true),
            new(SalesPermissions.Discount, Code, "Give line discounts", "منح خصومات على البنود", IsSensitive: true),
            new(SalesPermissions.PriceOverride, Code, "Type prices instead of the price list", "تعديل الأسعار يدوياً بدلاً من قائمة الأسعار", IsSensitive: true),
        ],
        Menu:
        [
            new("sales.quotations", "Quotations", "عروض الأسعار", "/sales/quotations", "sales", 10, SalesPermissions.Read),
            new("sales.orders", "Sales orders", "أوامر البيع", "/sales/orders", "sales", 20, SalesPermissions.Read),
            new("sales.deliveries", "Deliveries", "أذون التسليم", "/sales/deliveries", "sales", 30, SalesPermissions.Read),
            new("sales.invoices", "Sales invoices", "فواتير المبيعات", "/sales/invoices", "sales", 40, SalesPermissions.Read),
            new("sales.returns", "Sales returns", "مرتجعات المبيعات", "/sales/returns", "sales", 50, SalesPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<SalesDbContext>(SalesDbContext.SchemaName, order: 110);
        services.AddScoped<ISalesDbContext>(sp => sp.GetRequiredService<SalesDbContext>());
        services.AddScoped<SalesScope>();
        services.AddScoped<SalesPricing>();
        services.AddScoped<SalesQueries>();
        services.AddScoped<ISalesQueries>(sp => sp.GetRequiredService<SalesQueries>());
        services.AddScoped<ISalesReportQueries, SalesReportQueries>();
        services.AddScoped<SalesCreditService>();
        services.AddScoped<SalesSubmission>();
        services.AddScoped<OrderConfirmation>();
        services.AddScoped<InvoiceBuilder>();

        // Until the Workflow engine is installed nothing needs approval (it registers the real service, which wins).
        services.TryAddScoped<IApprovalService, NoApprovalRequired>();
        services.AddHandlersFromAssembly(typeof(ISalesDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.SalesInvoice).Assembly));
        services.AddSingleton(new EventAssembly(typeof(ApprovalCompleted).Assembly));
        services.AddSingleton(new EventAssembly(typeof(InvoiceSettled).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/sales").WithTags("Sales").RequireAuthorization().RequireCompany().RequireModule(Code);

        // ---- Quotations ----
        g.MapGet("/quotations", async ([AsParameters] ListParameters p, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListQuotationsQuery(p.ToFilter()), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapGet("/quotations/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetQuotationQuery(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapPost("/quotations", async (CreateQuotationCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(x => Results.Created($"/api/v1/sales/quotations/{x.Id}", x)))
            .RequirePermission(SalesPermissions.QuotationManage);
        g.MapPut("/quotations/{id:guid}/lines", async (Guid id, LinesBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateQuotationLinesCommand(id, b.Lines), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.QuotationManage);
        foreach (var (path, action) in new[] { ("send", QuotationAction.Send), ("revise", QuotationAction.Revise), ("accept", QuotationAction.Accept), ("reject", QuotationAction.Reject) })
        {
            g.MapPost($"/quotations/{{id:guid}}/{path}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new QuotationActionCommand(id, action), ct)).ToHttpResult())
                .RequirePermission(SalesPermissions.QuotationManage).RequireIdempotency();
        }

        g.MapPost("/quotations/{id:guid}/convert", async (Guid id, DateBody? b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConvertQuotationCommand(id, b?.Date), ct)).ToHttpResult(x => Results.Created($"/api/v1/sales/orders/{x.Id}", x)))
            .RequirePermission(SalesPermissions.OrderManage).RequireIdempotency();

        // ---- Orders ----
        g.MapGet("/orders", async ([AsParameters] ListParameters p, IDispatcher d, CancellationToken ct) => (await d.Send(new ListOrdersQuery(p.ToFilter()), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapGet("/orders/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetOrderQuery(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapPost("/orders", async (CreateOrderCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(x => Results.Created($"/api/v1/sales/orders/{x.Id}", x)))
            .RequirePermission(SalesPermissions.OrderManage);
        g.MapPut("/orders/{id:guid}/lines", async (Guid id, LinesBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateOrderLinesCommand(id, b.Lines), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.OrderManage);
        g.MapPost("/orders/{id:guid}/confirm", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new SubmitOrderCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.OrderManage).RequireIdempotency();
        g.MapPost("/orders/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CancelOrderCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.OrderManage).RequireIdempotency();
        g.MapPost("/orders/{id:guid}/short-close", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ShortCloseOrderCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.OrderManage).RequireIdempotency();

        // ---- Deliveries ----
        g.MapGet("/deliveries", async ([AsParameters] ListParameters p, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListDeliveriesQuery(p.ToFilter()), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapGet("/deliveries/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetDeliveryQuery(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapPost("/deliveries", async (CreateDeliveryCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(x => Results.Created($"/api/v1/sales/deliveries/{x.Id}", x)))
            .RequirePermission(SalesPermissions.DeliveryManage);
        g.MapPost("/deliveries/{id:guid}/pick", async (Guid id, PickBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PickDeliveryCommand(id, b.Lines), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.DeliveryManage);
        g.MapPost("/deliveries/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostDeliveryCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.DeliveryPost).RequireIdempotency();
        g.MapPost("/deliveries/{id:guid}/reverse", async (Guid id, DateBody? b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseDeliveryCommand(id, b?.Date), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.DeliveryPost).RequireIdempotency();
        g.MapPost("/deliveries/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CancelDeliveryCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.DeliveryManage).RequireIdempotency();

        // ---- Invoices ----
        g.MapGet("/invoices", async ([AsParameters] ListParameters p, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListInvoicesQuery(p.ToFilter()), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapGet("/invoices/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetInvoiceQuery(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapPost("/invoices/preview", async (CreateInvoiceCommand b, IDispatcher d, CancellationToken ct) => (await d.Send(new PreviewInvoiceQuery(b), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.InvoiceManage);
        g.MapPost("/invoices", async (CreateInvoiceCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(x => Results.Created($"/api/v1/sales/invoices/{x.Id}", x)))
            .RequirePermission(SalesPermissions.InvoiceManage);
        g.MapPut("/invoices/{id:guid}/lines", async (Guid id, LinesBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateInvoiceLinesCommand(id, b.Lines), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.InvoiceManage);
        g.MapPost("/invoices/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new SubmitInvoiceCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.InvoiceManage).RequireIdempotency();
        g.MapPost("/invoices/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostInvoiceCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.InvoicePost).RequireIdempotency();
        g.MapPost("/invoices/{id:guid}/cancel", async (Guid id, DateBody? b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelInvoiceCommand(id, b?.Date), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.InvoiceCancel).RequireIdempotency();

        // ---- Returns (credit notes) ----
        g.MapGet("/returns", async ([AsParameters] ListParameters p, IDispatcher d, CancellationToken ct) => (await d.Send(new ListReturnsQuery(p.ToFilter()), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapGet("/returns/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetReturnQuery(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.Read);
        g.MapPost("/returns", async (CreateReturnCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(x => Results.Created($"/api/v1/sales/returns/{x.Id}", x)))
            .RequirePermission(SalesPermissions.ReturnManage);
        g.MapPost("/returns/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostReturnCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.ReturnPost).RequireIdempotency();
        g.MapPost("/returns/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CancelReturnCommand(id), ct)).ToHttpResult())
            .RequirePermission(SalesPermissions.ReturnManage).RequireIdempotency();
    }
}

/// <summary>Common list filters: status, customer, document date range, branch, paging.</summary>
public sealed record ListParameters(string? Status, Guid? CustomerId, DateOnly? From, DateOnly? To, Guid? BranchId, int? Page, int? PageSize)
{
    public SalesListFilter ToFilter() => new(Status, CustomerId, From, To, BranchId, new PageRequest(Page ?? 1, PageSize ?? 50));
}
