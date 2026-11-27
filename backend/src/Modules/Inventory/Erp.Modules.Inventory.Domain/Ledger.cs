using Erp.SharedKernel.Domain;

namespace Erp.Modules.Inventory.Domain;

/// <summary>
/// One immutable row of the inventory ledger (Part 7.2). Never updated or deleted — a database trigger enforces it;
/// corrections are reversal movements (<see cref="ReversalOfId"/>). Quantities and costs carry the direction's sign.
/// </summary>
public sealed class InventoryMovement : ICompanyScoped
{
    private InventoryMovement()
    {
    }

    public InventoryMovement(
        Guid companyId,
        Guid legalEntityId,
        Guid valuationAreaId,
        long postingSequence,
        Guid productId,
        Guid? variantId,
        Guid warehouseId,
        Guid? batchId,
        Guid? serialId,
        string movementType,
        decimal quantity,
        Guid uomId,
        decimal baseQuantity,
        decimal unitCost,
        decimal totalCost,
        DateOnly transactionDate,
        string sourceModule,
        string sourceDocType,
        Guid sourceDocId,
        Guid? sourceLineId,
        string? referenceNo,
        Guid? reversalOfId,
        string? journalPurpose,
        DateTime createdAtUtc,
        Guid? createdBy,
        Guid? relatedMovementId = null)
    {
        Id = SequentialGuid.New();
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        ValuationAreaId = valuationAreaId;
        PostingSequence = postingSequence;
        ProductId = productId;
        VariantId = variantId;
        WarehouseId = warehouseId;
        BatchId = batchId;
        SerialId = serialId;
        MovementType = movementType;
        Direction = (short)Math.Sign(baseQuantity == 0 ? totalCost : baseQuantity);
        Quantity = quantity;
        UomId = uomId;
        BaseQuantity = baseQuantity;
        UnitCost = unitCost;
        TotalCost = totalCost;
        TransactionDate = transactionDate;
        SourceModule = sourceModule;
        SourceDocType = sourceDocType;
        SourceDocId = sourceDocId;
        SourceLineId = sourceLineId;
        ReferenceNo = referenceNo;
        ReversalOfId = reversalOfId;
        JournalPurpose = journalPurpose;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = createdBy;
        RelatedMovementId = relatedMovementId;
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    /// <summary>The legal entity (default) or the warehouse, per the inventory policy (decisions log OQ-INV-1).</summary>
    public Guid ValuationAreaId { get; private set; }

    /// <summary>Order in which the item's cost was computed in its valuation area (posting sequence, OQ-INV-3).</summary>
    public long PostingSequence { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? VariantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid? BatchId { get; private set; }

    public Guid? SerialId { get; private set; }

    public string MovementType { get; private set; } = string.Empty;

    /// <summary>+1 in, −1 out (for value-only adjustments: the sign of the value change).</summary>
    public short Direction { get; private set; }

    public decimal Quantity { get; private set; }

    public Guid UomId { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public decimal TotalCost { get; private set; }

    public DateOnly TransactionDate { get; private set; }

    public string SourceModule { get; private set; } = string.Empty;

    public string SourceDocType { get; private set; } = string.Empty;

    public Guid SourceDocId { get; private set; }

    public Guid? SourceLineId { get; private set; }

    public string? ReferenceNo { get; private set; }

    public Guid? ReversalOfId { get; private set; }

    /// <summary>Purpose of the journal Inventory posted for this movement's source, if any (used to reverse it).</summary>
    public string? JournalPurpose { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public Guid? CreatedBy { get; private set; }

    /// <summary>Returns: the movement whose cost was used. Cost adjustments: the receipt that was revalued.</summary>
    public Guid? RelatedMovementId { get; private set; }
}

/// <summary>
/// Quantity per product/variant/warehouse/batch. "No variant"/"no batch" are stored as <see cref="Guid.Empty"/> so the
/// unique key works without nullable columns. The row with BatchKey = Empty is the item's aggregate row in the warehouse:
/// it carries the reservations and is the lock taken before any batch row (deterministic lock order, §19.4).
/// Updated only by set-based SQL inside the posting transaction.
/// </summary>
public sealed class InventoryBalance : ICompanyScoped
{
    private InventoryBalance()
    {
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantKey { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid BatchKey { get; private set; }

    public decimal OnHand { get; private set; }

    public decimal Reserved { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }
}

/// <summary>Running quantity and value of an item in a valuation area. Locked (UPDLOCK) before any cost is computed.</summary>
public sealed class ItemValuation : AggregateRoot, ICompanyScoped
{
    private ItemValuation()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid ValuationAreaId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantKey { get; private set; }

    public string CostingMethod { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal Value { get; private set; }

    public decimal AverageCost { get; private set; }

    public decimal LastCost { get; private set; }

    public long LastSequence { get; private set; }

    public ValuationState ToState() => new(Quantity, Value, AverageCost, LastCost);

    public void Apply(ValuationState state)
    {
        Quantity = state.Quantity;
        Value = state.Value;
        AverageCost = state.AverageCost;
        LastCost = state.LastCost;
    }

    public void SetMethodIfUnused(string method)
    {
        if (string.IsNullOrEmpty(CostingMethod) || (Quantity == 0 && LastSequence == 0))
        {
            CostingMethod = method;
        }
    }

    public long NextSequence() => ++LastSequence;
}

/// <summary>A FIFO receipt layer.</summary>
public sealed class CostLayer : ICompanyScoped
{
    private CostLayer()
    {
    }

    public CostLayer(Guid companyId, Guid valuationAreaId, Guid productId, Guid variantKey, Guid receiptMovementId, long sequence, decimal quantity, decimal unitCost, DateTime receivedAtUtc)
    {
        Id = SequentialGuid.New();
        CompanyId = companyId;
        ValuationAreaId = valuationAreaId;
        ProductId = productId;
        VariantKey = variantKey;
        ReceiptMovementId = receiptMovementId;
        Sequence = sequence;
        OriginalQuantity = quantity;
        RemainingQuantity = quantity;
        UnitCost = unitCost;
        ReceivedAtUtc = receivedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid ValuationAreaId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantKey { get; private set; }

    public Guid ReceiptMovementId { get; private set; }

    public long Sequence { get; private set; }

    public decimal OriginalQuantity { get; private set; }

    public decimal RemainingQuantity { get; set; }

    public decimal UnitCost { get; set; }

    public DateTime ReceivedAtUtc { get; private set; }
}

public sealed class LayerConsumption : ICompanyScoped
{
    private LayerConsumption()
    {
    }

    public LayerConsumption(Guid companyId, Guid layerId, Guid movementId, decimal quantity, decimal unitCost)
    {
        Id = SequentialGuid.New();
        CompanyId = companyId;
        LayerId = layerId;
        MovementId = movementId;
        Quantity = quantity;
        UnitCost = unitCost;
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid LayerId { get; private set; }

    public Guid MovementId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitCost { get; private set; }
}

public sealed class Batch : AggregateRoot, ICompanyScoped
{
    private Batch()
    {
    }

    public Batch(Guid companyId, Guid productId, string number, DateOnly? manufactureDate, DateOnly? expiryDate, Guid? supplierId)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        ProductId = productId;
        Number = Guard.MaxLength(Guard.NotEmpty(number, "inventory.batch-required", "Batch number").ToUpperInvariant(), 50, "inventory.batch-too-long", "Batch number");
        ManufactureDate = manufactureDate;
        ExpiryDate = expiryDate;
        SupplierId = supplierId;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly? ManufactureDate { get; private set; }

    public DateOnly? ExpiryDate { get; private set; }

    public Guid? SupplierId { get; private set; }

    public bool IsExpiredOn(DateOnly date) => ExpiryDate is { } expiry && expiry < date;
}

public enum SerialStatus
{
    InStock = 1,
    Reserved = 2,
    InTransit = 3,
    Issued = 4,
    Returned = 5,
    Scrapped = 6,
}

public sealed class SerialNumber : AggregateRoot, ICompanyScoped
{
    private SerialNumber()
    {
    }

    public SerialNumber(Guid companyId, Guid productId, string serial, Guid warehouseId)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        ProductId = productId;
        Serial = Guard.MaxLength(Guard.NotEmpty(serial, "inventory.serial-required", "Serial number").Trim(), 100, "inventory.serial-too-long", "Serial number");
        CurrentWarehouseId = warehouseId;
        Status = SerialStatus.InStock;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Serial { get; private set; } = string.Empty;

    public Guid? CurrentWarehouseId { get; private set; }

    public SerialStatus Status { get; private set; }

    public Guid? LastMovementId { get; private set; }

    public void Move(Guid? warehouseId, SerialStatus status, Guid movementId)
    {
        CurrentWarehouseId = warehouseId;
        Status = status;
        LastMovementId = movementId;
    }
}

public enum ReservationStatus
{
    Active = 1,
    PartiallyConsumed = 2,
    Consumed = 3,
    Released = 4,
}

public sealed class StockReservation : AggregateRoot, ICompanyScoped
{
    private StockReservation()
    {
    }

    public StockReservation(Guid companyId, string demandType, Guid demandId, Guid demandLineId, Guid productId, Guid? variantId, Guid warehouseId, decimal quantity)
        : base(SequentialGuid.New())
    {
        if (quantity <= 0)
        {
            throw new DomainException(Erp.SharedKernel.Results.Error.Validation("inventory.quantity-invalid", "Reserved quantity must be positive."));
        }

        CompanyId = companyId;
        DemandType = Guard.MaxLength(Guard.NotEmpty(demandType, "inventory.demand-required", "Demand type"), 50, "inventory.demand-too-long", "Demand type");
        DemandId = demandId;
        DemandLineId = demandLineId;
        ProductId = productId;
        VariantId = variantId;
        WarehouseId = warehouseId;
        Quantity = quantity;
        Status = ReservationStatus.Active;
    }

    public Guid CompanyId { get; private set; }

    public string DemandType { get; private set; } = string.Empty;

    public Guid DemandId { get; private set; }

    public Guid DemandLineId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? VariantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal Consumed { get; private set; }

    public decimal Released { get; private set; }

    public ReservationStatus Status { get; private set; }

    public decimal Open => Quantity - Consumed - Released;

    public void Increase(decimal quantity)
    {
        Quantity += quantity;
        Status = Consumed > 0 ? ReservationStatus.PartiallyConsumed : ReservationStatus.Active;
    }

    /// <returns>The quantity actually taken from the reservation.</returns>
    public decimal Consume(decimal quantity)
    {
        var take = Math.Min(Math.Max(quantity, 0), Open);
        Consumed += take;
        Status = Open == 0 ? (Released > 0 && Consumed == 0 ? ReservationStatus.Released : ReservationStatus.Consumed) : ReservationStatus.PartiallyConsumed;
        return take;
    }

    public decimal Release(decimal? quantity)
    {
        var take = Math.Min(quantity ?? Open, Open);
        Released += take;
        if (Open == 0)
        {
            Status = Consumed > 0 ? ReservationStatus.Consumed : ReservationStatus.Released;
        }

        return take;
    }
}
