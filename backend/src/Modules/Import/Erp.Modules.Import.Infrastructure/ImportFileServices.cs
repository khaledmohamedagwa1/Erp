using System.Globalization;
using ClosedXML.Excel;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Import.Application;
using Erp.Modules.Import.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Import.Infrastructure;

/// <summary>CSV (plain code) and XLSX (ClosedXML, first worksheet, header in row 1).</summary>
public sealed class ImportFileReader : IImportFileReader
{
    public Result<ImportTable> Read(byte[] content, ImportFileFormat format) => format switch
    {
        ImportFileFormat.Csv => CsvImportReader.Read(content),
        ImportFileFormat.Xlsx => ReadXlsx(content),
        _ => ImportErrors.UnsupportedFormat,
    };

    public static Result<ImportTable> ReadXlsx(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.FirstOrDefault();
            var used = sheet?.RangeUsed();
            if (sheet is null || used is null)
            {
                return ImportErrors.FileEmpty;
            }

            var lastColumn = used.LastColumn().ColumnNumber();
            var lastRow = used.LastRow().RowNumber();
            if (lastRow - 1 > ImportLimits.MaxRows)
            {
                return ImportLimits.TooManyRows;
            }

            var headers = Enumerable.Range(1, lastColumn).Select(c => Text(sheet.Cell(1, c)) ?? string.Empty).ToList();
            var rows = new List<ImportSourceRow>();
            for (var r = 2; r <= lastRow; r++)
            {
                var cells = Enumerable.Range(1, lastColumn).Select(c => Text(sheet.Cell(r, c))).ToList();
                if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                {
                    rows.Add(new ImportSourceRow(r, cells));
                }
            }

            return new ImportTable(headers, rows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Error.Validation("import.unreadable-file", "The file is not a readable .xlsx workbook.");
        }
    }

    private static string? Text(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsBlank)
        {
            return null;
        }

        if (value.IsDateTime)
        {
            var date = value.GetDateTime();
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (value.IsNumber)
        {
            return ((decimal)value.GetNumber()).ToString(CultureInfo.InvariantCulture);
        }

        if (value.IsBoolean)
        {
            return value.GetBoolean() ? "true" : "false";
        }

        if (value.IsError)
        {
            return "#ERROR";
        }

        return value.ToString(CultureInfo.InvariantCulture)?.Trim();
    }
}

/// <summary>Template workbook: "Data" sheet (headers, required ones marked with *, one example row) and a "Columns" help sheet.</summary>
public sealed class XlsxImportTemplateWriter : IImportTemplateWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public byte[] Write(ImportDefinitionInfo definition)
    {
        using var workbook = new XLWorkbook();
        var data = workbook.Worksheets.Add("Data");
        for (var c = 0; c < definition.Columns.Count; c++)
        {
            var column = definition.Columns[c];
            var header = data.Cell(1, c + 1);
            header.Value = column.Required ? column.Header + " *" : column.Header;
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = column.Required ? XLColor.FromHtml("#FDE9D9") : XLColor.FromHtml("#E8EEF4");
            data.Cell(2, c + 1).SetValue(column.Example);
            data.Cell(2, c + 1).Style.NumberFormat.Format = "@";
        }

        data.SheetView.FreezeRows(1);
        data.Columns().AdjustToContents();

        var help = workbook.Worksheets.Add("Columns");
        string[] titles = ["Column", "العمود", "Required", "Description", "Also accepted"];
        for (var c = 0; c < titles.Length; c++)
        {
            help.Cell(1, c + 1).Value = titles[c];
            help.Cell(1, c + 1).Style.Font.Bold = true;
        }

        for (var i = 0; i < definition.Columns.Count; i++)
        {
            var column = definition.Columns[i];
            help.Cell(i + 2, 1).Value = column.Header;
            help.Cell(i + 2, 2).Value = column.HeaderAr;
            help.Cell(i + 2, 3).Value = column.Required ? "yes" : "no";
            help.Cell(i + 2, 4).Value = column.Description;
            help.Cell(i + 2, 5).Value = string.Join(", ", column.Aliases);
        }

        var notes = definition.Columns.Count + 3;
        help.Cell(notes, 1).Value = $"{definition.Name} — {definition.NameAr}";
        help.Cell(notes + 1, 1).Value = "Headers may be in English or Arabic, in any order. Dates: yyyy-MM-dd. Delete the example row before importing.";
        help.Columns().AdjustToContents();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}

/// <summary>SQL Server savepoints on the shared scope transaction (ADR-006).</summary>
public sealed class SqlImportSavepoints(ErpDbSession session) : IImportSavepoints
{
    public Task CreateAsync(string name, CancellationToken cancellationToken) =>
        (session.Transaction ?? throw new InvalidOperationException("Imports run inside a transaction.")).SaveAsync(name, cancellationToken);

    public Task RollbackAsync(string name, CancellationToken cancellationToken) =>
        (session.Transaction ?? throw new InvalidOperationException("Imports run inside a transaction.")).RollbackAsync(name, cancellationToken);
}
