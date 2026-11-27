using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Procurement.Domain;

// ===================================================================== Purchase (supplier) invoice
public enum PurchaseInvoiceStatus
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Posted = 4,
    PartiallyPaid = 5,
    Paid = 6,
    Cancelled = 7,
}

public enum InvoiceLineKind
{
    /// <summary>Goods already received: relieves GRNI at the receipt value; the price difference is variance.</summary>
    Receipt = 1,

    /// <summary>Goods ordered but not received yet (invoice before receipt): Goods invoiced not received.</summary>
    Order = 2,

    /// <summary>Services/non-stock costs posted to an expense (or asset) account.</summary>
    Expense = 3,
}

public enum MatchStatus
{
    NotMatched = 0,
    Matched = 1,
    Exceptions = 2,
}

/// <summary>
/// Invoice line. Amounts are in the invoice currency; tax figures come from the tax engine. Match fields are set on submit;
/// posting fields (functional currency) are set when the invoice is posted.
/// </summary>
public sealed record PurchaseInvoiceLine(
    Guid LineId,
    InvoiceLineKind Kind,
    Guid? GoodsReceiptId,
    Guid? GoodsReceiptLineId,
    Guid? PurchaseOrderId,
    Guid? PurchaseOrderLineId,
    Guid? ProductId,
    Guid? AccountId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxGroupId,
    decimal NetAmount = 0,
    decimal RecoverableTax = 0,
    decimal NonRecoverableTax = 0,
    decimal OrderUnitPrice = 0,
    decimal PriceVariancePercent = 0,
    bool QuantityException = false,
    bool PriceException = false,
    decimal BaseNet = 0,
    decimal GrniBase = 0,
    decimal VarianceToInventory = 0,
    decimal VarianceToPpv = 0)
{
    public decimal TaxAmount => RecoverableTax + NonRecoverableTax;

    public decimal GrossAmount => NetAmount + TaxAmount;
}

[Audited]
public sealed class PurchaseInvoice : AggregateRoot, ICompanyScoped
{
    private PurchaseInvoice()
    {
    }

    public PurchaseInvoice(Guid companyId, Guid legalEntityId, Guid supplierId, string supplierInvoiceNumber, DateOnly invoiceDate, string currency,
        decimal exchangeRate, Guid? paymentTermsId, string? notes, IEnumerable<PurchaseInvoiceLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        SupplierId = supplierId;
        SupplierInvoiceNumber = Guard.MaxLength(Guard.NotEmpty(supplierInvoiceNumber, "procurement.supplier-invoice-number-required", "Supplier invoice number"),
            50, "procurement.supplier-invoice-number-too-long", "Supplier invoice number");
        InvoiceDate = invoiceDate;
        Currency = currency;
        ExchangeRate = exchangeRate;
        PaymentTermsId = paymentTermsId;
        Notes = LineRules.Trim(notes, 500);
        Status = PurchaseInvoiceStatus.Draft;
        SetLinesInternal(lines);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string SupplierInvoiceNumber { get; private set; } = string.Empty;

    /// <summary>Internal gapless number, assigned when the invoice is posted.</summary>
    public string? Number { get; private set; }

    public DateOnly InvoiceDate { get; private set; }

    public DateOnly? PostingDate { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public Guid? PaymentTermsId { get; private set; }

    public string? Notes { get; private set; }

    public PurchaseInvoiceStatus Status { get; private set; }

    public MatchStatus MatchStatus { get; private set; }

    public List<PurchaseInvoiceLine> Lines { get; private set; } = [];

    public decimal NetTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal GrandTotal { get; private set; }

    public decimal BaseTotal { get; private set; }

    public decimal OpenAmount { get; private set; }

    public Guid? JournalId { get; private set; }

    public string? CancelReason { get; private set; }

    public bool IsPosted => Status is PurchaseInvoiceStatus.Posted or PurchaseInvoiceStatus.PartiallyPaid or PurchaseInvoiceStatus.Paid;

    public void SetLines(IEnumerable<PurchaseInvoiceLine> lines)
    {
        Require(PurchaseInvoiceStatus.Draft);
        SetLinesInternal(lines);
        MatchStatus = MatchStatus.NotMatched;
    }

    /// <summary>Stores the match outcome (lines carry their match flags) and moves on according to the approval decision.</summary>
    public void Submit(IReadOnlyList<PurchaseInvoiceLine> matchedLines, bool approvalRequired)
    {
        Require(PurchaseInvoiceStatus.Draft);
        Lines = matchedLines.ToList();
        MatchStatus = Lines.Any(l => l.QuantityException || l.PriceException) ? MatchStatus.Exceptions : MatchStatus.Matched;
        if (MatchStatus == MatchStatus.Exceptions && !approvalRequired)
        {
            throw new DomainException(Error.Conflict("procurement.match-exceptions",
                "The invoice is outside the three-way match tolerance and no approval rule applies. Correct the invoice or configure an approval rule."));
        }

        Status = approvalRequired ? PurchaseInvoiceStatus.PendingApproval : PurchaseInvoiceStatus.Approved;
    }

    public void CompleteApproval(bool approved)
    {
        Require(PurchaseInvoiceStatus.PendingApproval);
        Status = approved ? PurchaseInvoiceStatus.Approved : PurchaseInvoiceStatus.Draft;
    }

    public void MarkPosted(string number, DateOnly postingDate, IReadOnlyList<PurchaseInvoiceLine> postedLines, decimal baseTotal, Guid journalId)
    {
        Require(PurchaseInvoiceStatus.Approved);
        Number = number;
        PostingDate = postingDate;
        Lines = postedLines.ToList();
        BaseTotal = baseTotal;
        JournalId = journalId;
        OpenAmount = GrandTotal;
        Status = PurchaseInvoiceStatus.Posted;
        Raise(new PurchaseInvoicePosted(Id, SupplierId, number, GrandTotal, Currency));
    }

    public void Cancel(string reason)
    {
        if (Status is PurchaseInvoiceStatus.Draft or PurchaseInvoiceStatus.PendingApproval or PurchaseInvoiceStatus.Approved)
        {
            Status = PurchaseInvoiceStatus.Cancelled;
            CancelReason = LineRules.Trim(reason, 500);
            return;
        }

        if (Status != PurchaseInvoiceStatus.Posted)
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase invoice", Status));
        }

        CancelReason = LineRules.Trim(reason, 500) ?? throw new DomainException(ProcurementErrors.ReasonRequired);
        Status = PurchaseInvoiceStatus.Cancelled;
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
            "Paid" => PurchaseInvoiceStatus.Paid,
            "PartiallyPaid" => PurchaseInvoiceStatus.PartiallyPaid,
            _ => PurchaseInvoiceStatus.Posted,
        };
    }

    private void SetLinesInternal(IEnumerable<PurchaseInvoiceLine> lines)
    {
        var list = LineRules.Require(lines);
        foreach (var line in list)
        {
            LineRules.Positive(line.Quantity);
            LineRules.NotNegative(line.UnitPrice);
            var valid = line.Kind switch
            {
                InvoiceLineKind.Receipt => line.GoodsReceiptId is not null && line.GoodsReceiptLineId is not null,
                InvoiceLineKind.Order => line.PurchaseOrderId is not null && line.PurchaseOrderLineId is not null,
                InvoiceLineKind.Expense => line.AccountId is not null,
                _ => false,
            };
            if (!valid)
            {
                throw new DomainException(Error.Validation("procurement.invoice-line-invalid",
                    "Receipt lines need a goods receipt line, order lines a purchase order line, expense lines an account."));
            }
        }

        Lines = list.Select(l => l.LineId == Guid.Empty ? l with { LineId = SequentialGuid.New() } : l).ToList();
        NetTotal = Lines.Sum(l => l.NetAmount);
        TaxTotal = Lines.Sum(l => l.TaxAmount);
        GrandTotal = NetTotal + TaxTotal;
    }

    private void Require(PurchaseInvoiceStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase invoice", Status));
        }
    }
}

// ===================================================================== Purchase return / debit note
public enum PurchaseReturnStatus
{
    Draft = 1,
    Approved = 2,
    Posted = 3,
    Cancelled = 4,
}

/// <summary>Return line (quantity in the receipt line's unit). Posting fields are in the functional currency.</summary>
public sealed record PurchaseReturnLine(
    Guid LineId,
    Guid GoodsReceiptLineId,
    decimal Quantity,
    Guid ProductId = default,
    Guid? VariantId = null,
    Guid UomId = default,
    decimal UninvoicedQuantity = 0,
    decimal InvoicedQuantity = 0,
    decimal CostBase = 0,
    decimal DebitNoteNet = 0,
    decimal DebitNoteTax = 0,
    List<string>? Serials = null,
    decimal GrniBase = 0);

[Audited]
public sealed class PurchaseReturn : AggregateRoot, ICompanyScoped
{
    private PurchaseReturn()
    {
    }

    public PurchaseReturn(Guid companyId, Guid legalEntityId, Guid supplierId, Guid goodsReceiptId, Guid warehouseId, DateOnly date, string? reason,
        IEnumerable<PurchaseReturnLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        SupplierId = supplierId;
        GoodsReceiptId = goodsReceiptId;
        WarehouseId = warehouseId;
        Date = date;
        Reason = LineRules.Trim(reason, 500);
        Status = PurchaseReturnStatus.Draft;
        var list = LineRules.Require(lines);
        list.ForEach(l => LineRules.Positive(l.Quantity));
        Lines = list.Select(l => l.LineId == Guid.Empty ? l with { LineId = SequentialGuid.New() } : l).ToList();
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid GoodsReceiptId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string? Number { get; private set; }

    public DateOnly Date { get; private set; }

    public string? Reason { get; private set; }

    public PurchaseReturnStatus Status { get; private set; }

    public List<PurchaseReturnLine> Lines { get; private set; } = [];

    public decimal TotalCost { get; private set; }

    /// <summary>Debit-note amount (invoice currency, gross) for the part of the return that had been invoiced.</summary>
    public decimal DebitNoteTotal { get; private set; }

    public string? DebitNoteCurrency { get; private set; }

    public Guid? JournalId { get; private set; }

    public void Approve() => Move(PurchaseReturnStatus.Draft, PurchaseReturnStatus.Approved);

    public void Cancel()
    {
        if (Status is not (PurchaseReturnStatus.Draft or PurchaseReturnStatus.Approved))
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase return", Status));
        }

        Status = PurchaseReturnStatus.Cancelled;
    }

    public void MarkPosted(string number, IReadOnlyList<PurchaseReturnLine> postedLines, decimal debitNoteTotal, string? debitNoteCurrency, Guid? journalId)
    {
        Move(PurchaseReturnStatus.Approved, PurchaseReturnStatus.Posted);
        Number = number;
        Lines = postedLines.ToList();
        TotalCost = Lines.Sum(l => l.CostBase);
        DebitNoteTotal = debitNoteTotal;
        DebitNoteCurrency = debitNoteCurrency;
        JournalId = journalId;
    }

    private void Move(PurchaseReturnStatus from, PurchaseReturnStatus to)
    {
        if (Status != from)
        {
            throw new DomainException(ProcurementErrors.InvalidState("purchase return", Status));
        }

        Status = to;
    }
}

// ===================================================================== Landed cost
public enum LandedCostStatus
{
    Draft = 1,
    Posted = 2,
    Cancelled = 3,
}

public sealed record LandedCostCharge(Guid LineId, string Description, decimal Amount, Guid? TaxGroupId);

/// <summary>Share of the charges for one receipt line, in the functional currency.</summary>
public sealed record LandedCostAllocation(
    Guid GoodsReceiptId,
    Guid GoodsReceiptLineId,
    Guid ReceiptMovementId,
    Guid ProductId,
    Guid WarehouseId,
    decimal Quantity,
    decimal Value,
    decimal AllocatedBase,
    decimal AppliedToInventory = 0,
    decimal ToVariance = 0);

[Audited]
public sealed class LandedCost : AggregateRoot, ICompanyScoped
{
    private LandedCost()
    {
    }

    public LandedCost(Guid companyId, Guid legalEntityId, Guid? supplierId, string? supplierReference, DateOnly date, string currency, decimal exchangeRate,
        AllocationBasis basis, IEnumerable<LandedCostCharge> charges, IEnumerable<Guid> goodsReceiptIds)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        SupplierId = supplierId;
        SupplierReference = LineRules.Trim(supplierReference, 50);
        Date = date;
        Currency = currency;
        ExchangeRate = exchangeRate;
        Basis = basis;
        Status = LandedCostStatus.Draft;
        var list = LineRules.Require(charges);
        list.ForEach(c => LineRules.Positive(c.Amount));
        Charges = list.Select(c => c.LineId == Guid.Empty ? c with { LineId = SequentialGuid.New() } : c).ToList();
        GoodsReceiptIds = LineRules.Require(goodsReceiptIds).Distinct().ToList();
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    /// <summary>Supplier to pay (creates a payable); null books the charges against Landed cost clearing.</summary>
    public Guid? SupplierId { get; private set; }

    public string? SupplierReference { get; private set; }

    public string? Number { get; private set; }

    public DateOnly Date { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public AllocationBasis Basis { get; private set; }

    public LandedCostStatus Status { get; private set; }

    public List<LandedCostCharge> Charges { get; private set; } = [];

    public List<Guid> GoodsReceiptIds { get; private set; } = [];

    public List<LandedCostAllocation> Allocations { get; private set; } = [];

    public decimal ChargesTotal => Charges.Sum(c => c.Amount);

    public decimal GrossTotal { get; private set; }

    public Guid? JournalId { get; private set; }

    public void Cancel()
    {
        if (Status != LandedCostStatus.Draft)
        {
            throw new DomainException(ProcurementErrors.InvalidState("landed cost", Status));
        }

        Status = LandedCostStatus.Cancelled;
    }

    public void MarkPosted(string number, IReadOnlyList<LandedCostAllocation> allocations, decimal grossTotal, Guid journalId)
    {
        if (Status != LandedCostStatus.Draft)
        {
            throw new DomainException(ProcurementErrors.InvalidState("landed cost", Status));
        }

        Number = number;
        Allocations = allocations.ToList();
        GrossTotal = grossTotal;
        JournalId = journalId;
        Status = LandedCostStatus.Posted;
    }
}

public sealed record GoodsReceiptPosted(Guid GoodsReceiptId, Guid PurchaseOrderId, Guid SupplierId, decimal TotalBaseValue) : IDomainEvent;

public sealed record PurchaseInvoicePosted(Guid PurchaseInvoiceId, Guid SupplierId, string Number, decimal GrandTotal, string Currency) : IDomainEvent;
