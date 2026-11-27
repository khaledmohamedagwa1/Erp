using System.Globalization;
using Erp.Application.Reporting;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>
/// Tax compliance reports. The VAT return is built from the general ledger (tax lines carry their tax code), so its totals reconcile to the
/// tax accounts by construction; the reconciliation figures are shown as report parameters. The withholding report reads Finance
/// receipts and payments with tax withheld.
/// </summary>
public sealed class TaxReports(IServiceProvider services, ReportLookups lookups) : IReportArea
{
    private const string VatSource = "Posted OutputTax/InputTax journal lines with tax code (IAccountLedgerQueries.GetTaxLinesAsync), tax codes (ITaxCodeQueries)";
    private const string WithholdingSource = "Posted receipts and payments with tax withheld (IWithholdingReportQueries)";

    public string Area => ReportAreas.Tax;

    public bool IsAvailable => services.GetService<IAccountLedgerQueries>() is not null && services.GetService<ITaxCodeQueries>() is not null;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        new(ReportAreas.Tax, "vat-return", "VAT return", "إقرار ضريبة القيمة المضافة",
            "Output and input tax by reporting box and tax code for the period, reconciled to the movement on the tax accounts.", VatSource,
            ReportingPermissions.TaxRead, "accounting", ["from", "to"]),
        new(ReportAreas.Tax, "withholding", "Withholding tax", "ضريبة الخصم من المنبع",
            "Tax withheld from suppliers (payments) and by customers (receipts), with certificate numbers.", WithholdingSource,
            ReportingPermissions.TaxRead, "finance", ["from", "to", "customerId", "supplierId"]),
    ];

    public async Task<Result<ReportOutput>> RunAsync(string key, ReportQuery q, CancellationToken ct)
    {
        ReportTable? table = key switch
        {
            "vat-return" => await VatReturnAsync(q, ct),
            "withholding" => await WithholdingAsync(q, ct),
            _ => null,
        };
        return table is null ? ReportingErrors.UnknownReport : new ReportOutput(table);
    }

    private async Task<ReportTable> VatReturnAsync(ReportQuery q, CancellationToken ct)
    {
        var ledger = services.GetRequiredService<IAccountLedgerQueries>();
        var codes = (await services.GetRequiredService<ITaxCodeQueries>().GetTaxCodesAsync(q.To, ct)).ToDictionary(c => c.Id);
        var lines = await ledger.GetTaxLinesAsync(q.From, q.To, ct);
        var movements = await ledger.GetTaxAccountMovementsAsync(q.From, q.To, ct);

        ReportColumn[] columns =
        [
            Text("box", "Box", "البند"), Text("direction", "Direction", "النوع"), Text("taxCode", "Tax code", "رمز الضريبة"), Text("taxName", "Tax", "الضريبة"),
            Percent("rate", "Rate", "النسبة"), Money("taxable", "Taxable amount", "المبلغ الخاضع"), Money("tax", "Tax", "الضريبة"),
            Text("taxableBasis", "Taxable basis", "أساس المبلغ الخاضع"), Number("documents", "Documents", "المستندات"),
        ];
        var rows = lines.GroupBy(l => (l.Role, l.TaxCodeId)).Select(g =>
        {
            codes.TryGetValue(g.Key.TaxCodeId ?? Guid.Empty, out var code);
            var output = g.Key.Role == PostingRoles.OutputTax;
            var tax = g.Sum(l => l.TaxAmount);
            var posted = g.Where(l => l.TaxableBase is not null).ToList();
            var derived = g.Where(l => l.TaxableBase is null).ToList();
            // Lines posted without a taxable amount: derive it from the tax and the code's rate (marked "Derived").
            var derivedTaxable = code?.CurrentRate is > 0 ? Math.Round(derived.Sum(l => l.TaxAmount) * 100m / code.CurrentRate.Value, 2, MidpointRounding.AwayFromZero) : 0m;
            var basis = derived.Count == 0 ? "Posted" : posted.Count == 0 ? "Derived" : "Mixed";
            return new object?[]
            {
                code?.ReportingBox ?? string.Empty, output ? "Output" : "Input", code?.Code ?? "(no tax code)",
                code is null ? string.Empty : q.Arabic && !string.IsNullOrWhiteSpace(code.NameAr) ? code.NameAr : code.NameEn,
                code?.CurrentRate is { } rate ? rate / 100m : null, posted.Sum(l => l.TaxableBase!.Value) + derivedTaxable, tax, basis,
                (decimal)g.Select(l => l.SourceDocId).Distinct().Count(),
            };
        }).OrderBy(r => (string)r[1]! == "Output" ? 0 : 1).ThenBy(r => (string)r[0]!, StringComparer.Ordinal).ThenBy(r => (string)r[2]!, StringComparer.Ordinal).ToList();

        var outputTax = rows.Where(r => (string)r[1]! == "Output").Sum(r => (decimal)r[6]!);
        var inputTax = rows.Where(r => (string)r[1]! == "Input").Sum(r => (decimal)r[6]!);
        var glNet = movements.Sum(m => m.NetCredit);
        var parameters = Period(q).Concat(
        [
            new ReportParameter("Output tax", "ضريبة المخرجات", Format(outputTax)),
            new ReportParameter("Input tax", "ضريبة المدخلات", Format(inputTax)),
            new ReportParameter("Net tax payable", "صافي الضريبة المستحقة", Format(outputTax - inputTax)),
            new ReportParameter("Net movement on tax accounts (GL)", "صافي حركة حسابات الضريبة", Format(glNet)),
            new ReportParameter("Unreconciled difference (e.g. manual journals)", "فرق غير مسوى (مثل القيود اليدوية)", Format(glNet - (outputTax - inputTax))),
        ]).ToList();
        var totals = new object?[columns.Length];
        totals[6] = outputTax - inputTax;
        return new ReportTable("tax/vat-return", "VAT return", "إقرار ضريبة القيمة المضافة", columns, rows, totals, parameters);
    }

    private async Task<ReportTable> WithholdingAsync(ReportQuery q, CancellationToken ct)
    {
        ReportColumn[] columns =
        [
            Date("date", "Date", "التاريخ"), Text("number", "Document", "المستند"), Text("direction", "Direction", "النوع"),
            Text("partyCode", "Party code", "رمز الطرف"), Text("party", "Party", "الطرف"), Text("taxGroup", "Tax", "الضريبة"), Text("currency", "Currency", "العملة"),
            Money("taxableAmount", "Taxable (document currency)", "الخاضع (عملة المستند)"), Money("withheldAmount", "Withheld (document currency)", "المخصوم (عملة المستند)"),
            Money("taxableBase", "Taxable", "الخاضع"), Money("withheldBase", "Withheld", "المخصوم"), Text("certificate", "Certificate", "الشهادة"),
        ];
        var queries = services.GetService<IWithholdingReportQueries>();
        if (queries is null)
        {
            return new ReportTable("tax/withholding", "Withholding tax", "ضريبة الخصم من المنبع", columns, [], null, Period(q));
        }

        var (partyType, partyId) = q.CustomerId is { } c ? (PartyKind.Customer, (Guid?)c) : q.SupplierId is { } s ? (PartyKind.Supplier, s) : ((PartyKind?)null, (Guid?)null);
        var facts = await queries.GetWithholdingFactsAsync(q.From, q.To, partyType, partyId, ct);
        var customers = await lookups.CustomersAsync(facts.Where(f => f.PartyType == PartyKind.Customer).Select(f => f.PartyId), ct);
        var suppliers = await lookups.SuppliersAsync(facts.Where(f => f.PartyType == PartyKind.Supplier).Select(f => f.PartyId), ct);
        var groups = (await services.GetRequiredService<ITaxCodeQueries>().GetTaxGroupsAsync(ct)).ToDictionary(g => g.Id);
        var rows = facts.Select(f =>
        {
            var parties = f.PartyType == PartyKind.Customer ? customers : suppliers;
            var group = f.TaxGroupId is { } id && groups.TryGetValue(id, out var g) ? (q.Arabic && !string.IsNullOrWhiteSpace(g.NameAr) ? g.NameAr : g.NameEn) : string.Empty;
            return new object?[]
            {
                f.Date, f.Number, f.Direction == "Receipt" ? "Withheld by customer" : "Withheld from supplier", parties.Code(f.PartyId), parties.Name(f.PartyId, q.Arabic),
                group, f.Currency, f.TaxableAmount, f.WithheldAmount, f.TaxableBase, f.WithheldBase, f.CertificateNumber ?? string.Empty,
            };
        }).ToList();
        return new ReportTable("tax/withholding", "Withholding tax", "ضريبة الخصم من المنبع", columns, rows, Totals(columns.Length, rows, 9, 10), Period(q));
    }

    private static string Format(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
