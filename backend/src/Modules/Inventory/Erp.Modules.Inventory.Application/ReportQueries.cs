using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Application;

/// <summary>Ledger reads for the Reporting module. Quantities are base units and values functional currency (signed: issues negative).</summary>
public sealed class InventoryReportQueries(IInventoryDbContext db) : IInventoryReportQueries
{
    public async Task<IReadOnlyList<StockPosition>> GetStockPositionsAsync(Guid? warehouseId = null, IReadOnlyCollection<Guid>? productIds = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Balances.AsNoTracking().Where(b => b.OnHand != 0 || b.Reserved != 0);
        if (warehouseId is { } w)
        {
            query = query.Where(b => b.WarehouseId == w);
        }

        if (productIds is { Count: > 0 })
        {
            query = query.Where(b => productIds.Contains(b.ProductId));
        }

        var balances = await query.ToListAsync(cancellationToken);
        if (balances.Count == 0)
        {
            return [];
        }

        var warehouses = balances.Select(b => b.WarehouseId).Distinct().ToList();
        var products = balances.Select(b => b.ProductId).Distinct().ToList();

        // The valuation area of a warehouse is the one its movements were posted to (the warehouse or its legal entity).
        var areaPairs = await db.Movements.AsNoTracking().Where(m => warehouses.Contains(m.WarehouseId))
            .Select(m => new { m.WarehouseId, m.ValuationAreaId }).Distinct().ToListAsync(cancellationToken);
        var areaOf = areaPairs.GroupBy(a => a.WarehouseId).ToDictionary(g => g.Key, g => g.First().ValuationAreaId);
        var areas = areaOf.Values.Distinct().ToList();
        var valuations = await db.Valuations.AsNoTracking().Where(v => areas.Contains(v.ValuationAreaId) && products.Contains(v.ProductId))
            .ToDictionaryAsync(v => (v.ValuationAreaId, v.ProductId, v.VariantKey), cancellationToken);

        var batchIds = balances.Where(b => b.BatchKey != Guid.Empty).Select(b => b.BatchKey).Distinct().ToList();
        var batches = batchIds.Count == 0 ? [] : await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);

        return balances.Select(b =>
        {
            var cost = areaOf.TryGetValue(b.WarehouseId, out var area) && valuations.TryGetValue((area, b.ProductId, b.VariantKey), out var v) ? v.AverageCost : 0m;
            var batch = b.BatchKey == Guid.Empty ? null : batches.GetValueOrDefault(b.BatchKey);
            return new StockPosition(b.ProductId, Keys.Nullable(b.VariantKey), b.WarehouseId, Keys.Nullable(b.BatchKey), batch?.Number, batch?.ExpiryDate,
                b.OnHand, b.Reserved, cost, Math.Round(b.OnHand * cost, 2, MidpointRounding.AwayFromZero));
        }).ToList();
    }

    public async Task<InventoryLedgerPage> GetLedgerAsync(InventoryLedgerFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = db.Movements.AsNoTracking();
        if (filter.WarehouseId is { } w)
        {
            query = query.Where(m => m.WarehouseId == w);
        }

        if (filter.ProductIds is { Count: > 0 } products)
        {
            query = query.Where(m => products.Contains(m.ProductId));
        }

        if (!string.IsNullOrWhiteSpace(filter.BatchNumber))
        {
            var number = filter.BatchNumber.Trim();
            var batchIds = await db.Batches.AsNoTracking().Where(b => b.Number == number).Select(b => b.Id).ToListAsync(cancellationToken);
            query = query.Where(m => m.BatchId != null && batchIds.Contains(m.BatchId.Value));
        }

        var opening = await query.Where(m => m.TransactionDate < filter.From)
            .GroupBy(_ => 1).Select(g => new { Quantity = g.Sum(m => m.BaseQuantity), Value = g.Sum(m => m.TotalCost) }).FirstOrDefaultAsync(cancellationToken);
        var period = query.Where(m => m.TransactionDate >= filter.From && m.TransactionDate <= filter.To);
        var totals = await period.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Quantity = g.Sum(m => m.BaseQuantity), Value = g.Sum(m => m.TotalCost) }).FirstOrDefaultAsync(cancellationToken);
        var rows = await period.OrderBy(m => m.TransactionDate).ThenBy(m => m.PostingSequence).ThenBy(m => m.Id)
            .Skip(Math.Max(0, (page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);

        var rowBatchIds = rows.Where(r => r.BatchId != null).Select(r => r.BatchId!.Value).Distinct().ToList();
        var numbers = rowBatchIds.Count == 0
            ? []
            : await db.Batches.AsNoTracking().Where(b => rowBatchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Number, cancellationToken);
        var items = rows.Select(m => new InventoryLedgerEntry(m.Id, m.TransactionDate, m.PostingSequence, m.ProductId, m.VariantId, m.WarehouseId,
            m.BatchId is { } bid ? numbers.GetValueOrDefault(bid) : null, m.MovementType, m.BaseQuantity, m.UnitCost, m.TotalCost, m.SourceModule,
            m.SourceDocType, m.SourceDocId, m.ReferenceNo)).ToList();
        return new InventoryLedgerPage(items, totals?.Count ?? 0, opening?.Quantity ?? 0, opening?.Value ?? 0, totals?.Quantity ?? 0, totals?.Value ?? 0);
    }

    public async Task<IReadOnlyList<InboundMovement>> GetInboundMovementsAsync(DateOnly asOf, Guid? warehouseId = null, IReadOnlyCollection<Guid>? productIds = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Movements.AsNoTracking().Where(m => m.BaseQuantity > 0 && m.TransactionDate <= asOf && m.ReversalOfId == null);
        if (warehouseId is { } w)
        {
            query = query.Where(m => m.WarehouseId == w);
        }

        if (productIds is { Count: > 0 })
        {
            query = query.Where(m => productIds.Contains(m.ProductId));
        }

        return await query.OrderByDescending(m => m.TransactionDate).ThenByDescending(m => m.PostingSequence)
            .Select(m => new InboundMovement(m.ProductId, m.WarehouseId, m.TransactionDate, m.BaseQuantity))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BatchStock>> GetBatchStockAsync(DateOnly? expiringOnOrBefore = null, Guid? warehouseId = null,
        IReadOnlyCollection<Guid>? productIds = null, CancellationToken cancellationToken = default)
    {
        var batches = db.Batches.AsNoTracking();
        if (expiringOnOrBefore is { } limit)
        {
            batches = batches.Where(b => b.ExpiryDate != null && b.ExpiryDate <= limit);
        }

        if (productIds is { Count: > 0 })
        {
            batches = batches.Where(b => productIds.Contains(b.ProductId));
        }

        var balances = db.Balances.AsNoTracking().Where(b => b.OnHand != 0);
        if (warehouseId is { } w)
        {
            balances = balances.Where(b => b.WarehouseId == w);
        }

        var rows = await (from batch in batches
                          join balance in balances on batch.Id equals balance.BatchKey
                          group balance by new { batch.Id, batch.ProductId, batch.Number, batch.ManufactureDate, batch.ExpiryDate, batch.SupplierId, balance.WarehouseId } into g
                          select new { g.Key, OnHand = g.Sum(x => x.OnHand) }).ToListAsync(cancellationToken);
        return rows.Select(r => new BatchStock(r.Key.Id, r.Key.ProductId, r.Key.Number, r.Key.ManufactureDate, r.Key.ExpiryDate, r.Key.SupplierId,
                r.Key.WarehouseId, r.OnHand))
            .OrderBy(r => r.ExpiryDate ?? DateOnly.MaxValue).ThenBy(r => r.Number).ToList();
    }
}
