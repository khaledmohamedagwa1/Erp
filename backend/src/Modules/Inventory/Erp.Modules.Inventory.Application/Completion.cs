using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Inventory.Application;

// ===================================================================== Bins
public static class BinErrors
{
    public static readonly Error NotFound = Error.NotFound("inventory.bin-not-found", "The bin was not found in this warehouse or is inactive.");
    public static readonly Error Insufficient = Error.Conflict("inventory.bin-insufficient", "The bin does not hold enough of this item.");
    public static readonly Error DuplicateCode = Error.Conflict("inventory.bin-duplicate", "A bin with this code already exists in the warehouse.");
    public static readonly Error SameBin = Error.Validation("inventory.bin-same", "Source and destination bins must differ.");
}

/// <summary>
/// Bin quantities for postings (put-away / pick) and bin transfers. Always called after the item's aggregate balance row is locked by
/// the stock update, so bin rows of an item/warehouse change one transaction at a time.
/// </summary>
public sealed class BinStock(IInventoryDbContext db, ICurrentUser user, IClock clock)
{
    private readonly Dictionary<Guid, IReadOnlyList<StorageBin>> _bins = [];

    public async Task<IReadOnlyList<StorageBin>> ActiveBinsAsync(Guid warehouseId, CancellationToken ct)
    {
        if (!_bins.TryGetValue(warehouseId, out var bins))
        {
            bins = await db.Bins.AsNoTracking().Where(b => b.WarehouseId == warehouseId && b.IsActive).OrderBy(b => b.Code).ToListAsync(ct);
            _bins[warehouseId] = bins;
        }

        return bins;
    }

    public void Forget(Guid warehouseId) => _bins.Remove(warehouseId);

    /// <summary>Put-away (inbound) or pick (outbound) for one posted movement. Warehouses without active bins are left untouched.</summary>
    public async Task<Result> ApplyAsync(Guid companyId, StockKey key, decimal baseQuantity, Guid? binId, Guid movementId, string docType, Guid docId, CancellationToken ct)
    {
        var bins = await ActiveBinsAsync(key.WarehouseId, ct);
        if (bins.Count == 0)
        {
            return binId is null ? Result.Success() : BinErrors.NotFound;
        }

        if (binId is { } requested && bins.All(b => b.Id != requested))
        {
            return BinErrors.NotFound;
        }

        if (baseQuantity > 0)
        {
            var target = binId ?? bins.FirstOrDefault(b => b.IsDefaultReceiving)?.Id;
            if (target is { } bin)
            {
                await AddAsync(companyId, bin, key, baseQuantity, BinMovementKinds.Putaway, movementId, docType, docId, ct);
            }

            // No default receiving bin: the stock waits outside any bin until it is put away with a bin transfer.
            return Result.Success();
        }

        var quantity = -baseQuantity;
        var rows = await db.BinBalances.Where(b => b.WarehouseId == key.WarehouseId && b.ProductId == key.ProductId && b.VariantKey == key.VariantKey && b.BatchKey == key.BatchKey)
            .ToListAsync(ct);
        var order = bins.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var slots = rows.Where(r => r.Quantity > 0 && order.ContainsKey(r.BinId)).OrderBy(r => order[r.BinId])
            .Select(r => new BinPicking.Slot(r.BinId, r.Quantity)).ToList();
        var unbinned = await UnbinnedAsync(key, rows.Sum(r => r.Quantity), ct) + quantity; // on-hand before this issue
        var takes = BinPicking.Pick(quantity, slots, unbinned, binId);
        if (takes is null)
        {
            if (binId is not null)
            {
                return BinErrors.Insufficient;
            }

            // Negative stock allowed by policy: take what the bins hold, the rest goes below zero outside any bin.
            takes = BinPicking.Pick(quantity, slots, decimal.MaxValue, null)!;
        }

        foreach (var (bin, take) in takes.Where(t => t.BinId is not null))
        {
            await AddAsync(companyId, bin!.Value, key, -take, BinMovementKinds.Pick, movementId, docType, docId, ct);
        }

        return Result.Success();
    }

    /// <summary>Undoes the bin effects of a reversed movement.</summary>
    public async Task ReverseAsync(Guid originalMovementId, Guid reversalMovementId, CancellationToken ct)
    {
        var moves = await db.BinMovements.AsNoTracking().Where(m => m.InventoryMovementId == originalMovementId).ToListAsync(ct);
        foreach (var m in moves)
        {
            await AddAsync(m.CompanyId, m.BinId, new StockKey(m.ProductId, m.VariantKey, m.WarehouseId, m.BatchKey), -m.Quantity, m.Kind, reversalMovementId,
                m.SourceDocType, m.SourceDocId, ct);
        }
    }

    /// <summary>Quantity of the key that is in the warehouse but in no bin (current on-hand minus the bins).</summary>
    public async Task<decimal> UnbinnedAsync(StockKey key, decimal inBins, CancellationToken ct)
    {
        var onHand = await db.Balances.AsNoTracking()
            .Where(b => b.WarehouseId == key.WarehouseId && b.ProductId == key.ProductId && b.VariantKey == key.VariantKey && b.BatchKey == key.BatchKey)
            .Select(b => (decimal?)b.OnHand).FirstOrDefaultAsync(ct) ?? 0;
        return onHand - inBins;
    }

    public async Task AddAsync(Guid companyId, Guid binId, StockKey key, decimal quantity, string kind, Guid? movementId, string docType, Guid docId, CancellationToken ct)
    {
        var row = db.BinBalances.Local.FirstOrDefault(b => b.BinId == binId && b.ProductId == key.ProductId && b.VariantKey == key.VariantKey && b.BatchKey == key.BatchKey)
                  ?? await db.BinBalances.FirstOrDefaultAsync(b => b.BinId == binId && b.ProductId == key.ProductId && b.VariantKey == key.VariantKey && b.BatchKey == key.BatchKey, ct);
        if (row is null)
        {
            row = new BinBalance(companyId, binId, key.WarehouseId, key.ProductId, key.VariantKey, key.BatchKey);
            db.BinBalances.Add(row);
        }

        row.Add(quantity);
        db.BinMovements.Add(new BinMovement(companyId, binId, key.WarehouseId, key.ProductId, key.VariantKey, key.BatchKey, quantity, kind, movementId, docType, docId,
            clock.UtcNow, user.UserId));
    }
}

public sealed record BinDto(Guid Id, Guid WarehouseId, string Code, string Name, bool IsDefaultReceiving, bool IsActive);

public sealed record BinStockDto(Guid? BinId, string? BinCode, Guid ProductId, Guid? VariantId, Guid? BatchId, string? BatchNumber, decimal Quantity);

public sealed record CreateBinCommand(Guid WarehouseId, string Code, string? Name, bool IsDefaultReceiving) : ICommand<BinDto>;

public sealed class CreateBinValidator : AbstractValidator<CreateBinCommand>
{
    public CreateBinValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).MaximumLength(100);
    }
}

public sealed class CreateBinHandler(IInventoryDbContext db, PostingContextResolver contexts) : ICommandHandler<CreateBinCommand, BinDto>
{
    public async Task<Result<BinDto>> Handle(CreateBinCommand command, CancellationToken cancellationToken)
    {
        var ctx = await contexts.ResolveAsync([command.WarehouseId], null, cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        if (ctx.Value.Warehouses[command.WarehouseId].Type == "Transit")
        {
            return Error.Validation("inventory.transit-not-allowed", "Transit warehouses cannot have bins.");
        }

        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Bins.AnyAsync(b => b.WarehouseId == command.WarehouseId && b.Code == code, cancellationToken))
        {
            return BinErrors.DuplicateCode;
        }

        if (command.IsDefaultReceiving)
        {
            await ClearDefaultAsync(db, command.WarehouseId, null, cancellationToken);
        }

        var bin = new StorageBin(ctx.Value.CompanyId, command.WarehouseId, code, command.Name ?? code, command.IsDefaultReceiving);
        db.Bins.Add(bin);
        await db.SaveChangesAsync(cancellationToken);
        return BinMapping.ToDto(bin);
    }

    internal static async Task ClearDefaultAsync(IInventoryDbContext db, Guid warehouseId, Guid? except, CancellationToken ct)
    {
        foreach (var other in await db.Bins.Where(b => b.WarehouseId == warehouseId && b.IsDefaultReceiving && b.Id != except).ToListAsync(ct))
        {
            other.SetDefaultReceiving(false);
        }
    }
}

public sealed record UpdateBinCommand(Guid Id, string? Name, bool? IsDefaultReceiving, bool? IsActive) : ICommand<BinDto>;

public sealed class UpdateBinHandler(IInventoryDbContext db) : ICommandHandler<UpdateBinCommand, BinDto>
{
    public async Task<Result<BinDto>> Handle(UpdateBinCommand command, CancellationToken cancellationToken)
    {
        var bin = await db.Bins.FirstOrDefaultAsync(b => b.Id == command.Id, cancellationToken);
        if (bin is null)
        {
            return BinErrors.NotFound;
        }

        if (command.Name is not null)
        {
            bin.Rename(command.Name);
        }

        if (command.IsActive is false)
        {
            if (await db.BinBalances.AnyAsync(b => b.BinId == bin.Id && b.Quantity != 0, cancellationToken))
            {
                return Error.Conflict("inventory.bin-not-empty", "Move the stock out of the bin before deactivating it.");
            }

            bin.Deactivate();
        }
        else if (command.IsActive is true)
        {
            bin.Activate();
        }

        if (command.IsDefaultReceiving is { } isDefault && bin.IsActive)
        {
            if (isDefault)
            {
                await CreateBinHandler.ClearDefaultAsync(db, bin.WarehouseId, bin.Id, cancellationToken);
            }

            bin.SetDefaultReceiving(isDefault);
        }

        await db.SaveChangesAsync(cancellationToken);
        return BinMapping.ToDto(bin);
    }
}

public sealed record ListBinsQuery(Guid WarehouseId) : IQuery<IReadOnlyList<BinDto>>;

public sealed class ListBinsHandler(IInventoryDbContext db) : IQueryHandler<ListBinsQuery, IReadOnlyList<BinDto>>
{
    public async Task<Result<IReadOnlyList<BinDto>>> Handle(ListBinsQuery query, CancellationToken cancellationToken)
    {
        var bins = await db.Bins.AsNoTracking().Where(b => b.WarehouseId == query.WarehouseId).OrderBy(b => b.Code).ToListAsync(cancellationToken);
        return bins.Select(BinMapping.ToDto).ToList();
    }
}

/// <summary>Stock per bin of a warehouse; the row without a bin is stock not yet put away.</summary>
public sealed record BinStockQuery(Guid WarehouseId, Guid? ProductId) : IQuery<IReadOnlyList<BinStockDto>>;

public sealed class BinStockQueryHandler(IInventoryDbContext db) : IQueryHandler<BinStockQuery, IReadOnlyList<BinStockDto>>
{
    public async Task<Result<IReadOnlyList<BinStockDto>>> Handle(BinStockQuery query, CancellationToken cancellationToken) =>
        Result.Success(await LoadAsync(db, query, cancellationToken));

    internal static async Task<IReadOnlyList<BinStockDto>> LoadAsync(IInventoryDbContext db, BinStockQuery query, CancellationToken cancellationToken)
    {
        var bins = await db.Bins.AsNoTracking().Where(b => b.WarehouseId == query.WarehouseId).ToDictionaryAsync(b => b.Id, cancellationToken);
        var rows = db.BinBalances.AsNoTracking().Where(b => b.WarehouseId == query.WarehouseId && b.Quantity != 0);
        var balances = db.Balances.AsNoTracking().Where(b => b.WarehouseId == query.WarehouseId && b.OnHand != 0);
        if (query.ProductId is { } productId)
        {
            rows = rows.Where(b => b.ProductId == productId);
            balances = balances.Where(b => b.ProductId == productId);
        }

        var binRows = await rows.ToListAsync(cancellationToken);
        var stock = await balances.ToListAsync(cancellationToken);
        var batchIds = binRows.Select(b => b.BatchKey).Concat(stock.Select(b => b.BatchKey)).Where(b => b != Guid.Empty).Distinct().ToList();
        var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Number, cancellationToken);

        var result = binRows.Select(r => new BinStockDto(r.BinId, bins.GetValueOrDefault(r.BinId)?.Code, r.ProductId, Keys.Nullable(r.VariantKey), Keys.Nullable(r.BatchKey),
            r.BatchKey == Guid.Empty ? null : batches.GetValueOrDefault(r.BatchKey), r.Quantity)).ToList();

        // Batch-tracked items keep their quantity on batch rows; the aggregate row (BatchKey empty) then only carries reservations.
        var batchedProducts = stock.Where(s => s.BatchKey != Guid.Empty).Select(s => (s.ProductId, s.VariantKey)).ToHashSet();
        foreach (var s in stock.Where(s => s.BatchKey != Guid.Empty || !batchedProducts.Contains((s.ProductId, s.VariantKey))))
        {
            var inBins = binRows.Where(r => r.ProductId == s.ProductId && r.VariantKey == s.VariantKey && r.BatchKey == s.BatchKey).Sum(r => r.Quantity);
            var unbinned = s.OnHand - inBins;
            if (unbinned != 0)
            {
                result.Add(new BinStockDto(null, null, s.ProductId, Keys.Nullable(s.VariantKey), Keys.Nullable(s.BatchKey),
                    s.BatchKey == Guid.Empty ? null : batches.GetValueOrDefault(s.BatchKey), unbinned));
            }
        }

        return result.OrderBy(r => r.ProductId).ThenBy(r => r.BatchNumber).ThenBy(r => r.BinCode ?? string.Empty).ToList();
    }
}

/// <summary>Moves stock between bins of one warehouse (a null bin = stock not in any bin). Quantity only: no ledger movement, no value.</summary>
public sealed record TransferBinStockCommand(Guid WarehouseId, Guid ProductId, Guid? VariantId, string? BatchNumber, Guid? FromBinId, Guid? ToBinId, decimal Quantity)
    : ICommand<IReadOnlyList<BinStockDto>>;

public sealed class TransferBinStockValidator : AbstractValidator<TransferBinStockCommand>
{
    public TransferBinStockValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}

public sealed class TransferBinStockHandler(IInventoryDbContext db, IInventoryStore store, PostingContextResolver contexts, BinStock binStock)
    : ICommandHandler<TransferBinStockCommand, IReadOnlyList<BinStockDto>>
{
    public async Task<Result<IReadOnlyList<BinStockDto>>> Handle(TransferBinStockCommand command, CancellationToken cancellationToken)
    {
        if (command.FromBinId == command.ToBinId)
        {
            return BinErrors.SameBin;
        }

        var ctx = await contexts.ResolveAsync([command.WarehouseId], null, cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        var bins = await binStock.ActiveBinsAsync(command.WarehouseId, cancellationToken);
        if ((command.FromBinId is { } f && bins.All(b => b.Id != f)) || (command.ToBinId is { } t && bins.All(b => b.Id != t)))
        {
            return BinErrors.NotFound;
        }

        var batchKey = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(command.BatchNumber))
        {
            var number = command.BatchNumber.Trim().ToUpperInvariant();
            var batch = await db.Batches.AsNoTracking().FirstOrDefaultAsync(b => b.ProductId == command.ProductId && b.Number == number, cancellationToken);
            if (batch is null)
            {
                return Error.NotFound("inventory.batch-not-found", $"Batch {number} was not found.");
            }

            batchKey = batch.Id;
        }

        var key = new StockKey(command.ProductId, Keys.Of(command.VariantId), command.WarehouseId, batchKey);
        await store.LockItemAsync(ctx.Value.CompanyId, key.ProductId, key.VariantKey, key.WarehouseId, cancellationToken);
        var rows = await db.BinBalances.Where(b => b.WarehouseId == key.WarehouseId && b.ProductId == key.ProductId && b.VariantKey == key.VariantKey && b.BatchKey == key.BatchKey)
            .ToListAsync(cancellationToken);
        var available = command.FromBinId is { } from
            ? rows.FirstOrDefault(r => r.BinId == from)?.Quantity ?? 0
            : await binStock.UnbinnedAsync(key, rows.Sum(r => r.Quantity), cancellationToken);
        if (available < command.Quantity)
        {
            return BinErrors.Insufficient;
        }

        var transferId = SequentialGuid.New();
        if (command.FromBinId is { } source)
        {
            await binStock.AddAsync(ctx.Value.CompanyId, source, key, -command.Quantity, BinMovementKinds.BinTransfer, null, BinMovementKinds.BinTransfer, transferId, cancellationToken);
        }

        if (command.ToBinId is { } target)
        {
            await binStock.AddAsync(ctx.Value.CompanyId, target, key, command.Quantity, BinMovementKinds.BinTransfer, null, BinMovementKinds.BinTransfer, transferId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(await BinStockQueryHandler.LoadAsync(db, new BinStockQuery(command.WarehouseId, command.ProductId), cancellationToken));
    }
}

internal static class BinMapping
{
    public static BinDto ToDto(StorageBin b) => new(b.Id, b.WarehouseId, b.Code, b.Name, b.IsDefaultReceiving, b.IsActive);
}

// ===================================================================== Recost
public sealed record RecostCorrectionDto(Guid MovementId, DateOnly TransactionDate, string MovementType, string SourceDocType, decimal BaseQuantity,
    decimal RecordedCost, decimal RecalculatedCost, decimal Difference);

public sealed record RecostRunDto(Guid Id, Guid ProductId, Guid? VariantId, Guid ValuationAreaId, DateOnly FromDate, string Trigger, string Status, string? Reference,
    DateOnly? PostingDate, decimal TotalDifference, Guid? JournalId, string? FailureReason, DateTime? CompletedAtUtc, bool Preview, IReadOnlyList<RecostCorrectionDto> Corrections);

internal static class RecostMapping
{
    public static RecostRunDto ToDto(RecostRun r, bool preview = false) => new(r.Id, r.ProductId, r.VariantId, r.ValuationAreaId, r.FromDate, r.Trigger.ToString(),
        preview ? "Preview" : r.Status.ToString(), r.Reference, r.PostingDate, r.TotalDifference, r.JournalId, r.FailureReason, r.CompletedAtUtc, preview,
        r.Corrections.Select(c => new RecostCorrectionDto(c.MovementId, c.TransactionDate, c.MovementType, c.SourceDocType, c.BaseQuantity, c.RecordedCost,
            c.RecalculatedCost, c.Difference)).ToList());
}

/// <summary>
/// Replays a weighted-average item's movements in its valuation area in transaction-date order and corrects the cost of the issues
/// whose cost changes (sales issues → cost of goods sold, adjustment losses → adjustment loss). Receipts, returns at original cost,
/// transfers, production issues and reversed movements keep their recorded values. Corrections are value-only movements dated on the
/// posting date with one balanced journal.
/// </summary>
public sealed class RecostEngine(
    IInventoryDbContext db,
    IInventoryStore store,
    PostingContextResolver contexts,
    ICatalogQueries catalog,
    IPeriodGuard periods,
    IJournalPostingService journals,
    ICurrentUser user,
    IClock clock)
{
    public const string SourceDocType = "InventoryRecost";
    public const string JournalPurpose = "inventory-recost";

    private static readonly IReadOnlyDictionary<string, string> CorrectableIssues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [InventoryMovementTypes.SalesIssue] = PostingRoles.CostOfGoodsSold,
        [InventoryMovementTypes.AdjustmentLoss] = PostingRoles.InventoryAdjustmentLoss,
    };

    public async Task<Result> RunAsync(RecostRun run, bool preview, CancellationToken ct)
    {
        var resolved = await contexts.ResolveAsync([run.WarehouseId], run.LegalEntityId, ct);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var ctx = resolved.Value;
        var product = (await catalog.GetProductsAsync([run.ProductId], ct)).FirstOrDefault();
        if (product is null)
        {
            return Error.NotFound("inventory.product-not-found", "A product was not found in this company.");
        }

        if (CostingMethods.Normalize(product.CostingMethod) == CostingMethods.Fifo)
        {
            return RecostErrors.FifoNotSupported;
        }

        var postingDate = DateOnly.FromDateTime(clock.UtcNow);
        if (!preview)
        {
            var open = await periods.EnsureOpenAsync(ctx.LegalEntity.Id, postingDate, ct);
            if (open.IsFailure)
            {
                return open.Error;
            }
        }

        var variantKey = Keys.Of(run.VariantId);
        await store.LockValuationAsync(ctx.CompanyId, run.ValuationAreaId, run.ProductId, variantKey, CostingMethods.WeightedAverage, ct);
        var valuation = await db.Valuations.FirstAsync(v => v.ValuationAreaId == run.ValuationAreaId && v.ProductId == run.ProductId && v.VariantKey == variantKey, ct);
        if (valuation.CostingMethod == CostingMethods.Fifo)
        {
            return RecostErrors.FifoNotSupported;
        }

        var movements = await db.Movements.AsNoTracking()
            .Where(m => m.ValuationAreaId == run.ValuationAreaId && m.ProductId == run.ProductId && m.VariantId == run.VariantId)
            .ToListAsync(ct);
        var corrections = Replay(movements, run.FromDate);
        if (preview || corrections.Count == 0)
        {
            run.Complete(postingDate, corrections, null, clock.UtcNow);
            return Result.Success();
        }

        // The journal is posted first: if it fails nothing has been changed in Inventory yet.
        var lines = new List<JournalLineRequest>();
        var byId = movements.ToDictionary(m => m.Id);
        foreach (var correction in corrections)
        {
            var issue = byId[correction.MovementId];
            var warehouse = ctx.Warehouses.TryGetValue(issue.WarehouseId, out var known) ? known : null;
            var stockContext = new PostingContext(ProductPostingGroup: product.PostingGroup, WarehouseId: issue.WarehouseId);
            var counterContext = new PostingContext(ProductPostingGroup: product.PostingGroup);
            var stockRole = warehouse is null ? PostingRoles.Inventory : InventoryPostingService.StockRole(warehouse);
            var amount = Math.Abs(correction.Difference);
            var counter = CorrectableIssues[issue.MovementType];
            var description = $"Recost {issue.SourceDocType} {issue.ReferenceNo}".Trim();
            if (correction.Difference > 0)
            {
                lines.Add(new JournalLineRequest(counter, null, amount, 0, counterContext, Description: description));
                lines.Add(new JournalLineRequest(stockRole, null, 0, amount, stockContext, Description: description));
            }
            else
            {
                lines.Add(new JournalLineRequest(stockRole, null, amount, 0, stockContext, Description: description));
                lines.Add(new JournalLineRequest(counter, null, 0, amount, counterContext, Description: description));
            }
        }

        var journal = await journals.PostAsync(new JournalRequest(ctx.LegalEntity.Id, postingDate, SourceModules.Inventory, SourceDocType, run.Id, JournalPurpose,
            $"Inventory recost {product.Sku} from {run.FromDate:yyyy-MM-dd}", ctx.LegalEntity.FunctionalCurrency, 1m, lines), ct);
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        foreach (var correction in corrections)
        {
            var issue = byId[correction.MovementId];
            var valueChange = correction.RecordedCost - correction.RecalculatedCost; // inventory value goes up when the issue was over-costed
            valuation.Apply(Changed(valuation.ToState(), valueChange));
            db.Movements.Add(new InventoryMovement(ctx.CompanyId, issue.LegalEntityId, issue.ValuationAreaId, valuation.NextSequence(), issue.ProductId, issue.VariantId,
                issue.WarehouseId, null, null, InventoryMovementTypes.CostAdjustment, 0, issue.UomId, 0, 0, valueChange, postingDate, SourceModules.Inventory,
                SourceDocType, run.Id, issue.Id, run.Reference, null, JournalPurpose, clock.UtcNow, user.UserId, issue.Id));
        }

        run.Complete(postingDate, corrections, journal.Value.JournalId, clock.UtcNow);
        return Result.Success();
    }

    private static ValuationState Changed(ValuationState state, decimal valueChange)
    {
        var value = state.Value + valueChange;
        return state with { Value = value, AverageCost = state.Quantity > 0 ? InventoryRounding.Cost(value / state.Quantity) : state.AverageCost };
    }

    /// <summary>Pure replay (weighted average, transaction-date order). Returns the issues dated on or after <paramref name="fromDate"/> whose cost changes.</summary>
    public static IReadOnlyList<RecostCorrection> Replay(IReadOnlyList<InventoryMovement> movements, DateOnly fromDate)
    {
        var reversedIds = movements.Where(m => m.ReversalOfId != null).Select(m => m.ReversalOfId!.Value).ToHashSet();
        var earlierCorrections = movements.Where(m => m.SourceDocType == SourceDocType && m.RelatedMovementId != null)
            .GroupBy(m => m.RelatedMovementId!.Value).ToDictionary(g => g.Key, g => g.Sum(m => m.TotalCost));
        var ordered = movements
            .Where(m => m.SourceDocType != SourceDocType && (m.PostingSequence > 0 || m.MovementType == InventoryMovementTypes.CostAdjustment))
            .OrderBy(m => m.TransactionDate).ThenBy(m => m.PostingSequence).ThenBy(m => m.CreatedAtUtc).ToList();
        var quantity = 0m;
        var value = 0m;
        var corrections = new List<RecostCorrection>();
        foreach (var m in ordered)
        {
            if (m.MovementType == InventoryMovementTypes.CostAdjustment || m.BaseQuantity >= 0)
            {
                quantity += m.BaseQuantity;
                value += m.TotalCost;
                continue;
            }

            var issued = -m.BaseQuantity;
            var recorded = -m.TotalCost - earlierCorrections.GetValueOrDefault(m.Id);
            var correctable = CorrectableIssues.ContainsKey(m.MovementType) && m.ReversalOfId == null && !reversedIds.Contains(m.Id) && m.RelatedMovementId == null;
            if (!correctable || quantity <= 0 || issued > quantity)
            {
                quantity -= issued;
                value -= recorded;
                continue;
            }

            var recalculated = issued == quantity ? value : InventoryRounding.Money(issued * InventoryRounding.Cost(value / quantity));
            quantity -= issued;
            value -= recalculated;
            if (m.TransactionDate >= fromDate && Math.Abs(recalculated - recorded) >= 0.01m)
            {
                corrections.Add(new RecostCorrection
                {
                    MovementId = m.Id,
                    TransactionDate = m.TransactionDate,
                    MovementType = m.MovementType,
                    SourceDocType = m.SourceDocType,
                    BaseQuantity = m.BaseQuantity,
                    RecordedCost = recorded,
                    RecalculatedCost = recalculated,
                });
            }
            else if (m.TransactionDate < fromDate)
            {
                // Before the recost window the recorded cost stands; keep the running value consistent with the ledger.
                value += recalculated - recorded;
            }
        }

        return corrections;
    }
}

public sealed record RunRecostCommand(Guid ProductId, Guid? VariantId, Guid WarehouseId, DateOnly FromDate, bool Preview = false) : ICommand<RecostRunDto>;

public sealed class RunRecostValidator : AbstractValidator<RunRecostCommand>
{
    public RunRecostValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
    }
}

public sealed class RunRecostHandler(IInventoryDbContext db, PostingContextResolver contexts, RecostEngine engine) : ICommandHandler<RunRecostCommand, RecostRunDto>
{
    public async Task<Result<RecostRunDto>> Handle(RunRecostCommand command, CancellationToken cancellationToken)
    {
        var ctx = await contexts.ResolveAsync([command.WarehouseId], null, cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        var run = new RecostRun(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, ctx.Value.AreaOf(command.WarehouseId), command.WarehouseId, command.ProductId,
            command.VariantId, command.FromDate, RecostTrigger.Manual, "Manual recost");
        var result = await engine.RunAsync(run, command.Preview, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        if (!command.Preview)
        {
            db.RecostRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
        }

        return RecostMapping.ToDto(run, command.Preview);
    }
}

public sealed record ListRecostRunsQuery(Guid? ProductId) : IQuery<IReadOnlyList<RecostRunDto>>;

public sealed class ListRecostRunsHandler(IInventoryDbContext db) : IQueryHandler<ListRecostRunsQuery, IReadOnlyList<RecostRunDto>>
{
    public async Task<Result<IReadOnlyList<RecostRunDto>>> Handle(ListRecostRunsQuery query, CancellationToken cancellationToken)
    {
        var source = db.RecostRuns.AsNoTracking();
        if (query.ProductId is { } productId)
        {
            source = source.Where(r => r.ProductId == productId);
        }

        var runs = await source.OrderByDescending(r => r.CompletedAtUtc).Take(200).ToListAsync(cancellationToken);
        return runs.Select(r => RecostMapping.ToDto(r)).ToList();
    }
}

public sealed record GetRecostRunQuery(Guid Id) : IQuery<RecostRunDto>;

public sealed class GetRecostRunHandler(IInventoryDbContext db) : IQueryHandler<GetRecostRunQuery, RecostRunDto>
{
    public async Task<Result<RecostRunDto>> Handle(GetRecostRunQuery query, CancellationToken cancellationToken)
    {
        var run = await db.RecostRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
        return run is null ? RecostErrors.NotFound : RecostMapping.ToDto(run);
    }
}

/// <summary>Runs a recost queued by a back-dated posting (after that posting committed). Failures are recorded on the run, not retried.</summary>
public sealed class RecostRequestedHandler(
    IUnitOfWork unitOfWork,
    IInbox inbox,
    IOutboxMessageContext message,
    IInventoryDbContext db,
    ITenantContext tenant,
    RecostEngine engine,
    IClock clock,
    ILogger<RecostRequestedHandler> logger) : InboxIntegrationEventHandler<RecostRequested>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(RecostRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(integrationEvent.CompanyId);
        }

        var run = await db.RecostRuns.FirstOrDefaultAsync(r => r.Id == integrationEvent.RecostRunId, cancellationToken);
        if (run is not { Status: RecostStatus.Pending })
        {
            return;
        }

        var result = await engine.RunAsync(run, preview: false, cancellationToken);
        if (result.IsFailure)
        {
            logger.LogWarning("Recost run {RunId} failed: {Code} {Message}", run.Id, result.Error.Code, result.Error.Message);
            run.Fail($"{result.Error.Code}: {result.Error.Message}", clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}


