namespace Erp.Modules.Sales.Contracts;

public static class SalesPermissions
{
    public const string Read = "sales.read";
    public const string QuotationManage = "sales.quotation.manage";
    public const string OrderManage = "sales.order.manage";
    public const string DeliveryManage = "sales.delivery.manage";
    public const string DeliveryPost = "sales.delivery.post";
    public const string InvoiceManage = "sales.invoice.manage";
    public const string InvoicePost = "sales.invoice.post";
    public const string InvoiceCancel = "sales.invoice.cancel";
    public const string ReturnManage = "sales.return.manage";
    public const string ReturnPost = "sales.return.post";

    /// <summary>Required to give any line discount above 0 % (quotations, orders, invoices).</summary>
    public const string Discount = "sales.invoice.discount";

    /// <summary>Required to type a unit price instead of taking it from the price list.</summary>
    public const string PriceOverride = "sales.price.override";
}

/// <summary>Source document types used by Sales for inventory, journal and open-item references.</summary>
public static class SalesDocumentTypes
{
    public const string Quotation = "SalesQuotation";
    public const string Order = "SalesOrder";
    public const string Delivery = "DeliveryNote";
    public const string Invoice = "SalesInvoice";
    public const string Return = "SalesReturn";
}

/// <summary>Confirmed, not yet delivered sales-order quantity (MRP demand).</summary>
public sealed record OpenSalesDemand(
    Guid OrderId,
    string OrderNumber,
    Guid OrderLineId,
    Guid ProductId,
    Guid? VariantId,
    Guid WarehouseId,
    DateOnly RequiredDate,
    decimal OpenBaseQuantity);

public interface ISalesQueries
{
    /// <summary>Open (confirmed, undelivered) order quantities in base units, for the active company.</summary>
    Task<IReadOnlyList<OpenSalesDemand>> GetOpenDemandAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default);

    /// <summary>Open demand for every product of the active company (used by MRP).</summary>
    Task<IReadOnlyList<OpenSalesDemand>> GetAllOpenDemandAsync(Guid? warehouseId = null, CancellationToken cancellationToken = default);

    /// <summary>Gross value (functional currency) of confirmed order quantities not yet invoiced for the customer.</summary>
    Task<decimal> GetUninvoicedOrderExposureAsync(Guid customerId, CancellationToken cancellationToken = default);
}
