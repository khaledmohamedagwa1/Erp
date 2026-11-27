using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Application;

public interface IInventoryDbContext
{
    DbSet<InventoryMovement> Movements { get; }

    DbSet<InventoryBalance> Balances { get; }

    DbSet<ItemValuation> Valuations { get; }

    DbSet<CostLayer> CostLayers { get; }

    DbSet<LayerConsumption> LayerConsumptions { get; }

    DbSet<Batch> Batches { get; }

    DbSet<SerialNumber> Serials { get; }

    DbSet<StockReservation> Reservations { get; }

    DbSet<OpeningStockDocument> OpeningStocks { get; }

    DbSet<StockAdjustment> Adjustments { get; }

    DbSet<StockTransfer> Transfers { get; }

    DbSet<StockCount> Counts { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public enum StockApplyStatus
{
    Applied,
    Insufficient,
}

/// <summary>
/// Set-based stock updates on the current transaction (implemented with SQL in Infrastructure). Each method locks the item's
/// aggregate row (BatchKey = Empty) first, then the batch row — always in that order (§19.4).
/// </summary>
public interface IInventoryStore
{
    Task ReceiveAsync(Guid companyId, StockKey key, decimal baseQuantity, CancellationToken cancellationToken);

    /// <summary>
    /// Takes <paramref name="baseQuantity"/> out and releases <paramref name="consumeReserved"/> of the item's reservations.
    /// Refuses (unless <paramref name="allowNegative"/>) when on-hand − reserved would go below zero for the item, or on-hand
    /// would go below zero for the batch.
    /// </summary>
    Task<StockApplyStatus> IssueAsync(Guid companyId, StockKey key, decimal baseQuantity, decimal consumeReserved, bool allowNegative, CancellationToken cancellationToken);

    Task<StockApplyStatus> ReserveAsync(Guid companyId, Guid productId, Guid variantKey, Guid warehouseId, decimal baseQuantity, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid companyId, Guid productId, Guid variantKey, Guid warehouseId, decimal baseQuantity, CancellationToken cancellationToken);

    /// <summary>Creates the valuation row if missing and holds an update lock on it until the transaction ends.</summary>
    Task LockValuationAsync(Guid companyId, Guid valuationAreaId, Guid productId, Guid variantKey, string costingMethod, CancellationToken cancellationToken);
}

internal static class Keys
{
    public static Guid Of(Guid? id) => id ?? Guid.Empty;

    public static Guid? Nullable(Guid key) => key == Guid.Empty ? null : key;
}

/// <summary>Resolves warehouses, legal entity, company and policy for a posting and sets the company context if missing.</summary>
public sealed class PostingContextResolver(
    IOrganizationQueries organization,
    IPolicyReader policies,
    ITenantContext tenant)
{
    public sealed record Resolved(
        Guid CompanyId,
        LegalEntitySummary LegalEntity,
        IReadOnlyDictionary<Guid, WarehouseSummary> Warehouses,
        InventoryPolicySettings Policy)
    {
        public Guid AreaOf(Guid warehouseId) =>
            string.Equals(Policy.ValuationArea, "Warehouse", StringComparison.OrdinalIgnoreCase) ? warehouseId : LegalEntity.Id;
    }

    /// <summary>Sets the company context from a legal entity when none is selected (internal callers).</summary>
    public async Task<bool> EnsureCompanyForLegalEntityAsync(Guid legalEntityId, CancellationToken ct)
    {
        var legalEntity = await organization.GetLegalEntityAsync(legalEntityId, ct);
        if (legalEntity is null)
        {
            return false;
        }

        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(legalEntity.CompanyId);
        }

        return tenant.CompanyId == legalEntity.CompanyId;
    }

    public async Task<Result<Resolved>> ResolveAsync(IEnumerable<Guid> warehouseIds, Guid? legalEntityId, CancellationToken ct)
    {
        var ids = warehouseIds.Distinct().ToList();
        var warehouses = (await organization.GetWarehousesAsync(ids, ct)).ToDictionary(w => w.Id);
        if (warehouses.Count != ids.Count)
        {
            return Error.NotFound("inventory.warehouse-not-found", "A warehouse was not found.");
        }

        if (warehouses.Values.Any(w => !w.IsActive))
        {
            return Error.Conflict("inventory.warehouse-inactive", "A warehouse is inactive.");
        }

        var legalEntities = warehouses.Values.Select(w => w.LegalEntityId).Distinct().ToList();
        if (legalEntities.Count != 1)
        {
            return InventoryErrors.CrossLegalEntityTransfer;
        }

        if (legalEntityId is { } requested && requested != legalEntities[0])
        {
            return Error.Validation("inventory.legal-entity-mismatch", "The warehouses do not belong to the requested legal entity.");
        }

        var companyId = warehouses.Values.First().CompanyId;
        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(companyId);
        }
        else if (tenant.CompanyId != companyId)
        {
            return Error.Forbidden("inventory.warehouse-not-in-company", "The warehouse does not belong to the active company.");
        }

        var legalEntity = await organization.GetLegalEntityAsync(legalEntities[0], ct)
            ?? throw new InvalidOperationException("Warehouse references a missing legal entity.");
        var policy = await policies.GetInventoryPolicyAsync(companyId, ct);
        return new Resolved(companyId, legalEntity, warehouses, policy);
    }
}

/// <summary>
/// The inventory posting engine (contract <see cref="IInventoryPostingService"/>). Everything runs in the caller's transaction:
/// balances and valuation are locked and updated, the immutable ledger is appended, FIFO layers consumed, serials and batches
/// updated, and — when asked — a balanced journal is posted through Accounting. Any failure must roll the transaction back.
/// </summary>
public sealed class InventoryPostingService(
    IInventoryDbContext db,
    IInventoryStore store,
    IUnitOfWork unitOfWork,
    PostingContextResolver contexts,
    ICatalogQueries catalog,
    IPeriodGuard periods,
    IJournalPostingService journals,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IClock clock) : IInventoryPostingService
{
    public const string JournalPurpose = "inventory";
    public const string TrueUpPurpose = "inventory-true-up";

    private sealed record WorkItem(
        InventoryPostingLine Line,
        int LineIndex,
        ProductInfo Product,
        StockKey Key,
        decimal BaseQuantity,
        decimal Quantity,
        Batch? Batch,
        string? Serial,
        Guid AreaId,
        bool SkipValuation);

    private sealed record Posted(WorkItem Work, InventoryMovement Movement);

    public async Task<Result<InventoryPostingResult>> PostAsync(InventoryPostingRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return InventoryErrors.TransactionRequired;
        }

        var existing = await TenantMovements()
            .Where(m => m.SourceDocType == request.SourceDocType && m.SourceDocId == request.SourceDocId && m.ReversalOfId == null)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return BuildResult(existing, null, alreadyPosted: true, existing[0].LegalEntityId);
        }

        _serials.Clear();
        var validation = ValidateRequest(request);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var warehouseIds = request.Lines.Select(l => l.WarehouseId)
            .Concat(request.Lines.Where(l => l.CounterWarehouseId is not null).Select(l => l.CounterWarehouseId!.Value));
        var resolved = await contexts.ResolveAsync(warehouseIds, request.LegalEntityId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var ctx = resolved.Value;
        var dateCheck = await CheckDateAsync(ctx, request.TransactionDate, cancellationToken);
        if (dateCheck.IsFailure)
        {
            return dateCheck.Error;
        }

        var products = (await catalog.GetProductsAsync(request.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var work = new List<WorkItem>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var expanded = await ExpandAsync(ctx, request, request.Lines[i], i, products, cancellationToken);
            if (expanded.IsFailure)
            {
                return expanded.Error;
            }

            work.AddRange(expanded.Value);
        }

        var serialCheck = await PrepareSerialsAsync(ctx.CompanyId, work, cancellationToken);
        if (serialCheck.IsFailure)
        {
            return serialCheck.Error;
        }

        var journalPurpose = request.PostJournal && request.Lines.Any(l => l.CounterRole is not null) ? JournalPurpose : null;
        var posted = new List<Posted>();
        var trueUps = new List<(WorkItem Work, decimal Amount)>();
        foreach (var item in LockOrder.Sort(work, w => w.Key))
        {
            var result = await PostItemAsync(ctx, request, item, journalPurpose, trueUps, cancellationToken);
            if (result.IsFailure)
            {
                return result.Error;
            }

            posted.Add(new Posted(item, result.Value));
        }

        ApplySerialMoves(posted);

        // One true-up per line (serial lines are expanded into several items).
        trueUps = trueUps.GroupBy(t => t.Work.Line.LineId)
            .Select(g => (g.First().Work, g.Sum(t => t.Amount)))
            .Where(t => t.Item2 != 0)
            .ToList();
        foreach (var (item, amount) in trueUps)
        {
            db.Movements.Add(NewMovement(ctx, request, item, InventoryMovementTypes.CostAdjustment, 0, 0, 0, -amount, 0, TrueUpPurpose, null, relatedMovementId: null));
        }

        await db.SaveChangesAsync(cancellationToken);

        Guid? journalId = null;
        if (journalPurpose is not null)
        {
            var journal = await PostStockJournalAsync(ctx, request, posted, cancellationToken);
            if (journal.IsFailure)
            {
                return journal.Error;
            }

            journalId = journal.Value;
        }

        if (trueUps.Count > 0)
        {
            var trueUp = await PostTrueUpJournalAsync(ctx, request, trueUps, cancellationToken);
            if (trueUp.IsFailure)
            {
                return trueUp.Error;
            }
        }

        return BuildResult(posted.Select(p => p.Movement).ToList(), journalId, alreadyPosted: false, ctx.LegalEntity.Id, products, posted);
    }

    private static Result ValidateRequest(InventoryPostingRequest request)
    {
        if (request.Lines.Count == 0)
        {
            return Error.Validation("inventory.lines-required", "At least one line is required.");
        }

        if (request.Lines.Select(l => l.LineId).Distinct().Count() != request.Lines.Count)
        {
            return Error.Validation("inventory.duplicate-line", "Line ids must be unique within a posting.");
        }

        foreach (var line in request.Lines)
        {
            var inbound = InventoryMovementTypes.Inbound.Contains(line.MovementType);
            var outbound = InventoryMovementTypes.Outbound.Contains(line.MovementType);
            if (!inbound && !outbound)
            {
                return Error.Validation("inventory.movement-type-invalid", $"'{line.MovementType}' cannot be posted directly.");
            }

            if (line.Quantity == 0 || (inbound && line.Quantity < 0) || (outbound && line.Quantity > 0))
            {
                return Error.Validation("inventory.quantity-sign", $"{line.MovementType} quantities must be {(inbound ? "positive" : "negative")}.");
            }

            if (line.UnitCost is < 0)
            {
                return Error.Validation("inventory.cost-invalid", "Unit cost cannot be negative.");
            }

            if (request.PostJournal && line.CounterRole is not null && !PostingRoles.IsKnown(line.CounterRole))
            {
                return Error.Validation("inventory.counter-role-invalid", $"Unknown posting role '{line.CounterRole}'.");
            }
        }

        return Result.Success();
    }

    private async Task<Result> CheckDateAsync(PostingContextResolver.Resolved ctx, DateOnly date, CancellationToken ct)
    {
        if (string.Equals(ctx.Policy.BackdatedPostings, "NotAllowed", StringComparison.OrdinalIgnoreCase) &&
            date < DateOnly.FromDateTime(clock.UtcNow))
        {
            return Error.Validation("inventory.backdating-not-allowed", "Back-dated inventory postings are not allowed by policy.");
        }

        return await periods.EnsureOpenAsync(ctx.LegalEntity.Id, date, ct);
    }

    private async Task<Result<IReadOnlyList<WorkItem>>> ExpandAsync(
        PostingContextResolver.Resolved ctx, InventoryPostingRequest request, InventoryPostingLine line, int index,
        IReadOnlyDictionary<Guid, ProductInfo> products, CancellationToken ct)
    {
        if (!products.TryGetValue(line.ProductId, out var product))
        {
            return Error.NotFound("inventory.product-not-found", "A product was not found in this company.");
        }

        if (!product.IsStockable)
        {
            return Error.Validation("inventory.product-not-stockable", $"{product.Sku} is not a stocked product.");
        }

        var unit = product.Units.FirstOrDefault(u => u.UomId == line.UomId);
        if (unit is null)
        {
            return Error.Validation("inventory.uom-invalid", $"{product.Sku} has no such unit.");
        }

        var baseQuantity = InventoryRounding.Quantity(line.Quantity * unit.FactorToBase);
        var inbound = baseQuantity > 0;
        var area = ctx.AreaOf(line.WarehouseId);
        var skipValuation = line.MovementType is InventoryMovementTypes.TransferIn or InventoryMovementTypes.TransferOut &&
                            line.CounterWarehouseId is { } counter && ctx.AreaOf(counter) == area;
        var variantKey = Keys.Of(line.VariantId);
        var items = new List<WorkItem>();

        if (product.Tracking == "Serial" && ctx.Policy.EnableSerialTracking)
        {
            var serials = line.Serials ?? [];
            if (Math.Abs(baseQuantity) != serials.Count || serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Count)
            {
                return Error.Validation("inventory.serials-mismatch", $"{product.Sku}: give exactly one distinct serial number per base unit.");
            }

            var perUnit = line.Quantity / Math.Abs(baseQuantity);
            foreach (var serial in serials)
            {
                items.Add(new WorkItem(line, index, product, new StockKey(product.Id, variantKey, line.WarehouseId, Guid.Empty),
                    Math.Sign(baseQuantity), perUnit, null, serial.Trim(), area, skipValuation));
            }

            return items;
        }

        if (product.Tracking == "Batch" && ctx.Policy.EnableBatchTracking)
        {
            if (inbound)
            {
                if (line.Batch is null || string.IsNullOrWhiteSpace(line.Batch.Number))
                {
                    return Error.Validation("inventory.batch-required", $"{product.Sku} is batch-tracked: a batch number is required.");
                }

                if (product.ExpiryTracked && ctx.Policy.EnableExpiryTracking && line.Batch.ExpiryDate is null &&
                    line.MovementType is InventoryMovementTypes.PurchaseReceipt or InventoryMovementTypes.OpeningStock or InventoryMovementTypes.ProductionReceipt)
                {
                    return Error.Validation("inventory.expiry-required", $"{product.Sku} is expiry-tracked: an expiry date is required.");
                }

                var batch = await FindOrCreateBatchAsync(ctx.CompanyId, product.Id, line.Batch, ct);
                items.Add(new WorkItem(line, index, product, new StockKey(product.Id, variantKey, line.WarehouseId, batch.Id), baseQuantity, line.Quantity, batch, null, area, skipValuation));
                return items;
            }

            if (line.Batch is not null && !string.IsNullOrWhiteSpace(line.Batch.Number))
            {
                var number = line.Batch.Number.Trim().ToUpperInvariant();
                var batch = await db.Batches.FirstOrDefaultAsync(b => b.ProductId == product.Id && b.Number == number, ct);
                if (batch is null)
                {
                    return Error.NotFound("inventory.batch-not-found", $"Batch {number} of {product.Sku} was not found.");
                }

                if (IsIssueBlockedByExpiry(line, batch, request.TransactionDate, ctx.Policy))
                {
                    return InventoryErrors.ExpiredBatch;
                }

                items.Add(new WorkItem(line, index, product, new StockKey(product.Id, variantKey, line.WarehouseId, batch.Id), baseQuantity, line.Quantity, batch, null, area, skipValuation));
                return items;
            }

            // FEFO allocation (suggested order, OQ-INV-7): earliest expiry first, expired batches skipped (OQ-INV-8).
            var available = await (from b in db.Balances.AsNoTracking()
                                   join batch in db.Batches.AsNoTracking() on b.BatchKey equals batch.Id
                                   where b.ProductId == product.Id && b.VariantKey == variantKey && b.WarehouseId == line.WarehouseId && b.OnHand > 0
                                   orderby batch.ExpiryDate == null, batch.ExpiryDate, batch.Number
                                   select new { Batch = batch, b.OnHand }).ToListAsync(ct);
            var needed = -baseQuantity;
            foreach (var candidate in available)
            {
                if (needed == 0)
                {
                    break;
                }

                if (IsIssueBlockedByExpiry(line, candidate.Batch, request.TransactionDate, ctx.Policy))
                {
                    continue;
                }

                var take = Math.Min(needed, candidate.OnHand);
                var share = InventoryRounding.Quantity(line.Quantity * (take / -baseQuantity));
                items.Add(new WorkItem(line, index, product, new StockKey(product.Id, variantKey, line.WarehouseId, candidate.Batch.Id), -take, share, candidate.Batch, null, area, skipValuation));
                needed -= take;
            }

            if (needed > 0)
            {
                return InventoryErrors.InsufficientStock with { Message = $"Not enough unexpired stock of {product.Sku} in the warehouse." };
            }

            return items;
        }

        items.Add(new WorkItem(line, index, product, new StockKey(product.Id, variantKey, line.WarehouseId, Guid.Empty), baseQuantity, line.Quantity, null, null, area, skipValuation));
        return items;
    }

    private static bool IsIssueBlockedByExpiry(InventoryPostingLine line, Batch batch, DateOnly date, InventoryPolicySettings policy) =>
        policy.EnableExpiryTracking && batch.IsExpiredOn(date) &&
        line.MovementType is InventoryMovementTypes.SalesIssue or InventoryMovementTypes.TransferOut or InventoryMovementTypes.ProductionIssue;

    private async Task<Batch> FindOrCreateBatchAsync(Guid companyId, Guid productId, BatchInput input, CancellationToken ct)
    {
        var number = input.Number.Trim().ToUpperInvariant();
        var batch = db.Batches.Local.FirstOrDefault(b => b.ProductId == productId && b.Number == number)
                    ?? await db.Batches.FirstOrDefaultAsync(b => b.ProductId == productId && b.Number == number, ct);
        if (batch is null)
        {
            batch = new Batch(companyId, productId, number, input.ManufactureDate, input.ExpiryDate, input.SupplierId);
            db.Batches.Add(batch);
        }

        return batch;
    }

    private readonly Dictionary<(Guid ProductId, string Serial), SerialNumber> _serials = [];

    private async Task<Result> PrepareSerialsAsync(Guid companyId, IReadOnlyList<WorkItem> work, CancellationToken ct)
    {
        var serialItems = work.Where(w => w.Serial is not null).ToList();
        if (serialItems.Count == 0)
        {
            return Result.Success();
        }

        var productIds = serialItems.Select(w => w.Product.Id).Distinct().ToList();
        var numbers = serialItems.Select(w => w.Serial!).Distinct().ToList();
        var existing = await db.Serials.Where(s => productIds.Contains(s.ProductId) && numbers.Contains(s.Serial)).ToListAsync(ct);
        foreach (var serial in existing)
        {
            _serials[(serial.ProductId, serial.Serial.ToUpperInvariant())] = serial;
        }

        var leaving = serialItems.Where(w => w.BaseQuantity < 0).Select(w => (w.Product.Id, w.Serial!.ToUpperInvariant())).ToHashSet();
        foreach (var item in serialItems)
        {
            _serials.TryGetValue((item.Product.Id, item.Serial!.ToUpperInvariant()), out var serial);
            if (item.BaseQuantity < 0)
            {
                if (serial is null || serial.Status != SerialStatus.InStock || serial.CurrentWarehouseId != item.Line.WarehouseId)
                {
                    return Error.Conflict("inventory.serial-not-in-stock", $"Serial {item.Serial} of {item.Product.Sku} is not in stock in the warehouse.");
                }
            }
            else if (serial is not null && serial.Status == SerialStatus.InStock && !leaving.Contains((item.Product.Id, item.Serial!.ToUpperInvariant())))
            {
                return Error.Conflict("inventory.serial-in-stock", $"Serial {item.Serial} of {item.Product.Sku} is already in stock.");
            }
            else if (serial is null)
            {
                serial = new SerialNumber(companyId, item.Product.Id, item.Serial!, item.Line.WarehouseId);
                db.Serials.Add(serial);
                _serials[(item.Product.Id, item.Serial!.ToUpperInvariant())] = serial;
            }
        }

        return Result.Success();
    }

    private void ApplySerialMoves(IReadOnlyList<Posted> posted)
    {
        // Outbound first, then inbound, so a serial moving between warehouses in one posting ends up in stock at the destination.
        foreach (var p in posted.Where(p => p.Work.Serial is not null).OrderBy(p => p.Work.BaseQuantity > 0))
        {
            var serial = _serials[(p.Work.Product.Id, p.Work.Serial!.ToUpperInvariant())];
            if (p.Work.BaseQuantity < 0)
            {
                serial.Move(null, p.Work.Line.MovementType == InventoryMovementTypes.TransferOut ? SerialStatus.InTransit : SerialStatus.Issued, p.Movement.Id);
            }
            else
            {
                serial.Move(p.Work.Line.WarehouseId, SerialStatus.InStock, p.Movement.Id);
            }
        }
    }

    private async Task<Result<InventoryMovement>> PostItemAsync(
        PostingContextResolver.Resolved ctx, InventoryPostingRequest request, WorkItem item, string? journalPurpose,
        List<(WorkItem Work, decimal Amount)> trueUps, CancellationToken ct)
    {
        var method = CostingMethods.Normalize(item.Product.CostingMethod);
        var allowNegative = ctx.Policy.AllowNegativeStock && method == CostingMethods.WeightedAverage;
        var line = item.Line;
        var inbound = item.BaseQuantity > 0;
        var quantity = Math.Abs(item.BaseQuantity);

        InventoryMovement? costSource = null;
        if (line.CostSourceMovementId is { } sourceId)
        {
            costSource = await db.Movements.AsNoTracking().FirstOrDefaultAsync(m => m.Id == sourceId, ct);
            if (costSource is null || costSource.ProductId != item.Product.Id)
            {
                return Error.NotFound("inventory.cost-source-not-found", "The movement whose cost should be used was not found for this product.");
            }
        }

        // 1. Quantity (balance) — set-based with the negative check.
        if (inbound)
        {
            await store.ReceiveAsync(ctx.CompanyId, item.Key, quantity, ct);
        }
        else
        {
            var consume = 0m;
            if (line.ConsumeReservation is { } reservationRef)
            {
                var reservation = await db.Reservations.FirstOrDefaultAsync(r =>
                    r.DemandType == reservationRef.DemandType && r.DemandId == reservationRef.DemandId && r.DemandLineId == reservationRef.DemandLineId &&
                    r.ProductId == item.Product.Id && r.WarehouseId == line.WarehouseId, ct);
                consume = reservation?.Consume(quantity) ?? 0;
            }

            if (await store.IssueAsync(ctx.CompanyId, item.Key, quantity, consume, allowNegative, ct) == StockApplyStatus.Insufficient)
            {
                return InventoryErrors.InsufficientStock with { Message = $"Not enough stock of {item.Product.Sku} in the warehouse." };
            }
        }

        // 2. Value.
        decimal unitCost;
        decimal totalCost;
        long sequence = 0;
        if (item.SkipValuation)
        {
            var current = await db.Valuations.AsNoTracking()
                .FirstOrDefaultAsync(v => v.ValuationAreaId == item.AreaId && v.ProductId == item.Product.Id && v.VariantKey == item.Key.VariantKey, ct);
            unitCost = inbound && line.UnitCost is { } given ? given : current is { Quantity: > 0 } ? InventoryRounding.Cost(current.Value / current.Quantity) : current?.LastCost ?? 0;
            totalCost = InventoryRounding.Money(item.BaseQuantity * unitCost);
            var movement = NewMovement(ctx, request, item, line.MovementType, item.Quantity, item.BaseQuantity, unitCost, totalCost, 0, journalPurpose, item.Batch?.Id, null);
            db.Movements.Add(movement);
            return movement;
        }

        await store.LockValuationAsync(ctx.CompanyId, item.AreaId, item.Product.Id, item.Key.VariantKey, method, ct);
        var valuation = await db.Valuations.FirstAsync(v => v.ValuationAreaId == item.AreaId && v.ProductId == item.Product.Id && v.VariantKey == item.Key.VariantKey, ct);
        valuation.SetMethodIfUnused(method);
        var state = valuation.ToState();
        InventoryMovement posted;

        if (inbound)
        {
            var cost = line.UnitCost ?? costSource?.UnitCost;
            if (cost is null && line.MovementType == InventoryMovementTypes.AdjustmentGain)
            {
                cost = state.Quantity > 0 ? InventoryRounding.Cost(state.Value / state.Quantity) : state.LastCost > 0 ? state.LastCost : null;
            }

            if (cost is null)
            {
                return InventoryErrors.CostRequired with { Message = $"A unit cost is required for {item.Product.Sku}." };
            }

            ReceiptOutcome outcome;
            if (valuation.CostingMethod == CostingMethods.Fifo)
            {
                if (state.Quantity < 0)
                {
                    return InventoryErrors.FifoNegativeNotAllowed;
                }

                outcome = Fifo.Receive(state, quantity, cost.Value);
            }
            else
            {
                outcome = WeightedAverage.Receive(state, quantity, cost.Value);
            }

            sequence = valuation.NextSequence();
            valuation.Apply(outcome.State);
            unitCost = cost.Value;
            totalCost = outcome.TotalCost;
            posted = NewMovement(ctx, request, item, line.MovementType, item.Quantity, item.BaseQuantity, unitCost, totalCost, sequence, journalPurpose, item.Batch?.Id, costSource?.Id);
            db.Movements.Add(posted);
            if (valuation.CostingMethod == CostingMethods.Fifo)
            {
                db.CostLayers.Add(new CostLayer(ctx.CompanyId, item.AreaId, item.Product.Id, item.Key.VariantKey, posted.Id, sequence, quantity, cost.Value, clock.UtcNow));
            }

            if (outcome.TrueUp != 0)
            {
                trueUps.Add((item, outcome.TrueUp));
            }

            return posted;
        }

        if (valuation.CostingMethod == CostingMethods.Fifo)
        {
            var layers = await db.CostLayers
                .Where(l => l.ValuationAreaId == item.AreaId && l.ProductId == item.Product.Id && l.VariantKey == item.Key.VariantKey && l.RemainingQuantity > 0)
                .OrderBy(l => l.Sequence).ToListAsync(ct);
            if (costSource is not null)
            {
                // Purchase return at original receipt cost: take from that receipt's layer first.
                layers = layers.OrderBy(l => l.ReceiptMovementId == costSource.Id ? 0 : 1).ThenBy(l => l.Sequence).ToList();
            }

            var issue = Fifo.Issue(state, layers.Select(l => new LayerSlice(l.Id, l.RemainingQuantity, l.UnitCost)), quantity);
            if (issue.IsFailure)
            {
                return issue.Error with { Message = $"Not enough stock of {item.Product.Sku} in the valuation area." };
            }

            sequence = valuation.NextSequence();
            valuation.Apply(issue.Value.Outcome.State);
            unitCost = issue.Value.Outcome.UnitCost;
            totalCost = -issue.Value.Outcome.TotalCost;
            posted = NewMovement(ctx, request, item, line.MovementType, item.Quantity, item.BaseQuantity, unitCost, totalCost, sequence, journalPurpose, item.Batch?.Id, costSource?.Id);
            db.Movements.Add(posted);
            foreach (var take in issue.Value.Takes)
            {
                layers.First(l => l.Id == take.LayerId).RemainingQuantity -= take.Quantity;
                db.LayerConsumptions.Add(new LayerConsumption(ctx.CompanyId, take.LayerId, posted.Id, take.Quantity, take.UnitCost));
            }

            return posted;
        }

        var wac = WeightedAverage.Issue(state, quantity, allowNegative, costSource?.UnitCost);
        if (wac.IsFailure)
        {
            return wac.Error with { Message = $"Not enough stock of {item.Product.Sku} in the valuation area." };
        }

        sequence = valuation.NextSequence();
        valuation.Apply(wac.Value.State);
        unitCost = wac.Value.UnitCost;
        totalCost = -wac.Value.TotalCost;
        posted = NewMovement(ctx, request, item, line.MovementType, item.Quantity, item.BaseQuantity, unitCost, totalCost, sequence, journalPurpose, item.Batch?.Id, costSource?.Id);
        db.Movements.Add(posted);
        return posted;
    }

    private InventoryMovement NewMovement(
        PostingContextResolver.Resolved ctx, InventoryPostingRequest request, WorkItem item, string type, decimal quantity, decimal baseQuantity,
        decimal unitCost, decimal totalCost, long sequence, string? journalPurpose, Guid? batchId, Guid? relatedMovementId)
    {
        var serialId = item.Serial is not null && _serials.TryGetValue((item.Product.Id, item.Serial.ToUpperInvariant()), out var s) ? s.Id : (Guid?)null;
        return new InventoryMovement(
            ctx.CompanyId, ctx.LegalEntity.Id, item.AreaId, sequence, item.Product.Id, item.Line.VariantId, item.Line.WarehouseId,
            type == InventoryMovementTypes.CostAdjustment ? null : batchId, type == InventoryMovementTypes.CostAdjustment ? null : serialId,
            type, quantity, item.Line.UomId, baseQuantity, unitCost, totalCost, request.TransactionDate,
            request.SourceModule, request.SourceDocType, request.SourceDocId, item.Line.LineId, request.ReferenceNo, null, journalPurpose,
            clock.UtcNow, currentUser.UserId, relatedMovementId);
    }

    /// <summary>Movements of the whole tenant: internal callers may not have selected a company yet. RLS still applies.</summary>
    private IQueryable<InventoryMovement> TenantMovements()
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant context required.");
        return db.Movements.IgnoreQueryFilters().AsNoTracking().Where(m => EF.Property<Guid>(m, "TenantId") == tenantId);
    }

    internal static string StockRole(WarehouseSummary warehouse) =>
        warehouse.Type == "Transit" ? PostingRoles.InventoryInTransit : PostingRoles.Inventory;

    private async Task<Result<Guid?>> PostStockJournalAsync(PostingContextResolver.Resolved ctx, InventoryPostingRequest request, IReadOnlyList<Posted> posted, CancellationToken ct)
    {
        var lines = new List<JournalLineRequest>();
        foreach (var group in posted.Where(p => p.Work.Line.CounterRole is not null).GroupBy(p => p.Work.Line.LineId))
        {
            var first = group.First().Work;
            var total = group.Sum(p => p.Movement.TotalCost);
            if (total == 0)
            {
                continue;
            }

            var warehouse = ctx.Warehouses[first.Line.WarehouseId];
            var stockContext = new PostingContext(ProductPostingGroup: first.Product.PostingGroup, WarehouseId: warehouse.Id);
            var counterContext = new PostingContext(ProductPostingGroup: first.Product.PostingGroup);
            var amount = Math.Abs(total);
            if (total > 0)
            {
                lines.Add(new JournalLineRequest(StockRole(warehouse), null, amount, 0, stockContext, Description: first.Product.Sku));
                lines.Add(new JournalLineRequest(first.Line.CounterRole, null, 0, amount, counterContext, Description: first.Product.Sku));
            }
            else
            {
                lines.Add(new JournalLineRequest(first.Line.CounterRole, null, amount, 0, counterContext, Description: first.Product.Sku));
                lines.Add(new JournalLineRequest(StockRole(warehouse), null, 0, amount, stockContext, Description: first.Product.Sku));
            }
        }

        if (lines.Count == 0)
        {
            return (Guid?)null;
        }

        var result = await journals.PostAsync(new JournalRequest(
            ctx.LegalEntity.Id, request.TransactionDate, request.SourceModule, request.SourceDocType, request.SourceDocId, JournalPurpose,
            request.JournalDescription ?? $"{request.SourceDocType} {request.ReferenceNo}".Trim(), ctx.LegalEntity.FunctionalCurrency, 1m, lines), ct);
        return result.IsSuccess ? result.Value.JournalId : result.Error;
    }

    private async Task<Result> PostTrueUpJournalAsync(PostingContextResolver.Resolved ctx, InventoryPostingRequest request, IReadOnlyList<(WorkItem Work, decimal Amount)> trueUps, CancellationToken ct)
    {
        var lines = new List<JournalLineRequest>();
        foreach (var (item, amount) in trueUps)
        {
            var warehouse = ctx.Warehouses[item.Line.WarehouseId];
            var stockContext = new PostingContext(ProductPostingGroup: item.Product.PostingGroup, WarehouseId: warehouse.Id);
            var abs = Math.Abs(amount);
            if (amount > 0)
            {
                lines.Add(new JournalLineRequest(PostingRoles.CostTrueUp, null, abs, 0, Description: item.Product.Sku));
                lines.Add(new JournalLineRequest(StockRole(warehouse), null, 0, abs, stockContext, Description: item.Product.Sku));
            }
            else
            {
                lines.Add(new JournalLineRequest(StockRole(warehouse), null, abs, 0, stockContext, Description: item.Product.Sku));
                lines.Add(new JournalLineRequest(PostingRoles.CostTrueUp, null, 0, abs, Description: item.Product.Sku));
            }
        }

        var result = await journals.PostAsync(new JournalRequest(
            ctx.LegalEntity.Id, request.TransactionDate, SourceModules.Inventory, request.SourceDocType, request.SourceDocId, TrueUpPurpose,
            "Cost true-up after negative stock", ctx.LegalEntity.FunctionalCurrency, 1m, lines), ct);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    private static InventoryPostingResult BuildResult(
        IReadOnlyList<InventoryMovement> movements, Guid? journalId, bool alreadyPosted, Guid legalEntityId,
        IReadOnlyDictionary<Guid, ProductInfo>? products = null, IReadOnlyList<Posted>? posted = null)
    {
        var batchNumbers = posted?.Where(p => p.Work.Batch is not null).ToDictionary(p => p.Movement.Id, p => p.Work.Batch!.Number) ?? [];
        var serialNumbers = posted?.Where(p => p.Work.Serial is not null).ToDictionary(p => p.Movement.Id, p => p.Work.Serial!) ?? [];
        var lines = movements.Where(m => m.MovementType != InventoryMovementTypes.CostAdjustment)
            .GroupBy(m => m.SourceLineId ?? Guid.Empty)
            .Select(g =>
            {
                var baseQuantity = g.Sum(m => m.BaseQuantity);
                var total = g.Sum(m => m.TotalCost);
                var unit = baseQuantity == 0 ? 0 : InventoryRounding.Cost(total / baseQuantity);
                return new InventoryLineResult(g.Key, baseQuantity, unit, total, g.OrderBy(m => m.PostingSequence).ThenBy(m => m.Id).Select(m => new MovementDetail(
                    m.Id, m.WarehouseId, m.BatchId, batchNumbers.GetValueOrDefault(m.Id), m.SerialId, serialNumbers.GetValueOrDefault(m.Id),
                    m.BaseQuantity, m.UnitCost, m.TotalCost)).ToList());
            })
            .ToList();
        _ = products;
        return new InventoryPostingResult(legalEntityId, lines, lines.Sum(l => l.TotalCost), journalId, alreadyPosted);
    }

    // ---------------- Reversal ----------------
    public async Task<Result<InventoryPostingResult>> ReverseAsync(string sourceDocType, Guid sourceDocId, DateOnly reversalDate, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return InventoryErrors.TransactionRequired;
        }

        var all = await TenantMovements()
            .Where(m => m.SourceDocType == sourceDocType && m.SourceDocId == sourceDocId).ToListAsync(cancellationToken);
        var originals = all.Where(m => m.ReversalOfId == null).ToList();
        if (originals.Count == 0)
        {
            return Error.NotFound("inventory.source-not-found", "Nothing was posted for this source document.");
        }

        var reversals = all.Where(m => m.ReversalOfId != null).ToList();
        if (reversals.Count > 0)
        {
            return BuildResult(reversals, null, alreadyPosted: true, originals[0].LegalEntityId);
        }

        var warehouseIds = originals.Select(m => m.WarehouseId).Distinct().ToList();
        var resolved = await contexts.ResolveAsync(warehouseIds, originals[0].LegalEntityId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var ctx = resolved.Value;
        var open = await periods.EnsureOpenAsync(ctx.LegalEntity.Id, reversalDate, cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        var created = new List<InventoryMovement>();
        var ordered = originals.OrderBy(m => new StockKey(m.ProductId, Keys.Of(m.VariantId), m.WarehouseId, Keys.Of(m.BatchId))).ToList();
        foreach (var original in ordered)
        {
            var key = new StockKey(original.ProductId, Keys.Of(original.VariantId), original.WarehouseId, Keys.Of(original.BatchId));
            var quantity = Math.Abs(original.BaseQuantity);
            if (original.BaseQuantity > 0)
            {
                if (await store.IssueAsync(ctx.CompanyId, key, quantity, 0, allowNegative: false, cancellationToken) == StockApplyStatus.Insufficient)
                {
                    return InventoryErrors.ReversalStockConsumed;
                }
            }
            else if (original.BaseQuantity < 0)
            {
                await store.ReceiveAsync(ctx.CompanyId, key, quantity, cancellationToken);
            }

            long sequence = 0;
            if (original.PostingSequence > 0 || original.MovementType == InventoryMovementTypes.CostAdjustment)
            {
                var valuation = await LockedValuationAsync(ctx.CompanyId, original.ValuationAreaId, original.ProductId, key.VariantKey, cancellationToken);
                var reversed = await ReverseValueAsync(valuation, original, cancellationToken);
                if (reversed.IsFailure)
                {
                    return reversed.Error;
                }

                sequence = valuation.NextSequence();
            }

            if (original.SerialId is { } serialId)
            {
                var serial = await db.Serials.FirstAsync(s => s.Id == serialId, cancellationToken);
                serial.Move(original.BaseQuantity > 0 ? null : original.WarehouseId, original.BaseQuantity > 0 ? SerialStatus.Issued : SerialStatus.InStock, original.Id);
            }

            var reversal = new InventoryMovement(
                original.CompanyId, original.LegalEntityId, original.ValuationAreaId, sequence, original.ProductId, original.VariantId, original.WarehouseId,
                original.BatchId, original.SerialId, original.MovementType, -original.Quantity, original.UomId, -original.BaseQuantity, original.UnitCost,
                -original.TotalCost, reversalDate, original.SourceModule, original.SourceDocType, original.SourceDocId, original.SourceLineId,
                original.ReferenceNo, original.Id, null, clock.UtcNow, currentUser.UserId, original.RelatedMovementId);
            db.Movements.Add(reversal);
            created.Add(reversal);
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var purpose in originals.Where(m => m.JournalPurpose is not null).Select(m => m.JournalPurpose!).Distinct())
        {
            var journal = await journals.ReverseAsync(ctx.LegalEntity.Id, sourceDocType, sourceDocId, purpose, reversalDate, cancellationToken);
            if (journal.IsFailure)
            {
                return journal.Error;
            }
        }

        return BuildResult(created, null, alreadyPosted: false, ctx.LegalEntity.Id);
    }

    private async Task<ItemValuation> LockedValuationAsync(Guid companyId, Guid areaId, Guid productId, Guid variantKey, CancellationToken ct)
    {
        await store.LockValuationAsync(companyId, areaId, productId, variantKey, CostingMethods.WeightedAverage, ct);
        return await db.Valuations.FirstAsync(v => v.ValuationAreaId == areaId && v.ProductId == productId && v.VariantKey == variantKey, ct);
    }

    private async Task<Result> ReverseValueAsync(ItemValuation valuation, InventoryMovement original, CancellationToken ct)
    {
        var state = valuation.ToState();
        var newQuantity = state.Quantity - original.BaseQuantity;
        var newValue = state.Value - original.TotalCost;
        if (valuation.CostingMethod == CostingMethods.Fifo)
        {
            if (original.MovementType == InventoryMovementTypes.CostAdjustment && original.RelatedMovementId is { } receiptId)
            {
                var layer = await db.CostLayers.FirstOrDefaultAsync(l => l.ReceiptMovementId == receiptId, ct);
                if (layer is { RemainingQuantity: > 0 })
                {
                    layer.UnitCost = InventoryRounding.Cost(layer.UnitCost - (original.TotalCost / layer.RemainingQuantity));
                }
            }
            else if (original.BaseQuantity > 0)
            {
                var layer = await db.CostLayers.FirstOrDefaultAsync(l => l.ReceiptMovementId == original.Id, ct);
                if (layer is null || layer.RemainingQuantity < original.BaseQuantity)
                {
                    return InventoryErrors.ReversalStockConsumed;
                }

                layer.RemainingQuantity -= original.BaseQuantity;
            }
            else if (original.BaseQuantity < 0)
            {
                var consumptions = await db.LayerConsumptions.Where(c => c.MovementId == original.Id).ToListAsync(ct);
                foreach (var consumption in consumptions)
                {
                    var layer = await db.CostLayers.FirstAsync(l => l.Id == consumption.LayerId, ct);
                    layer.RemainingQuantity += consumption.Quantity;
                }
            }
        }

        if (newQuantity == 0)
        {
            newValue = 0;
        }

        var average = newQuantity > 0 ? InventoryRounding.Cost(newValue / newQuantity) : state.AverageCost;
        valuation.Apply(new ValuationState(newQuantity, newValue, average, state.LastCost));
        return Result.Success();
    }

    // ---------------- Value-only cost adjustment ----------------
    public async Task<Result<CostAdjustmentResult>> AdjustCostAsync(CostAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return InventoryErrors.TransactionRequired;
        }

        var done = await TenantMovements().FirstOrDefaultAsync(m =>
            m.SourceDocType == request.SourceDocType && m.SourceDocId == request.SourceDocId && m.SourceLineId == request.SourceLineId &&
            m.MovementType == InventoryMovementTypes.CostAdjustment && m.ReversalOfId == null, cancellationToken);
        if (done is not null)
        {
            return new CostAdjustmentResult(done.TotalCost, request.Amount - done.TotalCost, done.Id);
        }

        var receipt = await TenantMovements().FirstOrDefaultAsync(m => m.Id == request.ReceiptMovementId, cancellationToken);
        if (receipt is null || receipt.BaseQuantity <= 0)
        {
            return Error.NotFound("inventory.receipt-not-found", "The receipt movement was not found.");
        }

        var resolved = await contexts.ResolveAsync([receipt.WarehouseId], receipt.LegalEntityId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var ctx = resolved.Value;
        var open = await periods.EnsureOpenAsync(ctx.LegalEntity.Id, request.Date, cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        var valuation = await LockedValuationAsync(ctx.CompanyId, receipt.ValuationAreaId, receipt.ProductId, Keys.Of(receipt.VariantId), cancellationToken);
        var state = valuation.ToState();
        decimal applied;
        if (valuation.CostingMethod == CostingMethods.Fifo)
        {
            var layer = await db.CostLayers.FirstOrDefaultAsync(l => l.ReceiptMovementId == receipt.Id, cancellationToken);
            if (layer is null || layer.RemainingQuantity <= 0)
            {
                applied = 0;
            }
            else
            {
                applied = InventoryRounding.Money(request.Amount * (layer.RemainingQuantity / layer.OriginalQuantity));
                layer.UnitCost = InventoryRounding.Cost(layer.UnitCost + (applied / layer.RemainingQuantity));
            }
        }
        else
        {
            var ratio = state.Quantity <= 0 ? 0 : Math.Min(1m, state.Quantity / receipt.BaseQuantity);
            applied = InventoryRounding.Money(request.Amount * ratio);
        }

        Guid? movementId = null;
        if (applied != 0)
        {
            var newValue = state.Value + applied;
            var average = state.Quantity > 0 ? InventoryRounding.Cost(newValue / state.Quantity) : state.AverageCost;
            valuation.Apply(new ValuationState(state.Quantity, newValue, average, state.LastCost));
            var sequence = valuation.NextSequence();
            var movement = new InventoryMovement(
                ctx.CompanyId, receipt.LegalEntityId, receipt.ValuationAreaId, sequence, receipt.ProductId, receipt.VariantId, receipt.WarehouseId,
                null, null, InventoryMovementTypes.CostAdjustment, 0, receipt.UomId, 0, 0, applied, request.Date,
                request.SourceModule, request.SourceDocType, request.SourceDocId, request.SourceLineId, null, null, null,
                clock.UtcNow, currentUser.UserId, receipt.Id);
            db.Movements.Add(movement);
            movementId = movement.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CostAdjustmentResult(applied, request.Amount - applied, movementId);
    }
}

/// <summary>Contract implementation of reservations.</summary>
public sealed class ReservationService(
    IInventoryDbContext db,
    IInventoryStore store,
    IUnitOfWork unitOfWork,
    PostingContextResolver contexts) : IReservationService
{
    public async Task<Result<ReservationInfo>> ReserveAsync(ReservationRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return InventoryErrors.TransactionRequired;
        }

        if (request.BaseQuantity <= 0)
        {
            return Error.Validation("inventory.quantity-invalid", "Reserved quantity must be positive.");
        }

        var resolved = await contexts.ResolveAsync([request.WarehouseId], null, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        if (!resolved.Value.Policy.EnableReservations)
        {
            return Error.Conflict("inventory.reservations-disabled", "Warehouse reservations are disabled by policy.");
        }

        var variantKey = Keys.Of(request.VariantId);
        if (await store.ReserveAsync(resolved.Value.CompanyId, request.ProductId, variantKey, request.WarehouseId, request.BaseQuantity, cancellationToken) == StockApplyStatus.Insufficient)
        {
            return InventoryErrors.InsufficientStock with { Message = "Not enough available stock to reserve." };
        }

        var reference = request.Reference;
        var reservation = await db.Reservations.FirstOrDefaultAsync(r =>
            r.DemandType == reference.DemandType && r.DemandId == reference.DemandId && r.DemandLineId == reference.DemandLineId &&
            r.ProductId == request.ProductId && r.WarehouseId == request.WarehouseId, cancellationToken);
        if (reservation is null)
        {
            reservation = new StockReservation(resolved.Value.CompanyId, reference.DemandType, reference.DemandId, reference.DemandLineId,
                request.ProductId, request.VariantId, request.WarehouseId, request.BaseQuantity);
            db.Reservations.Add(reservation);
        }
        else
        {
            reservation.Increase(request.BaseQuantity);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(reservation);
    }

    public async Task<Result<ReservationInfo>> ReleaseAsync(ReservationRef reference, decimal? baseQuantity = null, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return InventoryErrors.TransactionRequired;
        }

        var reservation = await db.Reservations.FirstOrDefaultAsync(r =>
            r.DemandType == reference.DemandType && r.DemandId == reference.DemandId && r.DemandLineId == reference.DemandLineId, cancellationToken);
        if (reservation is null)
        {
            return Error.NotFound("inventory.reservation-not-found", "The reservation was not found.");
        }

        var released = reservation.Release(baseQuantity);
        if (released > 0)
        {
            await store.ReleaseAsync(reservation.CompanyId, reservation.ProductId, Keys.Of(reservation.VariantId), reservation.WarehouseId, released, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(reservation);
    }

    public async Task<IReadOnlyList<ReservationInfo>> GetAsync(string demandType, Guid demandId, CancellationToken cancellationToken = default)
    {
        var list = await db.Reservations.AsNoTracking().Where(r => r.DemandType == demandType && r.DemandId == demandId).ToListAsync(cancellationToken);
        return list.Select(ToInfo).ToList();
    }

    internal static ReservationInfo ToInfo(StockReservation r) => new(
        r.Id, new ReservationRef(r.DemandType, r.DemandId, r.DemandLineId), r.ProductId, r.VariantId, r.WarehouseId,
        r.Quantity, r.Consumed, r.Released, r.Open, r.Status.ToString());
}

/// <summary>Contract implementation of stock queries.</summary>
public sealed class StockQueries(IInventoryDbContext db, PostingContextResolver contexts) : IStockQueries
{
    public async Task<IReadOnlyList<StockLevel>> GetStockAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Balances.AsNoTracking().Where(b => productIds.Contains(b.ProductId));
        if (warehouseId is { } w)
        {
            query = query.Where(b => b.WarehouseId == w);
        }

        var rows = await query.GroupBy(b => new { b.ProductId, b.VariantKey, b.WarehouseId })
            .Select(g => new { g.Key.ProductId, g.Key.VariantKey, g.Key.WarehouseId, OnHand = g.Sum(b => b.OnHand), Reserved = g.Sum(b => b.Reserved) })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new StockLevel(r.ProductId, Keys.Nullable(r.VariantKey), r.WarehouseId, r.OnHand, r.Reserved, r.OnHand - r.Reserved)).ToList();
    }

    public async Task<ItemCost?> GetItemCostAsync(Guid productId, Guid? variantId, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        var resolved = await contexts.ResolveAsync([warehouseId], null, cancellationToken);
        if (resolved.IsFailure)
        {
            return null;
        }

        var area = resolved.Value.AreaOf(warehouseId);
        var variantKey = Keys.Of(variantId);
        var v = await db.Valuations.AsNoTracking().FirstOrDefaultAsync(x => x.ValuationAreaId == area && x.ProductId == productId && x.VariantKey == variantKey, cancellationToken);
        return v is null ? null : new ItemCost(v.ValuationAreaId, v.ProductId, variantId, v.Quantity, v.Value, v.AverageCost, v.LastCost);
    }

    public async Task<decimal> GetValuationTotalAsync(Guid legalEntityId, CancellationToken cancellationToken = default)
    {
        if (!await contexts.EnsureCompanyForLegalEntityAsync(legalEntityId, cancellationToken))
        {
            return 0;
        }

        // Valuation areas are either the legal entity itself or its warehouses; movements carry the legal entity, so sum by it.
        var areas = await db.Movements.AsNoTracking().Where(m => m.LegalEntityId == legalEntityId).Select(m => m.ValuationAreaId).Distinct().ToListAsync(cancellationToken);
        return await db.Valuations.AsNoTracking().Where(v => areas.Contains(v.ValuationAreaId)).SumAsync(v => v.Value, cancellationToken);
    }

    public async Task<MovementInfo?> GetMovementAsync(Guid movementId, CancellationToken cancellationToken = default)
    {
        var m = await db.Movements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == movementId, cancellationToken);
        return m is null ? null : ToInfo(m);
    }

    public async Task<IReadOnlyList<MovementInfo>> GetMovementsBySourceAsync(string sourceDocType, Guid sourceDocId, CancellationToken cancellationToken = default)
    {
        var list = await db.Movements.AsNoTracking().Where(m => m.SourceDocType == sourceDocType && m.SourceDocId == sourceDocId)
            .OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToListAsync(cancellationToken);
        return list.Select(ToInfo).ToList();
    }

    internal static MovementInfo ToInfo(InventoryMovement m) => new(
        m.Id, m.ProductId, m.VariantId, m.WarehouseId, m.MovementType, m.BaseQuantity, m.UnitCost, m.TotalCost, m.TransactionDate,
        m.SourceModule, m.SourceDocType, m.SourceDocId, m.SourceLineId);
}
