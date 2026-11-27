using Erp.Application.Messaging;
using Erp.Modules.Catalog.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Catalog.Application;

/// <summary>Resolves codes and runs the regular catalog commands, so imported products pass exactly the same rules as manual entry.</summary>
public sealed class CatalogImportService(ICatalogDbContext db, IDispatcher dispatcher) : ICatalogImportService
{
    public async Task<Result<Guid>> CreateProductAsync(ProductImportRequest request, CancellationToken cancellationToken = default)
    {
        var categoryCode = request.CategoryCode.Trim().ToUpperInvariant();
        var category = await db.Categories.AsNoTracking().Where(c => c.Code == categoryCode).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
        if (category is null)
        {
            return CatalogAppErrors.CategoryNotFound with { Message = $"Category '{request.CategoryCode}' was not found." };
        }

        var uomCode = request.BaseUomCode.Trim().ToUpperInvariant();
        var uom = await db.Uoms.AsNoTracking().Where(u => u.Code == uomCode).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
        if (uom is null)
        {
            return CatalogAppErrors.UomNotFound with { Message = $"Unit '{request.BaseUomCode}' was not found." };
        }

        Guid? brand = null;
        if (!string.IsNullOrWhiteSpace(request.BrandCode))
        {
            var brandCode = request.BrandCode.Trim().ToUpperInvariant();
            brand = await db.Brands.AsNoTracking().Where(b => b.Code == brandCode).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(cancellationToken);
            if (brand is null)
            {
                return CatalogAppErrors.BrandNotFound with { Message = $"Brand '{request.BrandCode}' was not found." };
            }
        }

        Guid? priceList = null;
        if (!string.IsNullOrWhiteSpace(request.PriceListCode))
        {
            var listCode = request.PriceListCode.Trim().ToUpperInvariant();
            priceList = await db.PriceLists.AsNoTracking().Where(p => p.Code == listCode).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);
            if (priceList is null)
            {
                return CatalogAppErrors.PriceListNotFound with { Message = $"Price list '{request.PriceListCode}' was not found." };
            }

            if (request.Price is null)
            {
                return Error.Validation("catalog.import-price-required", "A price is required when a price list is given.");
            }
        }
        else if (request.Price is not null)
        {
            return Error.Validation("catalog.import-price-list-required", "A price list is required when a price is given.");
        }

        var barcodes = string.IsNullOrWhiteSpace(request.Barcode) ? null : new[] { new NewProductBarcode(request.Barcode.Trim(), null) };
        var created = await dispatcher.Send(new CreateProductCommand(request.Sku, request.NameEn, request.NameAr, request.Type, uom.Value, category.Value, brand,
            request.Tracking, request.ExpiryTracked, request.TaxCategory, null, null, null, barcodes), cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (priceList is not null)
        {
            var price = await dispatcher.Send(new AddPriceCommand(priceList.Value, created.Value.Id, null, uom.Value, 0, request.Price!.Value, null, null), cancellationToken);
            if (price.IsFailure)
            {
                return price.Error;
            }
        }

        return created.Value.Id;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindProductIdsBySkuAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken = default)
    {
        var normalized = skus.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList();
        if (normalized.Count == 0)
        {
            return new Dictionary<string, Guid>();
        }

        var found = await db.Products.AsNoTracking().Where(p => normalized.Contains(p.Sku)).Select(p => new { p.Sku, p.Id }).ToListAsync(cancellationToken);
        return found.ToDictionary(p => p.Sku.ToUpperInvariant(), p => p.Id, StringComparer.OrdinalIgnoreCase);
    }
}
