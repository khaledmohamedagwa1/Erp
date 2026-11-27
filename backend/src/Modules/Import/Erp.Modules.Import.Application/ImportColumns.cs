using System.Text;
using Erp.Modules.Import.Domain;

namespace Erp.Modules.Import.Application;

/// <summary>
/// A column of an import layout. Headers are matched case-insensitively ignoring spaces and punctuation, against the English
/// header, the key and any alias (Arabic aliases included), so "Name (Arabic)", "name_ar" and "الاسم بالعربية" all match.
/// </summary>
public sealed record ImportColumn(string Key, string Header, string HeaderAr, bool Required, string Description, string Example, params string[] Aliases);

/// <summary>What the API and the template expose about an import type.</summary>
public sealed record ImportDefinitionInfo(ImportType Type, string Name, string NameAr, string TargetPermission, bool RequiresLegalEntity, bool RequiresOpeningDate,
    IReadOnlyList<ImportColumn> Columns);

/// <summary>Maps the file's header row to column keys.</summary>
public sealed class ImportColumnMap
{
    private readonly Dictionary<string, int> _indexByKey;

    private ImportColumnMap(Dictionary<string, int> indexByKey, IReadOnlyList<string> unknownHeaders)
    {
        _indexByKey = indexByKey;
        UnknownHeaders = unknownHeaders;
    }

    public IReadOnlyList<string> UnknownHeaders { get; }

    public static string NormalizeHeader(string header)
    {
        var builder = new StringBuilder(header.Length);
        foreach (var ch in header)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>Builds the map; errors (header row 1) for missing required columns and for columns given twice.</summary>
    public static (ImportColumnMap Map, IReadOnlyList<ImportRowError> Errors) Build(IReadOnlyList<ImportColumn> columns, IReadOnlyList<string> headers)
    {
        var lookup = new Dictionary<string, ImportColumn>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            foreach (var name in new[] { column.Key, column.Header, column.HeaderAr }.Concat(column.Aliases))
            {
                var normalized = NormalizeHeader(name);
                if (normalized.Length > 0)
                {
                    lookup.TryAdd(normalized, column);
                }
            }
        }

        var errors = new List<ImportRowError>();
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i]?.Trim() ?? string.Empty;
            if (header.Length == 0)
            {
                continue;
            }

            if (!lookup.TryGetValue(NormalizeHeader(header), out var column))
            {
                unknown.Add(header);
                continue;
            }

            if (!indexByKey.TryAdd(column.Key, i))
            {
                errors.Add(new ImportRowError(1, column.Header, "import.duplicate-column", $"Column '{column.Header}' appears more than once."));
            }
        }

        foreach (var column in columns.Where(c => c.Required && !indexByKey.ContainsKey(c.Key)))
        {
            errors.Add(new ImportRowError(1, column.Header, "import.missing-column", $"Required column '{column.Header}' ({column.HeaderAr}) is missing."));
        }

        return (new ImportColumnMap(indexByKey, unknown), errors);
    }

    public string? Get(ImportSourceRow row, string key) =>
        _indexByKey.TryGetValue(key, out var index) && index < row.Cells.Count ? row.Cells[index]?.Trim() is { Length: > 0 } value ? value : null : null;
}

/// <summary>Typed access to one row's cells; every conversion problem is recorded against the row and column.</summary>
public sealed class ImportRowValues(ImportSourceRow row, ImportColumnMap map, IReadOnlyList<ImportColumn> columns)
{
    private readonly List<ImportRowError> _errors = [];

    public int Row => row.RowNumber;

    public IReadOnlyList<ImportRowError> Errors => _errors;

    public bool HasErrors => _errors.Count > 0;

    public void AddError(string key, string code, string message) => _errors.Add(new ImportRowError(Row, Header(key), code, message));

    public string? Text(string key, int maxLength = 500)
    {
        var value = map.Get(row, key);
        if (value is not null && value.Length > maxLength)
        {
            AddError(key, "import.value-too-long", $"At most {maxLength} characters.");
        }

        return value;
    }

    public string RequiredText(string key, int maxLength = 500)
    {
        var value = Text(key, maxLength);
        if (value is null)
        {
            AddError(key, "import.value-required", "A value is required.");
        }

        return value ?? string.Empty;
    }

    public decimal? Decimal(string key)
    {
        var value = map.Get(row, key);
        if (value is null)
        {
            return null;
        }

        if (ImportValues.TryDecimal(value, out var result))
        {
            return result;
        }

        AddError(key, "import.invalid-number", $"'{value}' is not a number.");
        return null;
    }

    public decimal RequiredDecimal(string key)
    {
        if (map.Get(row, key) is null)
        {
            AddError(key, "import.value-required", "A value is required.");
            return 0;
        }

        return Decimal(key) ?? 0;
    }

    public DateOnly? Date(string key)
    {
        var value = map.Get(row, key);
        if (value is null)
        {
            return null;
        }

        if (ImportValues.TryDate(value, out var result))
        {
            return result;
        }

        AddError(key, "import.invalid-date", $"'{value}' is not a date (use yyyy-MM-dd).");
        return null;
    }

    public DateOnly RequiredDate(string key)
    {
        if (map.Get(row, key) is null)
        {
            AddError(key, "import.value-required", "A value is required.");
            return default;
        }

        return Date(key) ?? default;
    }

    public bool Bool(string key, bool defaultValue)
    {
        var value = map.Get(row, key);
        if (value is null)
        {
            return defaultValue;
        }

        if (ImportValues.TryBool(value, out var result))
        {
            return result;
        }

        AddError(key, "import.invalid-boolean", $"'{value}' is not yes/no.");
        return defaultValue;
    }

    private string Header(string key) => columns.FirstOrDefault(c => c.Key == key)?.Header ?? key;
}
