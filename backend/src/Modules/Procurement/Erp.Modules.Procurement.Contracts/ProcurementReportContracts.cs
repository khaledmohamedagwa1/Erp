using Erp.SharedKernel.Documents;

namespace Erp.Modules.Procurement.Contracts;

public sealed record PurchaseReportFilter(
    DateOnly From,
    DateOnly To,
    Guid? SupplierId = null,
    Guid? WarehouseId = null,
    Guid? BranchId = null,
    IReadOnlyCollection<Guid>? ProductIds = null);

/// <summary>
/// One line of a posted supplier invoice. <see cref="NetBase"/> is the functional-currency net; <see cref="UnitPrice"/> is in
/// the invoice currency and the line's unit. Warehouse and branch come from the linked receipt/order (null for expense lines).
/// </summary>
public sealed record PurchaseFact(
    Guid InvoiceId,
    string InvoiceNumber,
    string SupplierInvoiceNumber,
    DateOnly Date,
    Guid SupplierId,
    Guid? WarehouseId,
    Guid? BranchId,
    string Kind,
    Guid? ProductId,
    string? Description,
    Guid? UomId,
    decimal Quantity,
    decimal BaseQuantity,
    decimal UnitPrice,
    string Currency,
    decimal ExchangeRate,
    decimal NetBase,
    decimal TaxBase);

/// <summary>One line of a posted goods receipt (line unit) with how much of it was invoiced and returned.</summary>
public sealed record ReceiptFact(
    Guid ReceiptId,
    string ReceiptNumber,
    DateOnly Date,
    Guid SupplierId,
    Guid WarehouseId,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    Guid ProductId,
    Guid UomId,
    decimal ReceivedQuantity,
    decimal AcceptedQuantity,
    decimal RejectedQuantity,
    decimal InvoicedQuantity,
    decimal ReturnedQuantity,
    decimal UninvoicedQuantity,
    decimal BaseValue,
    decimal UnitBaseValue,
    string? BatchNumber,
    DateOnly? ExpiryDate);

/// <summary>Read access to procurement documents for reporting and document printing.</summary>
public interface IProcurementReportQueries
{
    /// <summary>Lines of posted (incl. partially/fully paid) supplier invoices dated in the range (posting date, else invoice date).</summary>
    Task<IReadOnlyList<PurchaseFact>> GetPurchaseFactsAsync(PurchaseReportFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Lines of posted goods receipts dated in the range.</summary>
    Task<IReadOnlyList<ReceiptFact>> GetReceiptFactsAsync(PurchaseReportFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Printable purchase order (approved or later, not cancelled); null otherwise.</summary>
    Task<PrintableDocument?> GetPurchaseOrderPrintAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default);

    /// <summary>Printable posted goods receipt; null otherwise.</summary>
    Task<PrintableDocument?> GetGoodsReceiptPrintAsync(Guid receiptId, CancellationToken cancellationToken = default);
}
