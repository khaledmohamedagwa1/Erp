using System.Globalization;
using Erp.Application.Reporting;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>Inventory reports from the stock ledger, balances, valuations and batches (Inventory.Contracts <see cref="IInventoryReportQueries"/>).</summary>
public sealed class InventoryReports(IServiceProvider services, ReportLookups lookups) : IReportArea
{
    private static readonly string[] StockFilters = ["warehouseId", "productId", "categoryId"];

    public string Area => ReportAreas.Inventory;

    public bool IsAvailable => services.GetService<IInventoryReportQueries>() is not null;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        Def("stock-on-hand", "Stock on hand", "المخزون المتاح", "Current quantity, reserved, available and value per warehouse, product and batch.",
            "Inventory balances valued at the valuation-area average cost (IInventoryReportQueries.GetStockPositionsAsync)", StockFilters),
        Def("ledger", "Inventory ledger", "دفتر حركة المخزون", "Every stock movement in the period with opening and closing quantity/value.",
            "Inventory movements (IInventoryReportQueries.GetLedgerAsync)", ["from", "to", "warehouseId", "productId", "categoryId", "batchNumber"]),
        Def("valuation-by-category", "Inventory valuation by category", "تقييم المخزون حسب الفئة", "Quantity and value of stock on hand per product category.",
            "Inventory balances and valuations (IInventoryReportQueries.GetStockPositionsAsync) + catalog categories", StockFilters),
        Def("aging", "Stock aging", "أعمار المخزون", "On-hand quantity split into age buckets (0-30, 31-60, 61-90, 91-180, 181+ days) by receipt date, first-in-first-out.",
            "Inventory balances and inbound movements (IInventoryReportQueries.GetInboundMovementsAsync)", StockFilters),
        Def("expiry", "Expiring batches", "التشغيلات قريبة الانتهاء", "Batches with stock that expire within N days (default 30) or have already expired.",
            "Batches with on-hand stock (IInventoryReportQueries.GetBatchStockAsync)", ["days", "warehouseId", "productId", "categoryId"]),
        Def("batch-trace", "Batch traceability", "تتبع التشغيلة", "Every movement of a batch number: receipts, issues, transfers, sales and returns.",
            "Inventory movements of the batch (IInventoryReportQueries.GetLedgerAsync)", ["batchNumber", "productId", "warehouseId"]),
        ReadModelReports.StockValueHistory,
    ];

    public async Task<Result<ReportOutput>> RunAsync(string key, ReportQuery q, CancellationToken ct)
    {
        var queries = services.GetRequiredService<IInventoryReportQueries>();
        return key switch
        {
            "stock-on-hand" => new ReportOutput(await StockOnHandAsync(queries, q, ct)),
            "ledger" => await LedgerAsync(queries, q, q.From, q.To, q.BatchNumber, "inventory/ledger", "Inventory ledger", "دفتر حركة المخزون", ct),
            "valuation-by-category" => new ReportOutput(await ValuationByCategoryAsync(queries, q, ct)),
            "aging" => new ReportOutput(await AgingAsync(queries, q, ct)),
            "expiry" => new ReportOutput(await ExpiryAsync(queries, q, ct)),
            "value-history" => new ReportOutput(await services.GetRequiredService<ReadModelReports>().StockValueHistoryAsync(q, ct)),
            "batch-trace" => string.IsNullOrWhiteSpace(q.BatchNumber)
                ? ReportingErrors.BatchRequired
                : await LedgerAsync(queries, q, DateOnly.MinValue, DateOnly.MaxValue, q.BatchNumber, "inventory/batch-trace", "Batch traceability", "تتبع التشغيلة", ct),
            _ => ReportingErrors.UnknownReport,
        };
    }

    private static ReportDefinition Def(string key, string en, string ar, string description, string source, IReadOnlyList<string> filters) =>
        new(ReportAreas.Inventory, key, en, ar, description, source, ReportingPermissions.InventoryRead, "inventory", filters);

    private async Task<ReportTable> StockOnHandAsync(IInventoryReportQueries queries, ReportQuery q, CancellationToken ct)
    {
        var positions = await queries.GetStockPositionsAsync(q.WarehouseId, q.ProductIds, ct);
        var products = await lookups.ProductsAsync(positions.Select(p => p.ProductId), ct);
        var warehouses = await lookups.WarehousesAsync(positions.Select(p => p.WarehouseId), ct);
        ReportColumn[] columns =
        [
            Warehouse, Sku, Product, Text("batch", "Batch", "التشغيلة"), Date("expiry", "Expiry", "الانتهاء"),
            Quantity("onHand", "On hand", "الرصيد"), Quantity("reserved", "Reserved", "المحجوز"), Quantity("available", "Available", "المتاح"),
            Money("unitCost", "Unit cost", "تكلفة الوحدة"), Money("value", "Value", "القيمة"),
        ];
        var rows = positions
            .OrderBy(p => warehouses.Name(p.WarehouseId), StringComparer.Ordinal).ThenBy(p => products.Sku(p.ProductId), StringComparer.Ordinal).ThenBy(p => p.BatchNumber)
            .Select(p => new object?[]
            {
                warehouses.Name(p.WarehouseId), products.Sku(p.ProductId), products.Name(p.ProductId, q.Arabic), p.BatchNumber, p.ExpiryDate,
                p.OnHand, p.Reserved, p.OnHand - p.Reserved, p.UnitCost, p.Value,
            }).ToList();
        return new ReportTable("inventory/stock-on-hand", "Stock on hand", "المخزون المتاح", columns, rows, Totals(columns.Length, rows, 9),
            [new("As of", "حتى تاريخ", q.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);
    }

    private async Task<Result<ReportOutput>> LedgerAsync(IInventoryReportQueries queries, ReportQuery q, DateOnly from, DateOnly to, string? batchNumber,
        string key, string titleEn, string titleAr, CancellationToken ct)
    {
        var page = q.ForExport ? 1 : q.Page;
        var pageSize = q.ForExport ? q.ExportCap + 1 : q.PageSize;
        var ledger = await queries.GetLedgerAsync(new InventoryLedgerFilter(from, to, q.WarehouseId, q.ProductIds, batchNumber), page, pageSize, ct);
        if (q.ForExport && ledger.TotalCount > q.ExportCap)
        {
            return ReportingErrors.ExportTooLarge(ledger.TotalCount, q.ExportCap);
        }

        var products = await lookups.ProductsAsync(ledger.Items.Select(m => m.ProductId), ct);
        var warehouses = await lookups.WarehousesAsync(ledger.Items.Select(m => m.WarehouseId), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Warehouse, Sku, Product, Text("batch", "Batch", "التشغيلة"),
            Text("movementType", "Movement", "الحركة"), Text("documentType", "Document type", "نوع المستند"), Text("reference", "Reference", "المرجع"),
            Quantity("quantity", "Quantity", "الكمية"), Money("unitCost", "Unit cost", "تكلفة الوحدة"), Money("value", "Value", "القيمة"),
        ];
        var rows = ledger.Items.Select(m => new object?[]
        {
            m.Date, warehouses.Name(m.WarehouseId), products.Sku(m.ProductId), products.Name(m.ProductId, q.Arabic), m.BatchNumber,
            m.MovementType, m.SourceDocType, m.ReferenceNo, m.BaseQuantity, m.UnitCost, m.TotalCost,
        }).ToList();
        var totals = new object?[columns.Length];
        totals[8] = ledger.PeriodQuantity;
        totals[10] = ledger.PeriodValue;
        var invariant = CultureInfo.InvariantCulture;
        List<ReportParameter> parameters = batchNumber is not null && key == "inventory/batch-trace"
            ? [new("Batch", "التشغيلة", batchNumber)]
            :
            [
                .. Period(q),
                new("Opening quantity", "الكمية الافتتاحية", ledger.OpeningQuantity.ToString("0.######", invariant)),
                new("Opening value", "القيمة الافتتاحية", ledger.OpeningValue.ToString("0.00", invariant)),
                new("Closing quantity", "الكمية الختامية", (ledger.OpeningQuantity + ledger.PeriodQuantity).ToString("0.######", invariant)),
                new("Closing value", "القيمة الختامية", (ledger.OpeningValue + ledger.PeriodValue).ToString("0.00", invariant)),
            ];
        return new ReportOutput(new ReportTable(key, titleEn, titleAr, columns, rows, totals, parameters), IsPaged: true, ledger.TotalCount);
    }

    private async Task<ReportTable> ValuationByCategoryAsync(IInventoryReportQueries queries, ReportQuery q, CancellationToken ct)
    {
        var positions = await queries.GetStockPositionsAsync(q.WarehouseId, q.ProductIds, ct);
        var products = await lookups.ProductsAsync(positions.Select(p => p.ProductId), ct);
        ReportColumn[] columns =
        [
            Text("categoryCode", "Category code", "رمز الفئة"), Text("category", "Category", "الفئة"),
            Number("products", "Products", "الأصناف"), Quantity("quantity", "Quantity (base units)", "الكمية (وحدات أساسية)"),
            Money("value", "Value", "القيمة"), Percent("share", "Share %", "النسبة %"),
        ];
        var total = positions.Sum(p => p.Value);
        var rows = positions.GroupBy(p => products.TryGetValue(p.ProductId, out var info) ? info.CategoryId : Guid.Empty).Select(g =>
        {
            var info = products.Values.FirstOrDefault(p => p.CategoryId == g.Key);
            var value = g.Sum(p => p.Value);
            return new object?[]
            {
                info?.CategoryCode ?? string.Empty, info is null ? string.Empty : (q.Arabic ? info.CategoryNameAr : info.CategoryNameEn),
                (decimal)g.Select(p => p.ProductId).Distinct().Count(), g.Sum(p => p.OnHand), value, ReportMath.Percent(value, total),
            };
        }).OrderByDescending(r => (decimal)r[4]!).ToList();
        var totals = Totals(columns.Length, rows, 2, 3, 4);
        totals[5] = total == 0 ? 0m : 100m;
        return new ReportTable("inventory/valuation-by-category", "Inventory valuation by category", "تقييم المخزون حسب الفئة", columns, rows, totals,
            [new("As of", "حتى تاريخ", q.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);
    }

    private async Task<ReportTable> AgingAsync(IInventoryReportQueries queries, ReportQuery q, CancellationToken ct)
    {
        var positions = await queries.GetStockPositionsAsync(q.WarehouseId, q.ProductIds, ct);
        var stock = positions.GroupBy(p => (p.ProductId, p.WarehouseId))
            .Select(g => (g.Key.ProductId, g.Key.WarehouseId, OnHand: g.Sum(p => p.OnHand), Value: g.Sum(p => p.Value)))
            .Where(s => s.OnHand > 0).ToList();
        var inbound = (await queries.GetInboundMovementsAsync(q.Today, q.WarehouseId, q.ProductIds, ct))
            .GroupBy(m => (m.ProductId, m.WarehouseId)).ToDictionary(g => g.Key, g => g.Select(m => (m.Date, m.BaseQuantity)).ToList());
        var products = await lookups.ProductsAsync(stock.Select(s => s.ProductId), ct);
        var warehouses = await lookups.WarehousesAsync(stock.Select(s => s.WarehouseId), ct);
        List<ReportColumn> columns = [Warehouse, Sku, Product, Quantity("onHand", "On hand", "الرصيد"), Money("value", "Value", "القيمة")];
        columns.AddRange(ReportMath.AgingLabels.Select(l => Quantity($"days{l.Replace("-", "to", StringComparison.Ordinal).Replace("+", "Plus", StringComparison.Ordinal)}",
            $"{l} days", $"{l} يوم")));
        var rows = stock.OrderBy(s => warehouses.Name(s.WarehouseId), StringComparer.Ordinal).ThenBy(s => products.Sku(s.ProductId), StringComparer.Ordinal).Select(s =>
        {
            var buckets = ReportMath.AgeStock(s.OnHand, inbound.GetValueOrDefault((s.ProductId, s.WarehouseId)) ?? [], q.Today);
            return new object?[] { warehouses.Name(s.WarehouseId), products.Sku(s.ProductId), products.Name(s.ProductId, q.Arabic), s.OnHand, s.Value }
                .Concat(buckets.Cast<object?>()).ToArray();
        }).ToList();
        return new ReportTable("inventory/aging", "Stock aging", "أعمار المخزون", columns, rows,
            Totals(columns.Count, rows, [3, 4, .. Enumerable.Range(5, ReportMath.AgingLabels.Length)]),
            [new("As of", "حتى تاريخ", q.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);
    }

    private async Task<ReportTable> ExpiryAsync(IInventoryReportQueries queries, ReportQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days ?? 30, 0, 3650);
        var batches = await queries.GetBatchStockAsync(q.Today.AddDays(days), q.WarehouseId, q.ProductIds, ct);
        var products = await lookups.ProductsAsync(batches.Select(b => b.ProductId), ct);
        var warehouses = await lookups.WarehousesAsync(batches.Select(b => b.WarehouseId), ct);
        ReportColumn[] columns =
        [
            Warehouse, Sku, Product, Text("batch", "Batch", "التشغيلة"), Date("expiry", "Expiry", "الانتهاء"),
            Number("daysToExpiry", "Days to expiry", "أيام حتى الانتهاء"), Text("status", "Status", "الحالة"), Quantity("onHand", "On hand", "الرصيد"),
        ];
        var rows = batches.Select(b =>
        {
            var left = b.ExpiryDate!.Value.DayNumber - q.Today.DayNumber;
            var status = left < 0 ? (q.Arabic ? "منتهي" : "Expired") : (q.Arabic ? "قريب الانتهاء" : "Expiring");
            return new object?[] { warehouses.Name(b.WarehouseId), products.Sku(b.ProductId), products.Name(b.ProductId, q.Arabic), b.Number, b.ExpiryDate, (decimal)left, status, b.OnHand };
        }).ToList();
        return new ReportTable("inventory/expiry", "Expiring batches", "التشغيلات قريبة الانتهاء", columns, rows, Totals(columns.Length, rows, 7),
            [new("Within days", "خلال أيام", days.ToString(CultureInfo.InvariantCulture)), new("As of", "حتى تاريخ", q.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);
    }
}
