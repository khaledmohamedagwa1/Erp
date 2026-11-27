namespace Erp.Modules.Catalog.Contracts;

public sealed record ProductReportInfo(
    Guid ProductId,
    string Sku,
    string NameEn,
    string NameAr,
    Guid CategoryId,
    string CategoryCode,
    string CategoryNameEn,
    string CategoryNameAr,
    IReadOnlyList<ProductUnitInfo> Units);

/// <summary>Read access to catalog data for reporting (names, categories, units).</summary>
public interface ICatalogReportQueries
{
    /// <summary>Products of the active company with their category; unknown ids are omitted.</summary>
    Task<IReadOnlyList<ProductReportInfo>> GetProductReportInfoAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);

    /// <summary>Products in the category or any of its sub-categories.</summary>
    Task<IReadOnlyCollection<Guid>> GetProductIdsInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>Unit codes by unit id (for printing).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(IReadOnlyCollection<Guid> uomIds, CancellationToken cancellationToken = default);
}
