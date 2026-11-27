using Erp.SharedKernel.Results;

namespace Erp.Modules.Catalog.Contracts;

/// <summary>
/// A product row from a data import. References are business codes (category, units, price list), resolved by the catalog.
/// <see cref="PriceListCode"/> and <see cref="Price"/> are optional and add one price in the base unit.
/// </summary>
public sealed record ProductImportRequest(
    string Sku,
    string NameEn,
    string NameAr,
    string Type,
    string CategoryCode,
    string BaseUomCode,
    string? BrandCode,
    string? Tracking,
    bool ExpiryTracked,
    string? TaxCategory,
    string? Barcode,
    string? PriceListCode,
    decimal? Price);

/// <summary>Command-style catalog operations for the Import module. Runs inside the caller's transaction and the catalog's own validation.</summary>
public interface ICatalogImportService
{
    /// <summary>Creates the product (and its optional price). Returns the new product id.</summary>
    Task<Result<Guid>> CreateProductAsync(ProductImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Product ids of the active company by SKU (case-insensitive key, upper-case in the result). Unknown SKUs are omitted.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindProductIdsBySkuAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken = default);
}
