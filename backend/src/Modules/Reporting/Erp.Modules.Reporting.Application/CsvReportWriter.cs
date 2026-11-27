using System.Globalization;
using System.Text;
using Erp.Application.Reporting;

namespace Erp.Modules.Reporting.Application;

/// <summary>
/// RFC 4180 CSV in UTF-8 with a byte-order mark (so Excel opens Arabic text correctly). Values are written with the invariant
/// culture (dot decimal separator, ISO dates); headers use the requested language. Rows are streamed to the output.
/// </summary>
public static class CsvReportWriter
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static async Task WriteAsync(ReportTable table, Stream output, bool arabic, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 16 * 1024, leaveOpen: true);
        await writer.WriteAsync(Line(table.Columns.Select(c => arabic ? c.LabelAr : c.LabelEn)).AsMemory(), cancellationToken);
        var count = 0;
        foreach (var row in table.Rows)
        {
            await writer.WriteAsync(Line(row.Select((v, i) => Format(v, table.Columns[i].Type))).AsMemory(), cancellationToken);
            if (++count % 1000 == 0)
            {
                await writer.FlushAsync(cancellationToken);
            }
        }

        if (table.Totals is { } totals)
        {
            await writer.WriteAsync(Line(totals.Select((v, i) => i == 0 && v is null ? (arabic ? "الإجمالي" : "Total") : Format(v, table.Columns[i].Type))).AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
    }

    public static string Line(IEnumerable<string> values) => string.Join(',', values.Select(Escape)) + "\r\n";

    /// <summary>Quotes a field when it contains a comma, quote, CR or LF; doubles embedded quotes.</summary>
    public static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    public static string Format(object? value, ReportColumnType type) => value switch
    {
        null => string.Empty,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        decimal m when type == ReportColumnType.Money => m.ToString("0.00", CultureInfo.InvariantCulture),
        decimal q => q.ToString("0.######", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
