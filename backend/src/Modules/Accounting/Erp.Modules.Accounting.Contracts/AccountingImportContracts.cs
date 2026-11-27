using Erp.SharedKernel.Results;

namespace Erp.Modules.Accounting.Contracts;

/// <summary>
/// An account row from a data import. The parent is a business code (usually a group account of the seeded template);
/// a blank <see cref="Type"/> takes the parent's type. Imported accounts are never control accounts.
/// </summary>
public sealed record AccountImportRequest(
    string Code,
    string NameEn,
    string NameAr,
    string? Type,
    string ParentCode,
    bool IsPostingAccount,
    bool AllowManualPosting,
    string? CashFlowCategory);

public sealed record OpeningBalanceImportLine(string AccountCode, decimal Debit, decimal Credit, string? Description);

/// <summary>GL opening balances of a legal entity. Must balance; control accounts are refused (use the sub-ledger opening imports).</summary>
public sealed record OpeningBalanceImportRequest(Guid LegalEntityId, DateOnly OpeningDate, string? Description, IReadOnlyList<OpeningBalanceImportLine> Lines);

/// <summary>Command-style accounting operations for the Import module (same rules as manual entry, caller's transaction).</summary>
public interface IAccountingImportService
{
    Task<Result<Guid>> CreateAccountAsync(AccountImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates and posts the opening balance document (one journal). Returns the journal id.</summary>
    Task<Result<Guid>> PostOpeningBalancesAsync(OpeningBalanceImportRequest request, CancellationToken cancellationToken = default);
}
