using Erp.Modules.Parties.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Parties.Application;

public sealed class PartyReportQueries(IPartiesDbContext db) : IPartyReportQueries
{
    public async Task<IReadOnlyList<PartyDisplay>> GetCustomersAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken = default) =>
        customerIds.Count == 0
            ? []
            : await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
                .Select(c => new PartyDisplay(c.Id, "Customer", c.Code, c.NameEn, c.NameAr, c.TaxRegistrationNo)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PartyDisplay>> GetSuppliersAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken = default) =>
        supplierIds.Count == 0
            ? []
            : await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id))
                .Select(s => new PartyDisplay(s.Id, "Supplier", s.Code, s.NameEn, s.NameAr, s.TaxRegistrationNo)).ToListAsync(cancellationToken);
}
