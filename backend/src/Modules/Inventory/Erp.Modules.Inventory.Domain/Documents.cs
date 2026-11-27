using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Domain;

/// <summary>A stock line on an inventory document (stored as JSON on the document row).</summary>
public sealed class StockDocumentLine
{
    public Guid LineId { get; set; }

    public Guid ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid UomId { get; set; }

    /// <summary>In <see cref="UomId"/>. Adjustments: positive = gain, negative = loss.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Base-currency cost per base unit; required on opening stock, optional on gains (current cost used).</summary>
    public decimal? UnitCost { get; set; }

    public string? BatchNumber { get; set; }

    public DateOnly? ManufactureDate { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    public List<string> Serials { get; set; } = [];

    // Filled when posted / issued.
    public decimal? PostedBaseQuantity { get; set; }

    public decimal? PostedUnitCost { get; set; }

    public decimal? PostedTotalCost { get; set; }

    // Transfers only.
    public decimal ReceivedBaseQuantity { get; set; }

    public decimal LostBaseQuantity { get; set; }

    public List<string> ReceivedSerials { get; set; } = [];

    internal static List<StockDocumentLine> Validate(IEnumerable<StockDocumentLine> lines, bool signed)
    {
        var list = lines.ToList();
        if (list.Count == 0)
        {
            throw new DomainException(Error.Validation("inventory.lines-required", "At least one line is required."));
        }

        foreach (var line in list)
        {
            if (line.LineId == Guid.Empty)
            {
                line.LineId = SequentialGuid.New();
            }

            if (line.Quantity == 0 || (!signed && line.Quantity < 0))
            {
                throw new DomainException(Error.Validation("inventory.quantity-invalid", signed ? "Quantities must not be zero." : "Quantities must be positive."));
            }

            if (line.UnitCost is < 0)
            {
                throw new DomainException(Error.Validation("inventory.cost-invalid", "Unit cost cannot be negative."));
            }

            line.Serials = line.Serials.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        }

        if (list.Select(l => l.LineId).Distinct().Count() != list.Count)
        {
            throw new DomainException(Error.Validation("inventory.duplicate-line", "Line ids must be unique."));
        }

        return list;
    }
}

public enum OpeningStockStatus
{
    Draft = 1,
    InReview = 2,
    Approved = 3,
    Posted = 4,
    Cancelled = 5,
}

/// <summary>Opening stock: Draft → Review → Approval → Posting (Part 7.5).</summary>
[Audited]
public sealed class OpeningStockDocument : AggregateRoot, IWarehouseScoped
{
    private OpeningStockDocument()
    {
    }

    public OpeningStockDocument(Guid companyId, Guid legalEntityId, Guid warehouseId, string number, DateOnly date, IEnumerable<StockDocumentLine> lines)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        WarehouseId = warehouseId;
        Number = number;
        Date = date;
        Status = OpeningStockStatus.Draft;
        SetLines(lines);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public OpeningStockStatus Status { get; private set; }

    public List<StockDocumentLine> Lines { get; private set; } = [];

    public decimal TotalCost { get; private set; }

    public Guid? JournalId { get; private set; }

    public void SetLines(IEnumerable<StockDocumentLine> lines)
    {
        Require(OpeningStockStatus.Draft);
        var list = StockDocumentLine.Validate(lines, signed: false);
        if (list.Any(l => l.UnitCost is null))
        {
            throw new DomainException(InventoryErrors.CostRequired);
        }

        Lines = list;
    }

    public void Submit() => Transition(OpeningStockStatus.Draft, OpeningStockStatus.InReview);

    public void ReturnToDraft() => Transition(OpeningStockStatus.InReview, OpeningStockStatus.Draft);

    public void Approve() => Transition(OpeningStockStatus.InReview, OpeningStockStatus.Approved);

    public void Cancel()
    {
        if (Status is OpeningStockStatus.Posted or OpeningStockStatus.Cancelled)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        Status = OpeningStockStatus.Cancelled;
    }

    public void MarkPosted(decimal totalCost, Guid? journalId)
    {
        Require(OpeningStockStatus.Approved);
        Status = OpeningStockStatus.Posted;
        TotalCost = totalCost;
        JournalId = journalId;
    }

    private void Transition(OpeningStockStatus from, OpeningStockStatus to)
    {
        Require(from);
        Status = to;
    }

    private void Require(OpeningStockStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }
    }
}

public enum AdjustmentReason
{
    Damage = 1,
    Loss = 2,
    Found = 3,
    CountVariance = 4,
    Expiry = 5,
    Other = 6,
}

public enum AdjustmentStatus
{
    Draft = 1,
    Approved = 2,
    Posted = 3,
    Cancelled = 4,
}

/// <summary>Stock adjustment: positive lines are gains, negative lines losses. Draft → Approved → Posted.</summary>
[Audited]
public sealed class StockAdjustment : AggregateRoot, IWarehouseScoped
{
    private StockAdjustment()
    {
    }

    public StockAdjustment(Guid companyId, Guid legalEntityId, Guid warehouseId, string number, DateOnly date, AdjustmentReason reason, string? notes, IEnumerable<StockDocumentLine> lines, Guid? stockCountId = null)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        WarehouseId = warehouseId;
        Number = number;
        Date = date;
        Reason = reason;
        Notes = notes is { Length: > 500 } ? notes[..500] : notes;
        StockCountId = stockCountId;
        Status = AdjustmentStatus.Draft;
        Lines = StockDocumentLine.Validate(lines, signed: true);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public AdjustmentReason Reason { get; private set; }

    public string? Notes { get; private set; }

    public Guid? StockCountId { get; private set; }

    public AdjustmentStatus Status { get; private set; }

    public List<StockDocumentLine> Lines { get; private set; } = [];

    public decimal NetCost { get; private set; }

    public Guid? JournalId { get; private set; }

    public void SetLines(IEnumerable<StockDocumentLine> lines)
    {
        Require(AdjustmentStatus.Draft);
        Lines = StockDocumentLine.Validate(lines, signed: true);
    }

    public void Approve()
    {
        Require(AdjustmentStatus.Draft);
        Status = AdjustmentStatus.Approved;
    }

    public void Cancel()
    {
        if (Status is not (AdjustmentStatus.Draft or AdjustmentStatus.Approved))
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        Status = AdjustmentStatus.Cancelled;
    }

    public void MarkPosted(decimal netCost, Guid? journalId)
    {
        Require(AdjustmentStatus.Approved);
        Status = AdjustmentStatus.Posted;
        NetCost = netCost;
        JournalId = journalId;
    }

    private void Require(AdjustmentStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }
    }
}

public enum TransferStatus
{
    Draft = 1,
    Approved = 2,
    InTransit = 3,
    PartiallyReceived = 4,
    Completed = 5,
    Cancelled = 6,
}

/// <summary>
/// Stock transfer via the legal entity's transit warehouse: Draft → Approved → (issue) InTransit → (receive)
/// PartiallyReceived / Completed. Closing with a discrepancy writes the remainder off as an in-transit loss. Cost is carried.
/// </summary>
[Audited]
public sealed class StockTransfer : AggregateRoot, ICompanyScoped
{
    private StockTransfer()
    {
    }

    public StockTransfer(Guid companyId, Guid legalEntityId, Guid fromWarehouseId, Guid toWarehouseId, Guid transitWarehouseId, string number, DateOnly date, string? notes, IEnumerable<StockDocumentLine> lines)
        : base(SequentialGuid.New())
    {
        if (fromWarehouseId == toWarehouseId)
        {
            throw new DomainException(Error.Validation("inventory.same-warehouse", "Source and destination warehouses must differ."));
        }

        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        FromWarehouseId = fromWarehouseId;
        ToWarehouseId = toWarehouseId;
        TransitWarehouseId = transitWarehouseId;
        Number = number;
        Date = date;
        Notes = notes is { Length: > 500 } ? notes[..500] : notes;
        Status = TransferStatus.Draft;
        Lines = StockDocumentLine.Validate(lines, signed: false);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid FromWarehouseId { get; private set; }

    public Guid ToWarehouseId { get; private set; }

    public Guid TransitWarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public string? Notes { get; private set; }

    public TransferStatus Status { get; private set; }

    public List<StockDocumentLine> Lines { get; private set; } = [];

    public int ReceiptCount { get; private set; }

    public void Approve() => Transition(TransferStatus.Draft, TransferStatus.Approved);

    public void Cancel()
    {
        if (Status is not (TransferStatus.Draft or TransferStatus.Approved))
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        Status = TransferStatus.Cancelled;
    }

    public void MarkIssued(IReadOnlyDictionary<Guid, (decimal BaseQuantity, decimal UnitCost, decimal TotalCost)> issued)
    {
        Require(TransferStatus.Approved);
        foreach (var line in Lines)
        {
            var i = issued[line.LineId];
            line.PostedBaseQuantity = i.BaseQuantity;
            line.PostedUnitCost = i.UnitCost;
            line.PostedTotalCost = i.TotalCost;
        }

        Status = TransferStatus.InTransit;
    }

    public decimal OpenBaseQuantity(StockDocumentLine line) => (line.PostedBaseQuantity ?? 0) - line.ReceivedBaseQuantity - line.LostBaseQuantity;

    /// <summary>Validates a receipt and returns the receipt number (1-based).</summary>
    public int RecordReceipt(IReadOnlyDictionary<Guid, (decimal BaseQuantity, IReadOnlyList<string> Serials)> received)
    {
        if (Status is not (TransferStatus.InTransit or TransferStatus.PartiallyReceived))
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        foreach (var (lineId, r) in received)
        {
            var line = Lines.FirstOrDefault(l => l.LineId == lineId)
                ?? throw new DomainException(Error.Validation("inventory.line-not-found", "Unknown transfer line."));
            if (r.BaseQuantity <= 0 || r.BaseQuantity > OpenBaseQuantity(line))
            {
                throw new DomainException(Error.Validation("inventory.receipt-exceeds-issued", "Received quantity must be positive and not exceed the quantity in transit."));
            }

            line.ReceivedBaseQuantity += r.BaseQuantity;
            line.ReceivedSerials.AddRange(r.Serials);
        }

        ReceiptCount++;
        Status = Lines.All(l => OpenBaseQuantity(l) == 0) ? TransferStatus.Completed : TransferStatus.PartiallyReceived;
        return ReceiptCount;
    }

    /// <summary>Writes off whatever is still in transit and completes the transfer.</summary>
    public IReadOnlyList<(StockDocumentLine Line, decimal BaseQuantity)> CloseWithDiscrepancy()
    {
        if (Status is not (TransferStatus.InTransit or TransferStatus.PartiallyReceived))
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        var lost = Lines.Where(l => OpenBaseQuantity(l) > 0).Select(l => (l, OpenBaseQuantity(l))).ToList();
        foreach (var (line, quantity) in lost)
        {
            line.LostBaseQuantity += quantity;
        }

        Status = TransferStatus.Completed;
        return lost;
    }

    private void Transition(TransferStatus from, TransferStatus to)
    {
        Require(from);
        Status = to;
    }

    private void Require(TransferStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }
    }
}

public enum CountStatus
{
    Open = 1,
    Counting = 2,
    VarianceCalculated = 3,
    Approved = 4,
    Posted = 5,
    Cancelled = 6,
}

public sealed class StockCountLine
{
    public Guid LineId { get; set; }

    public Guid ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid? BatchId { get; set; }

    public string? BatchNumber { get; set; }

    public Guid BaseUomId { get; set; }

    public decimal SnapshotQuantity { get; set; }

    public decimal? CountedQuantity { get; set; }

    public decimal MovementsSinceSnapshot { get; set; }

    public decimal Variance { get; set; }
}

/// <summary>
/// Stock count in snapshot mode (OQ-INV-6): Open → snapshot (Counting) → counts → variance → approval → posting, which
/// generates and posts a stock adjustment. Movements keep flowing during the count. Quantities are in base units.
/// Lines left uncounted are ignored. Serial-tracked products are counted through adjustments, not counts.
/// </summary>
[Audited]
public sealed class StockCount : AggregateRoot, IWarehouseScoped
{
    private StockCount()
    {
    }

    public StockCount(Guid companyId, Guid legalEntityId, Guid warehouseId, string number, DateOnly date, string? notes)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        WarehouseId = warehouseId;
        Number = number;
        Date = date;
        Notes = notes is { Length: > 500 } ? notes[..500] : notes;
        Status = CountStatus.Open;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public string? Notes { get; private set; }

    public CountStatus Status { get; private set; }

    public DateTime? SnapshotAtUtc { get; private set; }

    public List<StockCountLine> Lines { get; private set; } = [];

    public Guid? AdjustmentId { get; private set; }

    public void TakeSnapshot(DateTime atUtc, IEnumerable<StockCountLine> lines)
    {
        Require(CountStatus.Open);
        SnapshotAtUtc = atUtc;
        Lines = lines.ToList();
        foreach (var line in Lines.Where(l => l.LineId == Guid.Empty))
        {
            line.LineId = SequentialGuid.New();
        }

        Status = CountStatus.Counting;
    }

    public void EnterCount(Guid productId, Guid? variantId, Guid? batchId, string? batchNumber, Guid baseUomId, decimal counted)
    {
        Require(CountStatus.Counting);
        if (counted < 0)
        {
            throw new DomainException(Error.Validation("inventory.quantity-invalid", "Counted quantity cannot be negative."));
        }

        var line = Lines.FirstOrDefault(l => l.ProductId == productId && l.VariantId == variantId && l.BatchId == batchId);
        if (line is null)
        {
            // Found stock that the snapshot did not know about.
            line = new StockCountLine { LineId = SequentialGuid.New(), ProductId = productId, VariantId = variantId, BatchId = batchId, BatchNumber = batchNumber, BaseUomId = baseUomId };
            Lines.Add(line);
        }

        line.CountedQuantity = counted;
    }

    public void ReopenCounting()
    {
        Require(CountStatus.VarianceCalculated);
        Status = CountStatus.Counting;
    }

    /// <param name="currentOnHand">Current on-hand per line id.</param>
    public void CalculateVariances(IReadOnlyDictionary<Guid, decimal> currentOnHand)
    {
        if (Status is not (CountStatus.Counting or CountStatus.VarianceCalculated or CountStatus.Approved))
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        if (Lines.All(l => l.CountedQuantity is null))
        {
            throw new DomainException(Error.Validation("inventory.nothing-counted", "Enter at least one count."));
        }

        foreach (var line in Lines)
        {
            var current = currentOnHand.GetValueOrDefault(line.LineId);
            line.MovementsSinceSnapshot = CountMath.MovementsSinceSnapshot(line.SnapshotQuantity, current);
            line.Variance = line.CountedQuantity is { } counted ? CountMath.Variance(counted, line.SnapshotQuantity, line.MovementsSinceSnapshot) : 0;
        }

        if (Status == CountStatus.Counting)
        {
            Status = CountStatus.VarianceCalculated;
        }
    }

    public void Approve()
    {
        Require(CountStatus.VarianceCalculated);
        Status = CountStatus.Approved;
    }

    public void Cancel()
    {
        if (Status is CountStatus.Posted or CountStatus.Cancelled)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        Status = CountStatus.Cancelled;
    }

    public void MarkPosted(Guid? adjustmentId)
    {
        Require(CountStatus.Approved);
        AdjustmentId = adjustmentId;
        Status = CountStatus.Posted;
    }

    private void Require(CountStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }
    }
}
