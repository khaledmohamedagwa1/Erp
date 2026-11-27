using System.Reflection;
using Erp.SharedKernel.Domain;
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

    // Added for the limitation-closing waves (POS, fixed assets, commissions, standard costing).
    public const string CashRounding = nameof(CashRounding);
    public const string CardClearing = nameof(CardClearing);
    public const string AssetRevaluationReserve = nameof(AssetRevaluationReserve);
    public const string ImpairmentLoss = nameof(ImpairmentLoss);
    public const string CommissionExpense = nameof(CommissionExpense);
    public const string CommissionPayable = nameof(CommissionPayable);
    public const string MaterialUsageVariance = nameof(MaterialUsageVariance);
    public const string ConversionCostVariance = nameof(ConversionCostVariance);
    public const string AbsorptionVariance = nameof(AbsorptionVariance);

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
    public const string People = "people";
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
    public const string OpeningBalanceManage = "accounting.opening-balance.manage";
    public const string YearEndClose = "accounting.year-end.close";
}

/// <summary>Source document types of journals created by the Accounting module itself.</summary>
public static class AccountingDocumentTypes
{
    public const string ManualJournal = "ManualJournal";
    public const string OpeningBalance = "OpeningBalance";

    /// <summary>Year-end closing entries. Excluded from income statements and cash-flow statements.</summary>
    public const string YearEndClose = "YearEndClose";
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
/// <see cref="BaseAmount"/> optionally fixes the functional-currency amount of the line instead of converting at the
/// journal rate — used by sub-ledgers that relieve an open item at its original rate while cash moves at today's rate.
/// The journal must still balance in base currency (residuals up to 0.01 go to FxRoundingDifference).
/// <see cref="BaseOnly"/> lines carry no transaction-currency amount: Debit/Credit are then functional-currency amounts
/// (realized exchange differences on a foreign-currency settlement). <see cref="TaxableAmount"/> is the transaction-currency
/// taxable amount behind a tax line (positive), kept for the VAT return.
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
    string? Description = null,
    decimal? BaseAmount = null,
    bool BaseOnly = false,
    decimal? TaxableAmount = null,
    Guid? ProfitCenterId = null);

public sealed record AccountInfo(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    string Type,
    bool IsPostingAccount,
    bool IsControl,
    string ControlSubledger,
    bool AllowManualPosting,
    bool IsActive);

/// <summary>Read access to the chart of accounts for other modules (e.g. to validate a user-chosen expense account).</summary>
public interface IAccountQueries
{
    Task<AccountInfo?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A posting request from a source document. (LegalEntityId, SourceDocType, SourceDocId, Purpose) is unique:
/// posting the same request twice returns the existing journal, so callers may retry safely.
/// <see cref="BranchId"/> (optional) defaults the profit center of lines that do not name one, through the branch → profit center mapping.
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
    IReadOnlyList<JournalLineRequest> Lines,
    Guid? BranchId = null);

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

/// <summary>
/// Raised (through the Accounting outbox) whenever a journal is posted, including reversals. Other modules subscribe to react
/// to posted source documents without coupling to the source module (e.g. e-invoicing of posted sales invoices).
/// </summary>
public sealed record JournalPosted(
    Guid JournalId,
    Guid LegalEntityId,
    string Number,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string Purpose,
    decimal TotalBaseDebit,
    Guid CompanyId,
    DateOnly JournalDate) : IDomainEvent;

/// <summary>One posted journal line on an account, for sub-ledgers that reconcile against the general ledger.</summary>
public sealed record AccountLedgerLine(
    Guid JournalId,
    Guid LineId,
    string Number,
    DateOnly Date,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string Currency,
    decimal ExchangeRate,
    decimal Debit,
    decimal Credit,
    decimal BaseDebit,
    decimal BaseCredit,
    string Description);

/// <summary>A posted tax line (output or input tax) with its tax code, in functional currency. Signed: positive = tax payable increases.</summary>
public sealed record TaxLedgerLine(
    Guid LegalEntityId,
    DateOnly Date,
    string Number,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string Role,
    Guid? TaxCodeId,
    decimal TaxAmount,
    decimal? TaxableBase);

/// <summary>Movement in a period on the accounts the tax roles resolve to (for reconciling a VAT return to the ledger).</summary>
public sealed record TaxAccountMovement(Guid AccountId, string Code, string NameEn, string Role, decimal NetCredit);

/// <summary>Detailed ledger reads for sub-ledgers and tax reporting.</summary>
public interface IAccountLedgerQueries
{
    /// <summary>
    /// Posted lines on <paramref name="accountId"/> dated up to <paramref name="upTo"/>, excluding journals from
    /// <paramref name="excludeSourceModule"/> (that module already tracks its own lines).
    /// </summary>
    Task<IReadOnlyList<AccountLedgerLine>> GetAccountLinesAsync(Guid legalEntityId, Guid accountId, DateOnly upTo, string? excludeSourceModule,
        CancellationToken cancellationToken = default);

    /// <summary>The account a role resolves to without context, or null.</summary>
    Task<Guid?> ResolveRoleAccountAsync(Guid legalEntityId, string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lines posted to the OutputTax and InputTax roles in [from, to] for every legal entity of the active
    /// company (closing entries excluded). Output tax is credit-positive, input tax debit-positive.
    /// </summary>
    Task<IReadOnlyList<TaxLedgerLine>> GetTaxLinesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>Net credit movement in [from, to] on the accounts mapped to the OutputTax and InputTax roles (company-wide).</summary>
    Task<IReadOnlyList<TaxAccountMovement>> GetTaxAccountMovementsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
