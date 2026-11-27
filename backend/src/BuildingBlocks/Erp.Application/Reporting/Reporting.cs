using Erp.SharedKernel.Documents;

namespace Erp.Application.Reporting;

public enum ReportColumnType
{
    Text,
    Number,
    Quantity,
    Money,
    Percent,
    Date,
}

public sealed record ReportColumn(string Key, string LabelEn, string LabelAr, ReportColumnType Type);

public sealed record ReportParameter(string LabelEn, string LabelAr, string Value);

/// <summary>
/// A tabular report as produced by the server: columns, rows (one value per column, in column order) and optional totals.
/// Exporters (CSV, Excel, HTML) and the JSON API all render from this one shape.
/// </summary>
public sealed record ReportTable(
    string Key,
    string TitleEn,
    string TitleAr,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    object?[]? Totals = null,
    IReadOnlyList<ReportParameter>? Parameters = null);

/// <summary>Company letterhead and language for rendered output.</summary>
public sealed record RenderContext(
    string Language,
    string CompanyNameEn,
    string CompanyNameAr,
    string? LegalEntityName,
    string? TaxRegistrationNo,
    DateTime GeneratedAtUtc)
{
    public bool IsArabic => Language.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resolved counterparty details printed on a document.</summary>
public sealed record PrintPartyDetails(string Code, string Name, string? TaxRegistrationNo);

/// <summary>
/// Document rendering port (decision OQ-LIC-1). The first implementation produces printable, self-contained HTML
/// (RTL for Arabic, page-break friendly); a PDF implementation can be added behind the same port.
/// </summary>
public interface IDocumentRenderer
{
    string ContentType { get; }

    string RenderReport(ReportTable report, RenderContext context);

    string RenderDocument(PrintableDocument document, PrintPartyDetails? party, IReadOnlyDictionary<Guid, string> unitNames, RenderContext context);
}

/// <summary>
/// HTML → PDF conversion port (decision OQ-LIC-1, second step). The default implementation is not configured
/// (<see cref="IsAvailable"/> is false and endpoints answer 501); the Gotenberg adapter converts through an optional
/// Gotenberg service. Called only from read endpoints — never inside a database transaction.
/// </summary>
public interface IPdfRenderer
{
    bool IsAvailable { get; }

    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default);
}

/// <summary>Used when no PDF engine is configured.</summary>
public sealed class NullPdfRenderer : IPdfRenderer
{
    public bool IsAvailable => false;

    public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No PDF renderer is configured (set Pdf:GotenbergUrl).");
}
