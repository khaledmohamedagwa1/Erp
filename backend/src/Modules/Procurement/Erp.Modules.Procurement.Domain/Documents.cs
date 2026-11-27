using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Procurement.Domain;

public static class ProcurementErrors
{
    public static Error InvalidState(string document, object status) =>
        Error.Conflict("invalid-state-transition", $"The {document} cannot do this while it is {status}.");

    public static readonly Error LinesRequired = Error.Validation("procurement.lines-required", "At least one line is required.");
    public static readonly Error QuantityPositive = Error.Validation("procurement.quantity-positive", "Quantities must be greater than zero.");
    public static readonly Error PriceNegative = Error.Validation("procurement.price-negative", "Prices cannot be negative.");
    public static readonly Error LineNotFound = Error.NotFound("procurement.line-not-found", "A referenced line was not found.");
    public static readonly Error OverReceipt = Error.Conflict("procurement.over-receipt", "The received quantity exceeds the ordered quantity plus tolerance.");
    public static readonly Error HasReceipts = Error.Conflict("procurement.order-has-receipts", "The order already has receipts.");
    public static readonly Error OverReturn = Error.Conflict("procurement.over-return", "The returned quantity exceeds what was received and not yet returned.");
    public static readonly Error AlreadyInvoiced = Error.Conflict("procurement.receipt-invoiced", "The receipt is (partly) invoiced; cancel the invoice first.");
    public static readonly Error ReasonRequired = Error.Validation("procurement.reason-required", "A reason is required.");
}

internal static class LineRules
{
    public static List<T> Require<T>(IEnumerable<T>? lines)
    {
        var list = lines?.ToList() ?? [];
        if (list.Count == 0)
        {
            throw new DomainException(ProcurementErrors.LinesRequired);
        }

        return list;
    }

    public static void Positive(decimal value)
    {
        if (value <= 0)
        {
            throw new DomainException(ProcurementErrors.QuantityPositive);
        }
    }

    public static void NotNegative(decimal value)
    {
        if (value < 0)
        {
            throw new DomainException(ProcurementErrors.PriceNegative);
        }
    }

    public static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}

// ===================================================================== Purchase request
public enum PurchaseRequestStatus
{
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    Rejected = 4,
    Ordered = 5,
    Closed = 6,
}

public sealed record RequestLine(Guid LineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, DateOnly? NeededBy, string? Notes);

[Audited]
public sealed class PurchaseRequest : AggregateRoot, IWarehouseScoped
{
    private PurchaseRequest()
    {
    }

    public PurchaseRequest(Guid companyId, Guid legalEntityId, Guid? warehouseId, string number, DateOnly date, string? notes, IEnumerable<RequestLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        WarehouseId = warehouseId;
        Number = number;
        Date = date;
        Notes = LineRules.Trim(notes, 500);
        Status = PurchaseRequestStatus.Draft;
        SetLinesInternal(lines);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public string? Notes { get; private set; }

    public PurchaseRequestStatus Status { get; private set; }

    public string? RejectionReason { get; private set; }

    public List<RequestLine> Lines { get; private set; } = [];

    public void SetLines(IEnumerable<RequestLine> lines)
    {
        Require(PurchaseRequestStatus.Draft);
        SetLinesInternal(lines);
    }

    /// <param name="approvalRequired">From the approval engine: when false the request is approved immediately.</param>
    public void Submit(bool approvalRequired)
    {
        Require(PurchaseRequestStatus.Draft);
        Status = approvalRequired ? PurchaseRequestStatus.Submitted : PurchaseRequestStatus.Approved;
        RejectionReason = null;
    }

    public void CompleteApproval(bool approved, string? reason)
    {
        Require(PurchaseRequestStatus.Submitted);
        Status = approved ? PurchaseRequestStatus.Approved : PurchaseRequestStatus.Rejected;
        RejectionReason = approved ? null : LineRules.Trim(reason, 500);
    }

    public void Revise()
    {
        Require(PurchaseRequestStatus.Rejected);
        Status = PurchaseRequestStatus.Draft;
    }

    public void MarkOrdered()
    {
        if (Status is not (PurchaseRequestStatus.Approved or PurchaseRequestStatus.Ordered))
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase request", Status));
        }

        Status = PurchaseRequestStatus.Ordered;
    }

    public void Close()
    {
        if (Status is not (PurchaseRequestStatus.Approved or PurchaseRequestStatus.Ordered))
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase request", Status));
        }

        Status = PurchaseRequestStatus.Closed;
    }

    private void SetLinesInternal(IEnumerable<RequestLine> lines)
    {
        var list = LineRules.Require(lines);
        list.ForEach(l => LineRules.Positive(l.Quantity));
        Lines = list.Select(l => l.LineId == Guid.Empty ? l with { LineId = SequentialGuid.New() } : l).ToList();
    }

    private void Require(PurchaseRequestStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase request", Status));
        }
    }
}

// ===================================================================== Purchase quotation
public enum QuotationStatus
{
    Draft = 1,
    Received = 2,
    Accepted = 3,
    Rejected = 4,
}

public sealed record QuotationLine(Guid LineId, Guid? RequestLineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal UnitPrice, int LeadTimeDays);

[Audited]
public sealed class PurchaseQuotation : AggregateRoot, ICompanyScoped
{
    private PurchaseQuotation()
    {
    }

    public PurchaseQuotation(Guid companyId, Guid? requestId, Guid supplierId, string number, string? supplierReference, DateOnly date, DateOnly? validUntil,
        string currency, IEnumerable<QuotationLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        RequestId = requestId;
        SupplierId = supplierId;
        Number = number;
        SupplierReference = LineRules.Trim(supplierReference, 100);
        Date = date;
        ValidUntil = validUntil;
        Currency = currency;
        Status = QuotationStatus.Draft;
        var list = LineRules.Require(lines);
        foreach (var line in list)
        {
            LineRules.Positive(line.Quantity);
            LineRules.NotNegative(line.UnitPrice);
        }

        Lines = list.Select(l => l.LineId == Guid.Empty ? l with { LineId = SequentialGuid.New() } : l).ToList();
    }

    public Guid CompanyId { get; private set; }

    public Guid? RequestId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public string? SupplierReference { get; private set; }

    public DateOnly Date { get; private set; }

    public DateOnly? ValidUntil { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public QuotationStatus Status { get; private set; }

    public List<QuotationLine> Lines { get; private set; } = [];

    public void MarkReceived() => Move(QuotationStatus.Draft, QuotationStatus.Received);

    public void Accept()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Received))
        {
            throw new DomainException(ProcurementErrors.InvalidState("quotation", Status));
        }

        Status = QuotationStatus.Accepted;
    }

    public void Reject()
    {
        if (Status is not (QuotationStatus.Draft or QuotationStatus.Received))
        {
            throw new DomainException(ProcurementErrors.InvalidState("quotation", Status));
        }

        Status = QuotationStatus.Rejected;
    }

    private void Move(QuotationStatus from, QuotationStatus to)
    {
        if (Status != from)
        {
            throw new DomainException(ProcurementErrors.InvalidState("quotation", Status));
        }

        Status = to;
    }
}

// ===================================================================== Purchase order
public enum PurchaseOrderStatus
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Sent = 4,
    PartiallyReceived = 5,
    FullyReceived = 6,
    Closed = 7,
    Cancelled = 8,
}

/// <summary>Order line. <see cref="ReceivedQuantity"/> is net of returns. Quantities in the line's unit.</summary>
public sealed record PurchaseOrderLine(
    Guid LineId,
    Guid ProductId,
    Guid? VariantId,
    Guid UomId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    Guid? TaxGroupId,
    decimal NetAmount,
    decimal TaxAmount,
    Guid? RequestLineId,
    DateOnly? ExpectedDate,
    decimal ReceivedQuantity = 0,
    decimal InvoicedQuantity = 0,
    decimal InvoicedNotReceivedQuantity = 0,
    decimal InvoicedNotReceivedBase = 0,
    decimal InvoicedNotReceivedUnitPrice = 0,
    decimal InvoicedNotReceivedRate = 0,
    Guid? InvoicedNotReceivedTaxGroupId = null)
{
    /// <summary>Unit price after the line discount (transaction currency).</summary>
    public decimal NetUnitPrice => Money.Cost(UnitPrice * (1 - (DiscountPercent / 100m)));

    public decimal OpenQuantity => Math.Max(0, Quantity - ReceivedQuantity);
}

[Audited]
public sealed class PurchaseOrder : AggregateRoot, IBranchScoped, IWarehouseScoped
{
    private PurchaseOrder()
    {
    }

    public PurchaseOrder(Guid companyId, Guid legalEntityId, Guid? branchId, Guid warehouseId, Guid supplierId, string number, DateOnly date, DateOnly? expectedDate,
        string currency, decimal exchangeRate, Guid? paymentTermsId, Guid? requestId, string? notes, IEnumerable<PurchaseOrderLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        SupplierId = supplierId;
        Number = number;
        Date = date;
        ExpectedDate = expectedDate;
        Currency = currency;
        ExchangeRate = exchangeRate;
        PaymentTermsId = paymentTermsId;
        RequestId = requestId;
        Notes = LineRules.Trim(notes, 500);
        Status = PurchaseOrderStatus.Draft;
        SetLinesInternal(lines);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public DateOnly? ExpectedDate { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>Functional-currency units per one unit of <see cref="Currency"/>. Receipts are valued at this rate.</summary>
    public decimal ExchangeRate { get; private set; }

    public Guid? PaymentTermsId { get; private set; }

    public Guid? RequestId { get; private set; }

    public string? Notes { get; private set; }

    public PurchaseOrderStatus Status { get; private set; }

    public bool WasSent { get; private set; }

    public List<PurchaseOrderLine> Lines { get; private set; } = [];

    public decimal NetTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal GrandTotal { get; private set; }

    public void SetLines(IEnumerable<PurchaseOrderLine> lines)
    {
        Require(PurchaseOrderStatus.Draft);
        SetLinesInternal(lines);
    }

    public void Submit(bool approvalRequired)
    {
        Require(PurchaseOrderStatus.Draft);
        Status = approvalRequired ? PurchaseOrderStatus.PendingApproval : PurchaseOrderStatus.Approved;
    }

    public void CompleteApproval(bool approved)
    {
        Require(PurchaseOrderStatus.PendingApproval);
        Status = approved ? PurchaseOrderStatus.Approved : PurchaseOrderStatus.Draft;
    }

    public void Send()
    {
        Require(PurchaseOrderStatus.Approved);
        Status = PurchaseOrderStatus.Sent;
        WasSent = true;
    }

    public bool CanReceive => Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived;

    public void Cancel()
    {
        if (Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.PendingApproval or PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent))
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase order", Status));
        }

        if (Lines.Any(l => l.ReceivedQuantity > 0 || l.InvoicedQuantity > 0))
        {
            throw new DomainException(ProcurementErrors.HasReceipts);
        }

        Status = PurchaseOrderStatus.Cancelled;
    }

    /// <summary>Short-close: no further receipts are expected.</summary>
    public void Close()
    {
        if (Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.FullyReceived))
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase order", Status));
        }

        Status = PurchaseOrderStatus.Closed;
    }

    public PurchaseOrderLine Line(Guid lineId) => Lines.FirstOrDefault(l => l.LineId == lineId) ?? throw new DomainException(ProcurementErrors.LineNotFound);

    /// <summary>Records received (positive) or reversed/returned (negative) quantity and recomputes the receipt status.</summary>
    public void ApplyReceipt(Guid lineId, decimal quantity, decimal quantityTolerancePercent)
    {
        if (quantity > 0 && !CanReceive)
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase order", Status));
        }

        var line = Line(lineId);
        var received = line.ReceivedQuantity + quantity;
        if (received < 0)
        {
            throw new DomainException(ProcurementErrors.OverReturn);
        }

        if (quantity > 0 && received > Money.Quantity(line.Quantity * (1 + (quantityTolerancePercent / 100m))))
        {
            throw new DomainException(ProcurementErrors.OverReceipt);
        }

        Replace(line with { ReceivedQuantity = received });
        RecomputeReceiptStatus();
    }

    public void ApplyInvoice(Guid lineId, decimal quantity)
    {
        var line = Line(lineId);
        Replace(line with { InvoicedQuantity = line.InvoicedQuantity + quantity });
    }

    /// <summary>Invoice-before-receipt: invoiced quantity and its functional value waiting for the goods.</summary>
    public void ApplyInvoiceBeforeReceipt(Guid lineId, decimal quantity, decimal baseValue, decimal unitPrice = 0, decimal rate = 0, Guid? taxGroupId = null)
    {
        var line = Line(lineId);
        Replace(line with
        {
            InvoicedQuantity = line.InvoicedQuantity + quantity,
            InvoicedNotReceivedQuantity = line.InvoicedNotReceivedQuantity + quantity,
            InvoicedNotReceivedBase = line.InvoicedNotReceivedBase + baseValue,
            InvoicedNotReceivedUnitPrice = unitPrice > 0 ? unitPrice : line.InvoicedNotReceivedUnitPrice,
            InvoicedNotReceivedRate = rate > 0 ? rate : line.InvoicedNotReceivedRate,
            InvoicedNotReceivedTaxGroupId = taxGroupId ?? line.InvoicedNotReceivedTaxGroupId,
        });
    }

    /// <summary>Takes received quantity out of the invoiced-not-received pool; returns its functional value (average).</summary>
    public decimal ConsumeInvoicedNotReceived(Guid lineId, decimal quantity)
    {
        var line = Line(lineId);
        if (line.InvoicedNotReceivedQuantity <= 0)
        {
            return 0;
        }

        var take = Math.Min(quantity, line.InvoicedNotReceivedQuantity);
        var value = take == line.InvoicedNotReceivedQuantity
            ? line.InvoicedNotReceivedBase
            : Money.Round(line.InvoicedNotReceivedBase * take / line.InvoicedNotReceivedQuantity);
        Replace(line with
        {
            InvoicedNotReceivedQuantity = line.InvoicedNotReceivedQuantity - take,
            InvoicedNotReceivedBase = line.InvoicedNotReceivedBase - value,
        });
        return value;
    }

    /// <summary>Puts quantity back into the invoiced-not-received pool (the receipt that consumed it was reversed).</summary>
    public void RestoreInvoicedNotReceived(Guid lineId, decimal quantity, decimal baseValue)
    {
        var line = Line(lineId);
        Replace(line with
        {
            InvoicedNotReceivedQuantity = line.InvoicedNotReceivedQuantity + quantity,
            InvoicedNotReceivedBase = line.InvoicedNotReceivedBase + baseValue,
        });
    }

    /// <summary>Undoes an invoice-before-receipt (invoice cancelled); refused once the goods were received against it.</summary>
    public void RevertInvoiceBeforeReceipt(Guid lineId, decimal quantity, decimal baseValue)
    {
        var line = Line(lineId);
        if (line.InvoicedNotReceivedQuantity < quantity)
        {
            throw new DomainException(Error.Conflict("procurement.invoice-goods-received",
                "Goods invoiced before receipt have since been received; reverse the receipt first."));
        }

        Replace(line with
        {
            InvoicedQuantity = line.InvoicedQuantity - quantity,
            InvoicedNotReceivedQuantity = line.InvoicedNotReceivedQuantity - quantity,
            InvoicedNotReceivedBase = line.InvoicedNotReceivedQuantity == quantity ? 0 : line.InvoicedNotReceivedBase - baseValue,
        });
    }

    private void RecomputeReceiptStatus()
    {
        if (Status is PurchaseOrderStatus.Closed or PurchaseOrderStatus.Cancelled)
        {
            return;
        }

        if (Lines.All(l => l.ReceivedQuantity == 0))
        {
            Status = WasSent ? PurchaseOrderStatus.Sent : PurchaseOrderStatus.Approved;
        }
        else
        {
            Status = Lines.All(l => l.ReceivedQuantity >= l.Quantity) ? PurchaseOrderStatus.FullyReceived : PurchaseOrderStatus.PartiallyReceived;
        }
    }

    private void Replace(PurchaseOrderLine line)
    {
        var index = Lines.FindIndex(l => l.LineId == line.LineId);
        Lines = Lines.Select((l, i) => i == index ? line : l).ToList();
    }

    private void SetLinesInternal(IEnumerable<PurchaseOrderLine> lines)
    {
        var list = LineRules.Require(lines);
        foreach (var line in list)
        {
            LineRules.Positive(line.Quantity);
            LineRules.NotNegative(line.UnitPrice);
            if (line.DiscountPercent is < 0 or > 100)
            {
                throw new DomainException(Error.Validation("procurement.discount-invalid", "Discount must be between 0 and 100 %."));
            }
        }

        Lines = list.Select(l => l.LineId == Guid.Empty ? l with { LineId = SequentialGuid.New() } : l).ToList();
        NetTotal = Lines.Sum(l => l.NetAmount);
        TaxTotal = Lines.Sum(l => l.TaxAmount);
        GrandTotal = NetTotal + TaxTotal;
    }

    private void Require(PurchaseOrderStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase order", Status));
        }
    }
}

// ===================================================================== Goods receipt
public enum GoodsReceiptStatus
{
    Draft = 1,
    Inspected = 2,
    Approved = 3,
    Posted = 4,
    Reversed = 5,
    Cancelled = 6,
}

/// <summary>
/// Receipt line. <see cref="AcceptedQuantity"/> goes into stock; rejected quantity is recorded only. After posting the line knows
/// its stock movement and functional value; returns consume not-yet-invoiced quantity first.
/// </summary>
public sealed record GoodsReceiptLine(
    Guid LineId,
    Guid PurchaseOrderLineId,
    Guid ProductId,
    Guid? VariantId,
    Guid UomId,
    decimal ReceivedQuantity,
    decimal AcceptedQuantity,
    decimal RejectedQuantity,
    string? BatchNumber,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    List<string>? Serials,
    decimal UnitPrice = 0,
    Guid? MovementId = null,
    decimal BaseQuantity = 0,
    decimal BaseValue = 0,
    string? CounterRole = null,
    decimal InvoicedQuantity = 0,
    decimal ReturnedUninvoicedQuantity = 0,
    decimal ReturnedInvoicedQuantity = 0,
    decimal InvoicedUnitPrice = 0,
    decimal InvoiceRate = 0,
    Guid? InvoiceTaxGroupId = null,
    decimal GrniClearedBase = 0)
{
    public decimal ReturnedQuantity => ReturnedUninvoicedQuantity + ReturnedInvoicedQuantity;

    /// <summary>Accepted quantity that can still be put on a supplier invoice.</summary>
    public decimal InvoiceableQuantity => Math.Max(0, AcceptedQuantity - InvoicedQuantity - ReturnedUninvoicedQuantity);

    public decimal ReturnableQuantity => Math.Max(0, AcceptedQuantity - ReturnedQuantity);

    /// <summary>Functional value of one unit (in the line's unit) as posted to inventory.</summary>
    public decimal UnitBaseValue => AcceptedQuantity == 0 ? 0 : BaseValue / AcceptedQuantity;
}

[Audited]
public sealed class GoodsReceipt : AggregateRoot, IWarehouseScoped
{
    /// <summary>Counter role of lines received against an earlier invoice (already invoiced, nothing to clear in GRNI).</summary>
    public const string GoodsInvoicedNotReceivedRole = "GoodsInvoicedNotReceived";

    private GoodsReceipt()
    {
    }

    public GoodsReceipt(Guid companyId, Guid legalEntityId, Guid purchaseOrderId, Guid supplierId, Guid warehouseId, string number, DateOnly date,
        string currency, decimal exchangeRate, string? notes, IEnumerable<GoodsReceiptLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        PurchaseOrderId = purchaseOrderId;
        SupplierId = supplierId;
        WarehouseId = warehouseId;
        Number = number;
        Date = date;
        Currency = currency;
        ExchangeRate = exchangeRate;
        Notes = LineRules.Trim(notes, 500);
        Status = GoodsReceiptStatus.Draft;
        SetLinesInternal(lines);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid PurchaseOrderId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public string? Notes { get; private set; }

    public string? InspectionNotes { get; private set; }

    public GoodsReceiptStatus Status { get; private set; }

    public List<GoodsReceiptLine> Lines { get; private set; } = [];

    public decimal TotalBaseValue { get; private set; }

    public Guid? InventoryJournalId { get; private set; }

    public void SetLines(IEnumerable<GoodsReceiptLine> lines)
    {
        Require(GoodsReceiptStatus.Draft);
        SetLinesInternal(lines);
    }

    /// <summary>Quality inspection: sets the accepted quantity per line (the rest is rejected).</summary>
    public void Inspect(IReadOnlyDictionary<Guid, decimal> acceptedByLine, string? notes)
    {
        Require(GoodsReceiptStatus.Draft);
        Lines = Lines.Select(l =>
        {
            if (!acceptedByLine.TryGetValue(l.LineId, out var accepted))
            {
                return l;
            }

            if (accepted < 0 || accepted > l.ReceivedQuantity)
            {
                throw new DomainException(Error.Validation("procurement.accepted-invalid", "Accepted quantity must be between 0 and the received quantity."));
            }

            return l with { AcceptedQuantity = accepted, RejectedQuantity = l.ReceivedQuantity - accepted };
        }).ToList();
        InspectionNotes = LineRules.Trim(notes, 500);
        Status = GoodsReceiptStatus.Inspected;
    }

    public void Approve()
    {
        if (Status is not (GoodsReceiptStatus.Draft or GoodsReceiptStatus.Inspected))
        {
            throw new DomainException(ProcurementErrors.InvalidState("goods receipt", Status));
        }

        if (Lines.All(l => l.AcceptedQuantity == 0))
        {
            throw new DomainException(Error.Validation("procurement.nothing-accepted", "Nothing was accepted on this receipt."));
        }

        Status = GoodsReceiptStatus.Approved;
    }

    public void Cancel()
    {
        if (Status is not (GoodsReceiptStatus.Draft or GoodsReceiptStatus.Inspected or GoodsReceiptStatus.Approved))
        {
            throw new DomainException(ProcurementErrors.InvalidState("goods receipt", Status));
        }

        Status = GoodsReceiptStatus.Cancelled;
    }

    public void MarkPosted(IReadOnlyList<GoodsReceiptLine> postedLines, Guid? journalId)
    {
        Require(GoodsReceiptStatus.Approved);
        Lines = postedLines.Select(l => l.CounterRole == GoodsInvoicedNotReceivedRole ? l with { InvoicedQuantity = l.AcceptedQuantity } : l).ToList();
        TotalBaseValue = Lines.Sum(l => l.BaseValue);
        InventoryJournalId = journalId;
        Status = GoodsReceiptStatus.Posted;
        Raise(new GoodsReceiptPosted(Id, PurchaseOrderId, SupplierId, TotalBaseValue));
    }

    public void Reverse()
    {
        Require(GoodsReceiptStatus.Posted);
        if (Lines.Any(l => (l.InvoicedQuantity > 0 && l.CounterRole != GoodsInvoicedNotReceivedRole) || l.ReturnedQuantity > 0))
        {
            throw new DomainException(ProcurementErrors.AlreadyInvoiced);
        }

        Status = GoodsReceiptStatus.Reversed;
    }

    public GoodsReceiptLine Line(Guid lineId) => Lines.FirstOrDefault(l => l.LineId == lineId) ?? throw new DomainException(ProcurementErrors.LineNotFound);

    /// <summary>
    /// Records invoiced quantity and returns the Goods-received-not-invoiced value it clears: the share of the receipt value for
    /// the still-invoiceable quantity covered (the remainder when it covers all of it, so the line clears to exactly zero).
    /// </summary>
    public decimal ApplyInvoice(Guid lineId, decimal quantity, decimal unitPrice, decimal rate, Guid? taxGroupId)
    {
        Require(GoodsReceiptStatus.Posted);
        var line = Line(lineId);
        LineRules.Positive(quantity);
        var relief = GrniRelief(line, Math.Min(quantity, line.InvoiceableQuantity));
        Replace(line with
        {
            InvoicedQuantity = line.InvoicedQuantity + quantity,
            InvoicedUnitPrice = unitPrice,
            InvoiceRate = rate,
            InvoiceTaxGroupId = taxGroupId,
            GrniClearedBase = line.GrniClearedBase + relief,
        });
        return relief;
    }

    /// <summary>Undoes an invoice (the invoice was cancelled).</summary>
    public void RevertInvoice(Guid lineId, decimal quantity, decimal relief)
    {
        var line = Line(lineId);
        Replace(line with
        {
            InvoicedQuantity = Math.Max(0, line.InvoicedQuantity - quantity),
            GrniClearedBase = line.GrniClearedBase - relief,
        });
    }

    /// <summary>
    /// Records a return; not-yet-invoiced quantity is returned first. Returns the (uninvoiced, invoiced) parts and the
    /// Goods-received-not-invoiced value cleared by the uninvoiced part.
    /// </summary>
    public (decimal Uninvoiced, decimal Invoiced, decimal GrniRelief) ApplyReturn(Guid lineId, decimal quantity)
    {
        Require(GoodsReceiptStatus.Posted);
        var line = Line(lineId);
        if (quantity <= 0 || quantity > line.ReturnableQuantity)
        {
            throw new DomainException(ProcurementErrors.OverReturn);
        }

        var uninvoiced = Math.Min(quantity, line.InvoiceableQuantity);
        var invoiced = quantity - uninvoiced;
        var relief = GrniRelief(line, uninvoiced);
        Replace(line with
        {
            ReturnedUninvoicedQuantity = line.ReturnedUninvoicedQuantity + uninvoiced,
            ReturnedInvoicedQuantity = line.ReturnedInvoicedQuantity + invoiced,
            GrniClearedBase = line.GrniClearedBase + relief,
        });
        return (uninvoiced, invoiced, relief);
    }

    private static decimal GrniRelief(GoodsReceiptLine line, decimal take)
    {
        if (take <= 0 || line.CounterRole == GoodsInvoicedNotReceivedRole)
        {
            return 0;
        }

        return take >= line.InvoiceableQuantity ? line.BaseValue - line.GrniClearedBase : Money.Round(line.UnitBaseValue * take);
    }

    private void Replace(GoodsReceiptLine line) => Lines = Lines.Select(l => l.LineId == line.LineId ? line : l).ToList();

    private void SetLinesInternal(IEnumerable<GoodsReceiptLine> lines)
    {
        var list = LineRules.Require(lines);
        Lines = list.Select(l =>
        {
            LineRules.Positive(l.ReceivedQuantity);
            var accepted = l.AcceptedQuantity <= 0 ? l.ReceivedQuantity : l.AcceptedQuantity;
            if (accepted > l.ReceivedQuantity)
            {
                throw new DomainException(Error.Validation("procurement.accepted-invalid", "Accepted quantity must be between 0 and the received quantity."));
            }

            return l with
            {
                LineId = l.LineId == Guid.Empty ? SequentialGuid.New() : l.LineId,
                AcceptedQuantity = accepted,
                RejectedQuantity = l.ReceivedQuantity - accepted,
                BatchNumber = LineRules.Trim(l.BatchNumber, 50),
            };
        }).ToList();
    }

    private void Require(GoodsReceiptStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(ProcurementErrors.InvalidState("goods receipt", Status));
        }
    }
}
