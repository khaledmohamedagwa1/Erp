using Erp.Application.Reporting;
using Erp.Modules.People.Contracts;
using Erp.Modules.Pos.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>
/// Commission reports (optional "commissions" module): the commission statement from posted commission runs
/// (People.Contracts <see cref="ICommissionReportQueries"/>). Sales by salesperson lives in the sales area (<see cref="SalesReports"/>).
/// </summary>
public sealed class PeopleReports(IServiceProvider services) : IReportArea
{
    public string Area => ReportAreas.People;

    public bool IsAvailable => services.GetService<ICommissionReportQueries>() is not null;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        new(ReportAreas.People, "commission-statement", "Commission statement", "كشف العمولات",
            "Commissioned documents of posted commission runs per salesperson (reversed runs are flagged).",
            "ICommissionReportQueries.GetStatementAsync", PeoplePermissions.CommissionRead, "commissions", ["from", "to", "salespersonId"]),
    ];

    public async Task<Result<ReportOutput>> RunAsync(string key, ReportQuery q, CancellationToken ct)
    {
        ReportTable? table = key switch
        {
            "commission-statement" => await CommissionStatementAsync(q, ct),
            _ => null,
        };
        return table is null ? ReportingErrors.UnknownReport : new ReportOutput(table);
    }

    /// <summary>Sales by salesperson: posted invoices and credit notes plus POS receipts and refunds (run from the sales area).</summary>
    public static async Task<ReportTable> SalesBySalespersonAsync(IServiceProvider services, ReportQuery q, CancellationToken ct)
    {
        var rows = new List<(Guid? Salesperson, Guid Document, decimal Net, decimal Cost)>();
        if (services.GetService<ISalesReportQueries>() is { } sales)
        {
            var facts = await sales.GetSalesFactsAsync(new SalesReportFilter(q.From, q.To, q.BranchId, SalespersonId: q.SalespersonId), ct);
            rows.AddRange(facts.Select(f => (f.SalespersonId, f.DocumentId, f.NetBase, f.CostBase)));
        }

        if (services.GetService<IPosSalesQueries>() is { } pos)
        {
            var facts = await pos.GetSalesFactsAsync(q.From, q.To, null, q.SalespersonId, ct);
            rows.AddRange(facts.Where(f => q.BranchId is null || f.BranchId == q.BranchId).Select(f => (f.SalespersonId, f.ReceiptId, f.NetBase, f.CostBase)));
        }

        var people = await EmployeesAsync(services, rows.Where(r => r.Salesperson != null).Select(r => r.Salesperson!.Value), ct);
        ReportColumn[] columns =
        [
            Text("salespersonCode", "Code", "الرمز"), Text("salesperson", "Salesperson", "مندوب المبيعات"),
            Number("documents", "Documents", "المستندات"),
            Money("netRevenue", "Net revenue", "صافي الإيرادات"),
            Money("cogs", "Cost of sales", "تكلفة المبيعات"),
            Money("grossProfit", "Gross profit", "مجمل الربح"),
            Percent("margin", "Margin %", "هامش الربح %"),
        ];
        var table = rows.GroupBy(r => r.Salesperson).Select(g =>
        {
            var revenue = g.Sum(r => r.Net);
            var cogs = g.Sum(r => r.Cost);
            var (code, name) = g.Key is { } id && people.TryGetValue(id, out var e)
                ? (e.Code, q.Arabic && !string.IsNullOrWhiteSpace(e.NameAr) ? e.NameAr : e.NameEn)
                : (string.Empty, g.Key is null ? (q.Arabic ? "بدون مندوب" : "(no salesperson)") : g.Key.ToString()!);
            return new object?[] { code, name, (decimal)g.Select(r => r.Document).Distinct().Count(), revenue, cogs, revenue - cogs, ReportMath.Margin(revenue, revenue - cogs) };
        }).OrderByDescending(r => (decimal)r[3]!).ToList();
        var totals = Totals(columns.Length, table, 2, 3, 4, 5);
        totals[6] = ReportMath.Margin((decimal)totals[3]!, (decimal)totals[5]!);
        return new ReportTable("sales/by-salesperson", "Sales by salesperson", "المبيعات حسب مندوب المبيعات", columns, table, totals, Period(q));
    }

    private async Task<ReportTable> CommissionStatementAsync(ReportQuery q, CancellationToken ct)
    {
        var lines = await services.GetRequiredService<ICommissionReportQueries>().GetStatementAsync(q.From, q.To, q.SalespersonId, ct);
        var people = await EmployeesAsync(services, lines.Select(l => l.SalespersonId), ct);
        ReportColumn[] columns =
        [
            Text("salesperson", "Salesperson", "مندوب المبيعات"),
            Text("run", "Commission run", "دورة العمولات"),
            Text("period", "Period", "الفترة"),
            Date("date", "Date", "التاريخ"),
            Text("document", "Document", "المستند"),
            Money("basis", "Commission basis", "أساس العمولة"),
            Percent("rate", "Rate %", "النسبة %"),
            Money("commission", "Commission", "العمولة"),
            Text("status", "Status", "الحالة"),
        ];
        var rows = lines
            .OrderBy(l => people.TryGetValue(l.SalespersonId, out var e) ? e.Code : l.SalespersonId.ToString(), StringComparer.Ordinal)
            .ThenBy(l => l.PeriodFrom).ThenBy(l => l.DocumentDate).ThenBy(l => l.DocumentNumber, StringComparer.Ordinal)
            .Select(l => new object?[]
            {
                people.TryGetValue(l.SalespersonId, out var e) ? $"{e.Code} {(q.Arabic && !string.IsNullOrWhiteSpace(e.NameAr) ? e.NameAr : e.NameEn)}" : l.SalespersonId.ToString(),
                l.RunNumber, l.PeriodFrom.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), l.DocumentDate, l.DocumentNumber, l.BasisAmount,
                l.RatePercent, l.Reversed ? 0m : l.Commission,
                l.Reversed ? (q.Arabic ? "معكوسة" : "Reversed") : (q.Arabic ? "مرحلة" : "Posted"),
            }).ToList();
        return new ReportTable("people/commission-statement", "Commission statement", "كشف العمولات", columns, rows, Totals(columns.Length, rows, 7), Period(q));
    }

    private static async Task<IReadOnlyDictionary<Guid, EmployeeInfo>> EmployeesAsync(IServiceProvider services, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 || services.GetService<IEmployeeQueries>() is not { } employees
            ? new Dictionary<Guid, EmployeeInfo>()
            : (await employees.GetEmployeesAsync(list, ct)).ToDictionary(e => e.Id);
    }
}
