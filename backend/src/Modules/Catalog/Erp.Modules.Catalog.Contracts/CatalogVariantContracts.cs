using Erp.SharedKernel.Results;

namespace Erp.Modules.Catalog.Contracts;

public sealed record VariantInfo(Guid Id, Guid ProductId, string Sku, IReadOnlyDictionary<string, string> Attributes);

/// <summary>Variants of products (attribute values such as Size or Colour) for other modules, e.g. variant-specific BOM lines.</summary>
public interface ICatalogVariantService
{
    /// <summary>Variants of the given products in the active company.</summary>
    Task<IReadOnlyList<VariantInfo>> GetVariantsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates one variant per combination of the attribute values (cartesian product) that does not exist yet, SKU
    /// <c>{product SKU}-{value}-{value}</c>; returns all variants of the product. Runs the catalog's regular command.
    /// </summary>
    Task<Result<IReadOnlyList<VariantInfo>>> GenerateVariantsAsync(Guid productId, IReadOnlyDictionary<string, IReadOnlyList<string>> attributeValues,
        CancellationToken cancellationToken = default);
}
