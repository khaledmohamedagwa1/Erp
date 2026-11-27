using Erp.SharedKernel.Documents;

namespace Erp.Modules.Sales.Contracts;

public sealed record SalesReportFilter(
    DateOnly From,
    DateOnly To,
    Guid? BranchId = null,
    Guid? WarehouseId = null,
    Guid? CustomerId = null,
    IReadOnlyCollection<Guid>? ProductIds = null);

/// <summary>
/// One posted sales document line in functional currency. Credit notes (returns) are negative.
/// <see cref="CostBase"/> is the cost of goods sold recorded for the line, rounded to cents as journalised (the restock cost for returned goods, zero when
/// the goods were not restocked). <see cref="DocumentType"/> is <see cref="SalesDocumentTypes.Invoice"/> or <see cref="SalesDocumentTypes.Return"/>.
/// </summary>
public sealed record SalesFact(
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    DateOnly Date,
    Guid? BranchId,
    Guid WarehouseId,
    Guid CustomerId,
    Guid ProductId,
    string Sku,
    string Description,
    bool IsService,
    decimal BaseQuantity,
    decimal NetBase,
    decimal TaxBase,
    decimal CostBase);

/// <summary>Read access to posted sales for reporting and document printing.</summary>
public interface ISalesReportQueries
{
    Task<IReadOnlyList<SalesFact>> GetSalesFactsAsync(SalesReportFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Printable posted invoice; null when it does not exist or is not posted.</summary>
    Task<PrintableDocument?> GetInvoicePrintAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Printable posted credit note (sales return); null when it does not exist or is not posted.</summary>
    Task<PrintableDocument?> GetReturnPrintAsync(Guid returnId, CancellationToken cancellationToken = default);
}
