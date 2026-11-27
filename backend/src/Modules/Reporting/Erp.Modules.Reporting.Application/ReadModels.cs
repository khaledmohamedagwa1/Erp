using Erp.Application.Abstractions;
using Erp.Application.Reporting;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.Modules.Reporting.Domain;
using Erp.Modules.Sales.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>
/// Maintains the pre-aggregated read models from module facts: the daily sales summary (per day and branch) and the stock value snapshot
/// (per warehouse, captured per day). Both are recomputed from the source for the affected day, so repeated or out-of-order deliveries
/// converge to the same rows. Requires the active tenant and company.
/// </summary>
public sealed class ReadModelProjector(IReportingDbContext db, IServiceProvider services, ITenantContext tenant, IClock clock)
{
    public async Task<int> RefreshSalesAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var sales = services.GetService<ISalesReportQueries>();
        if (sales is null || tenant.CompanyId is not { } companyId)
        {
            return 0;
        }

        var facts = await sales.GetSalesFactsAsync(new SalesReportFilter(from, to), ct);
        var existing = await db.DailySalesSummaries.Where(s => s.Date >= from && s.Date <= to).ToListAsync(ct);
        db.DailySalesSummaries.RemoveRange(existing);
        var now = clock.UtcNow;
        var rows = facts.GroupBy(f => (f.Date, Branch: f.BranchId ?? Guid.Empty))
            .Select(g => DailySalesSummary.Create(companyId, g.Key.Date, g.Key.Branch,
                g.Where(f => f.DocumentType == SalesDocumentTypes.Invoice).Select(f => f.DocumentId).Distinct().Count(),
                g.Where(f => f.DocumentType == SalesDocumentTypes.Return).Select(f => f.DocumentId).Distinct().Count(),
                g.Where(f => f.DocumentType == SalesDocumentTypes.Invoice).Sum(f => f.NetBase),
                g.Where(f => f.DocumentType == SalesDocumentTypes.Return).Sum(f => f.NetBase),
                g.Sum(f => f.TaxBase), g.Sum(f => f.CostBase), now))
            .ToList();
        db.DailySalesSummaries.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Captures today's stock quantity and value per warehouse (replacing an earlier capture of the same day).</summary>
    public async Task<int> RefreshStockSnapshotAsync(CancellationToken ct)
    {
        var inventory = services.GetService<IInventoryReportQueries>();
        if (inventory is null || tenant.CompanyId is not { } companyId)
        {
            return 0;
        }

        var today = DateOnly.FromDateTime(clock.UtcNow);
        var positions = await inventory.GetStockPositionsAsync(cancellationToken: ct);
        db.StockValueSnapshots.RemoveRange(await db.StockValueSnapshots.Where(s => s.Date == today).ToListAsync(ct));
        var now = clock.UtcNow;
        var rows = positions.GroupBy(p => p.WarehouseId)
            .Select(g => StockValueSnapshot.Create(companyId, today, g.Key, g.Sum(p => p.OnHand), g.Sum(p => p.Value), now))
            .ToList();
        db.StockValueSnapshots.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}

/// <summary>Keeps the read models current: every posted sales journal refreshes its day; every stock-moving journal refreshes today's snapshot.</summary>
public sealed class ReadModelJournalHandler(ReadModelProjector projector, ITenantContext tenant) : IIntegrationEventHandler<JournalPosted>
{
    private static readonly HashSet<string> StockModules = new(StringComparer.Ordinal)
    {
        SourceModules.Inventory, SourceModules.Procurement, SourceModules.Sales, SourceModules.Pos, SourceModules.Manufacturing,
    };

    public async Task HandleAsync(JournalPosted e, CancellationToken ct)
    {
        if (!StockModules.Contains(e.SourceModule))
        {
            return;
        }

        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(e.CompanyId);
        }

        if (e.SourceModule == SourceModules.Sales)
        {
            await projector.RefreshSalesAsync(e.JournalDate, e.JournalDate, ct);
        }

        await projector.RefreshStockSnapshotAsync(ct);
    }
}

public sealed record RebuildReadModelsResult(DateOnly From, DateOnly To, int SalesSummaryRows, int StockSnapshotRows);

/// <summary>Reports served from the read models (no aggregation over documents at request time).</summary>
public sealed class ReadModelReports(IReportingDbContext db, ReportLookups lookups)
{
    public static readonly ReportDefinition DailySales = new(ReportAreas.Sales, "daily-summary", "Daily sales (read model)", "المبيعات اليومية (نموذج قراءة مجمع)",
        "Pre-aggregated sales per day and branch, maintained when sales are posted (rebuild with POST /api/v1/reports/read-models/rebuild).",
        "Read model reporting.DailySalesSummary", ReportingPermissions.SalesRead, "sales", ["from", "to", "branchId"]);

    public static readonly ReportDefinition StockValueHistory = new(ReportAreas.Inventory, "value-history", "Stock value history (read model)",
        "تاريخ قيمة المخزون (نموذج قراءة مجمع)", "Stock quantity and value per warehouse as captured each day stock moved.",
        "Read model reporting.StockValueSnapshot", ReportingPermissions.InventoryRead, "inventory", ["from", "to", "warehouseId"]);

    public async Task<ReportTable> DailySalesAsync(ReportQuery q, CancellationToken ct)
    {
        var source = db.DailySalesSummaries.AsNoTracking().Where(s => s.Date >= q.From && s.Date <= q.To);
        if (q.BranchId is { } branch)
        {
            source = source.Where(s => s.BranchKey == branch);
        }

        var data = await source.OrderBy(s => s.Date).ThenBy(s => s.BranchKey).ToListAsync(ct);
        var branches = await lookups.BranchesAsync(data.Select(s => s.BranchKey).Where(b => b != Guid.Empty).Distinct().ToList(), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("branch", "Branch", "الفرع"), Number("invoices", "Invoices", "الفواتير"),
            Number("creditNotes", "Credit notes", "الإشعارات الدائنة"), Money("grossSales", "Sales", "المبيعات"), Money("returns", "Returns", "المرتجعات"),
            Money("netRevenue", "Net revenue", "صافي الإيرادات"), Money("tax", "Tax", "الضريبة"), Money("cogs", "Cost of sales", "تكلفة المبيعات"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
        ];
        var rows = data.Select(s => new object?[]
        {
            s.Date, s.BranchKey == Guid.Empty ? string.Empty : branches.TryGetValue(s.BranchKey, out var b) ? $"{b.Code} {b.Name}" : s.BranchKey.ToString(),
            (decimal)s.Invoices, (decimal)s.CreditNotes,
            s.GrossSales, s.Returns, s.NetRevenue, s.Tax, s.Cogs, s.NetRevenue - s.Cogs,
        }).ToList();
        return new ReportTable("sales/daily-summary", DailySales.TitleEn, DailySales.TitleAr, columns, rows, Totals(columns.Length, rows, 2, 3, 4, 5, 6, 7, 8, 9), Period(q));
    }

    public async Task<ReportTable> StockValueHistoryAsync(ReportQuery q, CancellationToken ct)
    {
        var source = db.StockValueSnapshots.AsNoTracking().Where(s => s.Date >= q.From && s.Date <= q.To);
        if (q.WarehouseId is { } warehouse)
        {
            source = source.Where(s => s.WarehouseId == warehouse);
        }

        var data = await source.OrderBy(s => s.Date).ThenBy(s => s.WarehouseId).ToListAsync(ct);
        var warehouses = await lookups.WarehousesAsync(data.Select(s => s.WarehouseId).Distinct().ToList(), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("warehouse", "Warehouse", "المخزن"), Quantity("quantity", "Quantity", "الكمية"), Money("value", "Value", "القيمة"),
        ];
        var rows = data.Select(s => new object?[] { s.Date, warehouses.Name(s.WarehouseId), s.Quantity, s.Value }).ToList();
        return new ReportTable("inventory/value-history", StockValueHistory.TitleEn, StockValueHistory.TitleAr, columns, rows, null, Period(q));
    }
}

