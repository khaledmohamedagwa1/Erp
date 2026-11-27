using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Catalog.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Catalog.Application;

public interface ICatalogDbContext
{
    DbSet<UnitOfMeasure> Uoms { get; }

    DbSet<ProductCategory> Categories { get; }

    DbSet<Brand> Brands { get; }

    DbSet<Product> Products { get; }

    DbSet<PriceList> PriceLists { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class CatalogAppErrors
{
    public static readonly Error UomCodeTaken = Error.Conflict("catalog.uom-code-taken", "A unit with this code already exists.");
    public static readonly Error UomNotFound = Error.NotFound("catalog.uom-not-found", "The unit of measure was not found.");
    public static readonly Error CategoryCodeTaken = Error.Conflict("catalog.category-code-taken", "A category with this code already exists.");
    public static readonly Error CategoryNotFound = Error.NotFound("catalog.category-not-found", "The category was not found.");
    public static readonly Error BrandCodeTaken = Error.Conflict("catalog.brand-code-taken", "A brand with this code already exists.");
    public static readonly Error BrandNotFound = Error.NotFound("catalog.brand-not-found", "The brand was not found.");
    public static readonly Error ProductNotFound = Error.NotFound("catalog.product-not-found", "The product was not found.");
    public static readonly Error PriceListCodeTaken = Error.Conflict("catalog.price-list-code-taken", "A price list with this code already exists.");
    public static readonly Error PriceListNotFound = Error.NotFound("catalog.price-list-not-found", "The price list was not found.");
    public static readonly Error BarcodeNotFound = Error.NotFound("catalog.barcode-not-found", "No product has this barcode.");
    public static readonly Error PriceNotFound = Error.NotFound("catalog.price-not-found", "No price applies for this product, unit, quantity and date.");
}

// ---------- DTOs ----------
public sealed record UomDto(Guid Id, string Code, string NameEn, string NameAr, string Class);

public sealed record CategoryDto(Guid Id, string Code, string NameEn, string NameAr, Guid? ParentId, string DefaultCostingMethod, string PostingGroup);

public sealed record BrandDto(Guid Id, string Code, string Name);

public sealed record ProductUnitDto(Guid UomId, string? Code, decimal FactorToBase, bool IsBase);

public sealed record ProductBarcodeDto(Guid Id, string Barcode, Guid? UomId, Guid? VariantId);

public sealed record ProductVariantDto(Guid Id, string Sku, IReadOnlyDictionary<string, string> Attributes);

public sealed record ProductDto(
    Guid Id,
    string Sku,
    string NameEn,
    string NameAr,
    string Type,
    bool IsStockable,
    Guid BaseUomId,
    Guid CategoryId,
    Guid? BrandId,
    string Tracking,
    bool ExpiryTracked,
    string TaxCategory,
    string? CostingMethodOverride,
    string? PostingGroupOverride,
    bool IsActive,
    IReadOnlyList<ProductUnitDto> Units,
    IReadOnlyList<ProductBarcodeDto> Barcodes,
    IReadOnlyList<ProductVariantDto> Variants);

public sealed record ProductListItemDto(Guid Id, string Sku, string NameEn, string NameAr, string Type, Guid BaseUomId, Guid CategoryId, string Tracking, bool IsActive);

public sealed record BarcodeLookupDto(Guid ProductId, string Sku, string NameEn, string NameAr, Guid UomId, decimal FactorToBase, Guid? VariantId, bool IsActive);

public sealed record PriceListItemDto(Guid Id, Guid ProductId, Guid? VariantId, Guid UomId, decimal MinQuantity, decimal Price, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record PriceListDto(Guid Id, string Code, string Name, string Currency, bool TaxInclusive, DateOnly? ValidFrom, DateOnly? ValidTo, bool IsActive, int ItemCount);

public sealed record PriceListDetailDto(PriceListDto Header, IReadOnlyList<PriceListItemDto> Items);

// ---------- Units ----------
public sealed record CreateUomCommand(string Code, string NameEn, string NameAr, string Class) : ICommand<UomDto>;

public sealed class CreateUomValidator : AbstractValidator<CreateUomCommand>
{
    public CreateUomValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Class).Must(c => Enum.TryParse<UomClass>(c, true, out _)).WithMessage("Class must be Count, Mass, Volume, Length or Time.");
    }
}

public sealed class CreateUomHandler(ICatalogDbContext db, ITenantContext tenant) : ICommandHandler<CreateUomCommand, UomDto>
{
    public async Task<Result<UomDto>> Handle(CreateUomCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Uoms.AnyAsync(u => u.Code == code, cancellationToken))
        {
            return CatalogAppErrors.UomCodeTaken;
        }

        var uom = UnitOfMeasure.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr, Enum.Parse<UomClass>(command.Class, true));
        db.Uoms.Add(uom);
        await db.SaveChangesAsync(cancellationToken);
        return CatalogMapping.ToDto(uom);
    }
}

/// <summary>Adds the standard units (PCS, BOX, CTN, KG…) that the company does not have yet.</summary>
public sealed record SeedStandardUomsCommand : ICommand<IReadOnlyList<UomDto>>;

public sealed class SeedStandardUomsHandler(ICatalogDbContext db, ITenantContext tenant) : ICommandHandler<SeedStandardUomsCommand, IReadOnlyList<UomDto>>
{
    public async Task<Result<IReadOnlyList<UomDto>>> Handle(SeedStandardUomsCommand command, CancellationToken cancellationToken)
    {
        var existing = (await db.Uoms.Select(u => u.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var added = UnitOfMeasure.Standard.Where(s => !existing.Contains(s.Code))
            .Select(s => UnitOfMeasure.Create(tenant.CompanyId!.Value, s.Code, s.En, s.Ar, s.Class)).ToList();
        db.Uoms.AddRange(added);
        await db.SaveChangesAsync(cancellationToken);
        return added.Select(CatalogMapping.ToDto).ToList();
    }
}

public sealed record ListUomsQuery : IQuery<IReadOnlyList<UomDto>>;

public sealed class ListUomsHandler(ICatalogDbContext db) : IQueryHandler<ListUomsQuery, IReadOnlyList<UomDto>>
{
    public async Task<Result<IReadOnlyList<UomDto>>> Handle(ListUomsQuery query, CancellationToken cancellationToken) =>
        (await db.Uoms.AsNoTracking().OrderBy(u => u.Code).ToListAsync(cancellationToken)).Select(CatalogMapping.ToDto).ToList();
}

// ---------- Categories ----------
public sealed record CreateCategoryCommand(string Code, string NameEn, string NameAr, Guid? ParentId, string? DefaultCostingMethod, string? PostingGroup)
    : ICommand<CategoryDto>;

public sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DefaultCostingMethod).Must(CatalogValidation.IsCostingMethodOrNull).WithMessage("Costing method must be WeightedAverage or FIFO.");
        RuleFor(x => x.PostingGroup).MaximumLength(40);
    }
}

public sealed class CreateCategoryHandler(ICatalogDbContext db, ITenantContext tenant) : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<Result<CategoryDto>> Handle(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Categories.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return CatalogAppErrors.CategoryCodeTaken;
        }

        if (command.ParentId is { } parent && !await db.Categories.AnyAsync(c => c.Id == parent, cancellationToken))
        {
            return CatalogAppErrors.CategoryNotFound;
        }

        var category = ProductCategory.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr,
            CatalogValidation.ParseCosting(command.DefaultCostingMethod) ?? CostingMethod.WeightedAverage, command.PostingGroup);
        category.MoveTo(command.ParentId, new Dictionary<Guid, Guid?>());
        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return CatalogMapping.ToDto(category);
    }
}

public sealed record UpdateCategoryCommand(Guid Id, string NameEn, string NameAr, Guid? ParentId, string DefaultCostingMethod, string PostingGroup) : ICommand<CategoryDto>;

public sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator()
    {
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DefaultCostingMethod).Must(c => CatalogValidation.ParseCosting(c) is not null).WithMessage("Costing method must be WeightedAverage or FIFO.");
        RuleFor(x => x.PostingGroup).NotEmpty().MaximumLength(40);
    }
}

public sealed class UpdateCategoryHandler(ICatalogDbContext db) : ICommandHandler<UpdateCategoryCommand, CategoryDto>
{
    public async Task<Result<CategoryDto>> Handle(UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        var categories = await db.Categories.ToListAsync(cancellationToken);
        var category = categories.FirstOrDefault(c => c.Id == command.Id);
        if (category is null || (command.ParentId is { } p && categories.All(c => c.Id != p)))
        {
            return CatalogAppErrors.CategoryNotFound;
        }

        category.Update(command.NameEn, command.NameAr, CatalogValidation.ParseCosting(command.DefaultCostingMethod)!.Value, command.PostingGroup);
        category.MoveTo(command.ParentId, categories.ToDictionary(c => c.Id, c => c.ParentId));
        await db.SaveChangesAsync(cancellationToken);
        return CatalogMapping.ToDto(category);
    }
}

public sealed record ListCategoriesQuery : IQuery<IReadOnlyList<CategoryDto>>;

public sealed class ListCategoriesHandler(ICatalogDbContext db) : IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(ListCategoriesQuery query, CancellationToken cancellationToken) =>
        (await db.Categories.AsNoTracking().OrderBy(c => c.Code).ToListAsync(cancellationToken)).Select(CatalogMapping.ToDto).ToList();
}

// ---------- Brands ----------
public sealed record CreateBrandCommand(string Code, string Name) : ICommand<BrandDto>;

public sealed class CreateBrandValidator : AbstractValidator<CreateBrandCommand>
{
    public CreateBrandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateBrandHandler(ICatalogDbContext db, ITenantContext tenant) : ICommandHandler<CreateBrandCommand, BrandDto>
{
    public async Task<Result<BrandDto>> Handle(CreateBrandCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Brands.AnyAsync(b => b.Code == code, cancellationToken))
        {
            return CatalogAppErrors.BrandCodeTaken;
        }

        var brand = Brand.Create(tenant.CompanyId!.Value, code, command.Name);
        db.Brands.Add(brand);
        await db.SaveChangesAsync(cancellationToken);
        return new BrandDto(brand.Id, brand.Code, brand.Name);
    }
}

public sealed record ListBrandsQuery : IQuery<IReadOnlyList<BrandDto>>;

public sealed class ListBrandsHandler(ICatalogDbContext db) : IQueryHandler<ListBrandsQuery, IReadOnlyList<BrandDto>>
{
    public async Task<Result<IReadOnlyList<BrandDto>>> Handle(ListBrandsQuery query, CancellationToken cancellationToken) =>
        await db.Brands.AsNoTracking().OrderBy(b => b.Code).Select(b => new BrandDto(b.Id, b.Code, b.Name)).ToListAsync(cancellationToken);
}

// ---------- Products ----------
public sealed record NewProductUnit(Guid UomId, decimal FactorToBase);

public sealed record NewProductBarcode(string Barcode, Guid? UomId);

public sealed record CreateProductCommand(
    string Sku,
    string NameEn,
    string NameAr,
    string Type,
    Guid BaseUomId,
    Guid CategoryId,
    Guid? BrandId,
    string? Tracking,
    bool ExpiryTracked,
    string? TaxCategory,
    string? CostingMethodOverride,
    string? PostingGroupOverride,
    IReadOnlyList<NewProductUnit>? Units,
    IReadOnlyList<NewProductBarcode>? Barcodes) : ICommand<ProductDto>;

public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(40);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Type).Must(t => Enum.TryParse<ProductType>(t, true, out _)).WithMessage("Unknown product type.");
        RuleFor(x => x.BaseUomId).NotEmpty();
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.Tracking).Must(t => t is null || Enum.TryParse<TrackingMode>(t, true, out _)).WithMessage("Tracking must be None, Batch or Serial.");
        RuleFor(x => x.CostingMethodOverride).Must(CatalogValidation.IsCostingMethodOrNull).WithMessage("Costing method must be WeightedAverage or FIFO.");
        RuleForEach(x => x.Units).ChildRules(u => u.RuleFor(x => x.FactorToBase).GreaterThan(0));
        RuleForEach(x => x.Barcodes).ChildRules(b => b.RuleFor(x => x.Barcode).NotEmpty().MaximumLength(64));
    }
}

public sealed class CreateProductHandler(ICatalogDbContext db, ITenantContext tenant) : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var sku = command.Sku.Trim().ToUpperInvariant();
        if (await CatalogChecks.SkuTakenAsync(db, sku, cancellationToken))
        {
            return CatalogErrors.SkuTaken;
        }

        var uomIds = new[] { command.BaseUomId }.Concat(command.Units?.Select(u => u.UomId) ?? []).Distinct().ToList();
        if (await db.Uoms.CountAsync(u => uomIds.Contains(u.Id), cancellationToken) != uomIds.Count)
        {
            return CatalogAppErrors.UomNotFound;
        }

        var reference = await CatalogChecks.ReferencesAsync(db, command.CategoryId, command.BrandId, cancellationToken);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        var barcodes = command.Barcodes?.Select(b => b.Barcode.Trim()).ToList() ?? [];
        if (barcodes.Count != barcodes.Distinct().Count() || await CatalogChecks.AnyBarcodeTakenAsync(db, barcodes, cancellationToken))
        {
            return CatalogErrors.DuplicateBarcode;
        }

        var product = Product.Create(tenant.CompanyId!.Value, sku, command.NameEn, command.NameAr, Enum.Parse<ProductType>(command.Type, true), command.BaseUomId, command.CategoryId);
        product.Update(command.NameEn, command.NameAr, command.CategoryId, command.BrandId, command.TaxCategory ?? "STANDARD",
            CatalogValidation.ParseCosting(command.CostingMethodOverride), command.PostingGroupOverride);
        product.SetTracking(command.Tracking is null ? TrackingMode.None : Enum.Parse<TrackingMode>(command.Tracking, true), command.ExpiryTracked);
        foreach (var unit in command.Units?.Where(u => u.UomId != command.BaseUomId) ?? [])
        {
            product.AddUnit(unit.UomId, unit.FactorToBase);
        }

        foreach (var barcode in command.Barcodes ?? [])
        {
            product.AddBarcode(barcode.Barcode, barcode.UomId, null);
        }

        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record UpdateProductCommand(
    Guid Id,
    string NameEn,
    string NameAr,
    Guid CategoryId,
    Guid? BrandId,
    Guid BaseUomId,
    string Tracking,
    bool ExpiryTracked,
    string TaxCategory,
    string? CostingMethodOverride,
    string? PostingGroupOverride) : ICommand<ProductDto>;

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.BaseUomId).NotEmpty();
        RuleFor(x => x.TaxCategory).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Tracking).Must(t => Enum.TryParse<TrackingMode>(t, true, out _)).WithMessage("Tracking must be None, Batch or Serial.");
        RuleFor(x => x.CostingMethodOverride).Must(CatalogValidation.IsCostingMethodOrNull).WithMessage("Costing method must be WeightedAverage or FIFO.");
    }
}

public sealed class UpdateProductHandler(ICatalogDbContext db, IProductUsageProbe usage) : ICommandHandler<UpdateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var product = await CatalogChecks.LoadProductAsync(db, command.Id, cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        var reference = await CatalogChecks.ReferencesAsync(db, command.CategoryId, command.BrandId, cancellationToken);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        if (command.BaseUomId != product.BaseUomId)
        {
            if (!await db.Uoms.AnyAsync(u => u.Id == command.BaseUomId, cancellationToken))
            {
                return CatalogAppErrors.UomNotFound;
            }

            product.ChangeBaseUom(command.BaseUomId, await usage.IsUsedAsync(product.Id, cancellationToken));
        }

        product.Update(command.NameEn, command.NameAr, command.CategoryId, command.BrandId, command.TaxCategory,
            CatalogValidation.ParseCosting(command.CostingMethodOverride), command.PostingGroupOverride);
        product.SetTracking(Enum.Parse<TrackingMode>(command.Tracking, true), command.ExpiryTracked);
        await db.SaveChangesAsync(cancellationToken);
        return await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record AddProductUnitCommand(Guid ProductId, Guid UomId, decimal FactorToBase) : ICommand<ProductDto>;

public sealed class AddProductUnitHandler(ICatalogDbContext db) : ICommandHandler<AddProductUnitCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(AddProductUnitCommand command, CancellationToken cancellationToken)
    {
        var product = await CatalogChecks.LoadProductAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        if (!await db.Uoms.AnyAsync(u => u.Id == command.UomId, cancellationToken))
        {
            return CatalogAppErrors.UomNotFound;
        }

        product.AddUnit(command.UomId, command.FactorToBase);
        await db.SaveChangesAsync(cancellationToken);
        return await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record AddProductBarcodeCommand(Guid ProductId, string Barcode, Guid? UomId, Guid? VariantId) : ICommand<ProductDto>;

public sealed class AddProductBarcodeValidator : AbstractValidator<AddProductBarcodeCommand>
{
    public AddProductBarcodeValidator() => RuleFor(x => x.Barcode).NotEmpty().MaximumLength(64);
}

public sealed class AddProductBarcodeHandler(ICatalogDbContext db) : ICommandHandler<AddProductBarcodeCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(AddProductBarcodeCommand command, CancellationToken cancellationToken)
    {
        var product = await CatalogChecks.LoadProductAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        if (await CatalogChecks.AnyBarcodeTakenAsync(db, [command.Barcode.Trim()], cancellationToken))
        {
            return CatalogErrors.DuplicateBarcode;
        }

        product.AddBarcode(command.Barcode, command.UomId, command.VariantId);
        await db.SaveChangesAsync(cancellationToken);
        return await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record AddProductVariantCommand(Guid ProductId, string Sku, IReadOnlyDictionary<string, string> Attributes) : ICommand<ProductDto>;

public sealed class AddProductVariantValidator : AbstractValidator<AddProductVariantCommand>
{
    public AddProductVariantValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Attributes).NotEmpty().WithMessage("A variant needs at least one attribute value.");
    }
}

public sealed class AddProductVariantHandler(ICatalogDbContext db) : ICommandHandler<AddProductVariantCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(AddProductVariantCommand command, CancellationToken cancellationToken)
    {
        var product = await CatalogChecks.LoadProductAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        if (await CatalogChecks.SkuTakenAsync(db, command.Sku.Trim().ToUpperInvariant(), cancellationToken))
        {
            return CatalogErrors.SkuTaken;
        }

        product.AddVariant(command.Sku, command.Attributes);
        await db.SaveChangesAsync(cancellationToken);
        return await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record SetProductActiveCommand(Guid ProductId, bool Active) : ICommand<ProductDto>;

public sealed class SetProductActiveHandler(ICatalogDbContext db) : ICommandHandler<SetProductActiveCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(SetProductActiveCommand command, CancellationToken cancellationToken)
    {
        var product = await CatalogChecks.LoadProductAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        if (command.Active)
        {
            product.Activate();
        }
        else
        {
            product.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record GetProductQuery(Guid Id) : IQuery<ProductDto>;

public sealed class GetProductHandler(ICatalogDbContext db) : IQueryHandler<GetProductQuery, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Units).Include(p => p.Barcodes).Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        return product is null ? CatalogAppErrors.ProductNotFound : await CatalogMapping.ToDtoAsync(db, product, cancellationToken);
    }
}

public sealed record ListProductsQuery(PageRequest Page, string? Search, Guid? CategoryId, string? Type, bool? Active) : IQuery<PagedResult<ProductListItemDto>>;

public sealed class ListProductsHandler(ICatalogDbContext db) : IQueryHandler<ListProductsQuery, PagedResult<ProductListItemDto>>
{
    public async Task<Result<PagedResult<ProductListItemDto>>> Handle(ListProductsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var upper = term.ToUpperInvariant();
            source = source.Where(p => p.Sku.Contains(upper) || p.NameEn.Contains(term) || p.NameAr.Contains(term) ||
                                       p.Barcodes.Any(b => b.Barcode == term) || p.Variants.Any(v => v.Sku == upper));
        }

        if (query.CategoryId is { } category)
        {
            source = source.Where(p => p.CategoryId == category);
        }

        if (query.Type is not null && Enum.TryParse<ProductType>(query.Type, true, out var type))
        {
            source = source.Where(p => p.Type == type);
        }

        if (query.Active is { } active)
        {
            source = source.Where(p => p.IsActive == active);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(p => p.Sku).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(p => new ProductListItemDto(p.Id, p.Sku, p.NameEn, p.NameAr, p.Type.ToString(), p.BaseUomId, p.CategoryId, p.Tracking.ToString(), p.IsActive))
            .ToListAsync(cancellationToken);
        return new PagedResult<ProductListItemDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record LookupBarcodeQuery(string Barcode) : IQuery<BarcodeLookupDto>;

public sealed class LookupBarcodeHandler(ICatalogDbContext db) : IQueryHandler<LookupBarcodeQuery, BarcodeLookupDto>
{
    public async Task<Result<BarcodeLookupDto>> Handle(LookupBarcodeQuery query, CancellationToken cancellationToken)
    {
        var code = query.Barcode.Trim();
        var product = await db.Products.AsNoTracking().Include(p => p.Units).Include(p => p.Barcodes.Where(b => b.Barcode == code))
            .FirstOrDefaultAsync(p => p.Barcodes.Any(b => b.Barcode == code), cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.BarcodeNotFound;
        }

        var barcode = product.Barcodes.Single();
        var uomId = barcode.UomId ?? product.BaseUomId;
        return new BarcodeLookupDto(product.Id, product.Sku, product.NameEn, product.NameAr, uomId, product.FactorOf(uomId), barcode.VariantId, product.IsActive);
    }
}

// ---------- Price lists ----------
public sealed record CreatePriceListCommand(string Code, string Name, string Currency, bool TaxInclusive, DateOnly? ValidFrom, DateOnly? ValidTo) : ICommand<PriceListDto>;

public sealed class CreatePriceListValidator : AbstractValidator<CreatePriceListCommand>
{
    public CreatePriceListValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
    }
}

public sealed class CreatePriceListHandler(ICatalogDbContext db, ITenantContext tenant) : ICommandHandler<CreatePriceListCommand, PriceListDto>
{
    public async Task<Result<PriceListDto>> Handle(CreatePriceListCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.PriceLists.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return CatalogAppErrors.PriceListCodeTaken;
        }

        var list = PriceList.Create(tenant.CompanyId!.Value, code, command.Name, command.Currency, command.TaxInclusive, command.ValidFrom, command.ValidTo);
        db.PriceLists.Add(list);
        await db.SaveChangesAsync(cancellationToken);
        return CatalogMapping.ToDto(list);
    }
}

public sealed record ListPriceListsQuery : IQuery<IReadOnlyList<PriceListDto>>;

public sealed class ListPriceListsHandler(ICatalogDbContext db) : IQueryHandler<ListPriceListsQuery, IReadOnlyList<PriceListDto>>
{
    public async Task<Result<IReadOnlyList<PriceListDto>>> Handle(ListPriceListsQuery query, CancellationToken cancellationToken) =>
        await db.PriceLists.AsNoTracking().OrderBy(p => p.Code)
            .Select(p => new PriceListDto(p.Id, p.Code, p.Name, p.Currency, p.TaxInclusive, p.ValidFrom, p.ValidTo, p.IsActive, p.Items.Count))
            .ToListAsync(cancellationToken);
}

public sealed record GetPriceListQuery(Guid Id, Guid? ProductId) : IQuery<PriceListDetailDto>;

public sealed class GetPriceListHandler(ICatalogDbContext db) : IQueryHandler<GetPriceListQuery, PriceListDetailDto>
{
    public async Task<Result<PriceListDetailDto>> Handle(GetPriceListQuery query, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.AsNoTracking()
            .Include(p => p.Items.Where(i => query.ProductId == null || i.ProductId == query.ProductId))
            .FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);
        if (list is null)
        {
            return CatalogAppErrors.PriceListNotFound;
        }

        return new PriceListDetailDto(CatalogMapping.ToDto(list), list.Items.OrderBy(i => i.ProductId).ThenBy(i => i.MinQuantity).Select(CatalogMapping.ToDto).ToList());
    }
}

public sealed record AddPriceCommand(Guid PriceListId, Guid ProductId, Guid? VariantId, Guid UomId, decimal MinQuantity, decimal Price, DateOnly? ValidFrom, DateOnly? ValidTo)
    : ICommand<PriceListItemDto>;

public sealed class AddPriceHandler(ICatalogDbContext db) : ICommandHandler<AddPriceCommand, PriceListItemDto>
{
    public async Task<Result<PriceListItemDto>> Handle(AddPriceCommand command, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.Include(p => p.Items.Where(i => i.ProductId == command.ProductId))
            .FirstOrDefaultAsync(p => p.Id == command.PriceListId, cancellationToken);
        if (list is null)
        {
            return CatalogAppErrors.PriceListNotFound;
        }

        var check = await CatalogChecks.ProductUnitAsync(db, command.ProductId, command.VariantId, command.UomId, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var item = list.AddItem(command.ProductId, command.VariantId, command.UomId, command.MinQuantity, command.Price, command.ValidFrom, command.ValidTo);
        await db.SaveChangesAsync(cancellationToken);
        return CatalogMapping.ToDto(item);
    }
}

public sealed record ReplaceProductPricesCommand(Guid PriceListId, Guid ProductId, IReadOnlyList<PriceLine> Lines) : ICommand<IReadOnlyList<PriceListItemDto>>;

public sealed class ReplaceProductPricesHandler(ICatalogDbContext db) : ICommandHandler<ReplaceProductPricesCommand, IReadOnlyList<PriceListItemDto>>
{
    public async Task<Result<IReadOnlyList<PriceListItemDto>>> Handle(ReplaceProductPricesCommand command, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.Include(p => p.Items.Where(i => i.ProductId == command.ProductId))
            .FirstOrDefaultAsync(p => p.Id == command.PriceListId, cancellationToken);
        if (list is null)
        {
            return CatalogAppErrors.PriceListNotFound;
        }

        foreach (var line in command.Lines ?? [])
        {
            var check = await CatalogChecks.ProductUnitAsync(db, command.ProductId, line.VariantId, line.UomId, cancellationToken);
            if (check.IsFailure)
            {
                return check.Error;
            }
        }

        list.ReplaceItemsForProduct(command.ProductId, command.Lines ?? []);
        await db.SaveChangesAsync(cancellationToken);
        return list.Items.Where(i => i.ProductId == command.ProductId).Select(CatalogMapping.ToDto).ToList();
    }
}

public sealed record PriceQuoteQuery(Guid PriceListId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, DateOnly Date) : IQuery<PriceQuote>;

public sealed class PriceQuoteHandler(IPriceCalculator calculator) : IQueryHandler<PriceQuoteQuery, PriceQuote>
{
    public async Task<Result<PriceQuote>> Handle(PriceQuoteQuery query, CancellationToken cancellationToken)
    {
        var quote = await calculator.GetPriceAsync(query.PriceListId, query.ProductId, query.VariantId, query.UomId, query.Quantity, query.Date, cancellationToken);
        return quote is null ? CatalogAppErrors.PriceNotFound : quote;
    }
}

// ---------- Contract implementations ----------
public sealed class CatalogQueries(ICatalogDbContext db) : ICatalogQueries
{
    public async Task<IReadOnlyList<ProductInfo>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
    {
        var products = await db.Products.AsNoTracking().Include(p => p.Units).Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var categoryIds = products.Select(p => p.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var uomIds = products.SelectMany(p => p.Units.Select(u => u.UomId)).Distinct().ToList();
        var codes = await db.Uoms.AsNoTracking().Where(u => uomIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);
        return products.Select(p =>
        {
            var category = categories[p.CategoryId];
            return new ProductInfo(p.Id, p.Sku, p.NameEn, p.NameAr, p.Type.ToString(), p.IsStockable, p.BaseUomId,
                p.Units.Select(u => new ProductUnitInfo(u.UomId, codes.GetValueOrDefault(u.UomId, "?"), u.FactorToBase)).ToList(),
                p.Tracking.ToString(), p.ExpiryTracked, (p.CostingMethodOverride ?? category.DefaultCostingMethod).ToString(),
                p.PostingGroupOverride ?? category.PostingGroup, p.TaxCategory, p.IsActive);
        }).ToList();
    }
}

public sealed class PriceCalculator(ICatalogDbContext db) : IPriceCalculator
{
    public async Task<PriceQuote?> GetPriceAsync(Guid priceListId, Guid productId, Guid? variantId, Guid uomId, decimal quantity, DateOnly date, CancellationToken cancellationToken = default)
    {
        var list = await db.PriceLists.AsNoTracking().Include(p => p.Items.Where(i => i.ProductId == productId))
            .FirstOrDefaultAsync(p => p.Id == priceListId, cancellationToken);
        if (list is null || !list.IsValidOn(date))
        {
            return null;
        }

        var product = await db.Products.AsNoTracking().Include(p => p.Units).FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null || product.Units.All(u => u.UomId != uomId))
        {
            return null;
        }

        var requestedFactor = product.FactorOf(uomId);
        var baseQuantity = UomConversion.ToBase(quantity, requestedFactor);
        var candidates = list.Items
            .Where(i => i.IsValidOn(date) && (i.VariantId == variantId || i.VariantId == null) && product.Units.Any(u => u.UomId == i.UomId))
            .Select(i => new { Item = i, Factor = product.FactorOf(i.UomId) })
            .Where(c => UomConversion.ToBase(c.Item.MinQuantity, c.Factor) <= baseQuantity)
            .OrderByDescending(c => c.Item.VariantId is not null)      // variant-specific first
            .ThenByDescending(c => c.Item.UomId == uomId)                // same unit before converted
            .ThenByDescending(c => UomConversion.ToBase(c.Item.MinQuantity, c.Factor)) // highest volume tier
            .ToList();
        var best = candidates.FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        var converted = best.Item.UomId != uomId;
        var unitPrice = converted ? Math.Round(best.Item.Price * requestedFactor / best.Factor, 4, MidpointRounding.AwayFromZero) : best.Item.Price;
        return new PriceQuote(unitPrice, list.Currency, list.TaxInclusive, best.Item.Id, converted);
    }
}

/// <summary>Default until the Inventory module provides real usage information.</summary>
public sealed class NoProductUsageProbe : IProductUsageProbe
{
    public Task<bool> IsUsedAsync(Guid productId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

internal static class CatalogValidation
{
    public static bool IsCostingMethodOrNull(string? value) => value is null || ParseCosting(value) is not null;

    public static CostingMethod? ParseCosting(string? value) =>
        value is not null && Enum.TryParse<CostingMethod>(value, true, out var method) && Enum.IsDefined(method) ? method : null;
}

internal static class CatalogChecks
{
    public static async Task<bool> SkuTakenAsync(ICatalogDbContext db, string sku, CancellationToken ct) =>
        await db.Products.AnyAsync(p => p.Sku == sku || p.Variants.Any(v => v.Sku == sku), ct);

    public static async Task<bool> AnyBarcodeTakenAsync(ICatalogDbContext db, IReadOnlyCollection<string> barcodes, CancellationToken ct) =>
        barcodes.Count > 0 && await db.Products.AnyAsync(p => p.Barcodes.Any(b => barcodes.Contains(b.Barcode)), ct);

    public static async Task<Result> ReferencesAsync(ICatalogDbContext db, Guid categoryId, Guid? brandId, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
        {
            return CatalogAppErrors.CategoryNotFound;
        }

        if (brandId is { } brand && !await db.Brands.AnyAsync(b => b.Id == brand, ct))
        {
            return CatalogAppErrors.BrandNotFound;
        }

        return Result.Success();
    }

    public static Task<Product?> LoadProductAsync(ICatalogDbContext db, Guid id, CancellationToken ct) =>
        db.Products.Include(p => p.Units).Include(p => p.Barcodes).Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == id, ct);

    public static async Task<Result> ProductUnitAsync(ICatalogDbContext db, Guid productId, Guid? variantId, Guid uomId, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Units).Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        if (product.Units.All(u => u.UomId != uomId))
        {
            return CatalogErrors.UnknownUnit;
        }

        return variantId is { } v && product.Variants.All(x => x.Id != v) ? CatalogErrors.UnknownVariant : Result.Success();
    }
}

internal static class CatalogMapping
{
    public static UomDto ToDto(UnitOfMeasure u) => new(u.Id, u.Code, u.NameEn, u.NameAr, u.Class.ToString());

    public static CategoryDto ToDto(ProductCategory c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.ParentId, c.DefaultCostingMethod.ToString(), c.PostingGroup);

    public static PriceListDto ToDto(PriceList p) => new(p.Id, p.Code, p.Name, p.Currency, p.TaxInclusive, p.ValidFrom, p.ValidTo, p.IsActive, p.Items.Count);

    public static PriceListItemDto ToDto(PriceListItem i) => new(i.Id, i.ProductId, i.VariantId, i.UomId, i.MinQuantity, i.Price, i.ValidFrom, i.ValidTo);

    public static async Task<ProductDto> ToDtoAsync(ICatalogDbContext db, Product p, CancellationToken ct)
    {
        var uomIds = p.Units.Select(u => u.UomId).ToList();
        var codes = await db.Uoms.AsNoTracking().Where(u => uomIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Code, ct);
        return new ProductDto(p.Id, p.Sku, p.NameEn, p.NameAr, p.Type.ToString(), p.IsStockable, p.BaseUomId, p.CategoryId, p.BrandId,
            p.Tracking.ToString(), p.ExpiryTracked, p.TaxCategory, p.CostingMethodOverride?.ToString(), p.PostingGroupOverride, p.IsActive,
            p.Units.OrderBy(u => u.FactorToBase).Select(u => new ProductUnitDto(u.UomId, codes.GetValueOrDefault(u.UomId), u.FactorToBase, u.UomId == p.BaseUomId)).ToList(),
            p.Barcodes.Select(b => new ProductBarcodeDto(b.Id, b.Barcode, b.UomId, b.VariantId)).ToList(),
            p.Variants.Select(v => new ProductVariantDto(v.Id, v.Sku, v.AttributeValues())).ToList());
    }
}

public sealed class PriceListSnapshotQueries(ICatalogDbContext db) : IPriceListSnapshotQueries
{
    public async Task<PriceListSnapshot?> GetSnapshotAsync(Guid priceListId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var list = await db.PriceLists.AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == priceListId, cancellationToken);
        if (list is null || !list.IsValidOn(date))
        {
            return null;
        }

        var version = await GetVersionAsync(priceListId, cancellationToken) ?? string.Empty;
        var items = list.Items.Where(i => i.IsValidOn(date))
            .Select(i => new PriceListSnapshotItem(i.ProductId, i.VariantId, i.UomId, i.MinQuantity, i.Price)).ToList();
        var productIds = items.Select(i => i.ProductId).Distinct().ToList();
        var barcodes = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.IsActive)
            .SelectMany(p => p.Barcodes).Select(b => new BarcodeInfo(b.Barcode, b.ProductId, b.VariantId, b.UomId)).ToListAsync(cancellationToken);
        return new PriceListSnapshot(list.Id, list.Code, list.Currency, list.TaxInclusive, version, items, barcodes);
    }

    public async Task<string?> GetVersionAsync(Guid priceListId, CancellationToken cancellationToken = default)
    {
        var rowVersion = await db.PriceLists.AsNoTracking().Where(p => p.Id == priceListId)
            .Select(p => EF.Property<byte[]>(p, "RowVersion")).FirstOrDefaultAsync(cancellationToken);
        return rowVersion is null ? null : Convert.ToBase64String(rowVersion);
    }
}
