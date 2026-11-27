using Erp.Application.Reporting;
using Erp.Modules.Reporting.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>Sales reports from posted invoices and credit notes (Sales.Contracts <see cref="ISalesReportQueries"/>), functional currency.</summary>
public sealed class SalesReports(IServiceProvider services, ReportLookups lookups) : IReportArea
{
    private const string Source = "Posted sales invoices and credit notes (ISalesReportQueries.GetSalesFactsAsync)";
    private static readonly string[] CommonFilters = ["from", "to", "branchId", "warehouseId", "customerId", "productId", "categoryId"];

    public string Area => ReportAreas.Sales;

    public bool IsAvailable => services.GetService<ISalesReportQueries>() is not null;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        Def("summary", "Sales summary by period", "ملخص المبيعات حسب الفترة", "Revenue, returns, tax, cost of sales and gross profit per day, month or year.", [.. CommonFilters, "groupBy"]),
        Def("by-product", "Sales by product", "المبيعات حسب الصنف", "Quantity, revenue, cost and gross profit per product.", CommonFilters),
        Def("by-customer", "Sales by customer", "المبيعات حسب العميل", "Revenue, tax and gross profit per customer.", CommonFilters),
        Def("by-branch", "Sales by branch", "المبيعات حسب الفرع", "Revenue and gross profit per branch.", CommonFilters),
        Def("returns", "Sales returns", "مرتجعات المبيعات", "Lines of posted credit notes.", CommonFilters),
        Def("gross-profit", "Gross profit by document", "مجمل الربح حسب المستند", "Revenue − cost of goods sold per posted invoice and credit note.", CommonFilters),
    ];

    public async Task<Result<ReportOutput>> RunAsync(string key, ReportQuery q, CancellationToken ct)
    {
        var groupBy = (q.GroupBy ?? "month").ToLowerInvariant();
        if (key == "summary" && groupBy is not ("day" or "month" or "year"))
        {
            return ReportingErrors.InvalidGroupBy;
        }

        var facts = await services.GetRequiredService<ISalesReportQueries>()
            .GetSalesFactsAsync(new SalesReportFilter(q.From, q.To, q.BranchId, q.WarehouseId, q.CustomerId, q.ProductIds), ct);
        ReportTable? table = key switch
        {
            "summary" => Summary(facts, groupBy, q),
            "by-product" => await ByProductAsync(facts, q, ct),
            "by-customer" => await ByCustomerAsync(facts, q, ct),
            "by-branch" => await ByBranchAsync(facts, q, ct),
            "returns" => await ReturnsAsync(facts, q, ct),
            "gross-profit" => await GrossProfitAsync(facts, q, ct),
            _ => null,
        };
        return table is null ? ReportingErrors.UnknownReport : new ReportOutput(table);
    }

    private static ReportDefinition Def(string key, string en, string ar, string description, IReadOnlyList<string> filters) =>
        new(ReportAreas.Sales, key, en, ar, description, Source, ReportingPermissions.SalesRead, "sales", filters);

    private static bool IsReturn(SalesFact f) => f.DocumentType == SalesDocumentTypes.Return;

    public static ReportTable Summary(IReadOnlyList<SalesFact> facts, string groupBy, ReportQuery q)
    {
        ReportColumn[] columns =
        [
            Text("period", "Period", "الفترة"),
            Number("invoices", "Invoices", "الفواتير"),
            Number("creditNotes", "Credit notes", "الإشعارات الدائنة"),
            Money("grossSales", "Sales", "المبيعات"),
            Money("returns", "Returns", "المرتجعات"),
            Money("netRevenue", "Net revenue", "صافي الإيرادات"),
            Money("tax", "Tax", "الضريبة"),
            Money("cogs", "Cost of sales", "تكلفة المبيعات"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
            Percent("margin", "Margin %", "هامش الربح %"),
        ];
        var rows = facts.GroupBy(f => ReportMath.Period(f.Date, groupBy)).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var revenue = g.Sum(f => f.NetBase);
            var cogs = g.Sum(f => f.CostBase);
            return new object?[]
            {
                g.Key,
                (decimal)g.Where(f => !IsReturn(f)).Select(f => f.DocumentId).Distinct().Count(),
                (decimal)g.Where(IsReturn).Select(f => f.DocumentId).Distinct().Count(),
                g.Where(f => !IsReturn(f)).Sum(f => f.NetBase),
                g.Where(IsReturn).Sum(f => f.NetBase),
                revenue,
                g.Sum(f => f.TaxBase),
                cogs,
                revenue - cogs,
                ReportMath.Margin(revenue, revenue - cogs),
            };
        }).ToList();
        var totals = Totals(columns.Length, rows, 1, 2, 3, 4, 5, 6, 7, 8);
        totals[9] = ReportMath.Margin((decimal)totals[5]!, (decimal)totals[8]!);
        return new ReportTable("sales/summary", "Sales summary by period", "ملخص المبيعات حسب الفترة", columns, rows, totals,
            [.. Period(q), new("Group by", "التجميع", groupBy)]);
    }

    private async Task<ReportTable> ByProductAsync(IReadOnlyList<SalesFact> facts, ReportQuery q, CancellationToken ct)
    {
        var products = await lookups.ProductsAsync(facts.Select(f => f.ProductId), ct);
        ReportColumn[] columns =
        [
            Sku, Product, Text("category", "Category", "الفئة"),
            Quantity("quantity", "Quantity (base unit)", "الكمية (الوحدة الأساسية)"),
            Money("netRevenue", "Net revenue", "صافي الإيرادات"),
            Money("cogs", "Cost of sales", "تكلفة المبيعات"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
            Percent("margin", "Margin %", "هامش الربح %"),
        ];
        var rows = facts.GroupBy(f => f.ProductId).Select(g =>
        {
            var revenue = g.Sum(f => f.NetBase);
            var cogs = g.Sum(f => f.CostBase);
            var category = products.TryGetValue(g.Key, out var p) ? (q.Arabic ? p.CategoryNameAr : p.CategoryNameEn) : string.Empty;
            return new object?[]
            {
                products.TryGetValue(g.Key, out var info) ? info.Sku : g.First().Sku, products.Name(g.Key, q.Arabic), category,
                g.Sum(f => f.BaseQuantity), revenue, cogs, revenue - cogs, ReportMath.Margin(revenue, revenue - cogs),
            };
        }).OrderByDescending(r => (decimal)r[4]!).ToList();
        var totals = Totals(columns.Length, rows, 3, 4, 5, 6);
        totals[7] = ReportMath.Margin((decimal)totals[4]!, (decimal)totals[6]!);
        return new ReportTable("sales/by-product", "Sales by product", "المبيعات حسب الصنف", columns, rows, totals, Period(q));
    }

    private async Task<ReportTable> ByCustomerAsync(IReadOnlyList<SalesFact> facts, ReportQuery q, CancellationToken ct)
    {
        var customers = await lookups.CustomersAsync(facts.Select(f => f.CustomerId), ct);
        ReportColumn[] columns =
        [
            Text("customerCode", "Customer code", "رمز العميل"), Text("customer", "Customer", "العميل"),
            Number("documents", "Documents", "المستندات"),
            Money("netRevenue", "Net revenue", "صافي الإيرادات"),
            Money("tax", "Tax", "الضريبة"),
            Money("gross", "Total incl. tax", "الإجمالي شامل الضريبة"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
            Percent("margin", "Margin %", "هامش الربح %"),
        ];
        var rows = facts.GroupBy(f => f.CustomerId).Select(g =>
        {
            var revenue = g.Sum(f => f.NetBase);
            var tax = g.Sum(f => f.TaxBase);
            var profit = revenue - g.Sum(f => f.CostBase);
            return new object?[]
            {
                customers.Code(g.Key), customers.Name(g.Key, q.Arabic), (decimal)g.Select(f => f.DocumentId).Distinct().Count(),
                revenue, tax, revenue + tax, profit, ReportMath.Margin(revenue, profit),
            };
        }).OrderByDescending(r => (decimal)r[3]!).ToList();
        var totals = Totals(columns.Length, rows, 2, 3, 4, 5, 6);
        totals[7] = ReportMath.Margin((decimal)totals[3]!, (decimal)totals[6]!);
        return new ReportTable("sales/by-customer", "Sales by customer", "المبيعات حسب العميل", columns, rows, totals, Period(q));
    }

    private async Task<ReportTable> ByBranchAsync(IReadOnlyList<SalesFact> facts, ReportQuery q, CancellationToken ct)
    {
        var branches = await lookups.BranchesAsync(facts.Where(f => f.BranchId != null).Select(f => f.BranchId!.Value), ct);
        ReportColumn[] columns =
        [
            Text("branch", "Branch", "الفرع"),
            Number("documents", "Documents", "المستندات"),
            Money("netRevenue", "Net revenue", "صافي الإيرادات"),
            Money("cogs", "Cost of sales", "تكلفة المبيعات"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
            Percent("margin", "Margin %", "هامش الربح %"),
        ];
        var rows = facts.GroupBy(f => f.BranchId).Select(g =>
        {
            var revenue = g.Sum(f => f.NetBase);
            var cogs = g.Sum(f => f.CostBase);
            var name = g.Key is { } id ? (branches.TryGetValue(id, out var b) ? $"{b.Code} {b.Name}" : id.ToString()) : (q.Arabic ? "بدون فرع" : "(no branch)");
            return new object?[] { name, (decimal)g.Select(f => f.DocumentId).Distinct().Count(), revenue, cogs, revenue - cogs, ReportMath.Margin(revenue, revenue - cogs) };
        }).OrderByDescending(r => (decimal)r[2]!).ToList();
        var totals = Totals(columns.Length, rows, 1, 2, 3, 4);
        totals[5] = ReportMath.Margin((decimal)totals[2]!, (decimal)totals[4]!);
        return new ReportTable("sales/by-branch", "Sales by branch", "المبيعات حسب الفرع", columns, rows, totals, Period(q));
    }

    private async Task<ReportTable> ReturnsAsync(IReadOnlyList<SalesFact> facts, ReportQuery q, CancellationToken ct)
    {
        var returns = facts.Where(IsReturn).ToList();
        var customers = await lookups.CustomersAsync(returns.Select(f => f.CustomerId), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("document", "Credit note", "الإشعار الدائن"), Text("customer", "Customer", "العميل"),
            Sku, Text("description", "Description", "الوصف"),
            Quantity("quantity", "Quantity (base unit)", "الكمية (الوحدة الأساسية)"),
            Money("net", "Net amount", "الصافي"),
            Money("tax", "Tax", "الضريبة"),
            Money("restockedCost", "Cost restocked", "تكلفة المرتد للمخزون"),
        ];
        var rows = returns.Select(f => new object?[]
        {
            f.Date, f.DocumentNumber, customers.Name(f.CustomerId, q.Arabic), f.Sku, f.Description, -f.BaseQuantity, -f.NetBase, -f.TaxBase, -f.CostBase,
        }).ToList();
        return new ReportTable("sales/returns", "Sales returns", "مرتجعات المبيعات", columns, rows, Totals(columns.Length, rows, 5, 6, 7, 8), Period(q));
    }

    private async Task<ReportTable> GrossProfitAsync(IReadOnlyList<SalesFact> facts, ReportQuery q, CancellationToken ct)
    {
        var customers = await lookups.CustomersAsync(facts.Select(f => f.CustomerId), ct);
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("document", "Document", "المستند"), Text("type", "Type", "النوع"), Text("customer", "Customer", "العميل"),
            Money("netRevenue", "Revenue", "الإيراد"),
            Money("cogs", "Cost of sales", "تكلفة المبيعات"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
            Percent("margin", "Margin %", "هامش الربح %"),
        ];
        var rows = facts.GroupBy(f => new { f.DocumentId, f.DocumentNumber, f.DocumentType, f.Date, f.CustomerId }).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.DocumentNumber)
            .Select(g =>
            {
                var revenue = g.Sum(f => f.NetBase);
                var cogs = g.Sum(f => f.CostBase);
                var type = g.Key.DocumentType == SalesDocumentTypes.Return ? (q.Arabic ? "إشعار دائن" : "Credit note") : (q.Arabic ? "فاتورة" : "Invoice");
                return new object?[] { g.Key.Date, g.Key.DocumentNumber, type, customers.Name(g.Key.CustomerId, q.Arabic), revenue, cogs, revenue - cogs, ReportMath.Margin(revenue, revenue - cogs) };
            }).ToList();
        var totals = Totals(columns.Length, rows, 4, 5, 6);
        totals[7] = ReportMath.Margin((decimal)totals[4]!, (decimal)totals[6]!);
        return new ReportTable("sales/gross-profit", "Gross profit by document", "مجمل الربح حسب المستند", columns, rows, totals, Period(q));
    }
}
