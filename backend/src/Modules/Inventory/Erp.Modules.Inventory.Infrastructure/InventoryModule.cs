using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Inventory.Application;
using Erp.Modules.Inventory.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Inventory.Infrastructure;

public sealed record LinesRequest(IReadOnlyList<StockLineInput> Lines);

public sealed record PostDateRequest(DateOnly? Date);

public sealed record ReceiveRequest(DateOnly? Date, IReadOnlyList<TransferReceiptLine>? Lines);

public sealed record CountsRequest(IReadOnlyList<CountEntry> Entries);

public sealed record BinRequest(string Code, string? Name, bool IsDefaultReceiving);

public sealed record BinUpdateRequest(string? Name, bool? IsDefaultReceiving, bool? IsActive);

public sealed class InventoryModule : IErpModule
{
    public const string Code = "inventory";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Inventory",
        "المخزون",
        IsCore: false,
        DependsOn: [],
        Permissions:
        [
            new(InventoryPermissions.Read, Code, "View stock, the inventory ledger, valuation, batches and serials", "عرض المخزون ودفتر الحركات والتقييم والتشغيلات والأرقام التسلسلية"),
            new(InventoryPermissions.DocumentManage, Code, "Create and edit opening stock, adjustments, transfers and counts", "إنشاء وتعديل أرصدة أول المدة والتسويات والتحويلات والجرد"),
            new(InventoryPermissions.DocumentApprove, Code, "Approve inventory documents", "اعتماد مستندات المخزون", IsSensitive: true),
            new(InventoryPermissions.DocumentPost, Code, "Post inventory documents (changes stock and the ledger)", "ترحيل مستندات المخزون", IsSensitive: true),
            new(InventoryPermissions.ReservationManage, Code, "Reserve and release stock", "حجز المخزون وإلغاء الحجز"),
            new(InventoryPermissions.BinManage, Code, "Set up bins and move stock between bins", "إعداد مواقع التخزين ونقل المخزون بينها"),
            new(InventoryPermissions.Recost, Code, "Recalculate issue costs of back-dated postings (posts cost corrections)", "إعادة احتساب تكلفة الصرف للحركات بأثر رجعي (يرحّل تسويات)", IsSensitive: true),
        ],
        Menu:
        [
            new("inventory.stock", "Stock", "الأرصدة", "/inventory/stock", "inventory", 10, InventoryPermissions.Read),
            new("inventory.movements", "Inventory ledger", "دفتر حركات المخزون", "/inventory/movements", "inventory", 20, InventoryPermissions.Read),
            new("inventory.transfers", "Transfers", "التحويلات", "/inventory/transfers", "inventory", 30, InventoryPermissions.Read),
            new("inventory.adjustments", "Adjustments", "التسويات", "/inventory/adjustments", "inventory", 40, InventoryPermissions.Read),
            new("inventory.counts", "Stock counts", "الجرد", "/inventory/counts", "inventory", 50, InventoryPermissions.Read),
            new("inventory.opening-stock", "Opening stock", "أرصدة أول المدة", "/inventory/opening-stock", "inventory", 60, InventoryPermissions.Read),
            new("inventory.bins", "Bins", "مواقع التخزين", "/inventory/bins", "inventory", 70, InventoryPermissions.Read),
            new("inventory.recost", "Recost", "إعادة احتساب التكلفة", "/inventory/recost", "inventory", 80, InventoryPermissions.Recost),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<InventoryDbContext>(InventoryDbContext.SchemaName, order: 80);
        services.AddScoped<IInventoryDbContext>(sp => sp.GetRequiredService<InventoryDbContext>());
        services.AddScoped<IInventoryStore, InventoryStore>();
        services.AddScoped<PostingContextResolver>();
        services.AddScoped<InventoryPostingService>();
        services.AddScoped<IInventoryPostingService>(sp => sp.GetRequiredService<InventoryPostingService>());
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IStockQueries, StockQueries>();
        services.AddScoped<IInventoryImportService, InventoryImportService>();
        services.AddScoped<IInventoryRevaluationService, InventoryRevaluationService>();
        services.AddScoped<IInventoryReportQueries, InventoryReportQueries>();
        services.AddScoped<InventoryDocumentSupport>();
        services.AddScoped<AdjustmentPoster>();
        services.AddScoped<TransferLegPoster>();
        services.AddScoped<BinStock>();
        services.AddScoped<RecostEngine>();

        // Replaces the catalog's default "never used" probe (registered with TryAdd by the Catalog module).
        services.AddScoped<IProductUsageProbe, InventoryProductUsageProbe>();
        services.AddHandlersFromAssembly(typeof(IInventoryDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.InventoryMovement).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/inventory").WithTags("Inventory").RequireAuthorization().RequireCompany().RequireModule(Code);

        // Inquiries
        g.MapGet("/stock", async (Guid? productId, Guid? warehouseId, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new StockInquiryQuery(productId, warehouseId, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/movements", async (Guid? productId, Guid? warehouseId, DateOnly? from, DateOnly? to, string? sourceDocType, Guid? sourceDocId, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new MovementsQuery(productId, warehouseId, from, to, sourceDocType, sourceDocId, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/valuation", async (Guid? productId, IDispatcher d, CancellationToken ct) => (await d.Send(new ValuationQuery(productId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/batches", async (Guid? productId, IDispatcher d, CancellationToken ct) => (await d.Send(new BatchesQuery(productId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/serials", async (Guid? productId, string? status, IDispatcher d, CancellationToken ct) => (await d.Send(new SerialsQuery(productId, status), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/returnable-issues", async (string sourceDocType, Guid sourceDocId, Guid sourceLineId, IStockQueries stock, CancellationToken ct) =>
                Results.Ok(await stock.GetReturnableIssuesAsync(sourceDocType, sourceDocId, sourceLineId, ct)))
            .RequirePermission(InventoryPermissions.Read);

        // Bins (opt-in per warehouse: a warehouse with active bins is bin-managed)
        g.MapGet("/warehouses/{warehouseId:guid}/bins", async (Guid warehouseId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListBinsQuery(warehouseId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/warehouses/{warehouseId:guid}/bins", async (Guid warehouseId, BinRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateBinCommand(warehouseId, body.Code, body.Name, body.IsDefaultReceiving), ct)).ToHttpResult(x => Results.Created($"/api/v1/inventory/bins/{x.Id}", x)))
            .RequirePermission(InventoryPermissions.BinManage);
        g.MapPut("/bins/{id:guid}", async (Guid id, BinUpdateRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateBinCommand(id, body.Name, body.IsDefaultReceiving, body.IsActive), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.BinManage);
        g.MapGet("/warehouses/{warehouseId:guid}/bin-stock", async (Guid warehouseId, Guid? productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BinStockQuery(warehouseId, productId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/bin-transfers", async (TransferBinStockCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.BinManage).RequireIdempotency();

        // Recost
        g.MapPost("/recost", async (RunRecostCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Recost).RequireIdempotency();
        g.MapGet("/recost-runs", async (Guid? productId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListRecostRunsQuery(productId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/recost-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetRecostRunQuery(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);

        // Opening stock
        g.MapGet("/opening-stock", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListOpeningStocksQuery(new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/opening-stock/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetOpeningStockQuery(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/opening-stock", async (CreateOpeningStockCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/inventory/opening-stock/{x.Id}", x)))
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPut("/opening-stock/{id:guid}/lines", async (Guid id, LinesRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateOpeningStockLinesCommand(id, body.Lines), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/opening-stock/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpeningStockActionCommand(id, DocumentAction.Submit), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/opening-stock/{id:guid}/return-to-draft", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpeningStockActionCommand(id, DocumentAction.ReturnToDraft), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentApprove);
        g.MapPost("/opening-stock/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpeningStockActionCommand(id, DocumentAction.Approve), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentApprove);
        g.MapPost("/opening-stock/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpeningStockActionCommand(id, DocumentAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/opening-stock/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostOpeningStockCommand(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentPost).RequireIdempotency();

        // Adjustments
        g.MapGet("/adjustments", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListAdjustmentsQuery(new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/adjustments/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetAdjustmentQuery(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/adjustments", async (CreateAdjustmentCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/inventory/adjustments/{x.Id}", x)))
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/adjustments/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AdjustmentActionCommand(id, DocumentAction.Approve), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentApprove);
        g.MapPost("/adjustments/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AdjustmentActionCommand(id, DocumentAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/adjustments/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostAdjustmentCommand(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentPost).RequireIdempotency();

        // Transfers
        g.MapGet("/transfers", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListTransfersQuery(new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapGet("/transfers/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetTransferQuery(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/transfers", async (CreateTransferCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/inventory/transfers/{x.Id}", x)))
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/transfers/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TransferActionCommand(id, DocumentAction.Approve), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentApprove);
        g.MapPost("/transfers/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TransferActionCommand(id, DocumentAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/transfers/{id:guid}/issue", async (Guid id, PostDateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new IssueTransferCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentPost).RequireIdempotency();
        g.MapPost("/transfers/{id:guid}/receive", async (Guid id, ReceiveRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReceiveTransferCommand(id, body?.Date, body?.Lines), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentPost).RequireIdempotency();
        g.MapPost("/transfers/{id:guid}/close", async (Guid id, PostDateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseTransferCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentPost).RequireIdempotency();

        // Counts
        g.MapGet("/counts/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetCountQuery(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/counts", async (CreateCountCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(x => Results.Created($"/api/v1/inventory/counts/{x.Id}", x)))
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/counts/{id:guid}/snapshot", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new SnapshotCountCommand(id), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPut("/counts/{id:guid}/counts", async (Guid id, CountsRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new EnterCountsCommand(id, body.Entries), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/counts/{id:guid}/finish", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CountActionCommand(id, CountAction.Finish), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/counts/{id:guid}/reopen", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CountActionCommand(id, CountAction.Reopen), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/counts/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CountActionCommand(id, CountAction.Approve), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentApprove);
        g.MapPost("/counts/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CountActionCommand(id, CountAction.Cancel), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentManage);
        g.MapPost("/counts/{id:guid}/post", async (Guid id, PostDateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostCountCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.DocumentPost).RequireIdempotency();

        // Reservations
        g.MapGet("/reservations", async (string demandType, Guid demandId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListReservationsQuery(demandType, demandId), ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.Read);
        g.MapPost("/reservations", async (ReserveStockCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.ReservationManage).RequireIdempotency();
        g.MapPost("/reservations/release", async (ReleaseStockCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(InventoryPermissions.ReservationManage).RequireIdempotency();
    }
}
