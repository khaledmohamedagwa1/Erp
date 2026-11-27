using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Sales.Domain;

public static class SalesErrors
{
    public static Error InvalidTransition(string document, string from, string action) =>
        Error.Conflict("sales.invalid-state-transition", $"A {document} in status {from} cannot be {action}.");

    public static readonly Error NoLines = Error.Validation("sales.lines-required", "At least one line is required.");
    public static readonly Error InvalidQuantity = Error.Validation("sales.invalid-quantity", "Quantities must be greater than zero.");
    public static readonly Error InvalidPrice = Error.Validation("sales.invalid-price", "Unit prices cannot be negative.");
    public static readonly Error InvalidDiscount = Error.Validation("sales.invalid-discount", "Discounts must be between 0 and 100 %.");
    public static readonly Error ReturnExceedsInvoiced = Error.Validation("sales.return-exceeds-invoiced", "The returned quantity exceeds what was invoiced and not yet returned.");
    public static readonly Error DeliveryExceedsOrdered = Error.Validation("sales.delivery-exceeds-ordered", "The quantity exceeds what is open on the sales order.");
    public static readonly Error InvoiceExceedsSource = Error.Validation("sales.invoice-exceeds-source", "The quantity exceeds what is open on the source line.");
    public static readonly Error LineNotFound = Error.NotFound("sales.line-not-found", "A referenced line was not found.");
}

/// <summary>Deterministic money arithmetic for sales lines (amounts rounded half away from zero to the currency's minor units).</summary>
public static class SalesMath
{
    public static decimal Round(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    /// <summary>Gross line amount before tax handling, the discount, and the amount the tax engine receives.</summary>
    public static (decimal LineAmount, decimal Discount, decimal AfterDiscount) LineAmounts(decimal quantity, decimal unitPrice, decimal discountPercent, int decimals)
    {
        if (quantity <= 0)
        {
            throw new DomainException(SalesErrors.InvalidQuantity);
        }

        if (unitPrice < 0)
        {
            throw new DomainException(SalesErrors.InvalidPrice);
        }

        if (discountPercent is < 0 or > 100)
        {
            throw new DomainException(SalesErrors.InvalidDiscount);
        }

        var lineAmount = Round(quantity * unitPrice, decimals);
        var discount = Round(lineAmount * discountPercent / 100m, decimals);
        return (lineAmount, discount, lineAmount - discount);
    }

    /// <summary>Proportional share of an amount for a partial quantity (returns, partial invoicing).</summary>
    public static decimal Prorate(decimal amount, decimal part, decimal whole, int decimals) =>
        whole == 0 ? 0 : part == whole ? amount : Round(amount * part / whole, decimals);
}

public enum CreditDecision
{
    Ok,
    Warn,
    RequireApproval,
    Block,
    OnHold,
}

/// <summary>Credit-limit rule (MR-8). A limit of 0 means "no limit configured"; a customer on hold is always refused.</summary>
public static class CreditCheck
{
    public static CreditDecision Evaluate(bool enabled, decimal creditLimit, bool onHold, string action, decimal exposureAfter)
    {
        if (onHold)
        {
            return CreditDecision.OnHold;
        }

        if (!enabled || creditLimit <= 0 || exposureAfter <= creditLimit)
        {
            return CreditDecision.Ok;
        }

        return action switch
        {
            "Warn" => CreditDecision.Warn,
            "Block" => CreditDecision.Block,
            _ => CreditDecision.RequireApproval,
        };
    }
}

public sealed class LineTax
{
    private LineTax()
    {
    }

    public LineTax(Guid taxCodeId, string code, decimal rate, decimal baseAmount, decimal amount)
    {
        TaxCodeId = taxCodeId;
        Code = code;
        Rate = rate;
        Base = baseAmount;
        Amount = amount;
    }

    public Guid TaxCodeId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public decimal Rate { get; private set; }

    public decimal Base { get; private set; }

    public decimal Amount { get; private set; }
}

/// <summary>
/// One document line, shared by every sales document. Amounts are in the document currency; quantities in the line's unit
/// with <see cref="BaseQuantity"/> in the product's base unit. Progress fields (delivered/invoiced/returned) are in the line unit.
/// </summary>
public sealed class SalesLine
{
    private SalesLine()
    {
    }

    public SalesLine(
        Guid productId,
        Guid? variantId,
        string sku,
        string description,
        Guid uomId,
        decimal quantity,
        decimal baseQuantity,
        decimal unitPrice,
        decimal discountPercent,
        bool isStockable,
        bool isService,
        string postingGroup,
        Guid? sourceLineUid)
    {
        if (quantity <= 0 || baseQuantity <= 0)
        {
            throw new DomainException(SalesErrors.InvalidQuantity);
        }

        Uid = SequentialGuid.New();
        ProductId = productId;
        VariantId = variantId;
        Sku = sku;
        Description = description;
        UomId = uomId;
        Quantity = quantity;
        BaseQuantity = baseQuantity;
        UnitPrice = unitPrice;
        DiscountPercent = discountPercent;
        IsStockable = isStockable;
        IsService = isService;
        PostingGroup = postingGroup;
        SourceLineUid = sourceLineUid;
    }

    public Guid Uid { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? VariantId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public Guid UomId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercent { get; private set; }

    public decimal LineAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal GrossAmount { get; private set; }

    public bool IsStockable { get; private set; }

    public bool IsService { get; private set; }

    public string PostingGroup { get; private set; } = string.Empty;

    public Guid? TaxGroupId { get; private set; }

    public List<LineTax> Taxes { get; private set; } = [];

    /// <summary>Order line (delivery/invoice), delivery line (invoice in delivery-driven mode) or invoice line (return).</summary>
    public Guid? SourceLineUid { get; private set; }

    /// <summary>Delivery note line used by an invoice in delivery-driven mode.</summary>
    public Guid? DeliveryLineUid { get; private set; }

    public decimal DeliveredQuantity { get; private set; }

    public decimal InvoicedQuantity { get; private set; }

    public decimal ReturnedQuantity { get; private set; }

    /// <summary>Cost of the stock issued (functional currency) — taken from inventory, never from purchase prices.</summary>
    public decimal CostAmount { get; private set; }

    public Guid? CostMovementId { get; private set; }

    public string? BatchNumber { get; private set; }

    /// <summary>Comma-separated serial numbers (issued, or returned).</summary>
    public string? Serials { get; private set; }

    public bool Restock { get; private set; }

    public void SetAmounts(decimal lineAmount, decimal discountAmount, decimal net, decimal tax, decimal gross, Guid? taxGroupId, IEnumerable<LineTax> taxes)
    {
        LineAmount = lineAmount;
        DiscountAmount = discountAmount;
        NetAmount = net;
        TaxAmount = tax;
        GrossAmount = gross;
        TaxGroupId = taxGroupId;
        Taxes = taxes.ToList();
    }

    public void SetStockDetail(string? batchNumber, IEnumerable<string>? serials)
    {
        BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();
        var list = serials?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        Serials = list is { Count: > 0 } ? string.Join(',', list) : null;
    }

    public IReadOnlyList<string> SerialList => string.IsNullOrEmpty(Serials) ? [] : Serials.Split(',');

    public void SetDeliveryLine(Guid deliveryLineUid) => DeliveryLineUid = deliveryLineUid;

    /// <summary>Keeps a line's identity when it is re-priced (references to it from other documents stay valid).</summary>
    public void KeepIdentity(Guid uid) => Uid = uid;

    public void SetRestock(bool restock) => Restock = restock;

    public void RecordCost(decimal costAmount, Guid? movementId, string? batchNumber)
    {
        CostAmount = costAmount;
        CostMovementId = movementId;
        if (batchNumber is not null)
        {
            BatchNumber = batchNumber;
        }
    }

    public decimal OpenToDeliver => Quantity - DeliveredQuantity;

    public decimal OpenToInvoice => Quantity - InvoicedQuantity;

    public decimal OpenToReturn => InvoicedQuantity > 0 ? InvoicedQuantity - ReturnedQuantity : Quantity - ReturnedQuantity;

    public void AddDelivered(decimal quantity)
    {
        if (DeliveredQuantity + quantity > Quantity || DeliveredQuantity + quantity < 0)
        {
            throw new DomainException(SalesErrors.DeliveryExceedsOrdered);
        }

        DeliveredQuantity += quantity;
    }

    public void AddInvoiced(decimal quantity)
    {
        if (InvoicedQuantity + quantity > Quantity || InvoicedQuantity + quantity < 0)
        {
            throw new DomainException(SalesErrors.InvoiceExceedsSource);
        }

        InvoicedQuantity += quantity;
    }

    public void AddReturned(decimal quantity)
    {
        if (quantity > 0 && ReturnedQuantity + quantity > Quantity)
        {
            throw new DomainException(SalesErrors.ReturnExceedsInvoiced);
        }

        ReturnedQuantity = Math.Max(0, ReturnedQuantity + quantity);
    }

    /// <summary>Base-unit equivalent of a quantity in this line's unit.</summary>
    public decimal ToBase(decimal quantity) => Quantity == 0 ? 0 : Math.Round(BaseQuantity * quantity / Quantity, 6, MidpointRounding.AwayFromZero);
}

/// <summary>Fields shared by every sales document. Not mapped as an entity of its own; each document has its own table.</summary>
public abstract class SalesDocument : AggregateRoot, IBranchScoped, IWarehouseScoped
{
    protected SalesDocument()
    {
    }

    protected SalesDocument(Guid companyId, Guid legalEntityId, Guid? branchId, Guid warehouseId, Guid customerId, string customerPostingGroup,
        string currency, decimal exchangeRate, Guid? priceListId, bool taxInclusive, DateOnly documentDate, string? reference, string? notes)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        CustomerId = customerId;
        CustomerPostingGroup = customerPostingGroup;
        Currency = currency.ToUpperInvariant();
        ExchangeRate = exchangeRate <= 0 ? throw new DomainException(Error.Validation("sales.invalid-rate", "The exchange rate must be positive.")) : exchangeRate;
        PriceListId = priceListId;
        TaxInclusive = taxInclusive;
        DocumentDate = documentDate;
        Reference = reference?.Trim();
        Notes = notes?.Trim();
        Number = "D-" + Id.ToString("N").ToUpperInvariant();
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string CustomerPostingGroup { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public Guid? PriceListId { get; private set; }

    public bool TaxInclusive { get; private set; }

    public DateOnly DocumentDate { get; private set; }

    /// <summary>Draft number ("D-…") until the document is numbered; then the legal, gapless number.</summary>
    public string Number { get; private set; } = string.Empty;

    public bool IsNumbered { get; private set; }

    public string? Reference { get; private set; }

    public string? Notes { get; private set; }

    public decimal NetTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal GrossTotal { get; private set; }

    public List<SalesLine> Lines { get; private set; } = [];

    protected abstract string DocumentName { get; }

    protected void SetLinesInternal(IEnumerable<SalesLine> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0)
        {
            throw new DomainException(SalesErrors.NoLines);
        }

        Lines = list;
        RecalculateTotals();
    }

    public void RecalculateTotals()
    {
        NetTotal = Lines.Sum(l => l.NetAmount);
        TaxTotal = Lines.Sum(l => l.TaxAmount);
        GrossTotal = Lines.Sum(l => l.GrossAmount);
    }

    protected void SetPricing(decimal exchangeRate, Guid? priceListId, bool taxInclusive)
    {
        ExchangeRate = exchangeRate;
        PriceListId = priceListId;
        TaxInclusive = taxInclusive;
    }

    protected void AssignNumber(string number)
    {
        if (!IsNumbered)
        {
            Number = number;
            IsNumbered = true;
        }
    }

    public SalesLine Line(Guid uid) => Lines.FirstOrDefault(l => l.Uid == uid) ?? throw new DomainException(SalesErrors.LineNotFound);

    /// <summary>EF owns the lines as JSON: replace the list so the change tracker sees the modification.</summary>
    public void TouchLines() => Lines = [.. Lines];

    protected DomainException Invalid(string from, string action) => new(SalesErrors.InvalidTransition(DocumentName, from, action));
}

public enum QuotationStatus
{
    Draft = 1,
    Sent = 2,
    Accepted = 3,
    Rejected = 4,
    Expired = 5,
    Converted = 6,
}

[Audited]
public sealed class SalesQuotation : SalesDocument
{
    private SalesQuotation()
    {
    }

    public SalesQuotation(Guid companyId, Guid legalEntityId, Guid? branchId, Guid warehouseId, Guid customerId, string customerPostingGroup, string currency,
        decimal exchangeRate, Guid? priceListId, bool taxInclusive, DateOnly date, DateOnly validUntil, string? reference, string? notes, IEnumerable<SalesLine> lines)
        : base(companyId, legalEntityId, branchId, warehouseId, customerId, customerPostingGroup, currency, exchangeRate, priceListId, taxInclusive, date, reference, notes)
    {
        ValidUntil = validUntil < date ? throw new DomainException(Error.Validation("sales.invalid-validity", "Valid-until cannot be before the quotation date.")) : validUntil;
        Status = QuotationStatus.Draft;
        Revision = 1;
        SetLinesInternal(lines);
    }

    public QuotationStatus Status { get; private set; }

    public DateOnly ValidUntil { get; private set; }

    public int Revision { get; private set; }

    public Guid? ConvertedOrderId { get; private set; }

    protected override string DocumentName => "quotation";

    public void ReplaceLines(IEnumerable<SalesLine> lines, decimal exchangeRate, Guid? priceListId, bool taxInclusive)
    {
        if (Status != QuotationStatus.Draft)
        {
            throw Invalid(Status.ToString(), "edited");
        }

        SetPricing(exchangeRate, priceListId, taxInclusive);
        SetLinesInternal(lines);
    }

    public void Send(string number)
    {
        if (Status != QuotationStatus.Draft)
        {
            throw Invalid(Status.ToString(), "sent");
        }

        AssignNumber(number);
        Status = QuotationStatus.Sent;
    }

    /// <summary>Back to draft for changes; the revision number increases.</summary>
    public void Revise()
    {
        if (Status != QuotationStatus.Sent)
        {
            throw Invalid(Status.ToString(), "revised");
        }

        Status = QuotationStatus.Draft;
        Revision++;
    }

    public void Accept(DateOnly today)
    {
        ExpireIfDue(today);
        if (Status != QuotationStatus.Sent)
        {
            throw Invalid(Status.ToString(), "accepted");
        }

        Status = QuotationStatus.Accepted;
    }

    public void Reject()
    {
        if (Status != QuotationStatus.Sent)
        {
            throw Invalid(Status.ToString(), "rejected");
        }

        Status = QuotationStatus.Rejected;
    }

    public bool ExpireIfDue(DateOnly today)
    {
        if (Status == QuotationStatus.Sent && today > ValidUntil)
        {
            Status = QuotationStatus.Expired;
            return true;
        }

        return false;
    }

    public void MarkConverted(Guid orderId)
    {
        if (Status != QuotationStatus.Accepted)
        {
            throw Invalid(Status.ToString(), "converted to an order");
        }

        Status = QuotationStatus.Converted;
        ConvertedOrderId = orderId;
    }
}

public enum SalesOrderStatus
{
    Draft = 1,
    PendingApproval = 2,
    Confirmed = 3,
    PartiallyDelivered = 4,
    FullyDelivered = 5,
    Closed = 6,
    Cancelled = 7,
}

[Audited]
public sealed class SalesOrder : SalesDocument
{
    private SalesOrder()
    {
    }

    public SalesOrder(Guid companyId, Guid legalEntityId, Guid? branchId, Guid warehouseId, Guid customerId, string customerPostingGroup, string currency,
        decimal exchangeRate, Guid? priceListId, bool taxInclusive, DateOnly date, DateOnly? requestedDate, Guid? quotationId, string? reference, string? notes,
        IEnumerable<SalesLine> lines)
        : base(companyId, legalEntityId, branchId, warehouseId, customerId, customerPostingGroup, currency, exchangeRate, priceListId, taxInclusive, date, reference, notes)
    {
        RequestedDate = requestedDate;
        QuotationId = quotationId;
        Status = SalesOrderStatus.Draft;
        SetLinesInternal(lines);
    }

    public SalesOrderStatus Status { get; private set; }

    public DateOnly? RequestedDate { get; private set; }

    public Guid? QuotationId { get; private set; }

    public Guid? ApprovalInstanceId { get; private set; }

    /// <summary>Set when the credit check approved the order through the approval workflow (or a warning was accepted).</summary>
    public bool CreditApproved { get; private set; }

    public bool StockReserved { get; private set; }

    /// <summary>Closed with open quantities; later invoices for delivered quantities keep it closed.</summary>
    public bool ShortClosed { get; private set; }

    protected override string DocumentName => "sales order";

    public bool IsOpen => Status is SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyDelivered or SalesOrderStatus.FullyDelivered;

    public void ReplaceLines(IEnumerable<SalesLine> lines, decimal exchangeRate, Guid? priceListId, bool taxInclusive)
    {
        if (Status != SalesOrderStatus.Draft)
        {
            throw Invalid(Status.ToString(), "edited");
        }

        SetPricing(exchangeRate, priceListId, taxInclusive);
        SetLinesInternal(lines);
    }

    public void SubmitForApproval(Guid instanceId, bool forCredit)
    {
        if (Status != SalesOrderStatus.Draft)
        {
            throw Invalid(Status.ToString(), "submitted");
        }

        Status = SalesOrderStatus.PendingApproval;
        ApprovalInstanceId = instanceId;
        CreditApproved = forCredit;
    }

    public void Confirm(string number, bool stockReserved)
    {
        if (Status is not (SalesOrderStatus.Draft or SalesOrderStatus.PendingApproval))
        {
            throw Invalid(Status.ToString(), "confirmed");
        }

        AssignNumber(number);
        StockReserved = stockReserved;
        Status = SalesOrderStatus.Confirmed;
    }

    public void RejectApproval()
    {
        if (Status != SalesOrderStatus.PendingApproval)
        {
            throw Invalid(Status.ToString(), "rejected");
        }

        Status = SalesOrderStatus.Draft;
        ApprovalInstanceId = null;
        CreditApproved = false;
    }

    public void RegisterDelivery(Guid lineUid, decimal quantity)
    {
        // Reversing a delivery (negative quantity) is allowed on an order that the delivery had closed.
        if (!IsOpen && !(Status == SalesOrderStatus.Closed && quantity < 0))
        {
            throw Invalid(Status.ToString(), "delivered");
        }

        Line(lineUid).AddDelivered(quantity);
        TouchLines();
        RefreshStatus();
    }

    public void RegisterInvoice(Guid lineUid, decimal quantity)
    {
        if (!IsOpen && Status != SalesOrderStatus.Closed)
        {
            throw Invalid(Status.ToString(), "invoiced");
        }

        Line(lineUid).AddInvoiced(quantity);
        TouchLines();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (Status is SalesOrderStatus.Cancelled || ShortClosed)
        {
            return;
        }

        var delivered = Lines.All(l => l.DeliveredQuantity >= l.Quantity);
        var anyDelivered = Lines.Any(l => l.DeliveredQuantity > 0);
        var invoiced = Lines.All(l => l.InvoicedQuantity >= l.Quantity);
        Status = delivered && invoiced ? SalesOrderStatus.Closed
            : delivered ? SalesOrderStatus.FullyDelivered
            : anyDelivered ? SalesOrderStatus.PartiallyDelivered
            : SalesOrderStatus.Confirmed;
    }

    public void Cancel()
    {
        if (Status is not (SalesOrderStatus.Draft or SalesOrderStatus.PendingApproval or SalesOrderStatus.Confirmed) ||
            Lines.Any(l => l.DeliveredQuantity > 0 || l.InvoicedQuantity > 0))
        {
            throw Invalid(Status.ToString(), "cancelled");
        }

        Status = SalesOrderStatus.Cancelled;
    }

    /// <summary>Closes an order with open quantities (the caller releases remaining reservations).</summary>
    public void ShortClose()
    {
        if (!IsOpen)
        {
            throw Invalid(Status.ToString(), "short-closed");
        }

        ShortClosed = true;
        Status = SalesOrderStatus.Closed;
    }
}

public enum DeliveryStatus
{
    Draft = 1,
    Picked = 2,
    Posted = 3,
    Reversed = 4,
    Cancelled = 5,
}

[Audited]
public sealed class DeliveryNote : SalesDocument
{
    private DeliveryNote()
    {
    }

    public DeliveryNote(SalesOrder order, DateOnly date, string? reference, string? notes, IEnumerable<SalesLine> lines)
        : base(order.CompanyId, order.LegalEntityId, order.BranchId, order.WarehouseId, order.CustomerId, order.CustomerPostingGroup, order.Currency,
            order.ExchangeRate, order.PriceListId, order.TaxInclusive, date, reference, notes)
    {
        SalesOrderId = order.Id;
        Status = DeliveryStatus.Draft;
        SetLinesInternal(lines);
    }

    public DeliveryStatus Status { get; private set; }

    public Guid SalesOrderId { get; private set; }

    public Guid? JournalId { get; private set; }

    protected override string DocumentName => "delivery note";

    public void Pick(IReadOnlyDictionary<Guid, (string? Batch, IReadOnlyList<string>? Serials)> picks)
    {
        if (Status is not (DeliveryStatus.Draft or DeliveryStatus.Picked))
        {
            throw Invalid(Status.ToString(), "picked");
        }

        foreach (var (uid, pick) in picks)
        {
            Line(uid).SetStockDetail(pick.Batch, pick.Serials);
        }

        TouchLines();
        Status = DeliveryStatus.Picked;
    }

    public void MarkPosted(string number, Guid? journalId)
    {
        if (Status is not (DeliveryStatus.Draft or DeliveryStatus.Picked))
        {
            throw Invalid(Status.ToString(), "posted");
        }

        AssignNumber(number);
        JournalId = journalId;
        Status = DeliveryStatus.Posted;
    }

    public void Reverse()
    {
        if (Status != DeliveryStatus.Posted || Lines.Any(l => l.InvoicedQuantity > 0))
        {
            throw Invalid(Status.ToString(), "reversed");
        }

        Status = DeliveryStatus.Reversed;
    }

    public void Cancel()
    {
        if (Status is not (DeliveryStatus.Draft or DeliveryStatus.Picked))
        {
            throw Invalid(Status.ToString(), "cancelled");
        }

        Status = DeliveryStatus.Cancelled;
    }

    public void RegisterInvoice(Guid lineUid, decimal quantity)
    {
        if (Status != DeliveryStatus.Posted)
        {
            throw Invalid(Status.ToString(), "invoiced");
        }

        Line(lineUid).AddInvoiced(quantity);
        TouchLines();
    }
}

public enum SalesInvoiceStatus
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Posted = 4,
    PartiallyPaid = 5,
    Paid = 6,
    Closed = 7,
    Cancelled = 8,
}

[Audited]
public sealed class SalesInvoice : SalesDocument
{
    private SalesInvoice()
    {
    }

    public SalesInvoice(Guid companyId, Guid legalEntityId, Guid? branchId, Guid warehouseId, Guid customerId, string customerPostingGroup, string currency,
        decimal exchangeRate, Guid? priceListId, bool taxInclusive, DateOnly date, Guid? salesOrderId, Guid? deliveryNoteId, Guid? paymentTermsId,
        string? reference, string? notes, IEnumerable<SalesLine> lines)
        : base(companyId, legalEntityId, branchId, warehouseId, customerId, customerPostingGroup, currency, exchangeRate, priceListId, taxInclusive, date, reference, notes)
    {
        SalesOrderId = salesOrderId;
        DeliveryNoteId = deliveryNoteId;
        PaymentTermsId = paymentTermsId;
        Status = SalesInvoiceStatus.Draft;
        SetLinesInternal(lines);
    }

    public SalesInvoiceStatus Status { get; private set; }

    public Guid? SalesOrderId { get; private set; }

    public Guid? DeliveryNoteId { get; private set; }

    public Guid? PaymentTermsId { get; private set; }

    public Guid? ApprovalInstanceId { get; private set; }

    public bool CreditApproved { get; private set; }

    /// <summary>True when posting issued the stock (invoice-driven policy at posting time).</summary>
    public bool IssuedStock { get; private set; }

    public Guid? JournalId { get; private set; }

    public decimal OpenAmount { get; private set; }

    public DateOnly? CancelledOn { get; private set; }

    protected override string DocumentName => "sales invoice";

    public bool IsPosted => Status is SalesInvoiceStatus.Posted or SalesInvoiceStatus.PartiallyPaid or SalesInvoiceStatus.Paid or SalesInvoiceStatus.Closed;

    public void ReplaceLines(IEnumerable<SalesLine> lines, decimal exchangeRate, Guid? priceListId, bool taxInclusive)
    {
        if (Status != SalesInvoiceStatus.Draft)
        {
            throw Invalid(Status.ToString(), "edited");
        }

        SetPricing(exchangeRate, priceListId, taxInclusive);
        SetLinesInternal(lines);
    }

    /// <summary>Re-applies server pricing just before posting (same lines, refreshed amounts).</summary>
    public void Reprice(decimal exchangeRate, IEnumerable<SalesLine> lines)
    {
        if (Status != SalesInvoiceStatus.Approved)
        {
            throw Invalid(Status.ToString(), "repriced");
        }

        SetPricing(exchangeRate, PriceListId, TaxInclusive);
        SetLinesInternal(lines);
    }

    public void SubmitForApproval(Guid instanceId, bool forCredit)
    {
        if (Status != SalesInvoiceStatus.Draft)
        {
            throw Invalid(Status.ToString(), "submitted");
        }

        Status = SalesInvoiceStatus.PendingApproval;
        ApprovalInstanceId = instanceId;
        CreditApproved = forCredit;
    }

    public void Approve()
    {
        if (Status is not (SalesInvoiceStatus.Draft or SalesInvoiceStatus.PendingApproval))
        {
            throw Invalid(Status.ToString(), "approved");
        }

        Status = SalesInvoiceStatus.Approved;
    }

    public void RejectApproval()
    {
        if (Status != SalesInvoiceStatus.PendingApproval)
        {
            throw Invalid(Status.ToString(), "rejected");
        }

        Status = SalesInvoiceStatus.Draft;
        ApprovalInstanceId = null;
        CreditApproved = false;
    }

    public void MarkPosted(string number, Guid journalId, bool issuedStock)
    {
        if (Status != SalesInvoiceStatus.Approved)
        {
            throw Invalid(Status.ToString(), "posted");
        }

        AssignNumber(number);
        JournalId = journalId;
        IssuedStock = issuedStock;
        OpenAmount = GrossTotal;
        foreach (var line in Lines)
        {
            line.AddInvoiced(line.Quantity);
        }

        TouchLines();
        Status = SalesInvoiceStatus.Posted;
    }

    /// <summary>Payment status reported by Finance (InvoiceSettled).</summary>
    public void ApplySettlement(string status, decimal openAmount)
    {
        if (!IsPosted)
        {
            return;
        }

        OpenAmount = openAmount;
        Status = status switch
        {
            "Paid" => SalesInvoiceStatus.Paid,
            "PartiallyPaid" => SalesInvoiceStatus.PartiallyPaid,
            _ => SalesInvoiceStatus.Posted,
        };
    }

    public void Cancel(DateOnly date)
    {
        if (Status != SalesInvoiceStatus.Posted)
        {
            throw Invalid(Status.ToString(), "cancelled");
        }

        if (Lines.Any(l => l.ReturnedQuantity > 0))
        {
            throw new DomainException(Error.Conflict("sales.invoice-has-returns", "An invoice with returns cannot be cancelled; return the remaining quantities instead."));
        }

        CancelledOn = date;
        Status = SalesInvoiceStatus.Cancelled;
    }

    /// <summary>Drafts and approved invoices that were never posted can simply be discarded.</summary>
    public void Discard()
    {
        if (Status is not (SalesInvoiceStatus.Draft or SalesInvoiceStatus.Approved or SalesInvoiceStatus.PendingApproval))
        {
            throw Invalid(Status.ToString(), "discarded");
        }

        Status = SalesInvoiceStatus.Cancelled;
    }

    public void RegisterReturn(Guid lineUid, decimal quantity)
    {
        if (!IsPosted)
        {
            throw Invalid(Status.ToString(), "returned");
        }

        var line = Line(lineUid);
        if (quantity > line.OpenToReturn)
        {
            throw new DomainException(SalesErrors.ReturnExceedsInvoiced);
        }

        line.AddReturned(quantity);
        TouchLines();
    }
}

public enum SalesReturnStatus
{
    Draft = 1,
    Posted = 2,
    Cancelled = 3,
}

/// <summary>Credit note for returned (or credited) quantities of a posted invoice.</summary>
[Audited]
public sealed class SalesReturn : SalesDocument
{
    private SalesReturn()
    {
    }

    public SalesReturn(SalesInvoice invoice, DateOnly date, string reason, string? notes, IEnumerable<SalesLine> lines)
        : base(invoice.CompanyId, invoice.LegalEntityId, invoice.BranchId, invoice.WarehouseId, invoice.CustomerId, invoice.CustomerPostingGroup,
            invoice.Currency, invoice.ExchangeRate, invoice.PriceListId, invoice.TaxInclusive, date, invoice.Number, notes)
    {
        SalesInvoiceId = invoice.Id;
        Reason = Guard.MaxLength(Guard.NotEmpty(reason, "sales.reason-required", "Reason"), 200, "sales.reason-too-long", "Reason");
        Status = SalesReturnStatus.Draft;
        SetLinesInternal(lines);
    }

    public SalesReturnStatus Status { get; private set; }

    public Guid SalesInvoiceId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid? JournalId { get; private set; }

    protected override string DocumentName => "sales return";

    public void MarkPosted(string number, Guid journalId)
    {
        if (Status != SalesReturnStatus.Draft)
        {
            throw Invalid(Status.ToString(), "posted");
        }

        AssignNumber(number);
        JournalId = journalId;
        Status = SalesReturnStatus.Posted;
    }

    public void Cancel()
    {
        if (Status != SalesReturnStatus.Draft)
        {
            throw Invalid(Status.ToString(), "cancelled");
        }

        Status = SalesReturnStatus.Cancelled;
    }
}
