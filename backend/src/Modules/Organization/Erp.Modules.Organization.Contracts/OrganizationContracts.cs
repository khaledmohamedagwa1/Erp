namespace Erp.Modules.Organization.Contracts;

public sealed record CompanySummary(Guid Id, string Code, string NameEn, string NameAr, string BaseCurrency);

/// <summary>Read access to organization data for other modules.</summary>
public interface IOrganizationQueries
{
    Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken = default);
}

public static class OrganizationPermissions
{
    public const string CompanyRead = "organization.company.read";
    public const string CompanyManage = "organization.company.manage";
    public const string StructureRead = "organization.structure.read";
    public const string StructureManage = "organization.structure.manage";
}
