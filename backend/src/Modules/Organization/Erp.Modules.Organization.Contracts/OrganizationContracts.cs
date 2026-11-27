namespace Erp.Modules.Organization.Contracts;

public sealed record CompanySummary(Guid Id, string Code, string NameEn, string NameAr, string BaseCurrency);

public sealed record LegalEntitySummary(Guid Id, Guid CompanyId, string Name, string FunctionalCurrency);

/// <summary>Type is Physical, Transit, Production or Virtual.</summary>
public sealed record WarehouseSummary(Guid Id, Guid CompanyId, Guid LegalEntityId, Guid? BranchId, string Code, string Name, string Type, bool IsActive);

/// <summary>Read access to organization data for other modules.</summary>
public interface IOrganizationQueries
{
    Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken = default);

    /// <summary>Any legal entity of the current tenant, regardless of the active company (callers check company ownership).</summary>
    Task<LegalEntitySummary?> GetLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken = default);

    /// <summary>Warehouses of the current tenant by id, regardless of the active company (callers check company ownership).</summary>
    Task<IReadOnlyList<WarehouseSummary>> GetWarehousesAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken = default);

    /// <summary>The system transit warehouse of a legal entity (one per legal entity).</summary>
    Task<WarehouseSummary?> GetTransitWarehouseAsync(Guid legalEntityId, CancellationToken cancellationToken = default);
}

public static class OrganizationPermissions
{
    public const string CompanyRead = "organization.company.read";
    public const string CompanyManage = "organization.company.manage";
    public const string StructureRead = "organization.structure.read";
    public const string StructureManage = "organization.structure.manage";
}
