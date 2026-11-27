using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Catalog.Application;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Catalog.Domain;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Modules.Catalog.Infrastructure;

public sealed record UpdateCategoryRequest(string NameEn, string NameAr, Guid? ParentId, string DefaultCostingMethod, string PostingGroup);

public sealed record UpdateProductRequest(
    string NameEn,
    string NameAr,
    Guid CategoryId,
    Guid? BrandId,
    Guid BaseUomId,
    string Tracking,
    bool ExpiryTracked,
    string TaxCategory,
    string? CostingMethodOverride,
    string? PostingGroupOverride);

public sealed record AddUnitRequest(Guid UomId, decimal FactorToBase);

public sealed record AddBarcodeRequest(string Barcode, Guid? UomId, Guid? VariantId);

public sealed record AddVariantRequest(string Sku, IReadOnlyDictionary<string, string> Attributes);

public sealed record AddPriceRequest(Guid ProductId, Guid? VariantId, Guid UomId, decimal MinQuantity, decimal Price, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record ReplacePricesRequest(IReadOnlyList<PriceLine> Lines);

public sealed class CatalogModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "catalog",
        "Products & pricing",
        "الأصناف والتسعير",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(CatalogPermissions.Read, "catalog", "View products, units, categories and prices", "عرض الأصناف والوحدات والتصنيفات والأسعار"),
            new(CatalogPermissions.ProductManage, "catalog", "Create and change products", "إنشاء الأصناف وتعديلها"),
            new(CatalogPermissions.PriceManage, "catalog", "Create and change price lists and prices", "إنشاء قوائم الأسعار وتعديل الأسعار", IsSensitive: true),
            new(CatalogPermissions.SetupManage, "catalog", "Manage units, categories and brands", "إدارة الوحدات والتصنيفات والعلامات التجارية"),
        ],
        Menu:
        [
            new("catalog.products", "Products", "الأصناف", "/catalog/products", "master-data", 10, CatalogPermissions.Read),
            new("catalog.price-lists", "Price lists", "قوائم الأسعار", "/catalog/price-lists", "master-data", 11, CatalogPermissions.Read),
            new("catalog.setup", "Units, categories & brands", "الوحدات والتصنيفات والعلامات", "/catalog/setup", "master-data", 12, CatalogPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<CatalogDbContext>(CatalogDbContext.SchemaName, order: 40);
        services.AddScoped<ICatalogDbContext>(sp => sp.GetRequiredService<CatalogDbContext>());
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<ICatalogReportQueries, CatalogReportQueries>();
        services.AddScoped<IPriceCalculator, PriceCalculator>();
        services.AddScoped<IPriceListSnapshotQueries, PriceListSnapshotQueries>();

        // Inventory registers a real probe; this default answers "never used".
        services.TryAddScoped<IProductUsageProbe, NoProductUsageProbe>();
        services.AddHandlersFromAssembly(typeof(ICatalogDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Product).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/catalog").WithTags("Catalog").RequireAuthorization().RequireCompany();

        // Units of measure
        group.MapGet("/uoms", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListUomsQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapPost("/uoms", async (CreateUomCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(u => Results.Created($"/api/v1/catalog/uoms/{u.Id}", u)))
            .RequirePermission(CatalogPermissions.SetupManage);
        group.MapPost("/uoms/seed-standard", async (IDispatcher d, CancellationToken ct) => (await d.Send(new SeedStandardUomsCommand(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.SetupManage);

        // Categories and brands
        group.MapGet("/categories", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCategoriesQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapPost("/categories", async (CreateCategoryCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(c => Results.Created($"/api/v1/catalog/categories/{c.Id}", c)))
            .RequirePermission(CatalogPermissions.SetupManage);
        group.MapPut("/categories/{id:guid}", async (Guid id, UpdateCategoryRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateCategoryCommand(id, body.NameEn, body.NameAr, body.ParentId, body.DefaultCostingMethod, body.PostingGroup), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.SetupManage);
        group.MapGet("/brands", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListBrandsQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapPost("/brands", async (CreateBrandCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(b => Results.Created($"/api/v1/catalog/brands/{b.Id}", b)))
            .RequirePermission(CatalogPermissions.SetupManage);

        // Products
        group.MapGet("/products", async (int? page, int? pageSize, string? search, Guid? categoryId, string? type, bool? active, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListProductsQuery(new PageRequest(page ?? 1, pageSize ?? 50), search, categoryId, type, active), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapGet("/products/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetProductQuery(id), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapPost("/products", async (CreateProductCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(p => Results.Created($"/api/v1/catalog/products/{p.Id}", p)))
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPut("/products/{id:guid}", async (Guid id, UpdateProductRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateProductCommand(id, body.NameEn, body.NameAr, body.CategoryId, body.BrandId, body.BaseUomId, body.Tracking,
                    body.ExpiryTracked, body.TaxCategory, body.CostingMethodOverride, body.PostingGroupOverride), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPost("/products/{id:guid}/units", async (Guid id, AddUnitRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddProductUnitCommand(id, body.UomId, body.FactorToBase), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPost("/products/{id:guid}/barcodes", async (Guid id, AddBarcodeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddProductBarcodeCommand(id, body.Barcode, body.UomId, body.VariantId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPost("/products/{id:guid}/variants", async (Guid id, AddVariantRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddProductVariantCommand(id, body.Sku, body.Attributes), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPost("/products/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetProductActiveCommand(id, false), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapPost("/products/{id:guid}/activate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetProductActiveCommand(id, true), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.ProductManage);
        group.MapGet("/barcodes/{code}", async (string code, IDispatcher d, CancellationToken ct) => (await d.Send(new LookupBarcodeQuery(code), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);

        // Price lists
        group.MapGet("/price-lists", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPriceListsQuery(), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapGet("/price-lists/{id:guid}", async (Guid id, Guid? productId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPriceListQuery(id, productId), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
        group.MapPost("/price-lists", async (CreatePriceListCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(p => Results.Created($"/api/v1/catalog/price-lists/{p.Id}", p)))
            .RequirePermission(CatalogPermissions.PriceManage);
        group.MapPost("/price-lists/{id:guid}/items", async (Guid id, AddPriceRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddPriceCommand(id, body.ProductId, body.VariantId, body.UomId, body.MinQuantity, body.Price, body.ValidFrom, body.ValidTo), ct))
                .ToHttpResult(i => Results.Created($"/api/v1/catalog/price-lists/{id}", i)))
            .RequirePermission(CatalogPermissions.PriceManage);
        group.MapPut("/price-lists/{id:guid}/products/{productId:guid}", async (Guid id, Guid productId, ReplacePricesRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReplaceProductPricesCommand(id, productId, body.Lines), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.PriceManage);
        group.MapGet("/price-quote", async (Guid priceListId, Guid productId, Guid? variantId, Guid uomId, decimal quantity, DateOnly? date, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PriceQuoteQuery(priceListId, productId, variantId, uomId, quantity, date ?? DateOnly.FromDateTime(DateTime.UtcNow)), ct)).ToHttpResult())
            .RequirePermission(CatalogPermissions.Read);
    }
}
