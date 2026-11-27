using Erp.Application.Reporting;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>Purchasing reports from posted supplier invoices and goods receipts (Procurement.Contracts <see cref="IProcurementReportQueries"/>).</summary>
public sealed class PurchasingReports(IServiceProvider services, ReportLookups lookups) : IReportArea
{
    private const string InvoiceSource = "Posted supplier invoices (IProcurementReportQueries.GetPurchaseFactsAsync)";
    private const string ReceiptSource = "Posted goods receipts (IProcurementReportQueries.GetReceiptFactsAsync)";
    private static readonly string[] CommonFilters = ["from", "to", "supplierId", "warehouseId", "branchId", "productId", "categoryId"];

    public string Area => ReportAreas.Purchasing;

    public bool IsAvailable => services.GetService<IProcurementReportQueries>() is not null;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        Def("by-supplier", "Purchases by supplier", "المشتريات حسب المورد", "Invoiced net, tax and total per supplier.", InvoiceSource),
        Def("by-product", "Purchases by product", "المشتريات حسب الصنف", "Invoiced quantity, value and average unit cost per product.", InvoiceSource),
        Def("price-history", "Purchase price history", "سجل أسعار الشراء", "Every invoiced price per product and supplier, newest first.", InvoiceSource),
        Def("receipt-analysis", "Goods receipt analysis", "تحليل استلام البضاعة", "Received, accepted, rejected, invoiced and not-yet-invoiced quantities per receipt line.", ReceiptSource),
    ];

    public async Task<Result<ReportOutput>> RunAsync(string key, ReportQuery q, CancellationToken ct)
    {
        var queries = services.GetRequiredService<IProcurementReportQueries>();
        var filter = new PurchaseReportFilter(q.From, q.To, q.SupplierId, q.WarehouseId, q.BranchId, q.ProductIds);
        ReportTable? table = key switch
        {
            "by-supplier" => await BySupplierAsync(await queries.GetPurchaseFactsAsync(filter, ct), q, ct),
            "by-product" => await ByProductAsync(await queries.GetPurchaseFactsAsync(filter, ct), q, ct),
            "price-history" => await PriceHistoryAsync(await queries.GetPurchaseFactsAsync(filter, ct), q, ct),
            "receipt-analysis" => await ReceiptAnalysisAsync(await queries.GetReceiptFactsAsync(filter, ct), q, ct),
            _ => null,
        };
        return table is null ? ReportingErrors.UnknownReport : new ReportOutput(table);
    }

    private static ReportDefinition Def(string key, string en, string ar, string description, string source) =>
        new(ReportAreas.Purchasing, key, en, ar, description, source, ReportingPermissions.PurchasingRead, "procurement", CommonFilters);

    private async Task<ReportTable> BySupplierAsync(IReadOnlyList<PurchaseFact> facts, ReportQuery q, CancellationToken ct)
    {
        var suppliers = await lookups.SuppliersAsync(facts.Select(f => f.SupplierId), ct);
        ReportColumn[] columns =
        [
            Text("supplierCode", "Supplier code", "رمز المورد"), Text("supplier", "Supplier", "المورد"),
            Number("invoices", "Invoices", "الفواتير"),
            Money("net", "Net", "الصافي"), Money("tax", "Tax", "الضريبة"), Money("total", "Total", "الإجمالي"),
        ];
        var rows = facts.GroupBy(f => f.SupplierId).Select(g =>
        {
            var net = g.Sum(f => f.NetBase);
            var tax = g.Sum(f => f.TaxBase);
            return new object?[] { suppliers.Code(g.Key), suppliers.Name(g.Key, q.Arabic), (decimal)g.Select(f => f.InvoiceId).Distinct().Count(), net, tax, net + tax };
        }).OrderByDescending(r => (decimal)r[3]!).ToList();
        return new ReportTable("purchasing/by-supplier", "Purchases by supplier", "المشتريات حسب المورد", columns, rows, Totals(columns.Length, rows, 2, 3, 4, 5), Period(q));
    }

    private async Task<ReportTable> ByProductAsync(IReadOnlyList<PurchaseFact> facts, ReportQuery q, CancellationToken ct)
    {
        var goods = facts.Where(f => f.ProductId != null).ToList();
        var products = await lookups.ProductsAsync(goods.Select(f => f.ProductId!.Value), ct);
        ReportColumn[] columns =
        [
            Sku, Product,
            Quantity("quantity", "Quantity (base unit)", "الكمية (الوحدة الأساسية)"),
            Money("net", "Net value", "صافي القيمة"),
            Money("averageCost", "Average unit cost", "متوسط تكلفة الوحدة"),
            Number("suppliers", "Suppliers", "الموردون"),
        ];
        var rows = goods.GroupBy(f => f.ProductId!.Value).Select(g =>
        {
            var quantity = g.Sum(f => f.BaseQuantity);
            var net = g.Sum(f => f.NetBase);
            return new object?[]
            {
                products.Sku(g.Key), products.Name(g.Key, q.Arabic), quantity, net, quantity == 0 ? 0m : Math.Round(net / quantity, 4), (decimal)g.Select(f => f.SupplierId).Distinct().Count(),
            };
        }).OrderByDescending(r => (decimal)r[3]!).ToList();
        return new ReportTable("purchasing/by-product", "Purchases by product", "المشتريات حسب الصنف", columns, rows, Totals(columns.Length, rows, 2, 3), Period(q));
    }

    private async Task<ReportTable> PriceHistoryAsync(IReadOnlyList<PurchaseFact> facts, ReportQuery q, CancellationToken ct)
    {
        var goods = facts.Where(f => f.ProductId != null).ToList();
        var products = await lookups.ProductsAsync(goods.Select(f => f.ProductId!.Value), ct);
        var suppliers = await lookups.SuppliersAsync(goods.Select(f => f.SupplierId), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("invoice", "Invoice", "الفاتورة"), Text("supplierInvoice", "Supplier invoice no.", "رقم فاتورة المورد"),
            Text("supplier", "Supplier", "المورد"), Sku, Product,
            Quantity("quantity", "Quantity", "الكمية"),
            Money("unitPrice", "Unit price", "سعر الوحدة"), Text("currency", "Currency", "العملة"),
            Money("baseUnitCost", "Unit cost (base unit, functional currency)", "تكلفة الوحدة الأساسية بالعملة الوظيفية"),
        ];
        var rows = goods.OrderBy(f => products.Sku(f.ProductId!.Value), StringComparer.Ordinal).ThenByDescending(f => f.Date).ThenByDescending(f => f.InvoiceNumber)
            .Select(f => new object?[]
            {
                f.Date, f.InvoiceNumber, f.SupplierInvoiceNumber, suppliers.Name(f.SupplierId, q.Arabic), products.Sku(f.ProductId!.Value), products.Name(f.ProductId.Value, q.Arabic),
                f.Quantity, f.UnitPrice, f.Currency, f.BaseQuantity == 0 ? 0m : Math.Round(f.NetBase / f.BaseQuantity, 4),
            }).ToList();
        return new ReportTable("purchasing/price-history", "Purchase price history", "سجل أسعار الشراء", columns, rows, null, Period(q));
    }

    private async Task<ReportTable> ReceiptAnalysisAsync(IReadOnlyList<ReceiptFact> facts, ReportQuery q, CancellationToken ct)
    {
        var products = await lookups.ProductsAsync(facts.Select(f => f.ProductId), ct);
        var suppliers = await lookups.SuppliersAsync(facts.Select(f => f.SupplierId), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("receipt", "Receipt", "إذن الاستلام"), Text("purchaseOrder", "Purchase order", "أمر الشراء"),
            Text("supplier", "Supplier", "المورد"), Sku, Product,
            Quantity("received", "Received", "المستلم"), Quantity("accepted", "Accepted", "المقبول"), Quantity("rejected", "Rejected", "المرفوض"),
            Quantity("invoiced", "Invoiced", "المفوتر"), Quantity("returned", "Returned", "المرتجع"), Quantity("uninvoiced", "Not invoiced", "غير مفوتر"),
            Money("receivedValue", "Received value", "قيمة المستلم"), Money("uninvoicedValue", "Not invoiced value", "قيمة غير المفوتر"),
        ];
        var rows = facts.Select(f => new object?[]
        {
            f.Date, f.ReceiptNumber, f.PurchaseOrderNumber, suppliers.Name(f.SupplierId, q.Arabic), products.Sku(f.ProductId), products.Name(f.ProductId, q.Arabic),
            f.ReceivedQuantity, f.AcceptedQuantity, f.RejectedQuantity, f.InvoicedQuantity, f.ReturnedQuantity, f.UninvoicedQuantity,
            ReportMath.Money(f.BaseValue), ReportMath.Money(f.UninvoicedQuantity * f.UnitBaseValue),
        }).ToList();
        return new ReportTable("purchasing/receipt-analysis", "Goods receipt analysis", "تحليل استلام البضاعة", columns, rows, Totals(columns.Length, rows, 12, 13), Period(q));
    }
}
