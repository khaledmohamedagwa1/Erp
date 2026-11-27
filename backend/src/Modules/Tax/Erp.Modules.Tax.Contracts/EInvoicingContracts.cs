namespace Erp.Modules.Tax.Contracts;

/// <summary>Seller or buyer on an electronic invoice.</summary>
public sealed record EInvoiceParty(string Name, string? TaxRegistrationNo, string? Code = null);

public sealed record EInvoiceLine(int LineNo, string Code, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent,
    decimal NetAmount, decimal TaxAmount, decimal GrossAmount);

public sealed record EInvoiceTaxBreakdown(string TaxCode, decimal Rate, decimal TaxableAmount, decimal TaxAmount);

public static class EInvoiceDocumentTypes
{
    public const string Invoice = "Invoice";
    public const string CreditNote = "CreditNote";
}

/// <summary>
/// Provider-neutral electronic invoice built from a posted sales invoice or credit note. Amounts are in the document currency.
/// <see cref="Counter"/> and <see cref="PreviousHash"/> chain the company's documents (ZATCA ICV/PIH style); adapters for authorities
/// without chaining ignore them. <see cref="Hash"/> is the SHA-256 (Base64) of the canonical document without the hash itself.
/// </summary>
public sealed record EInvoiceDocument(
    Guid Uuid,
    string DocumentType,
    string Number,
    DateOnly IssueDate,
    string Currency,
    EInvoiceParty Seller,
    EInvoiceParty Buyer,
    IReadOnlyList<EInvoiceLine> Lines,
    IReadOnlyList<EInvoiceTaxBreakdown> Taxes,
    decimal NetTotal,
    decimal TaxTotal,
    decimal GrossTotal,
    long Counter,
    string? PreviousHash,
    string Environment,
    string? Hash = null);

public enum EInvoiceOutcome
{
    /// <summary>Cleared/reported by the authority.</summary>
    Accepted = 1,

    /// <summary>Rejected for content reasons; the document must be corrected (or credited) and resubmitted by a user.</summary>
    Rejected = 2,

    /// <summary>Received; the verdict arrives later (asynchronous authorities).</summary>
    Submitted = 3,
}

public sealed record EInvoiceSubmissionResult(EInvoiceOutcome Outcome, string? ProviderReference, string? QrCode, IReadOnlyList<string> Errors);

/// <summary>
/// Port to a tax authority's e-invoicing platform (e.g. Egypt ETA, KSA ZATCA). Adapters translate <see cref="EInvoiceDocument"/> to the
/// authority's format, sign it and submit it. Transient failures (network, authority unavailable) must be thrown as exceptions so the
/// outbox retries; content errors are returned as <see cref="EInvoiceOutcome.Rejected"/>.
/// Only a mock adapter ships with the product: real adapters need the company's credentials, signing certificates or an HSM, and the
/// authority's onboarding — they are deliberately not included.
/// </summary>
public interface IEInvoicingProvider
{
    /// <summary>Name used in the company's e-invoicing settings, lower case (e.g. "mock").</summary>
    string Name { get; }

    Task<EInvoiceSubmissionResult> SubmitAsync(EInvoiceDocument document, CancellationToken cancellationToken = default);
}

public sealed record TaxCodeInfo(Guid Id, string Code, string NameEn, string NameAr, TaxKindCode Kind, decimal? CurrentRate, string? ReportingBox);

public sealed record TaxGroupInfo(Guid Id, string Code, string NameEn, string NameAr);

/// <summary>Tax master data lookups for reports (active company).</summary>
public interface ITaxCodeQueries
{
    Task<IReadOnlyList<TaxCodeInfo>> GetTaxCodesAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxGroupInfo>> GetTaxGroupsAsync(CancellationToken cancellationToken = default);
}

public static class EInvoicingPermissions
{
    public const string Read = "tax.einvoice.read";
    public const string Manage = "tax.einvoice.manage";
}
