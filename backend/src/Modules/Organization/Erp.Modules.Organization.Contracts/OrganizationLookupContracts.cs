namespace Erp.Modules.Organization.Contracts;

/// <summary>Code-based lookups for data import and other modules that receive business codes instead of ids.</summary>
public interface IOrganizationLookups
{
    /// <summary>Warehouse of the active company by code (case-insensitive), or null.</summary>
    Task<WarehouseSummary?> FindWarehouseByCodeAsync(string code, CancellationToken cancellationToken = default);
}
