namespace Erp.Modules.Catalog.Contracts;

public sealed record ProductUnitInfo(Guid UomId, string Code, decimal FactorToBase);

/// <summary>What other modules need to know about a product. Costing method and posting group are already resolved (product override, else category).</summary>
public sealed record ProductInfo(
    Guid Id,
    string Sku,
    string NameEn,
    string NameAr,
    string Type,
    bool IsStockable,
    Guid BaseUomId,
    IReadOnlyList<ProductUnitInfo> Units,
    string Tracking,
    bool ExpiryTracked,
    string CostingMethod,
    string PostingGroup,
    string TaxCategory,
    bool IsActive);

public sealed record PriceQuote(decimal UnitPrice, string Currency, bool TaxInclusive, Guid PriceListItemId, bool ConvertedFromOtherUnit);

public interface ICatalogQueries
{
    /// <summary>Products of the active company; unknown ids are omitted.</summary>
    Task<IReadOnlyList<ProductInfo>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);
}

public interface IPriceCalculator
{
    /// <summary>
    /// Best price for the quantity on the date: variant-specific prices first, then the highest applicable volume tier.
    /// If no price exists in the requested unit, a price in another unit of the product is converted. Null when none applies.
    /// </summary>
    Task<PriceQuote?> GetPriceAsync(Guid priceListId, Guid productId, Guid? variantId, Guid uomId, decimal quantity, DateOnly date, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells the catalog whether a product has been used by another module (stock, documents). Inventory replaces the
/// default "never used" implementation; used products cannot change their base unit.
/// </summary>
public interface IProductUsageProbe
{
    Task<bool> IsUsedAsync(Guid productId, CancellationToken cancellationToken = default);
}

public static class CatalogPermissions
{
    public const string Read = "catalog.read";
    public const string ProductManage = "catalog.product.manage";
    public const string PriceManage = "catalog.price.manage";
    public const string SetupManage = "catalog.setup.manage";
}
