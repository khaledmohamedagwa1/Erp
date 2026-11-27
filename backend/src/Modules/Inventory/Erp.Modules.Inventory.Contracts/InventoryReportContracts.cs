namespace Erp.Modules.Inventory.Contracts;

/// <summary>
/// Stock of one product/variant/batch in a warehouse (base units) valued at the average cost of the warehouse's valuation area
/// (functional currency).
/// </summary>
public sealed record StockPosition(
    Guid ProductId,
    Guid? VariantId,
    Guid WarehouseId,
    Guid? BatchId,
    string? BatchNumber,
    DateOnly? ExpiryDate,
    decimal OnHand,
    decimal Reserved,
    decimal UnitCost,
    decimal Value);

public sealed record InventoryLedgerFilter(
    DateOnly From,
    DateOnly To,
    Guid? WarehouseId = null,
    IReadOnlyCollection<Guid>? ProductIds = null,
    string? BatchNumber = null);

public sealed record InventoryLedgerEntry(
    Guid MovementId,
    DateOnly Date,
    long PostingSequence,
    Guid ProductId,
    Guid? VariantId,
    Guid WarehouseId,
    string? BatchNumber,
    string MovementType,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal TotalCost,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string? ReferenceNo);

/// <summary>A page of ledger entries plus the quantity/value carried in before <see cref="InventoryLedgerFilter.From"/>.</summary>
public sealed record InventoryLedgerPage(
    IReadOnlyList<InventoryLedgerEntry> Items,
    int TotalCount,
    decimal OpeningQuantity,
    decimal OpeningValue,
    decimal PeriodQuantity,
    decimal PeriodValue);

/// <summary>An inbound stock movement (receipt, transfer in, gain, production output) used to age on-hand stock.</summary>
public sealed record InboundMovement(Guid ProductId, Guid WarehouseId, DateOnly Date, decimal BaseQuantity);

/// <summary>Batch master data with the quantity currently on hand per warehouse (one row per warehouse; zero rows omitted).</summary>
public sealed record BatchStock(
    Guid BatchId,
    Guid ProductId,
    string Number,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    Guid? SupplierId,
    Guid WarehouseId,
    decimal OnHand);

/// <summary>Read access to the inventory ledger for reporting. Everything is scoped to the active company.</summary>
public interface IInventoryReportQueries
{
    /// <summary>Current stock by product/variant/batch/warehouse with value; zero rows are omitted.</summary>
    Task<IReadOnlyList<StockPosition>> GetStockPositionsAsync(Guid? warehouseId = null, IReadOnlyCollection<Guid>? productIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>Ledger movements in the period, ordered by posting sequence, paged in the database.</summary>
    Task<InventoryLedgerPage> GetLedgerAsync(InventoryLedgerFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Inbound movements (positive base quantity) on or before <paramref name="asOf"/>, newest first.</summary>
    Task<IReadOnlyList<InboundMovement>> GetInboundMovementsAsync(DateOnly asOf, Guid? warehouseId = null, IReadOnlyCollection<Guid>? productIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>Batches with stock; <paramref name="expiringOnOrBefore"/> keeps batches whose expiry date is set and not later than it.</summary>
    Task<IReadOnlyList<BatchStock>> GetBatchStockAsync(DateOnly? expiringOnOrBefore = null, Guid? warehouseId = null, IReadOnlyCollection<Guid>? productIds = null,
        CancellationToken cancellationToken = default);
}
