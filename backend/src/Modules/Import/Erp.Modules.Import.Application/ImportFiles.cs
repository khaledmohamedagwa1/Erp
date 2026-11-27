using System.Globalization;
using System.Text;
using Erp.Modules.Import.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Import.Application;

/// <summary>One data row of the file. <see cref="RowNumber"/> is the spreadsheet row (header = 1).</summary>
public sealed record ImportSourceRow(int RowNumber, IReadOnlyList<string?> Cells);

/// <summary>A file reduced to its header and text cells (dates as yyyy-MM-dd, numbers with invariant culture).</summary>
public sealed record ImportTable(IReadOnlyList<string> Headers, IReadOnlyList<ImportSourceRow> Rows);

/// <summary>Reads an uploaded CSV/XLSX file. XLSX lives in Infrastructure (ClosedXML); CSV is plain code.</summary>
public interface IImportFileReader
{
    Result<ImportTable> Read(byte[] content, ImportFileFormat format);
}

/// <summary>Writes the downloadable XLSX template of an import type.</summary>
public interface IImportTemplateWriter
{
    byte[] Write(ImportDefinitionInfo definition);
}

public static class ImportLimits
{
    public const int MaxRows = 50_000;

    public static Result<ImportFileFormat> FormatOf(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".csv" or ".txt" => ImportFileFormat.Csv,
            ".xlsx" => ImportFileFormat.Xlsx,
            _ => ImportErrors.UnsupportedFormat,
        };
    }

    public static Error TooManyRows => Error.Validation("import.too-many-rows", $"A file may contain at most {MaxRows:N0} data rows. Split it into several imports.");
}

/// <summary>
/// RFC 4180 CSV: quoted fields with doubled quotes and embedded line breaks, UTF-8 (BOM optional), delimiter detected from the
/// header line (comma, semicolon or tab — Excel in Arabic/European locales saves with semicolons).
/// </summary>
public static class CsvImportReader
{
    public static Result<ImportTable> Read(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Error.Validation("import.encoding", "The CSV file must be saved as UTF-8.");
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return ImportErrors.FileEmpty;
        }

        var delimiter = DetectDelimiter(text);
        var records = Parse(text, delimiter);
        if (records.Count == 0)
        {
            return ImportErrors.FileEmpty;
        }

        var headers = records[0].Cells.Select(c => c ?? string.Empty).ToList();
        var rows = records.Skip(1).Select(r => new ImportSourceRow(r.RowNumber, r.Cells)).ToList();
        return new ImportTable(headers, rows);
    }

    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (ch == '\n' || ch == '\r'))
            {
                break;
            }
            else if (!quoted && counts.ContainsKey(ch))
            {
                counts[ch]++;
            }
        }

        return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key == ',' ? 0 : 1).First().Key;
    }

    private static List<ImportSourceRow> Parse(string text, char delimiter)
    {
        var records = new List<ImportSourceRow>();
        var cells = new List<string?>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldWasQuoted = false;
        var line = 1;
        var recordStart = 1;

        void EndField()
        {
            var value = field.ToString();
            cells.Add(fieldWasQuoted ? value : value.Trim());
            field.Clear();
            fieldWasQuoted = false;
        }

        void EndRecord()
        {
            EndField();
            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                records.Add(new ImportSourceRow(recordStart, cells.ToList()));
            }

            cells.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        line++;
                    }

                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"' && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                quoted = true;
                fieldWasQuoted = true;
            }
            else if (ch == delimiter)
            {
                EndField();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRecord();
                line++;
                recordStart = line;
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || cells.Count > 0)
        {
            EndRecord();
        }

        return records;
    }
}

/// <summary>Cell value parsing shared by all import types (invariant culture; Arabic-Indic digits accepted).</summary>
public static class ImportValues
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                '٫' => '.',
                '٬' => ',',
                _ => ch,
            });
        }

        return builder.ToString();
    }

    public static bool TryDecimal(string value, out decimal result)
    {
        var text = Normalize(value).Replace(" ", string.Empty, StringComparison.Ordinal);

        // "1,234.5" and "1,234" use thousands separators; a single comma not followed by exactly three digits ("12,5") is a decimal comma.
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0 && !text.Contains('.', StringComparison.Ordinal) && comma == text.LastIndexOf(',') && text.Length - comma - 1 != 3)
        {
            text = text.Replace(',', '.');
        }
        else
        {
            text = text.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        var negative = text.StartsWith('(') && text.EndsWith(')');
        if (negative)
        {
            text = text[1..^1];
        }

        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out result))
        {
            result = negative ? -result : result;
            return true;
        }

        return false;
    }

    public static bool TryDate(string value, out DateOnly result)
    {
        var text = Normalize(value);
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = DateOnly.FromDateTime(dt);
            return true;
        }

        result = default;
        return false;
    }

    public static bool TryBool(string value, out bool result)
    {
        switch (Normalize(value).ToLowerInvariant())
        {
            case "1" or "true" or "yes" or "y" or "نعم" or "صح":
                result = true;
                return true;
            case "0" or "false" or "no" or "n" or "لا" or "خطأ":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
