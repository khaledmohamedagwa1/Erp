using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Domain;

/// <summary>
/// What a value-only cost adjustment did to a FIFO layer: the amount and the quantity still in the layer at that moment. Reversing
/// the adjustment later removes exactly the share that is still in stock (amount × remaining now / remaining then); the share already
/// consumed went to cost of sales at the adjusted cost and is credited to purchase price variance.
/// </summary>
public sealed class CostLayerAdjustment : ICompanyScoped
{
    private CostLayerAdjustment()
    {
    }

    public CostLayerAdjustment(Guid companyId, Guid layerId, Guid movementId, decimal remainingAtAdjustment, decimal amount)
    {
        Id = SequentialGuid.New();
        CompanyId = companyId;
        LayerId = layerId;
        MovementId = movementId;
        RemainingAtAdjustment = remainingAtAdjustment;
        Amount = amount;
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid LayerId { get; private set; }

    public Guid MovementId { get; private set; }

    public decimal RemainingAtAdjustment { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>Share of the adjustment still in the layer when <paramref name="remainingNow"/> units remain.</summary>
    public decimal StillInStock(decimal remainingNow) =>
        RemainingAtAdjustment <= 0 ? 0 : InventoryRounding.Money(Amount * Math.Min(Math.Max(remainingNow, 0), RemainingAtAdjustment) / RemainingAtAdjustment);
}

// ===================================================================== Bin locations
/// <summary>
/// A storage bin inside a warehouse. A warehouse becomes bin-managed when it has at least one active bin (opt-in): inbound stock is
/// put away to the given bin or the warehouse's default receiving bin, outbound stock is picked from the given bin or automatically.
/// Bins only split quantities; valuation and the inventory ledger are per warehouse.
/// </summary>
[Audited]
public sealed class StorageBin : AggregateRoot, IWarehouseScoped
{
    private StorageBin()
    {
    }

    public StorageBin(Guid companyId, Guid warehouseId, string code, string name, bool isDefaultReceiving)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        WarehouseId = warehouseId;
        Code = Guard.MaxLength(Guard.NotEmpty(code, "inventory.bin-code-required", "Bin code").Trim().ToUpperInvariant(), 30, "inventory.bin-code-too-long", "Bin code");
        Rename(name);
        IsDefaultReceiving = isDefaultReceiving;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsDefaultReceiving { get; private set; }

    public bool IsActive { get; private set; }

    public void Rename(string name) =>
        Name = Guard.MaxLength(string.IsNullOrWhiteSpace(name) ? Code : name.Trim(), 100, "inventory.bin-name-too-long", "Bin name");

    public void SetDefaultReceiving(bool value) => IsDefaultReceiving = value;

    public void Deactivate()
    {
        IsActive = false;
        IsDefaultReceiving = false;
    }

    public void Activate() => IsActive = true;
}

/// <summary>Quantity per bin (product/variant/batch, base units). Changed only while the item's aggregate balance row is locked.</summary>
public sealed class BinBalance : ICompanyScoped
{
    private BinBalance()
    {
    }

    public BinBalance(Guid companyId, Guid binId, Guid warehouseId, Guid productId, Guid variantKey, Guid batchKey)
    {
        Id = SequentialGuid.New();
        CompanyId = companyId;
        BinId = binId;
        WarehouseId = warehouseId;
        ProductId = productId;
        VariantKey = variantKey;
        BatchKey = batchKey;
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid BinId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantKey { get; private set; }

    public Guid BatchKey { get; private set; }

    public decimal Quantity { get; private set; }

    public void Add(decimal quantity) => Quantity = InventoryRounding.Quantity(Quantity + quantity);
}

public static class BinMovementKinds
{
    public const string Putaway = nameof(Putaway);
    public const string Pick = nameof(Pick);
    public const string BinTransfer = nameof(BinTransfer);
}

/// <summary>Immutable log of bin quantity changes (the inventory movement it belongs to, when any) — used to reverse postings exactly.</summary>
public sealed class BinMovement : ICompanyScoped
{
    private BinMovement()
    {
    }

    public BinMovement(Guid companyId, Guid binId, Guid warehouseId, Guid productId, Guid variantKey, Guid batchKey, decimal quantity, string kind,
        Guid? inventoryMovementId, string sourceDocType, Guid sourceDocId, DateTime atUtc, Guid? userId)
    {
        Id = SequentialGuid.New();
        CompanyId = companyId;
        BinId = binId;
        WarehouseId = warehouseId;
        ProductId = productId;
        VariantKey = variantKey;
        BatchKey = batchKey;
        Quantity = quantity;
        Kind = kind;
        InventoryMovementId = inventoryMovementId;
        SourceDocType = sourceDocType;
        SourceDocId = sourceDocId;
        CreatedAtUtc = atUtc;
        CreatedBy = userId;
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid BinId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantKey { get; private set; }

    public Guid BatchKey { get; private set; }

    /// <summary>Signed: positive into the bin.</summary>
    public decimal Quantity { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public Guid? InventoryMovementId { get; private set; }

    public string SourceDocType { get; private set; } = string.Empty;

    public Guid SourceDocId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public Guid? CreatedBy { get; private set; }
}

/// <summary>Picks a quantity from bins: the requested bin only, else bins in code order, the rest from stock not in any bin.</summary>
public static class BinPicking
{
    public sealed record Slot(Guid? BinId, decimal Available);

    /// <returns>(bin, quantity) takes; a null bin means stock outside any bin. Null when the quantity cannot be covered.</returns>
    public static IReadOnlyList<(Guid? BinId, decimal Quantity)>? Pick(decimal quantity, IReadOnlyList<Slot> bins, decimal unbinned, Guid? requestedBin)
    {
        var takes = new List<(Guid? BinId, decimal Quantity)>();
        var left = quantity;
        IEnumerable<Slot> candidates = requestedBin is { } requested ? bins.Where(b => b.BinId == requested) : bins;
        foreach (var slot in candidates)
        {
            if (left <= 0)
            {
                break;
            }

            var take = Math.Min(left, slot.Available);
            if (take > 0)
            {
                takes.Add((slot.BinId, take));
                left -= take;
            }
        }

        if (left > 0 && requestedBin is null && unbinned > 0)
        {
            var take = Math.Min(left, unbinned);
            takes.Add((null, take));
            left -= take;
        }

        return left > 0 ? null : takes;
    }
}

// ===================================================================== Returns
/// <summary>Splits a return across the issue movements it reverses, in proportion to what each can still take back.</summary>
public static class ReturnAllocation
{
    public sealed record Source(Guid MovementId, decimal Returnable);

    /// <summary>
    /// Proportional to the returnable quantities. When every figure is a whole number the split is in whole units (largest remainder),
    /// otherwise in 6 decimals with the last source taking the rounding remainder. Fails when the sources cannot take the quantity.
    /// </summary>
    public static IReadOnlyList<(Guid MovementId, decimal Quantity)>? Allocate(decimal quantity, IReadOnlyList<Source> sources)
    {
        var usable = sources.Where(s => s.Returnable > 0).ToList();
        var total = usable.Sum(s => s.Returnable);
        if (quantity <= 0 || total < quantity || usable.Count == 0)
        {
            return null;
        }

        if (usable.Count == 1)
        {
            return [(usable[0].MovementId, quantity)];
        }

        var whole = quantity == decimal.Truncate(quantity) && usable.All(s => s.Returnable == decimal.Truncate(s.Returnable));
        if (whole)
        {
            var exact = usable.Select(s => (s.MovementId, Exact: quantity * s.Returnable / total, s.Returnable)).ToList();
            var floors = exact.Select(e => (e.MovementId, Quantity: Math.Min(decimal.Floor(e.Exact), e.Returnable), Fraction: e.Exact - decimal.Floor(e.Exact), e.Returnable)).ToList();
            var left = quantity - floors.Sum(f => f.Quantity);
            foreach (var index in floors.Select((f, i) => (f, i)).OrderByDescending(x => x.f.Fraction).ThenBy(x => x.i).Select(x => x.i).ToList())
            {
                if (left <= 0)
                {
                    break;
                }

                if (floors[index].Quantity < floors[index].Returnable)
                {
                    floors[index] = floors[index] with { Quantity = floors[index].Quantity + 1 };
                    left--;
                }
            }

            return floors.Where(f => f.Quantity > 0).Select(f => (f.MovementId, f.Quantity)).ToList();
        }

        var result = new List<(Guid MovementId, decimal Quantity)>();
        var allocated = 0m;
        for (var i = 0; i < usable.Count; i++)
        {
            var share = i == usable.Count - 1
                ? quantity - allocated
                : Math.Min(usable[i].Returnable, InventoryRounding.Quantity(quantity * usable[i].Returnable / total));
            if (share > 0)
            {
                result.Add((usable[i].MovementId, share));
                allocated += share;
            }
        }

        return result;
    }
}

// ===================================================================== Recost
public enum RecostTrigger
{
    Manual = 1,
    BackdatedPosting = 2,
}

public enum RecostStatus
{
    Pending = 1,
    Completed = 2,
    NothingToCorrect = 3,
    Failed = 4,
}

/// <summary>One correction of a recost run: an issue whose cost changes when the item is replayed in transaction-date order.</summary>
public sealed class RecostCorrection
{
    public Guid MovementId { get; set; }

    public DateOnly TransactionDate { get; set; }

    public string MovementType { get; set; } = string.Empty;

    public string SourceDocType { get; set; } = string.Empty;

    public decimal BaseQuantity { get; set; }

    /// <summary>Cost currently on the ledger for the issue (positive amount).</summary>
    public decimal RecordedCost { get; set; }

    /// <summary>Cost after replaying the item in transaction-date order (positive amount).</summary>
    public decimal RecalculatedCost { get; set; }

    public decimal Difference => RecalculatedCost - RecordedCost;
}

/// <summary>
/// Recalculation of a weighted-average item's issue costs from a date (decisions log OQ-INV-3 follow-up). The item's movements in
/// its valuation area are replayed in transaction-date order; issues whose cost changes get value-only correction movements and a
/// journal (Dr/Cr the issue's counter account, Cr/Dr Inventory). Queued automatically when a back-dated posting arrives, or run manually.
/// </summary>
[Audited]
public sealed class RecostRun : AggregateRoot, ICompanyScoped
{
    private RecostRun()
    {
    }

    public RecostRun(Guid companyId, Guid legalEntityId, Guid valuationAreaId, Guid warehouseId, Guid productId, Guid? variantId, DateOnly fromDate, RecostTrigger trigger, string? reference)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        ValuationAreaId = valuationAreaId;
        WarehouseId = warehouseId;
        ProductId = productId;
        VariantId = variantId;
        FromDate = fromDate;
        Trigger = trigger;
        Reference = reference is { Length: > 100 } ? reference[..100] : reference;
        Status = RecostStatus.Pending;
        if (trigger == RecostTrigger.BackdatedPosting)
        {
            Raise(new RecostRequested(Id, companyId));
        }
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid ValuationAreaId { get; private set; }

    /// <summary>A warehouse of the valuation area (used to resolve the posting context).</summary>
    public Guid WarehouseId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? VariantId { get; private set; }

    public DateOnly FromDate { get; private set; }

    public RecostTrigger Trigger { get; private set; }

    public string? Reference { get; private set; }

    public RecostStatus Status { get; private set; }

    public DateOnly? PostingDate { get; private set; }

    public decimal TotalDifference { get; private set; }

    public Guid? JournalId { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public List<RecostCorrection> Corrections { get; private set; } = [];

    public string? FailureReason { get; private set; }

    public void Fail(string reason, DateTime atUtc)
    {
        if (Status != RecostStatus.Pending)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        FailureReason = reason.Length > 500 ? reason[..500] : reason;
        CompletedAtUtc = atUtc;
        Status = RecostStatus.Failed;
    }

    public void Complete(DateOnly postingDate, IReadOnlyList<RecostCorrection> corrections, Guid? journalId, DateTime atUtc)
    {
        if (Status != RecostStatus.Pending)
        {
            throw new DomainException(InventoryErrors.InvalidTransition);
        }

        PostingDate = postingDate;
        Corrections = corrections.ToList();
        TotalDifference = corrections.Sum(c => c.Difference);
        JournalId = journalId;
        CompletedAtUtc = atUtc;
        Status = corrections.Count == 0 ? RecostStatus.NothingToCorrect : RecostStatus.Completed;
    }
}

/// <summary>Queued by a back-dated posting; handled after commit by the recost engine.</summary>
public sealed record RecostRequested(Guid RecostRunId, Guid CompanyId) : IDomainEvent;

public static class RecostErrors
{
    public static readonly Error FifoNotSupported = Error.Conflict("inventory.recost-fifo-not-supported",
        "Recost recalculates weighted-average items only; FIFO items keep their layer costs.");

    public static readonly Error NotFound = Error.NotFound("inventory.recost-not-found", "The recost run was not found.");
}
