using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Contracts;

/// <summary>Movement types of the inventory ledger. The sign of a posting line's quantity must match the type's direction.</summary>
public static class InventoryMovementTypes
{
    public const string OpeningStock = nameof(OpeningStock);
    public const string PurchaseReceipt = nameof(PurchaseReceipt);
    public const string PurchaseReturn = nameof(PurchaseReturn);
    public const string SalesIssue = nameof(SalesIssue);
    public const string SalesReturn = nameof(SalesReturn);
    public const string TransferOut = nameof(TransferOut);
    public const string TransferIn = nameof(TransferIn);
    public const string AdjustmentGain = nameof(AdjustmentGain);
    public const string AdjustmentLoss = nameof(AdjustmentLoss);
    public const string ProductionIssue = nameof(ProductionIssue);
    public const string ProductionReturn = nameof(ProductionReturn);
    public const string ProductionReceipt = nameof(ProductionReceipt);
    public const string CostAdjustment = nameof(CostAdjustment);

    public static readonly IReadOnlySet<string> Inbound = new HashSet<string>(StringComparer.Ordinal)
    {
        OpeningStock, PurchaseReceipt, SalesReturn, TransferIn, AdjustmentGain, ProductionReturn, ProductionReceipt,
    };

    public static readonly IReadOnlySet<string> Outbound = new HashSet<string>(StringComparer.Ordinal)
    {
        PurchaseReturn, SalesIssue, TransferOut, AdjustmentLoss, ProductionIssue,
    };
}

public static class InventoryPermissions
{
    public const string Read = "inventory.read";
    public const string DocumentManage = "inventory.document.manage";
    public const string DocumentApprove = "inventory.document.approve";
    public const string DocumentPost = "inventory.document.post";
    public const string ReservationManage = "inventory.reservation.manage";
    public const string BinManage = "inventory.bin.manage";
    public const string Recost = "inventory.recost";
}

/// <summary>Batch details for an inbound line of a batch-tracked product (the batch is created on first receipt).</summary>
public sealed record BatchInput(string Number, DateOnly? ManufactureDate = null, DateOnly? ExpiryDate = null, Guid? SupplierId = null);

/// <summary>The demand a reservation belongs to (e.g. "SalesOrder", order id, order line id).</summary>
public sealed record ReservationRef(string DemandType, Guid DemandId, Guid DemandLineId);

/// <summary>
/// One line of an inventory posting.
/// <list type="bullet">
/// <item><see cref="Quantity"/> is in <see cref="UomId"/>: positive = into the warehouse, negative = out of it.</item>
/// <item><see cref="UnitCost"/> (base currency per <b>base</b> unit) is required for inbound lines unless
/// <see cref="CostSourceMovementId"/> is given; ignored for outbound lines, whose cost comes from the costing engine.</item>
/// <item><see cref="CostSourceMovementId"/> makes a return use the cost of the original movement (sales return at original issue
/// cost, purchase return at original receipt cost). For an inbound return of an issue that left in several movements (batches,
/// serials) the return is split across all movements of that issue line in proportion to what each can still take back, each
/// part restocked into its own batch at its own cost.</item>
/// <item><see cref="TotalCost"/>: inbound only — the exact value to receive (the unit cost is derived), so carried costs leave no residue.</item>
/// <item><see cref="BinId"/>: bin-managed warehouses only — put-away bin (inbound; default receiving bin when omitted) or pick bin
/// (outbound; bins are picked automatically when omitted).</item>
/// <item><see cref="Batch"/>: required inbound for batch-tracked products; optional outbound (FEFO allocation when omitted;
/// expired batches are never issued).</item>
/// <item><see cref="Serials"/>: required for serial-tracked products; count must equal the base quantity.</item>
/// <item><see cref="ConsumeReservation"/>: an outbound line that fulfils a reservation releases it by the issued quantity.</item>
/// <item><see cref="CounterWarehouseId"/>: for TransferOut/TransferIn — when both warehouses are in the same valuation area the
/// item valuation is left untouched (quantity only moves between warehouses).</item>
/// <item><see cref="CounterRole"/>: when the request asks Inventory to post the journal, the posting role on the other side of
/// the Inventory line (e.g. GoodsReceivedNotInvoiced for a receipt, CostOfGoodsSold for a sales issue).</item>
/// </list>
/// </summary>
public sealed record InventoryPostingLine(
    Guid LineId,
    Guid ProductId,
    Guid? VariantId,
    Guid WarehouseId,
    Guid UomId,
    decimal Quantity,
    string MovementType,
    decimal? UnitCost = null,
    BatchInput? Batch = null,
    IReadOnlyList<string>? Serials = null,
    Guid? CostSourceMovementId = null,
    ReservationRef? ConsumeReservation = null,
    Guid? CounterWarehouseId = null,
    string? CounterRole = null,
    decimal? TotalCost = null,
    Guid? BinId = null);

/// <summary>
/// A request from a source document. (SourceDocType, SourceDocId) identifies it: posting the same source twice returns the
/// first result (<see cref="InventoryPostingResult.AlreadyPosted"/>). Must run inside the caller's transaction (ADR-006).
/// When <see cref="PostJournal"/> is true Inventory also posts one balanced journal (purpose "inventory") with an Inventory
/// line and a <see cref="InventoryPostingLine.CounterRole"/> line per stock line; otherwise the caller composes its own journal
/// from the returned costs.
/// </summary>
public sealed record InventoryPostingRequest(
    Guid? LegalEntityId,
    DateOnly TransactionDate,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string? ReferenceNo,
    IReadOnlyList<InventoryPostingLine> Lines,
    bool PostJournal = false,
    string? JournalDescription = null);

public sealed record MovementDetail(
    Guid MovementId,
    Guid WarehouseId,
    Guid? BatchId,
    string? BatchNumber,
    Guid? SerialId,
    string? SerialNumber,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal TotalCost);

/// <summary>Cost of one posted line in base currency. BaseQuantity and TotalCost carry the line's sign (negative = out).</summary>
public sealed record InventoryLineResult(Guid LineId, decimal BaseQuantity, decimal UnitCost, decimal TotalCost, IReadOnlyList<MovementDetail> Movements);

public sealed record InventoryPostingResult(Guid LegalEntityId, IReadOnlyList<InventoryLineResult> Lines, decimal TotalCost, Guid? JournalId, bool AlreadyPosted);

/// <summary>Value-only change to stock received earlier (e.g. supplier invoice price differs from the receipt price).</summary>
public sealed record CostAdjustmentRequest(
    Guid ReceiptMovementId,
    decimal Amount,
    DateOnly Date,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    Guid SourceLineId);

/// <summary>
/// <see cref="AppliedToInventory"/> was added to the value of the stock still on hand; <see cref="ToVariance"/> relates to
/// quantities already consumed and belongs in purchase price variance (decisions log OQ-PUR-1). The caller posts the journal.
/// </summary>
public sealed record CostAdjustmentResult(decimal AppliedToInventory, decimal ToVariance, Guid? MovementId);

public interface IInventoryPostingService
{
    Task<Result<InventoryPostingResult>> PostAsync(InventoryPostingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts the exact opposite of every movement of the source at its original cost, dated <paramref name="reversalDate"/>,
    /// and reverses the journals Inventory posted for it. Idempotent. Fails if reversing a receipt would need stock that has
    /// already been consumed.
    /// </summary>
    Task<Result<InventoryPostingResult>> ReverseAsync(string sourceDocType, Guid sourceDocId, DateOnly reversalDate, CancellationToken cancellationToken = default);

    Task<Result<CostAdjustmentResult>> AdjustCostAsync(CostAdjustmentRequest request, CancellationToken cancellationToken = default);
}

public sealed record ReservationRequest(ReservationRef Reference, Guid ProductId, Guid? VariantId, Guid WarehouseId, decimal BaseQuantity);

public sealed record ReservationInfo(Guid Id, ReservationRef Reference, Guid ProductId, Guid? VariantId, Guid WarehouseId, decimal Quantity, decimal Consumed, decimal Released, decimal Open, string Status);

/// <summary>Reservations reduce available stock (Available = OnHand − Reserved). Must run inside the caller's transaction.</summary>
public interface IReservationService
{
    Task<Result<ReservationInfo>> ReserveAsync(ReservationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Releases <paramref name="baseQuantity"/> (or everything still open when null) of the reservation.</summary>
    Task<Result<ReservationInfo>> ReleaseAsync(ReservationRef reference, decimal? baseQuantity = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReservationInfo>> GetAsync(string demandType, Guid demandId, CancellationToken cancellationToken = default);
}

public sealed record StockLevel(Guid ProductId, Guid? VariantId, Guid WarehouseId, decimal OnHand, decimal Reserved, decimal Available);

public sealed record ItemCost(Guid ValuationAreaId, Guid ProductId, Guid? VariantId, decimal Quantity, decimal Value, decimal AverageCost, decimal LastCost);

public sealed record MovementInfo(
    Guid Id,
    Guid ProductId,
    Guid? VariantId,
    Guid WarehouseId,
    string MovementType,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal TotalCost,
    DateOnly TransactionDate,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    Guid? SourceLineId);

/// <summary>An issue movement of a source line and how much of it can still be returned (base units).</summary>
public sealed record ReturnableIssue(
    Guid MovementId,
    Guid WarehouseId,
    string? BatchNumber,
    string? SerialNumber,
    decimal IssuedQuantity,
    decimal ReturnedQuantity,
    decimal ReturnableQuantity,
    decimal UnitCost);

public interface IStockQueries
{
    /// <summary>
    /// The outbound movements posted for one source line (e.g. an invoice line) with the quantity already returned against each — what a
    /// return of that line restocks, batch by batch and at each movement's cost.
    /// </summary>
    Task<IReadOnlyList<ReturnableIssue>> GetReturnableIssuesAsync(string sourceDocType, Guid sourceDocId, Guid sourceLineId, CancellationToken cancellationToken = default);

    /// <summary>Stock per product/variant/warehouse in base units (batches summed).</summary>
    Task<IReadOnlyList<StockLevel>> GetStockAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default);

    /// <summary>Valuation of the item in the warehouse's valuation area (null when the item was never received there).</summary>
    Task<ItemCost?> GetItemCostAsync(Guid productId, Guid? variantId, Guid warehouseId, CancellationToken cancellationToken = default);

    /// <summary>Total inventory value of a legal entity (sum over its valuation areas), base currency.</summary>
    Task<decimal> GetValuationTotalAsync(Guid legalEntityId, CancellationToken cancellationToken = default);

    Task<MovementInfo?> GetMovementAsync(Guid movementId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MovementInfo>> GetMovementsBySourceAsync(string sourceDocType, Guid sourceDocId, CancellationToken cancellationToken = default);
}
