using Erp.SharedKernel.Results;

namespace Erp.Modules.Finance.Contracts;

public static class FinanceOpeningPermissions
{
    /// <summary>Create opening receivables/payables (posts journals against opening balance equity).</summary>
    public const string OpeningBalanceManage = "finance.opening-balance.manage";
}

/// <summary>Source document types of opening receivable/payable items (one open item and one journal each).</summary>
public static class FinanceOpeningDocTypes
{
    public const string OpeningReceivable = "OpeningReceivable";
    public const string OpeningPayable = "OpeningPayable";
}

/// <summary>
/// An open invoice (positive <see cref="Amount"/>) or open credit (negative) carried over from a previous system.
/// Amount is in <see cref="Currency"/> (blank = functional currency); <see cref="ExchangeRate"/> blank = recorded spot rate
/// on the document date. The due date drives aging.
/// </summary>
public sealed record OpeningItemImportRequest(
    PartyKind PartyType,
    Guid PartyId,
    Guid LegalEntityId,
    string DocumentNumber,
    DateOnly DocumentDate,
    DateOnly DueDate,
    string? Currency,
    decimal? ExchangeRate,
    decimal Amount,
    string? Description);

/// <summary>Finance opening-balance documents for the Import module. Must run inside the caller's transaction.</summary>
public interface IFinanceImportService
{
    /// <summary>
    /// Opens one receivable/payable item and posts its journal (receivable/payable control account against OpeningBalanceEquity,
    /// in functional currency). Refused when the same party already has an opening item with this document number. Returns the open item id.
    /// </summary>
    Task<Result<Guid>> CreateOpeningItemAsync(OpeningItemImportRequest request, CancellationToken cancellationToken = default);
}
