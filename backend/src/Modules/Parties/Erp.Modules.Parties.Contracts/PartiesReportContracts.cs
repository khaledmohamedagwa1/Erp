namespace Erp.Modules.Parties.Contracts;

/// <summary>Display details of a customer or supplier for reports and printed documents.</summary>
public sealed record PartyDisplay(Guid Id, string Kind, string Code, string NameEn, string NameAr, string? TaxRegistrationNo);

/// <summary>Batch reads of customers and suppliers for reporting. Scoped to the active company; unknown ids are omitted.</summary>
public interface IPartyReportQueries
{
    Task<IReadOnlyList<PartyDisplay>> GetCustomersAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PartyDisplay>> GetSuppliersAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken = default);
}
