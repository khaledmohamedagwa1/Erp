using Erp.Application.Reporting;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Reporting.Application;

/// <summary>Filters accepted by every report endpoint (each report uses the ones that apply). The company is always the active one.</summary>
public sealed record ReportRequest(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? BranchId = null,
    Guid? WarehouseId = null,
    Guid? CustomerId = null,
    Guid? SupplierId = null,
    Guid? ProductId = null,
    Guid? CategoryId = null,
    string? GroupBy = null,
    int? Days = null,
    string? BatchNumber = null,
    int Page = 1,
    int PageSize = 100,
    Guid? SalespersonId = null);

/// <summary>A request with defaults applied and the product/category filter resolved to product ids.</summary>
public sealed record ReportQuery(
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    Guid? BranchId,
    Guid? WarehouseId,
    Guid? CustomerId,
    Guid? SupplierId,
    IReadOnlyCollection<Guid>? ProductIds,
    string? GroupBy,
    int? Days,
    string? BatchNumber,
    int Page,
    int PageSize,
    bool ForExport,
    int ExportCap,
    bool Arabic,
    Guid? SalespersonId = null);

public sealed record ReportDefinition(
    string Area,
    string Key,
    string TitleEn,
    string TitleAr,
    string DescriptionEn,
    string Source,
    string Permission,
    string ModuleCode,
    IReadOnlyList<string> Filters,
    string? Href = null)
{
    public string FullKey => $"{Area}/{Key}";

    public bool IsLink => Href is not null;
}

/// <summary>Output of a report builder. When <see cref="IsPaged"/> the rows are already the requested page (database paging).</summary>
public sealed record ReportOutput(ReportTable Table, bool IsPaged = false, int TotalCount = 0);

public sealed record ReportColumnDto(string Key, string LabelEn, string LabelAr, string Type);

public sealed record ReportCatalogueEntry(
    string Area,
    string Key,
    string TitleEn,
    string TitleAr,
    string DescriptionEn,
    string Source,
    IReadOnlyList<string> Filters,
    IReadOnlyList<string> Formats,
    string Href);

/// <summary>JSON shape of a report: rows are objects keyed by column key.</summary>
public sealed record ReportResultDto(
    string Area,
    string Key,
    string TitleEn,
    string TitleAr,
    IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IDictionary<string, object?>> Rows,
    IDictionary<string, object?>? Totals,
    IReadOnlyList<ReportParameter> Parameters,
    int Page,
    int PageSize,
    int TotalCount);

public static class ReportingErrors
{
    public static readonly Error UnknownReport = Error.NotFound("reporting.report-not-found", "The report does not exist.");

    public static readonly Error ModuleDisabled = Error.NotFound("module-disabled", "The module behind this report is not enabled.");

    public static readonly Error InvalidRange = Error.Validation("reporting.invalid-range", "'from' must not be after 'to'.");

    public static readonly Error RangeTooLong = Error.Validation("reporting.range-too-long", "The date range may span at most 3 years.");

    public static readonly Error InvalidGroupBy = Error.Validation("reporting.invalid-group-by", "groupBy must be day, month or year.");

    public static readonly Error InvalidFormat = Error.Validation("reporting.invalid-format", "format must be json, csv, xlsx or html.");

    public static readonly Error BatchRequired = Error.Validation("reporting.batch-required", "batchNumber is required for batch traceability.");

    public static readonly Error DocumentNotFound = Error.NotFound("reporting.document-not-found", "The document does not exist or is not printable.");

    public static readonly Error UnknownDocumentType = Error.NotFound("reporting.document-type-not-found", "Unknown document type.");

    public static readonly Error ViewNotFound = Error.NotFound("reporting.view-not-found", "The saved view does not exist.");

    public static readonly Error LinkReport = Error.Validation("reporting.link-report",
        "This catalogue entry links to another module's report and cannot be used in dashboards or schedules.");

    public static readonly Error DashboardNotFound = Error.NotFound("reporting.dashboard-not-found", "The dashboard does not exist.");

    public static readonly Error WidgetNotFound = Error.NotFound("reporting.widget-not-found", "The widget does not exist on this dashboard.");

    public static readonly Error NotOwner = Error.Forbidden("reporting.not-owner", "Only the owner can change this.");

    public static readonly Error ScheduleNotFound = Error.NotFound("reporting.schedule-not-found", "The report schedule does not exist.");

    public static readonly Error RunNotFound = Error.NotFound("reporting.schedule-run-not-found", "The run does not exist or has no output.");

    public static Error InvalidParameter(string name, string reason) =>
        Error.Validation("reporting.parameter-invalid", $"Parameter '{name}': {reason}");

    public static readonly Error CompanyRequired = Error.Validation("company.required", "This endpoint requires the X-Company-Id header.");

    public static Error Forbidden(string permission) => Error.Forbidden("forbidden", $"Missing permission '{permission}'.");

    public static Error ExportTooLarge(int rows, int cap) => Error.Validation(
        "reporting.export-too-large", $"The export has more than {cap} rows{(rows > cap ? $" ({rows})" : string.Empty)}. Narrow the filters (date range, warehouse, product) and try again.");
}

/// <summary>Configuration section <c>Reporting</c>.</summary>
public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    /// <summary>Maximum number of rows in a CSV/Excel/HTML export.</summary>
    public int MaxExportRows { get; set; } = 50_000;

    /// <summary>How often the worker looks for due report schedules.</summary>
    public int ScheduleIntervalMinutes { get; set; } = 5;
}
