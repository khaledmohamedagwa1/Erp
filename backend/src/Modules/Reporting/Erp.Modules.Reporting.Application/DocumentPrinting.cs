using Erp.Application.Abstractions;
using Erp.Application.Reporting;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Documents;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Reporting.Application;

public sealed record PrintedDocument(string Html, string FileName);

/// <summary>
/// Printable HTML of posted documents. Each document type is read through its owning module's contract and requires
/// <see cref="ReportingPermissions.DocumentPrint"/> plus that module being enabled for the company.
/// </summary>
public sealed class DocumentPrinting(
    IServiceProvider services,
    ITenantContext tenant,
    ICurrentUser user,
    IPermissionService permissions,
    IModuleActivationService modules,
    ReportLookups lookups,
    ReportService reports,
    IDocumentRenderer renderer)
{
    private sealed record DocumentSource(string Module, Func<IServiceProvider, Guid, CancellationToken, Task<PrintableDocument?>?> Load);

    private static readonly Dictionary<string, DocumentSource> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sales-invoice"] = new("sales", (sp, id, ct) => sp.GetService<ISalesReportQueries>()?.GetInvoicePrintAsync(id, ct)),
        ["credit-note"] = new("sales", (sp, id, ct) => sp.GetService<ISalesReportQueries>()?.GetReturnPrintAsync(id, ct)),
        ["purchase-order"] = new("procurement", (sp, id, ct) => sp.GetService<IProcurementReportQueries>()?.GetPurchaseOrderPrintAsync(id, ct)),
        ["goods-receipt"] = new("procurement", (sp, id, ct) => sp.GetService<IProcurementReportQueries>()?.GetGoodsReceiptPrintAsync(id, ct)),
        ["voucher"] = new("finance", (sp, id, ct) => sp.GetService<IFinanceDocumentQueries>()?.GetSettlementPrintAsync(id, ct)),
    };

    public static IReadOnlyCollection<string> DocumentTypes => Sources.Keys;

    public async Task<Result<PrintedDocument>> PrintAsync(string documentType, Guid id, bool arabic, CancellationToken ct)
    {
        if (!Sources.TryGetValue(documentType, out var source))
        {
            return ReportingErrors.UnknownDocumentType;
        }

        if (tenant.CompanyId is not { } companyId)
        {
            return ReportingErrors.CompanyRequired;
        }

        if (user.UserId is not { } userId || !await permissions.HasPermissionAsync(userId, ReportingPermissions.DocumentPrint, ct))
        {
            return ReportingErrors.Forbidden(ReportingPermissions.DocumentPrint);
        }

        if (!await modules.IsEnabledAsync(companyId, source.Module, ct))
        {
            return ReportingErrors.ModuleDisabled;
        }

        var load = source.Load(services, id, ct);
        var document = load is null ? null : await load;
        if (document is null)
        {
            return ReportingErrors.DocumentNotFound;
        }

        PrintPartyDetails? party = null;
        if (document.Party is { } p)
        {
            var found = p.Kind == "Supplier" ? await lookups.SuppliersAsync([p.Id], ct) : await lookups.CustomersAsync([p.Id], ct);
            if (found.TryGetValue(p.Id, out var display))
            {
                party = new PrintPartyDetails(display.Code, arabic && !string.IsNullOrWhiteSpace(display.NameAr) ? display.NameAr : display.NameEn, display.TaxRegistrationNo);
            }
        }

        var units = await lookups.UnitCodesAsync(document.Lines.Where(l => l.UomId != null).Select(l => l.UomId!.Value).Distinct().ToList(), ct);
        var context = await reports.RenderContextAsync(arabic, document.LegalEntityId, ct);
        var html = renderer.RenderDocument(document, party, units, context);
        var name = string.IsNullOrWhiteSpace(document.Number) ? id.ToString() : document.Number;
        return new PrintedDocument(html, $"{document.DocumentType}-{SafeFileName(name)}.html");
    }

    public static string SafeFileName(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
