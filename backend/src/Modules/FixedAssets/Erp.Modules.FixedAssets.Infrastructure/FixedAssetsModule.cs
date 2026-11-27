using Erp.Application;
using Erp.Application.Abstractions;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.FixedAssets.Infrastructure;

public sealed record ActivateRequest(string Mode, Guid? ClearingAccountId, string? SourceDocType, Guid? SourceDocId, Guid? SourceLineId);

public sealed record TransferRequest(DateOnly Date, Guid? BranchId, string? Location, Guid? CustodianId, string? Note);

public sealed record RevalueRequest(DateOnly Date, decimal NewNetBookValue, Guid? ReserveAccountId, int? NewUsefulLifeMonths);

public sealed record UsageRequest(DateOnly Period, decimal Units);

public sealed record DisposeRequest(DateOnly Date, decimal Proceeds, Guid? ProceedsAccountId);

public sealed record ImpairRequest(DateOnly Date, decimal RecoverableAmount, string? Reason);

public sealed record PartialDisposeRequest(DateOnly Date, decimal? Quantity, decimal? Percent, decimal Proceeds, Guid? ProceedsAccountId);

public sealed record LegalEntityTransferRequest(DateOnly Date, Guid TargetLegalEntityId, Guid ClearingAccountId, Guid? TargetBranchId);

public sealed record AssetBookRequest(string Method, int UsefulLifeMonths, decimal? SalvageValue, decimal? DecliningRatePercent, decimal? Cost, decimal? OpeningAccumulated);

public sealed record ScheduledRunRequest(DateOnly? Today);

/// <summary>Posts last month's depreciation automatically for companies that switched it on (worker/all role).</summary>
public sealed class ScheduledDepreciationBackgroundService(
    IServiceScopeFactory scopes,
    ITenantDirectory tenants,
    FixedAssetsOptions options,
    ILogger<ScheduledDepreciationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.ScheduleIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenant in await tenants.ListActiveAsync(stoppingToken))
                {
                    IReadOnlyList<Guid> companies;
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id);
                        companies = await ScheduledDepreciationRunner.CompaniesWithAutoDepreciationAsync(
                            scope.ServiceProvider.GetRequiredService<IFixedAssetsDbContext>(), tenant.Id, stoppingToken);
                    }

                    foreach (var companyId in companies)
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                        context.SetTenant(tenant.Id);
                        context.SetCompany(companyId);
                        var today = DateOnly.FromDateTime(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow);
                        var result = await scope.ServiceProvider.GetRequiredService<ScheduledDepreciationRunner>().RunAsync(today, stoppingToken);
                        if (result.LegalEntities.Any(l => l.Outcome == "Posted"))
                        {
                            logger.LogInformation("Automatic depreciation {Period:yyyy-MM} posted for tenant {TenantCode}, company {CompanyId}", result.Period, tenant.Code, companyId);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic depreciation run failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

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
            new(FixedAssetsPermissions.Dispose, Code, "Dispose of assets, partly or wholly, and transfer them between legal entities (posts journals)",
                "استبعاد الأصول كلياً أو جزئياً ونقلها بين الكيانات القانونية (يرحّل قيوداً)", IsSensitive: true),
        ],
        Menu:
        [
            new("assets.assets", "Assets", "الأصول", "/assets/assets", "fixed-assets", 10, FixedAssetsPermissions.Read),
            new("assets.categories", "Asset categories", "فئات الأصول", "/assets/categories", "fixed-assets", 20, FixedAssetsPermissions.Read),
            new("assets.depreciation", "Depreciation runs", "دورات الإهلاك", "/assets/depreciation-runs", "fixed-assets", 30, FixedAssetsPermissions.Read),
            new("assets.register", "Asset register", "سجل الأصول", "/assets/reports/register", "fixed-assets", 40, FixedAssetsPermissions.Read),
            new("assets.books", "Depreciation books", "دفاتر الإهلاك", "/assets/books", "fixed-assets", 50, FixedAssetsPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<FixedAssetsDbContext>(FixedAssetsDbContext.SchemaName, order: 150);
        services.AddScoped<IFixedAssetsDbContext>(sp => sp.GetRequiredService<FixedAssetsDbContext>());
        services.AddScoped<AssetScope>();
        services.AddScoped<InvoiceLinkValidator>();
        services.AddScoped<ScheduledDepreciationRunner>();
        var options = new FixedAssetsOptions();
        configuration.GetSection(FixedAssetsOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        var role = configuration["Erp:Role"] ?? "all";
        if (role is "worker" or "all")
        {
            services.AddHostedService<ScheduledDepreciationBackgroundService>();
        }

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
        g.MapPost("/assets/{id:guid}/dispose-partial", async (Guid id, PartialDisposeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PartialDisposeAssetCommand(id, body.Date, body.Quantity, body.Percent, body.Proceeds, body.ProceedsAccountId), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Dispose).RequireIdempotency();
        g.MapPost("/assets/{id:guid}/impair", async (Guid id, ImpairRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ImpairAssetCommand(id, body.Date, body.RecoverableAmount, body.Reason), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Revalue).RequireIdempotency();
        g.MapPost("/assets/{id:guid}/transfer-legal-entity", async (Guid id, LegalEntityTransferRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TransferToLegalEntityCommand(id, body.Date, body.TargetLegalEntityId, body.ClearingAccountId, body.TargetBranchId), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Dispose).RequireIdempotency();

        // Additional (reporting-only) depreciation books
        g.MapGet("/books", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListBooksQuery(), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/books", async (CreateBookCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(b => Results.Created($"/api/v1/assets/books/{b.Id}", b)))
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapPut("/books/{id:guid}", async (Guid id, UpdateBookCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body with { Id = id }, ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapGet("/assets/{id:guid}/books", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new AssetBooksQuery(id), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPut("/assets/{id:guid}/books/{bookId:guid}", async (Guid id, Guid bookId, AssetBookRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetAssetBookCommand(id, bookId, body.Method, body.UsefulLifeMonths, body.SalvageValue, body.DecliningRatePercent, body.Cost,
                    body.OpeningAccumulated), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Manage);
        g.MapDelete("/assets/{id:guid}/books/{bookId:guid}", async (Guid id, Guid bookId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveAssetBookCommand(id, bookId), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(FixedAssetsPermissions.Manage);

        // Depreciation runs
        g.MapGet("/depreciation-runs", async (Guid? legalEntityId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListRunsQuery(legalEntityId), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/depreciation-runs/preview", async (DepreciationPeriodRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PreviewRunQuery(body.LegalEntityId, body.Period), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPost("/depreciation-runs", async (DepreciationPeriodRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostRunCommand(body.LegalEntityId, body.Period), ct)).ToHttpResult(r => r.AlreadyPosted ? Results.Ok(r) : Results.Created($"/api/v1/assets/depreciation-runs/{r.Id}", r)))
            .RequirePermission(FixedAssetsPermissions.DepreciationPost).RequireIdempotency();
        g.MapGet("/settings", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetAssetSettingsQuery(), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapPut("/settings", async (UpdateAssetSettingsCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.DepreciationPost);
        g.MapPost("/depreciation-runs/scheduled/run", async (ScheduledRunRequest? body, ScheduledDepreciationRunner runner, IClock clock, CancellationToken ct) =>
                Results.Ok(await runner.RunAsync(body?.Today ?? DateOnly.FromDateTime(clock.UtcNow), ct)))
            .RequirePermission(FixedAssetsPermissions.DepreciationPost);
        g.MapPost("/depreciation-runs/{id:guid}/reverse", async (Guid id, ReverseRunRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseRunCommand(id, body?.Date), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.DepreciationPost).RequireIdempotency();

        // Reports
        g.MapGet("/reports/register", async (Guid legalEntityId, DateOnly? asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterQuery(legalEntityId, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow)), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
        g.MapGet("/reports/book-register", async (Guid legalEntityId, Guid bookId, DateOnly? asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BookRegisterQuery(legalEntityId, bookId, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow)), ct)).ToHttpResult())
            .RequirePermission(FixedAssetsPermissions.Read);
    }
}
