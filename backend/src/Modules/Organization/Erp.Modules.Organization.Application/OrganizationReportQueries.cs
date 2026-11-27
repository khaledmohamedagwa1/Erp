using Erp.Application.Abstractions;
using Erp.Modules.Organization.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Organization.Application;

public sealed class OrganizationReportQueries(IOrganizationDbContext db, ITenantContext tenant) : IOrganizationReportQueries
{
    public async Task<Letterhead?> GetLetterheadAsync(Guid? legalEntityId = null, CancellationToken cancellationToken = default)
    {
        if (tenant.CompanyId is not { } companyId)
        {
            return null;
        }

        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return null;
        }

        // Legal entities are company-scoped: the query filter keeps another company's entity out.
        var legalEntity = legalEntityId is { } id
            ? await db.LegalEntities.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            : null;
        return new Letterhead(company.Code, company.NameEn, company.NameAr, company.BaseCurrency, legalEntity?.Name, legalEntity?.TaxRegistrationNo);
    }

    public async Task<IReadOnlyList<BranchSummary>> GetBranchesAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken = default) =>
        branchIds.Count == 0
            ? []
            : await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
                .Select(b => new BranchSummary(b.Id, b.LegalEntityId, b.Code, b.Name)).ToListAsync(cancellationToken);
}
