using Erp.Modules.Parties.Contracts;
using Erp.Modules.People.Contracts;
using Erp.Modules.Pos.Contracts;
using Erp.Modules.Pos.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Pos.Application;

/// <summary>
/// Salesperson of a POS sale: the one requested (must be an active salesperson), otherwise the customer's default salesperson, otherwise
/// the cashier's own employee record when it is a salesperson, otherwise none. Refunds inherit the sale's salesperson.
/// </summary>
public sealed class PosSalespersonResolver(IServiceProvider services, IPartyQueries parties)
{
    public static readonly Error Invalid = Error.Validation("pos.salesperson-invalid", "The salesperson must be an active employee flagged as salesperson.");

    public async Task<Result<Guid?>> ResolveAsync(Guid? requested, Guid customerId, Guid? cashierUserId, CancellationToken ct)
    {
        var employees = services.GetService<IEmployeeQueries>();
        if (requested is { } id)
        {
            return employees is not null && await employees.GetEmployeeAsync(id, ct) is { CanSell: true } ? id : Invalid;
        }

        if (employees is null)
        {
            return (Guid?)null;
        }

        if (await parties.GetCustomerAsync(customerId, ct) is { SalespersonId: { } fallback } && await employees.GetEmployeeAsync(fallback, ct) is { CanSell: true })
        {
            return fallback;
        }

        return cashierUserId is { } userId && await employees.FindByUserAsync(userId, ct) is { CanSell: true } cashier ? cashier.Id : (Guid?)null;
    }
}

/// <summary>Completed POS receipts and refunds as sales facts (functional currency: POS sells in the functional currency).</summary>
public sealed class PosSalesQueries(IPosDbContext db) : IPosSalesQueries
{
    public async Task<IReadOnlyList<PosSalesFact>> GetSalesFactsAsync(DateOnly from, DateOnly to, Guid? legalEntityId = null, Guid? salespersonId = null,
        CancellationToken cancellationToken = default)
    {
        var source = db.Receipts.AsNoTracking().Where(r => r.BusinessDate >= from && r.BusinessDate <= to);
        if (legalEntityId is { } le)
        {
            source = source.Where(r => r.LegalEntityId == le);
        }

        if (salespersonId is { } sp)
        {
            source = source.Where(r => r.SalespersonId == sp);
        }

        var receipts = await source.OrderBy(r => r.BusinessDate).ThenBy(r => r.Number).ToListAsync(cancellationToken);
        return receipts.SelectMany(r =>
        {
            var sign = r.Kind == ReceiptKind.Refund ? -1 : 1;
            var type = r.Kind == ReceiptKind.Refund ? PosDocumentTypes.Refund : PosDocumentTypes.Receipt;
            return r.Lines.Select(l => new PosSalesFact(type, r.Id, r.Number, r.BusinessDate, r.LegalEntityId, r.BranchId, r.WarehouseId, r.CustomerId,
                r.SalespersonId, l.ProductId, l.Sku, sign * l.BaseQuantity, sign * l.NetAmount, sign * l.TaxAmount, sign * l.CostAmount));
        }).ToList();
    }
}
