using System.Globalization;
using System.Net;
using System.Text;
using Erp.Application.Reporting;
using Erp.SharedKernel.Documents;

namespace Erp.Infrastructure.Rendering;

/// <summary>
/// Renders reports and business documents as self-contained, printable HTML (decision OQ-LIC-1). Arabic output sets
/// <c>dir="rtl"</c> and uses logical CSS properties so the same stylesheet works in both directions. Numbers use Western
/// digits (OQ-LOC-3). Table headers repeat on every printed page and rows never split across pages.
/// </summary>
public sealed class HtmlDocumentRenderer : IDocumentRenderer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const string Css = """
        *{box-sizing:border-box}
        body{font-family:"IBM Plex Sans Arabic","Noto Sans Arabic","Segoe UI",Tahoma,Arial,sans-serif;font-size:12px;color:#111827;margin:24px}
        header{display:flex;justify-content:space-between;gap:16px;border-bottom:2px solid #111827;padding-bottom:8px;margin-bottom:12px}
        h1{font-size:18px;margin:0 0 4px} h2{font-size:15px;margin:0}
        .muted{color:#4b5563}
        table{width:100%;border-collapse:collapse;margin-top:8px}
        thead{display:table-header-group} tr{page-break-inside:avoid;break-inside:avoid}
        th,td{border:1px solid #d1d5db;padding:4px 6px;text-align:start;vertical-align:top}
        th{background:#f3f4f6}
        td.num,th.num{text-align:end;font-variant-numeric:tabular-nums;white-space:nowrap}
        tfoot td{font-weight:700;background:#f9fafb}
        .fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:4px 16px;margin:8px 0}
        .totals{margin-inline-start:auto;width:320px}
        .totals td{border:none;padding:2px 6px} .totals .emph td{font-weight:700;border-top:1px solid #111827}
        footer{margin-top:16px;font-size:10px;color:#6b7280}
        @media print{body{margin:0} @page{size:A4;margin:14mm}}
        """;

    public string ContentType => "text/html; charset=utf-8";

    public string RenderReport(ReportTable report, RenderContext context)
    {
        var ar = context.IsArabic;
        var html = Begin(context, ar ? report.TitleAr : report.TitleEn);
        html.Append("<header><div><h1>").Append(E(ar ? report.TitleAr : report.TitleEn)).Append("</h1>");
        foreach (var p in report.Parameters ?? [])
        {
            html.Append("<div class=\"muted\">").Append(E(ar ? p.LabelAr : p.LabelEn)).Append(": ").Append(E(p.Value)).Append("</div>");
        }

        html.Append("</div>").Append(Letterhead(context)).Append("</header>");
        html.Append("<table><thead><tr>");
        foreach (var c in report.Columns)
        {
            html.Append("<th").Append(IsNumeric(c.Type) ? " class=\"num\"" : string.Empty).Append('>').Append(E(ar ? c.LabelAr : c.LabelEn)).Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (var row in report.Rows)
        {
            AppendRow(html, report.Columns, row);
        }

        html.Append("</tbody>");
        if (report.Totals is { } totals)
        {
            html.Append("<tfoot>");
            AppendRow(html, report.Columns, totals);
            html.Append("</tfoot>");
        }

        html.Append("</table>");
        return End(html, context, report.Rows.Count);
    }

    public string RenderDocument(PrintableDocument document, PrintPartyDetails? party, IReadOnlyDictionary<Guid, string> unitNames, RenderContext context)
    {
        var ar = context.IsArabic;
        var title = ar ? document.TitleAr : document.TitleEn;
        var html = Begin(context, $"{title} {document.Number}");
        html.Append("<header><div><h1>").Append(E(title)).Append("</h1><h2>").Append(E(document.Number)).Append("</h2>")
            .Append("<div class=\"muted\">").Append(E(ar ? "التاريخ" : "Date")).Append(": ").Append(document.Date.ToString("yyyy-MM-dd", Invariant)).Append("</div>")
            .Append("<div class=\"muted\">").Append(E(ar ? "الحالة" : "Status")).Append(": ").Append(E(document.Status)).Append("</div>")
            .Append("</div>").Append(Letterhead(context)).Append("</header>");

        html.Append("<div class=\"fields\">");
        if (party is not null)
        {
            var partyLabel = document.Party?.Kind == "Supplier" ? (ar ? "المورد" : "Supplier") : (ar ? "العميل" : "Customer");
            Field(html, partyLabel, $"{party.Code} — {party.Name}");
            if (!string.IsNullOrWhiteSpace(party.TaxRegistrationNo))
            {
                Field(html, ar ? "الرقم الضريبي" : "Tax registration", party.TaxRegistrationNo);
            }
        }

        Field(html, ar ? "العملة" : "Currency", document.Currency);
        foreach (var f in document.Fields)
        {
            Field(html, ar ? f.LabelAr : f.LabelEn, f.Value);
        }

        html.Append("</div>");

        var showPrice = document.Lines.Any(l => l.UnitPrice is not null);
        var showTax = document.Lines.Any(l => l.Tax is not null);
        html.Append("<table><thead><tr>")
            .Append("<th>#</th><th>").Append(E(ar ? "الكود" : "Code")).Append("</th><th>").Append(E(ar ? "البيان" : "Description")).Append("</th>")
            .Append("<th class=\"num\">").Append(E(ar ? "الكمية" : "Quantity")).Append("</th><th>").Append(E(ar ? "الوحدة" : "Unit")).Append("</th>");
        if (showPrice)
        {
            html.Append("<th class=\"num\">").Append(E(ar ? "السعر" : "Unit price")).Append("</th><th class=\"num\">").Append(E(ar ? "خصم %" : "Disc. %"))
                .Append("</th><th class=\"num\">").Append(E(ar ? "الصافي" : "Net")).Append("</th>");
        }

        if (showTax)
        {
            html.Append("<th class=\"num\">").Append(E(ar ? "الضريبة" : "Tax")).Append("</th><th class=\"num\">").Append(E(ar ? "الإجمالي" : "Total")).Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        var index = 0;
        foreach (var line in document.Lines)
        {
            index++;
            var unit = line.UomId is { } uom && unitNames.TryGetValue(uom, out var name) ? name : string.Empty;
            html.Append("<tr><td>").Append(index).Append("</td><td>").Append(E(line.Code)).Append("</td><td>").Append(E(line.Description))
                .Append("</td><td class=\"num\">").Append(Quantity(line.Quantity)).Append("</td><td>").Append(E(unit)).Append("</td>");
            if (showPrice)
            {
                html.Append("<td class=\"num\">").Append(Money(line.UnitPrice)).Append("</td><td class=\"num\">").Append(Money(line.DiscountPercent))
                    .Append("</td><td class=\"num\">").Append(Money(line.Net)).Append("</td>");
            }

            if (showTax)
            {
                html.Append("<td class=\"num\">").Append(Money(line.Tax)).Append("</td><td class=\"num\">").Append(Money(line.Total)).Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table>");

        if (document.Taxes.Count > 0)
        {
            html.Append("<table><thead><tr><th>").Append(E(ar ? "الضريبة" : "Tax")).Append("</th><th class=\"num\">").Append(E(ar ? "النسبة %" : "Rate %"))
                .Append("</th><th class=\"num\">").Append(E(ar ? "الوعاء" : "Base")).Append("</th><th class=\"num\">").Append(E(ar ? "المبلغ" : "Amount")).Append("</th></tr></thead><tbody>");
            foreach (var tax in document.Taxes)
            {
                html.Append("<tr><td>").Append(E(tax.Code)).Append("</td><td class=\"num\">").Append(Money(tax.Rate)).Append("</td><td class=\"num\">")
                    .Append(Money(tax.Base)).Append("</td><td class=\"num\">").Append(Money(tax.Amount)).Append("</td></tr>");
            }

            html.Append("</tbody></table>");
        }

        html.Append("<table class=\"totals\"><tbody>");
        foreach (var total in document.Totals)
        {
            html.Append(total.Emphasis ? "<tr class=\"emph\">" : "<tr>").Append("<td>").Append(E(ar ? total.LabelAr : total.LabelEn))
                .Append("</td><td class=\"num\">").Append(Money(total.Amount)).Append(' ').Append(E(document.Currency)).Append("</td></tr>");
        }

        html.Append("</tbody></table>");
        if (!string.IsNullOrWhiteSpace(document.Notes))
        {
            html.Append("<p>").Append(E(document.Notes)).Append("</p>");
        }

        return End(html, context, null);
    }

    public static string FormatValue(object? value, ReportColumnType type) => value switch
    {
        null => string.Empty,
        decimal d when type == ReportColumnType.Quantity => Quantity(d),
        decimal d when type == ReportColumnType.Percent => d.ToString("0.00", Invariant) + " %",
        decimal d => d.ToString("#,##0.00", Invariant),
        int i => i.ToString(Invariant),
        long l => l.ToString(Invariant),
        DateOnly date => date.ToString("yyyy-MM-dd", Invariant),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", Invariant),
        IFormattable f => f.ToString(null, Invariant),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsNumeric(ReportColumnType type) =>
        type is ReportColumnType.Number or ReportColumnType.Quantity or ReportColumnType.Money or ReportColumnType.Percent;

    private static void AppendRow(StringBuilder html, IReadOnlyList<ReportColumn> columns, object?[] row)
    {
        html.Append("<tr>");
        for (var i = 0; i < columns.Count; i++)
        {
            var value = i < row.Length ? row[i] : null;
            html.Append(IsNumeric(columns[i].Type) ? "<td class=\"num\">" : "<td>").Append(E(FormatValue(value, columns[i].Type))).Append("</td>");
        }

        html.Append("</tr>");
    }

    private static StringBuilder Begin(RenderContext context, string title)
    {
        var html = new StringBuilder(16 * 1024);
        html.Append("<!doctype html><html lang=\"").Append(context.IsArabic ? "ar" : "en").Append("\" dir=\"").Append(context.IsArabic ? "rtl" : "ltr")
            .Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(E(title)).Append("</title><style>").Append(Css).Append("</style></head><body>");
        return html;
    }

    private static string End(StringBuilder html, RenderContext context, int? rowCount)
    {
        html.Append("<footer>").Append(E(context.IsArabic ? "تم الإنشاء" : "Generated")).Append(' ')
            .Append(context.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", Invariant));
        if (rowCount is { } n)
        {
            html.Append(" · ").Append(n.ToString(Invariant)).Append(' ').Append(E(context.IsArabic ? "سطر" : "rows"));
        }

        html.Append("</footer></body></html>");
        return html.ToString();
    }

    private static string Letterhead(RenderContext context)
    {
        var sb = new StringBuilder("<div style=\"text-align:end\"><strong>");
        sb.Append(E(context.IsArabic ? context.CompanyNameAr : context.CompanyNameEn)).Append("</strong>");
        if (!string.IsNullOrWhiteSpace(context.LegalEntityName))
        {
            sb.Append("<div class=\"muted\">").Append(E(context.LegalEntityName)).Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(context.TaxRegistrationNo))
        {
            sb.Append("<div class=\"muted\">").Append(E(context.IsArabic ? "الرقم الضريبي" : "Tax registration")).Append(": ").Append(E(context.TaxRegistrationNo)).Append("</div>");
        }

        return sb.Append("</div>").ToString();
    }

    private static void Field(StringBuilder html, string label, string value) =>
        html.Append("<div><span class=\"muted\">").Append(E(label)).Append(":</span> ").Append(E(value)).Append("</div>");

    private static string Money(decimal? value) => value is null ? string.Empty : value.Value.ToString("#,##0.00", Invariant);

    private static string Quantity(decimal value) => value.ToString("#,##0.######", Invariant);

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
