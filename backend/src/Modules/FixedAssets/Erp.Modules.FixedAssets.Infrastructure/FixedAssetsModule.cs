using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.FixedAssets.Application;
using Erp.Modules.FixedAssets.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.FixedAssets.Infrastructure;

public sealed record ActivateRequest(string Mode, Guid? ClearingAccountId, string? SourceDocType, Guid? SourceDocId, Guid? SourceLineId);

public sealed record TransferRequest(DateOnly Date, Guid? BranchId, string? Location, Guid? CustodianId, string? Note);

public sealed record RevalueRequest(DateOnly Date, decimal NewNetBookValue, Guid ReserveAccountId, int? NewUsefulLifeMonths);

public sealed record UsageRequest(DateOnly Period, decimal Units);

public sealed record DisposeRequest(DateOnly Date, decimal Proceeds, Guid? ProceedsAccountId);

public sealed record DepreciationPeriodRequest(Guid LegalEntityId, DateOnly Period);

public sealed record ReverseRunRequest(DateOnly? Date);

public sealed class FixedAssetsModule : IErpModule
{
    public const string Code = "fixed-assets";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Fixed Assets",
        "الأصول الثابتة",
        IsCore: false,
        DependsOn: ["accounting"],
        Permissions:
        [
            new(FixedAssetsPermissions.Read, Code, "View asset categories, assets, schedules, depreciation runs and the asset register", "عرض فئات الأصول والأصول وجداول الإهلاك ودورات الإهلاك وسجل الأصول"),
            new(FixedAssetsPermissions.Manage, Code, "Maintain categories and assets, activate (capitalise), transfer and record usage", "إدارة الفئات والأصول وتفعيلها (رسملتها) ونقلها وتسجيل الاستخدام"),
            new(FixedAssetsPermissions.DepreciationPost, Code, "Post and reverse depreciation runs (posts journals)", "ترحيل دورات الإهلاك وعكسها (يرحّل قيوداً)", IsSensitive: true),
            new(FixedAssetsPermissions.Revalue, Code, "Revalue assets (posts journals)", "إعادة تقييم الأصول (يرحّل قيوداً)", IsSensitive: true),
            new(FixedAssetsPermissions.Dispose, Code, "Dispose of assets (posts journals)", "استبعاد الأصول (يرحّل قيوداً)", IsSensitive: true),
        ],
        Menu:
        [
            new("assets.assets", "Assets", "الأصول", "/assets/assets", "fixed-assets", 10, FixedAssetsPermissions.Read),
            new("assets.categories", "Asset categories", "فئات الأصول", "/assets/categories", "fixed-assets", 20, FixedAssetsPermissions.Read),
            new("assets.depreciation", "Depreciation runs", "دورات الإهلاك", "/assets/depreciation-runs", "fixed-assets", 30, FixedAssetsPermissions.Read),
            new("assets.register", "Asset register", "سجل الأصول", "/assets/reports/register", "fixed-assets", 40, FixedAssetsPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<FixedAssetsDbContext>(FixedAssetsDbContext.SchemaName, order: 150);
        services.AddScoped<IFixedAssetsDbContext>(sp => sp.GetRequiredService<FixedAssetsDbContext>());
        services.AddScoped<AssetScope>();
        services.AddHandlersFromAssembly(typeof(IFixedAssetsDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.Asset).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/assets").WithTags("Fixed Assets").RequireAuthorization().RequireCompany().RequireModule(Code);

        // Categories
        g.MapGet("/categories", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCategoriesQuery(), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/categories", async (CreateCategoryCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(c => Results.Created($"/api/v1/assets/categories/{c.Id}", c)))
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapPut("/categories/{id:guid}", async (Guid id, UpdateCategoryCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body with { Id = id }, ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage);

        // Assets
        g.MapGet("/assets", async (int? page, int? pageSize, Guid? legalEntityId, Guid? categoryId, string? status, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListAssetsQuery(new PageRequest(page ?? 1, pageSize ?? 50), legalEntityId, categoryId, status, search), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapGet("/assets/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetAssetQuery(id), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/assets", async (AssetInput body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateAssetCommand(body), ct)).ToHttpResult(a => Results.Created($"/api/v1/assets/assets/{a.Id}", a)))
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapPut("/assets/{id:guid}", async (Guid id, AssetInput body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateAssetCommand(id, body), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapDelete("/assets/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteAssetCommand(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapGet("/assets/{id:guid}/schedule", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetScheduleQuery(id), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapGet("/assets/{id:guid}/history", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetHistoryQuery(id), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/assets/{id:guid}/activate", async (Guid id, ActivateRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ActivateAssetCommand(id, body.Mode, body.ClearingAccountId, body.SourceDocType, body.SourceDocId, body.SourceLineId), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage).RequireIdempotency();
        g.MapPost("/assets/{id:guid}/transfer", async (Guid id, TransferRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TransferAssetCommand(id, body.Date, body.BranchId, body.Location, body.CustodianId, body.Note), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapPost("/assets/{id:guid}/usage", async (Guid id, UsageRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordUsageCommand(id, body.Period, body.Units), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapPost("/assets/{id:guid}/revalue", async (Guid id, RevalueRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevalueAssetCommand(id, body.Date, body.NewNetBookValue, body.ReserveAccountId, body.NewUsefulLifeMonths), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Revalue).RequireIdempotency();
        g.MapPost("/assets/{id:guid}/dispose", async (Guid id, DisposeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DisposeAssetCommand(id, body.Date, body.Proceeds, body.ProceedsAccountId), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Dispose).RequireIdempotency();

        // Depreciation runs
        g.MapGet("/depreciation-runs", async (Guid? legalEntityId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListRunsQuery(legalEntityId), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/depreciation-runs/preview", async (DepreciationPeriodRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PreviewRunQuery(body.LegalEntityId, body.Period), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/depreciation-runs", async (DepreciationPeriodRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostRunCommand(body.LegalEntityId, body.Period), ct)).ToHttpResult(r => r.AlreadyPosted ? Results.Ok(r) : Results.Created($"/api/v1/assets/depreciation-runs/{r.Id}", r)))
            .RequirePermission(FixedAssetsPermissions.DepreciationPost).RequireIdempotency();
        g.MapPost("/depreciation-runs/{id:guid}/reverse", async (Guid id, ReverseRunRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseRunCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.DepreciationPost).RequireIdempotency();

        // Reports
        g.MapGet("/reports/register", async (Guid legalEntityId, DateOnly? asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterQuery(legalEntityId, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow)), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
    }
}
