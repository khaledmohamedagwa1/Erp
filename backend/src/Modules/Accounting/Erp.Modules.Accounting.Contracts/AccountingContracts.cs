using System.Reflection;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Accounting.Contracts;

/// <summary>
/// Semantic posting roles. Operational modules post amounts against roles; Accounting resolves each role to a GL account
/// through the company's posting rules (ADR-011). No module outside Accounting ever references an account id directly.
/// </summary>
public static class PostingRoles
{
    public const string CustomerReceivable = nameof(CustomerReceivable);
    public const string CustomerAdvance = nameof(CustomerAdvance);
    public const string SupplierPayable = nameof(SupplierPayable);
    public const string SupplierAdvance = nameof(SupplierAdvance);
    public const string SalesRevenue = nameof(SalesRevenue);
    public const string SalesReturns = nameof(SalesReturns);
    public const string SalesDiscount = nameof(SalesDiscount);
    public const string ServiceRevenue = nameof(ServiceRevenue);
    public const string OutputTax = nameof(OutputTax);
    public const string InputTax = nameof(InputTax);
    public const string NonRecoverableInputTax = nameof(NonRecoverableInputTax);
    public const string WithholdingTaxPayable = nameof(WithholdingTaxPayable);
    public const string WithholdingTaxReceivable = nameof(WithholdingTaxReceivable);
    public const string Inventory = nameof(Inventory);
    public const string CostOfGoodsSold = nameof(CostOfGoodsSold);
    public const string InventoryAdjustmentLoss = nameof(InventoryAdjustmentLoss);
    public const string InventoryAdjustmentGain = nameof(InventoryAdjustmentGain);
    public const string InventoryInTransit = nameof(InventoryInTransit);
    public const string GoodsReceivedNotInvoiced = nameof(GoodsReceivedNotInvoiced);
    public const string GoodsInvoicedNotReceived = nameof(GoodsInvoicedNotReceived);
    public const string PurchasePriceVariance = nameof(PurchasePriceVariance);
    public const string LandedCostClearing = nameof(LandedCostClearing);
    public const string OpeningBalanceEquity = nameof(OpeningBalanceEquity);
    public const string CostTrueUp = nameof(CostTrueUp);
    public const string WorkInProgress = nameof(WorkInProgress);
    public const string AppliedLabor = nameof(AppliedLabor);
    public const string AppliedMachine = nameof(AppliedMachine);
    public const string AppliedOverhead = nameof(AppliedOverhead);
    public const string ProductionVariance = nameof(ProductionVariance);
    public const string AbnormalScrap = nameof(AbnormalScrap);
    public const string CashAccount = nameof(CashAccount);
    public const string BankAccount = nameof(BankAccount);
    public const string BankCharges = nameof(BankCharges);
    public const string CashOverShort = nameof(CashOverShort);
    public const string InternalTransferClearing = nameof(InternalTransferClearing);
    public const string RealizedFxGain = nameof(RealizedFxGain);
    public const string RealizedFxLoss = nameof(RealizedFxLoss);
    public const string UnrealizedFxGain = nameof(UnrealizedFxGain);
    public const string UnrealizedFxLoss = nameof(UnrealizedFxLoss);
    public const string FxRoundingDifference = nameof(FxRoundingDifference);
    public const string FixedAssetCost = nameof(FixedAssetCost);
    public const string AccumulatedDepreciation = nameof(AccumulatedDepreciation);
    public const string DepreciationExpense = nameof(DepreciationExpense);
    public const string AssetDisposalGainLoss = nameof(AssetDisposalGainLoss);
    public const string RetainedEarnings = nameof(RetainedEarnings);

    public static readonly IReadOnlyList<string> All = typeof(PostingRoles)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
}

/// <summary>Source module codes used on journals. Control accounts accept postings only from their sub-ledger modules.</summary>
public static class SourceModules
{
    public const string Accounting = "accounting";
    public const string Finance = "finance";
    public const string Sales = "sales";
    public const string Pos = "pos";
    public const string Procurement = "procurement";
    public const string Inventory = "inventory";
    public const string Manufacturing = "manufacturing";
    public const string FixedAssets = "fixed-assets";
}

public static class AccountingPermissions
{
    public const string AccountRead = "accounting.account.read";
    public const string AccountManage = "accounting.account.manage";
    public const string PeriodManage = "accounting.period.manage";
    public const string PostInSoftClosedPeriod = "accounting.period.post-soft-closed";
    public const string PostingRuleManage = "accounting.posting-rule.manage";
    public const string JournalRead = "accounting.journal.read";
    public const string JournalCreate = "accounting.journal.create";
    public const string JournalPost = "accounting.journal.post";
    public const string JournalReverse = "accounting.journal.reverse";
    public const string ReportRead = "accounting.report.read";
}

/// <summary>Determination context: optional keys that let a posting rule pick a more specific account.</summary>
public sealed record PostingContext(
    string? ProductPostingGroup = null,
    string? PartyPostingGroup = null,
    Guid? TaxCodeId = null,
    Guid? WarehouseId = null)
{
    public static readonly PostingContext Empty = new();

    public override string ToString() =>
        $"product={ProductPostingGroup ?? "-"}, party={PartyPostingGroup ?? "-"}, tax={TaxCodeId?.ToString() ?? "-"}, warehouse={WarehouseId?.ToString() ?? "-"}";
}

/// <summary>
/// One journal line. Give either <see cref="Role"/> (normal for module postings) or <see cref="AccountId"/>
/// (for an account chosen by the user or a sub-ledger, e.g. a specific bank account).
/// Amounts are in the journal's transaction currency; exactly one of Debit/Credit is positive.
/// </summary>
public sealed record JournalLineRequest(
    string? Role,
    Guid? AccountId,
    decimal Debit,
    decimal Credit,
    PostingContext? Context = null,
    Guid? CostCenterId = null,
    string? PartyType = null,
    Guid? PartyId = null,
    string? Description = null);

/// <summary>
/// A posting request from a source document. (LegalEntityId, SourceDocType, SourceDocId, Purpose) is unique:
/// posting the same request twice returns the existing journal, so callers may retry safely.
/// </summary>
public sealed record JournalRequest(
    Guid LegalEntityId,
    DateOnly JournalDate,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string Purpose,
    string Description,
    string Currency,
    decimal ExchangeRate,
    IReadOnlyList<JournalLineRequest> Lines);

public sealed record PostedJournal(Guid JournalId, string Number, decimal TotalBaseDebit, bool AlreadyPosted);

/// <summary>
/// The posting engine. Must be called inside the caller's database transaction (ADR-006): the journal commits or rolls back
/// together with the source document. Fails with <c>accounting.transaction-required</c> otherwise.
/// </summary>
public interface IJournalPostingService
{
    Task<Result<PostedJournal>> PostAsync(JournalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Posts the exact opposite of a posted journal, dated <paramref name="reversalDate"/> (must be in an open period). Idempotent.</summary>
    Task<Result<PostedJournal>> ReverseAsync(Guid legalEntityId, string sourceDocType, Guid sourceDocId, string purpose, DateOnly reversalDate, CancellationToken cancellationToken = default);
}

public interface IPeriodGuard
{
    /// <summary>Succeeds when the date is in an open period of the legal entity (or a soft-closed one and the user may post there).</summary>
    Task<Result> EnsureOpenAsync(Guid legalEntityId, DateOnly date, CancellationToken cancellationToken = default);
}

public sealed record TrialBalanceLine(Guid AccountId, string Code, string NameEn, string NameAr, string Type, decimal OpeningBalance, decimal Debit, decimal Credit, decimal ClosingBalance);

public sealed record TrialBalance(Guid LegalEntityId, DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceLine> Lines, decimal TotalDebit, decimal TotalCredit)
{
    public bool IsBalanced => TotalDebit == TotalCredit;
}

public interface ILedgerQueries
{
    /// <summary>Base-currency balance (debit positive) of an account up to and including <paramref name="asOf"/>.</summary>
    Task<decimal> GetAccountBalanceAsync(Guid legalEntityId, Guid accountId, DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary>Balance (debit positive) of the account a role resolves to without context.</summary>
    Task<decimal> GetRoleBalanceAsync(Guid legalEntityId, string role, DateOnly asOf, CancellationToken cancellationToken = default);

    Task<TrialBalance> GetTrialBalanceAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>Accounting readiness: the roles from <paramref name="roles"/> that do not resolve to an account for the legal entity.</summary>
    Task<IReadOnlyList<string>> GetUnresolvedRolesAsync(Guid legalEntityId, IEnumerable<string> roles, CancellationToken cancellationToken = default);
}
