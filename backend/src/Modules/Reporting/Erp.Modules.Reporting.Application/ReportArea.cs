using Erp.Application.Reporting;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Reporting.Application;

/// <summary>A group of reports backed by one module's contracts.</summary>
public interface IReportArea
{
    string Area { get; }

    IReadOnlyList<ReportDefinition> Definitions { get; }

    /// <summary>False when the module providing the data is not installed in this host.</summary>
    bool IsAvailable { get; }

    Task<Result<ReportOutput>> RunAsync(string key, ReportQuery query, CancellationToken cancellationToken);
}

internal static class Columns
{
    public static ReportColumn Text(string key, string en, string ar) => new(key, en, ar, ReportColumnType.Text);

    public static ReportColumn Number(string key, string en, string ar) => new(key, en, ar, ReportColumnType.Number);

    public static ReportColumn Quantity(string key, string en, string ar) => new(key, en, ar, ReportColumnType.Quantity);

    public static ReportColumn Money(string key, string en, string ar) => new(key, en, ar, ReportColumnType.Money);

    public static ReportColumn Percent(string key, string en, string ar) => new(key, en, ar, ReportColumnType.Percent);

    public static ReportColumn Date(string key, string en, string ar) => new(key, en, ar, ReportColumnType.Date);

    public static ReportColumn Sku => Text("sku", "SKU", "رمز الصنف");

    public static ReportColumn Product => Text("product", "Product", "الصنف");

    public static ReportColumn Warehouse => Text("warehouse", "Warehouse", "المستودع");

    public static IReadOnlyList<ReportParameter> Period(ReportQuery q) =>
    [
        new("From", "من", q.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        new("To", "إلى", q.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
    ];

    /// <summary>Totals row: null for text columns, sums for the listed numeric column indexes.</summary>
    public static object?[] Totals(int columnCount, IReadOnlyList<object?[]> rows, params int[] sumColumns)
    {
        var totals = new object?[columnCount];
        foreach (var i in sumColumns)
        {
            totals[i] = rows.Sum(r => r[i] is decimal d ? d : 0m);
        }

        return totals;
    }
}
