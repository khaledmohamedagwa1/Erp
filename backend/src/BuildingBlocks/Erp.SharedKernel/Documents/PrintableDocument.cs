namespace Erp.SharedKernel.Documents;

/// <summary>
/// A neutral, render-ready view of a posted business document (invoice, credit note, order, receipt, voucher).
/// Each owning module builds it from its own data and publishes it through its Contracts; the platform document renderer
/// turns it into printable output. Amounts are in the document currency unless the label says otherwise.
/// </summary>
public sealed record PrintableDocument(
    string DocumentType,
    string TitleEn,
    string TitleAr,
    string Number,
    DateOnly Date,
    string Currency,
    string Status,
    Guid CompanyId,
    Guid LegalEntityId,
    PrintParty? Party,
    IReadOnlyList<PrintField> Fields,
    IReadOnlyList<PrintLine> Lines,
    IReadOnlyList<PrintTax> Taxes,
    IReadOnlyList<PrintTotal> Totals,
    string? Notes = null);

/// <summary>The counterparty; <see cref="Kind"/> is "Customer" or "Supplier". Names are resolved by the renderer's caller.</summary>
public sealed record PrintParty(string Kind, Guid Id);

public sealed record PrintField(string LabelEn, string LabelAr, string Value);

/// <summary>One printed line. <see cref="ProductId"/>/<see cref="UomId"/> let the caller resolve codes and unit names.</summary>
public sealed record PrintLine(
    string Code,
    string Description,
    decimal Quantity,
    Guid? ProductId = null,
    Guid? UomId = null,
    decimal? UnitPrice = null,
    decimal? DiscountPercent = null,
    decimal? Net = null,
    decimal? Tax = null,
    decimal? Total = null);

public sealed record PrintTax(string Code, decimal Rate, decimal Base, decimal Amount);

public sealed record PrintTotal(string LabelEn, string LabelAr, decimal Amount, bool Emphasis = false);
