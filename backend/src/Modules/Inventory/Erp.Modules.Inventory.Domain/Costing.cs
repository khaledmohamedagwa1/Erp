using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Domain;

public static class InventoryRounding
{
    public static decimal Money(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    public static decimal Cost(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    public static decimal Quantity(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

public static class CostingMethods
{
    public const string WeightedAverage = "WeightedAverage";
    public const string Fifo = "FIFO";

    public static string Normalize(string? method) =>
        string.Equals(method, Fifo, StringComparison.OrdinalIgnoreCase) ? Fifo : WeightedAverage;
}

public static class InventoryErrors
{
    public static readonly Error InsufficientStock = Error.Conflict("inventory.insufficient-stock", "Not enough stock available.");
    public static readonly Error FifoNegativeNotAllowed = Error.Conflict("inventory.fifo-negative-not-allowed", "FIFO-costed items cannot go below zero.");
    public static readonly Error CostRequired = Error.Validation("inventory.cost-required", "A unit cost is required for this inbound line.");
    public static readonly Error ExpiredBatch = Error.Conflict("inventory.batch-expired", "Expired batches cannot be issued.");
    public static readonly Error TransactionRequired = Error.Failure("inventory.transaction-required", "Inventory postings must run inside the caller's transaction.");
    public static readonly Error ReversalStockConsumed = Error.Conflict("inventory.reversal-stock-consumed", "The received stock has already been consumed and cannot be reversed.");
    public static readonly Error NotFound = Error.NotFound("inventory.document-not-found", "The inventory document was not found.");
    public static readonly Error InvalidTransition = Error.Conflict("invalid-state-transition", "This action is not allowed in the document's current status.");
    public static readonly Error CrossLegalEntityTransfer = Error.Conflict("inventory.cross-legal-entity-transfer", "Transfers between legal entities are not supported (intercompany is out of scope).");
}

/// <summary>Quantity, value, average and last cost of an item in a valuation area (base units, base currency).</summary>
public readonly record struct ValuationState(decimal Quantity, decimal Value, decimal AverageCost, decimal LastCost);

public readonly record struct ReceiptOutcome(ValuationState State, decimal TotalCost, decimal TrueUp);

public readonly record struct IssueOutcome(ValuationState State, decimal UnitCost, decimal TotalCost);

/// <summary>
/// Weighted average cost (pure). Issues are valued at the current average; the last issue that empties the item takes the
/// exact remaining value so the valuation never keeps a rounding residue. With negative stock allowed, issues below zero
/// use the last known average; the receipt that covers the shortfall returns the difference as <c>TrueUp</c> (an expense when
/// positive), so value after = value before + receipt total − true-up (decisions log OQ-INV-4).
/// </summary>
public static class WeightedAverage
{
    public static ReceiptOutcome Receive(ValuationState state, decimal quantity, decimal unitCost)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Receipts must be positive.");
        }

        var total = InventoryRounding.Money(quantity * unitCost);
        var trueUp = 0m;
        if (state.Quantity < 0)
        {
            var covered = Math.Min(quantity, -state.Quantity);
            trueUp = InventoryRounding.Money(covered * (unitCost - state.AverageCost));
        }

        var newQuantity = state.Quantity + quantity;
        var newValue = state.Value + total - trueUp;
        var average = newQuantity > 0 ? InventoryRounding.Cost(newValue / newQuantity) : newQuantity == 0 ? unitCost : state.AverageCost;
        if (newQuantity == 0)
        {
            newValue = 0;
        }

        return new ReceiptOutcome(new ValuationState(newQuantity, newValue, average, unitCost), total, trueUp);
    }

    public static Result<IssueOutcome> Issue(ValuationState state, decimal quantity, bool allowNegative, decimal? explicitUnitCost = null)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Issues must be positive.");
        }

        if (!allowNegative && quantity > state.Quantity)
        {
            return InventoryErrors.InsufficientStock;
        }

        var cost = explicitUnitCost ?? (state.Quantity > 0 || state.AverageCost != 0 ? state.AverageCost : state.LastCost);
        var newQuantity = state.Quantity - quantity;
        var total = newQuantity == 0 ? state.Value : InventoryRounding.Money(quantity * cost);
        var newValue = newQuantity == 0 ? 0 : state.Value - total;
        var average = newQuantity > 0 ? InventoryRounding.Cost(newValue / newQuantity) : state.AverageCost;
        var unit = InventoryRounding.Cost(total / quantity);
        return new IssueOutcome(new ValuationState(newQuantity, newValue, average, state.LastCost), unit, total);
    }
}

/// <summary>A FIFO layer as the engine sees it.</summary>
public sealed record LayerSlice(Guid LayerId, decimal Remaining, decimal UnitCost);

public sealed record LayerTake(Guid LayerId, decimal Quantity, decimal UnitCost, decimal Cost);

/// <summary>First in, first out (pure). Layers are consumed in the order given; never below zero.</summary>
public static class Fifo
{
    public static ReceiptOutcome Receive(ValuationState state, decimal quantity, decimal unitCost)
    {
        if (state.Quantity < 0)
        {
            throw new InvalidOperationException("FIFO items cannot be negative.");
        }

        var total = InventoryRounding.Money(quantity * unitCost);
        var newQuantity = state.Quantity + quantity;
        var newValue = state.Value + total;
        return new ReceiptOutcome(new ValuationState(newQuantity, newValue, InventoryRounding.Cost(newValue / newQuantity), unitCost), total, 0);
    }

    public static Result<(IReadOnlyList<LayerTake> Takes, IssueOutcome Outcome)> Issue(ValuationState state, IEnumerable<LayerSlice> layers, decimal quantity)
    {
        var takes = new List<LayerTake>();
        var left = quantity;
        foreach (var layer in layers)
        {
            if (left == 0)
            {
                break;
            }

            var take = Math.Min(layer.Remaining, left);
            if (take <= 0)
            {
                continue;
            }

            takes.Add(new LayerTake(layer.LayerId, take, layer.UnitCost, InventoryRounding.Money(take * layer.UnitCost)));
            left -= take;
        }

        if (left > 0 || quantity > state.Quantity)
        {
            return InventoryErrors.InsufficientStock;
        }

        var newQuantity = state.Quantity - quantity;
        var total = newQuantity == 0 ? state.Value : takes.Sum(t => t.Cost);
        var newValue = state.Value - total;
        var average = newQuantity > 0 ? InventoryRounding.Cost(newValue / newQuantity) : state.AverageCost;
        return (takes, new IssueOutcome(new ValuationState(newQuantity, newValue, average, state.LastCost), InventoryRounding.Cost(total / quantity), total));
    }
}

/// <summary>Stock count arithmetic (snapshot mode, OQ-INV-6).</summary>
public static class CountMath
{
    /// <summary>Movements posted since the snapshot = current on-hand − snapshot on-hand.</summary>
    public static decimal MovementsSinceSnapshot(decimal snapshotQuantity, decimal currentOnHand) => currentOnHand - snapshotQuantity;

    /// <summary>variance = counted − (snapshot + movements after snapshot) = counted − current on-hand.</summary>
    public static decimal Variance(decimal counted, decimal snapshotQuantity, decimal movementsSinceSnapshot) =>
        counted - (snapshotQuantity + movementsSinceSnapshot);
}

public readonly record struct StockKey(Guid ProductId, Guid VariantKey, Guid WarehouseId, Guid BatchKey) : IComparable<StockKey>
{
    public int CompareTo(StockKey other)
    {
        var c = ProductId.CompareTo(other.ProductId);
        if (c != 0)
        {
            return c;
        }

        c = VariantKey.CompareTo(other.VariantKey);
        if (c != 0)
        {
            return c;
        }

        c = WarehouseId.CompareTo(other.WarehouseId);
        return c != 0 ? c : BatchKey.CompareTo(other.BatchKey);
    }
}

/// <summary>Deterministic lock order (product, variant, warehouse, batch) so concurrent postings never deadlock on each other.</summary>
public static class LockOrder
{
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, StockKey> key) => items.OrderBy(key).ToList();
}
