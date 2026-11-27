using Erp.Modules.Organization.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Organization.Application;

public sealed class OrganizationLookups(IOrganizationDbContext db) : IOrganizationLookups
{
    public async Task<WarehouseSummary?> FindWarehouseByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await db.Warehouses.AsNoTracking()
            .Where(w => w.Code.ToUpper() == normalized)
            .Select(w => new WarehouseSummary(w.Id, w.CompanyId, w.LegalEntityId, w.BranchId, w.Code, w.Name, w.Type.ToString(), w.IsActive))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
