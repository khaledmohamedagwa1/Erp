using Erp.Application.Abstractions;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Manufacturing.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Manufacturing.Application;

public interface IManufacturingDbContext
{
    DbSet<WorkCenter> WorkCenters { get; }

    DbSet<Routing> Routings { get; }

    DbSet<BillOfMaterials> Boms { get; }

    DbSet<BomVersion> BomVersions { get; }

    DbSet<ProductionOrder> ProductionOrders { get; }

    DbSet<ItemPlanningParameters> PlanningParameters { get; }

    DbSet<MrpRun> MrpRuns { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class MfgErrors
{
    public static readonly Error ProductNotFound = Error.NotFound("manufacturing.product-not-found", "A product was not found or is not stockable.");
    public static readonly Error WarehouseInvalid = Error.Validation("manufacturing.warehouse-invalid",
        "Warehouses must be active physical or production warehouses of the same legal entity in this company.");
    public static readonly Error UomInvalid = Error.Validation("manufacturing.uom-invalid", "The unit is not defined for the product.");
    public static readonly Error WorkCenterNotFound = Error.NotFound("manufacturing.work-center-not-found", "The work center was not found.");
    public static readonly Error OrderNotFound = Error.NotFound("manufacturing.production-order-not-found", "The production order was not found.");
    public static readonly Error BomNotFound = Error.NotFound("manufacturing.bom-not-found", "The bill of materials was not found.");
    public static readonly Error VersionNotFound = Error.NotFound("manufacturing.bom-version-not-found", "The BOM version was not found.");
    public static readonly Error RoutingNotFound = Error.NotFound("manufacturing.routing-not-found", "The routing was not found.");
    public static readonly Error RunNotFound = Error.NotFound("manufacturing.mrp-run-not-found", "The MRP run was not found.");
    public static readonly Error DuplicateLines = Error.Validation("manufacturing.duplicate-lines", "Each component may appear only once per request.");
    public static readonly Error QuantityInvalid = Error.Validation("manufacturing.quantity-invalid", "Quantities must be positive.");
}

/// <summary>Cross-module lookups shared by the handlers.</summary>
public sealed class ManufacturingSupport(ICatalogQueries catalog, IOrganizationQueries organization, ITenantContext tenant, IClock clock)
{
    public const string SourceModule = "manufacturing";

    public Guid CompanyId => tenant.CompanyId ?? throw new InvalidOperationException("A company must be selected.");

    public DateOnly Today => DateOnly.FromDateTime(clock.UtcNow);

    public async Task<IReadOnlyDictionary<Guid, ProductInfo>> ProductsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, ProductInfo>() : (await catalog.GetProductsAsync(list, ct)).ToDictionary(p => p.Id);
    }

    /// <summary>Checks all products exist, are active and stockable.</summary>
    public async Task<Result<IReadOnlyDictionary<Guid, ProductInfo>>> StockableProductsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var found = await ProductsAsync(wanted, ct);
        return wanted.All(id => found.TryGetValue(id, out var p) && p.IsActive && p.IsStockable) ? Result.Success(found) : MfgErrors.ProductNotFound;
    }

    /// <summary>Base units per one unit of <paramref name="uomId"/>; null when the product does not use that unit.</summary>
    public static decimal? Factor(ProductInfo product, Guid uomId) =>
        uomId == product.BaseUomId ? 1m : product.Units.FirstOrDefault(u => u.UomId == uomId)?.FactorToBase;

    public async Task<Result<(Guid LegalEntityId, string Currency)>> ProductionWarehousesAsync(Guid componentWarehouseId, Guid outputWarehouseId, CancellationToken ct)
    {
        var ids = new[] { componentWarehouseId, outputWarehouseId }.Distinct().ToList();
        var warehouses = await organization.GetWarehousesAsync(ids, ct);
        if (warehouses.Count != ids.Count ||
            warehouses.Any(w => !w.IsActive || w.CompanyId != CompanyId || w.Type is not ("Physical" or "Production")) ||
            warehouses.Select(w => w.LegalEntityId).Distinct().Count() != 1)
        {
            return MfgErrors.WarehouseInvalid;
        }

        var legalEntity = await organization.GetLegalEntityAsync(warehouses[0].LegalEntityId, ct);
        return legalEntity is null ? MfgErrors.WarehouseInvalid : (legalEntity.Id, legalEntity.FunctionalCurrency);
    }

    public async Task<string> CurrencyAsync(Guid legalEntityId, CancellationToken ct) =>
        (await organization.GetLegalEntityAsync(legalEntityId, ct))?.FunctionalCurrency
        ?? throw new DomainException(MfgErrors.WarehouseInvalid);
}
