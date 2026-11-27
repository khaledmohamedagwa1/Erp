using ClosedXML.Excel;
using Erp.Application.Reporting;

namespace Erp.Modules.Reporting.Infrastructure;

/// <summary>
/// Excel workbook via ClosedXML (MIT). One sheet: title, company, filter parameters, a header row, typed cells (numbers stay
/// numbers, dates are real dates) and a bold totals row. Arabic output sets the sheet right-to-left.
/// </summary>
public static class XlsxReportWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void Write(ReportTable table, RenderContext context, Stream output)
    {
        using var workbook = new XLWorkbook();
        var arabic = context.IsArabic;
        var sheet = workbook.Worksheets.Add(SheetName(arabic ? table.TitleAr : table.TitleEn));
        sheet.RightToLeft = arabic;

        var row = 1;
        sheet.Cell(row, 1).Value = arabic ? table.TitleAr : table.TitleEn;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontSize = 14;
        row++;
        sheet.Cell(row++, 1).Value = arabic ? context.CompanyNameAr : context.CompanyNameEn;
        foreach (var parameter in table.Parameters ?? [])
        {
            sheet.Cell(row, 1).Value = arabic ? parameter.LabelAr : parameter.LabelEn;
            sheet.Cell(row, 2).Value = parameter.Value;
            row++;
        }

        row++;
        var headerRow = row;
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var cell = sheet.Cell(row, c + 1);
            cell.Value = arabic ? table.Columns[c].LabelAr : table.Columns[c].LabelEn;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF4");
        }

        row++;
        foreach (var values in table.Rows)
        {
            WriteRow(sheet, row++, table.Columns, values);
        }

        if (table.Totals is { } totals)
        {
            WriteRow(sheet, row, table.Columns, totals);
            if (totals.Length > 0 && totals[0] is null)
            {
                sheet.Cell(row, 1).Value = arabic ? "الإجمالي" : "Total";
            }

            sheet.Row(row).Style.Font.Bold = true;
        }

        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, Math.Max(1, table.Columns.Count)).AdjustToContents(headerRow, Math.Min(row, headerRow + 500));
        workbook.SaveAs(output);
    }

    private static void WriteRow(IXLWorksheet sheet, int row, IReadOnlyList<ReportColumn> columns, object?[] values)
    {
        for (var c = 0; c < columns.Count && c < values.Length; c++)
        {
            var cell = sheet.Cell(row, c + 1);
            switch (values[c])
            {
                case null:
                    break;
                case decimal d:
                    cell.Value = d;
                    cell.Style.NumberFormat.Format = columns[c].Type switch
                    {
                        ReportColumnType.Money => "#,##0.00",
                        ReportColumnType.Percent => "0.00",
                        ReportColumnType.Number => "#,##0",
                        _ => "#,##0.######",
                    };
                    break;
                case int i:
                    cell.Value = i;
                    break;
                case DateOnly date:
                    cell.Value = date.ToDateTime(TimeOnly.MinValue);
                    cell.Style.NumberFormat.Format = "yyyy-mm-dd";
                    break;
                case DateTime dateTime:
                    cell.Value = dateTime;
                    cell.Style.NumberFormat.Format = "yyyy-mm-dd hh:mm";
                    break;
                default:
                    cell.Value = values[c]!.ToString();
                    break;
            }
        }
    }

    /// <summary>Excel sheet names: at most 31 characters and none of : \ / ? * [ ].</summary>
    public static string SheetName(string title)
    {
        var cleaned = new string(title.Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Report";
        }

        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
