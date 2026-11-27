namespace Erp.Modules.Organization.Contracts;

/// <summary>Company (and optionally legal entity) details printed on report and document headers.</summary>
public sealed record Letterhead(string CompanyCode, string CompanyNameEn, string CompanyNameAr, string BaseCurrency, string? LegalEntityName, string? TaxRegistrationNo);

public sealed record BranchSummary(Guid Id, Guid LegalEntityId, string Code, string Name);

/// <summary>Organization reads for reporting and printing. Scoped to the active company.</summary>
public interface IOrganizationReportQueries
{
    /// <summary>Header of the active company; null when there is none. The legal entity must belong to the company to be included.</summary>
    Task<Letterhead?> GetLetterheadAsync(Guid? legalEntityId = null, CancellationToken cancellationToken = default);

    /// <summary>Branches of the active company; unknown ids are omitted.</summary>
    Task<IReadOnlyList<BranchSummary>> GetBranchesAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken = default);
}
