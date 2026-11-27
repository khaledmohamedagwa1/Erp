using Erp.SharedKernel.Results;

namespace Erp.Modules.Parties.Contracts;

/// <summary>A customer row from a data import; group and payment terms are business codes.</summary>
public sealed record CustomerImportRequest(
    string Code,
    string Kind,
    string NameEn,
    string NameAr,
    string? TaxRegistrationNo,
    string Currency,
    string? CustomerGroupCode,
    string? PaymentTermsCode,
    string? PostingGroup,
    decimal? CreditLimit,
    string? CreditAction);

/// <summary>A supplier row from a data import; payment terms are a business code.</summary>
public sealed record SupplierImportRequest(
    string Code,
    string NameEn,
    string NameAr,
    string? TaxRegistrationNo,
    string Currency,
    string? PaymentTermsCode,
    string? PostingGroup);

/// <summary>Command-style party operations for the Import module (same validation as manual entry, caller's transaction).</summary>
public interface IPartyImportService
{
    Task<Result<Guid>> CreateCustomerAsync(CustomerImportRequest request, CancellationToken cancellationToken = default);

    Task<Result<Guid>> CreateSupplierAsync(SupplierImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Customer ids of the active company by code (keys upper-case). Unknown codes are omitted.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindCustomerIdsByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default);

    /// <summary>Supplier ids of the active company by code (keys upper-case). Unknown codes are omitted.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindSupplierIdsByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default);
}
