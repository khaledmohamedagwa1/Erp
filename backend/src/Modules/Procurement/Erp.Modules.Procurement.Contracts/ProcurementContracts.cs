namespace Erp.Modules.Procurement.Contracts;

/// <summary>Open (ordered but not yet received) purchase-order quantity, in the product's base unit.</summary>
public sealed record OpenSupplyLine(
    Guid ProductId,
    Guid? VariantId,
    Guid WarehouseId,
    Guid PurchaseOrderId,
    Guid PurchaseOrderLineId,
    string PurchaseOrderNumber,
    Guid SupplierId,
    decimal OpenBaseQuantity,
    DateOnly? ExpectedDate);

/// <summary>Read access to procurement data for other modules (MRP in Manufacturing, "incoming" stock in Inventory).</summary>
public interface IProcurementQueries
{
    /// <summary>Open supply of approved/sent/partially received purchase orders of the active company.</summary>
    Task<IReadOnlyList<OpenSupplyLine>> GetOpenSupplyAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default);

    /// <summary>Open supply for every product of the active company (used by MRP).</summary>
    Task<IReadOnlyList<OpenSupplyLine>> GetAllOpenSupplyAsync(Guid? warehouseId = null, CancellationToken cancellationToken = default);
}

/// <summary>Source document types used by procurement for inventory movements, journals and open items.</summary>
public static class ProcurementDocumentTypes
{
    public const string GoodsReceipt = "GoodsReceipt";
    public const string PurchaseInvoice = "PurchaseInvoice";
    public const string PurchaseReturn = "PurchaseReturn";
    public const string LandedCost = "LandedCost";
}

public static class ProcurementPermissions
{
    public const string Read = "procurement.read";
    public const string RequestManage = "procurement.request.manage";
    public const string QuotationManage = "procurement.quotation.manage";
    public const string OrderManage = "procurement.order.manage";
    public const string ReceiptManage = "procurement.receipt.manage";
    public const string ReceiptPost = "procurement.receipt.post";
    public const string InvoiceManage = "procurement.invoice.manage";
    public const string InvoicePost = "procurement.invoice.post";
    public const string ReturnManage = "procurement.return.manage";
    public const string ReturnPost = "procurement.return.post";
    public const string LandedCostManage = "procurement.landed-cost.manage";
    public const string LandedCostPost = "procurement.landed-cost.post";
}

/// <summary>
/// A purchase invoice line as other modules see it. <see cref="BaseNet"/> is the posted cost in the functional currency
/// (net plus non-recoverable tax); zero before posting.
/// </summary>
public sealed record PurchaseInvoiceLineSummary(Guid LineId, string Kind, Guid? AccountId, Guid? ProductId, string? Description, decimal Quantity, decimal BaseNet);

public sealed record PurchaseInvoiceSummary(Guid Id, Guid LegalEntityId, Guid SupplierId, string? Number, string SupplierInvoiceNumber, DateOnly InvoiceDate, bool IsPosted,
    IReadOnlyList<PurchaseInvoiceLineSummary> Lines);

/// <summary>Purchase invoices for other modules (Fixed Assets validates the invoice an asset is capitalised from).</summary>
public interface IPurchaseInvoiceQueries
{
    /// <summary>An invoice of the active company, or null.</summary>
    Task<PurchaseInvoiceSummary?> GetPurchaseInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}
