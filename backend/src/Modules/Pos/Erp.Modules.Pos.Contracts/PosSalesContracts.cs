namespace Erp.Modules.Pos.Contracts;

/// <summary>
/// One completed POS receipt line in functional currency (refund lines negative), for sales-by-salesperson reports and commissions.
/// <see cref="CostBase"/> is the cost of goods sold recorded for the line (refunds: the restocked cost).
/// </summary>
public sealed record PosSalesFact(
    string DocumentType,
    Guid ReceiptId,
    string Number,
    DateOnly Date,
    Guid LegalEntityId,
    Guid? BranchId,
    Guid WarehouseId,
    Guid CustomerId,
    Guid? SalespersonId,
    Guid ProductId,
    string Sku,
    decimal BaseQuantity,
    decimal NetBase,
    decimal TaxBase,
    decimal CostBase);

/// <summary>Read access to POS sales for reporting and commissions (active company).</summary>
public interface IPosSalesQueries
{
    Task<IReadOnlyList<PosSalesFact>> GetSalesFactsAsync(DateOnly from, DateOnly to, Guid? legalEntityId = null, Guid? salespersonId = null,
        CancellationToken cancellationToken = default);
}
