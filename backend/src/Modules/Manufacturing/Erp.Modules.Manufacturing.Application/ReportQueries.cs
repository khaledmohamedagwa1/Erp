using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Manufacturing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing.Application;

/// <summary>Production order reads for the Reporting module.</summary>
public sealed class ManufacturingReportQueries(IManufacturingDbContext db) : IManufacturingReportQueries
{
    private static readonly ProductionOrderStatus[] Reportable =
    [
        ProductionOrderStatus.Released, ProductionOrderStatus.InProgress, ProductionOrderStatus.PartiallyCompleted,
        ProductionOrderStatus.Completed, ProductionOrderStatus.Closed,
    ];

    public async Task<IReadOnlyList<ProductionFact>> GetProductionFactsAsync(ProductionReportFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.ProductionOrders.AsNoTracking().Where(o => Reportable.Contains(o.Status));
        if (filter.OpenWipOnly)
        {
            query = query.Where(o => o.WipBalance != 0);
        }
        else
        {
            query = query.Where(o => o.StartDate <= filter.To);
        }

        if (filter.WarehouseId is { } w)
        {
            query = query.Where(o => o.OutputWarehouseId == w || o.ComponentWarehouseId == w);
        }

        if (filter.ProductIds is { Count: > 0 } products)
        {
            query = query.Where(o => products.Contains(o.ProductId));
        }

        var orders = await query.ToListAsync(cancellationToken);
        return orders
            .Where(o => filter.OpenWipOnly || (o.StartDate >= filter.From && o.StartDate <= filter.To)
                || o.Transactions.Any(t => t.Date >= filter.From && t.Date <= filter.To))
            .OrderBy(o => o.StartDate).ThenBy(o => o.Number)
            .Select(ToFact)
            .ToList();
    }

    private static ProductionFact ToFact(ProductionOrder o)
    {
        var produced = o.CompletedQuantity + o.ScrappedQuantity;
        var components = o.Components.Select(c => new ProductionComponentFact(
            c.ProductId,
            Math.Round(c.RequiredQuantity * c.UomFactor, 6),
            Math.Round(c.QuantityPerUnit * produced * c.UomFactor, 6),
            Math.Round(c.IssuedQuantity * c.UomFactor, 6),
            c.IssuedCost)).ToList();
        var scrapCost = -o.Transactions.Where(t => t.Kind == ProductionTransactionKind.Scrap).Sum(t => t.WipAmount);
        DateOnly? last = o.Transactions.Count == 0 ? null : o.Transactions.Max(t => t.Date);
        return new ProductionFact(o.Id, o.Number, o.Status.ToString(), o.ProductId, o.ComponentWarehouseId, o.OutputWarehouseId, o.StartDate, o.DueDate, last,
            o.PlannedQuantity, o.CompletedQuantity, o.ScrappedQuantity, o.MaterialCost, o.LaborCost, o.MachineCost, o.OverheadCost, o.ReceivedCost,
            scrapCost, o.VarianceAmount, o.WipBalance, components);
    }
}
