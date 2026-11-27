using System.Text.RegularExpressions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Accounting.Domain;

public static partial class AccountingErrors
{
    public static readonly Error AccountNotFound = Error.NotFound("accounting.account-not-found", "The account was not found.");
    public static readonly Error AccountCodeTaken = Error.Conflict("accounting.account-code-taken", "An account with this code already exists.");
    public static readonly Error ParentNotGroup = Error.Validation("accounting.parent-not-group", "The parent must be a group (non-posting) account of the same type.");
    public static readonly Error NotPostingAccount = Error.Validation("accounting.not-posting-account", "Journals can only post to posting (leaf) accounts.");
    public static readonly Error AccountInactive = Error.Validation("accounting.account-inactive", "The account is inactive.");
    public static readonly Error ManualPostingNotAllowed = Error.Validation("accounting.manual-posting-not-allowed", "Manual journals cannot post to this account.");
    public static readonly Error ControlAccountRestricted = Error.Validation("accounting.control-account-restricted", "This control account only accepts postings from its sub-ledger.");
    public static readonly Error LineOneSided = Error.Validation("accounting.line-one-sided", "Each line needs a positive debit or a positive credit, not both.");
    public static readonly Error TooFewLines = Error.Validation("accounting.too-few-lines", "A journal needs at least two lines.");
    public static readonly Error Unbalanced = Error.Validation("accounting.journal-unbalanced", "Total debit must equal total credit.");
    public static readonly Error NotDraft = Error.Conflict("accounting.journal-not-draft", "Only draft journals can be changed or posted.");
    public static readonly Error NotPosted = Error.Conflict("accounting.journal-not-posted", "Only posted journals can be reversed.");
    public static readonly Error AlreadyReversed = Error.Conflict("accounting.journal-already-reversed", "The journal has already been reversed.");
    public static readonly Error JournalNotFound = Error.NotFound("accounting.journal-not-found", "The journal was not found.");
    public static readonly Error PeriodClosed = Error.Conflict("accounting.period-closed", "The accounting period is closed.");
    public static readonly Error PeriodNotFound = Error.Conflict("accounting.period-not-found", "No fiscal period covers this date. Create the fiscal year first.");
    public static readonly Error FiscalYearOverlaps = Error.Conflict("accounting.fiscal-year-overlaps", "The fiscal year overlaps an existing one.");
    public static readonly Error PeriodOrder = Error.Conflict("accounting.period-order", "Close periods in order (earlier periods first) and reopen them in reverse order.");
    public static readonly Error InvalidRate = Error.Validation("accounting.invalid-exchange-rate", "The exchange rate must be positive (1 for the functional currency).");
    public static readonly Error FiscalYearNotFound = Error.NotFound("accounting.fiscal-year-not-found", "The fiscal year was not found.");
    public static readonly Error FiscalYearClosed = Error.Conflict("accounting.fiscal-year-closed", "The fiscal year is closed. Reopen it (year-end reopen) before changing its periods.");
    public static readonly Error FiscalYearNotClosed = Error.Conflict("accounting.fiscal-year-not-closed", "The fiscal year is not closed.");
    public static readonly Error YearEndPeriodsNotReady = Error.Conflict("accounting.year-end-periods-not-ready",
        "Close every period of the year except the last, and soft-close the last period, before the year-end close.");
    public static readonly Error LaterYearClosed = Error.Conflict("accounting.later-year-closed", "A later fiscal year is already closed; reopen it first.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]{0,19}$")]
    private static partial Regex CodePattern();

    public static string Code(string? value)
    {
        var code = Guard.NotEmpty(value, "accounting.code-required", "Account code");
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainException(Error.Validation("accounting.code-invalid", "Account code must be 1–20 letters, digits, '.' or '-'."));
        }

        return code.ToUpperInvariant();
    }
}

public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Revenue = 4,
    Expense = 5,
}

public enum NormalBalance
{
    Debit = 1,
    Credit = 2,
}

/// <summary>Which sub-ledger feeds a control account. <see cref="None"/> = ordinary account.</summary>
public enum ControlSubledger
{
    None = 0,
    Receivables = 1,
    Payables = 2,
    Inventory = 3,
    Grni = 4,
    Gini = 5,
    Wip = 6,
}

/// <summary>Where an account's movements appear in the indirect cash-flow statement.</summary>
public enum CashFlowCategory
{
    /// <summary>Unclassified. Balance-sheet accounts without a category are shown under operating activities so the statement still ties.</summary>
    None = 0,
    Operating = 1,
    Investing = 2,
    Financing = 3,

    /// <summary>Cash and cash equivalents: the accounts whose change the statement explains.</summary>
    Cash = 4,
}

public enum AccountStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Which source modules may post to each kind of control account. Manual journals ("accounting") never may.</summary>
public static class ControlAccountRules
{
    private static readonly Dictionary<ControlSubledger, string[]> Allowed = new()
    {
        [ControlSubledger.Receivables] = ["finance", "sales", "pos"],
        [ControlSubledger.Payables] = ["finance", "procurement"],
        [ControlSubledger.Inventory] = ["inventory", "procurement", "sales", "pos", "manufacturing"],
        [ControlSubledger.Grni] = ["procurement", "inventory"],
        [ControlSubledger.Gini] = ["procurement", "inventory"],
        [ControlSubledger.Wip] = ["manufacturing"],
    };

    public static bool Allows(ControlSubledger subledger, string sourceModule) =>
        subledger == ControlSubledger.None || (Allowed.TryGetValue(subledger, out var modules) && modules.Contains(sourceModule));
}

[Audited]
public sealed class Account : AggregateRoot, ICompanyScoped
{
    private Account()
    {
    }

    private Account(Guid id, Guid companyId, string code, string nameEn, string nameAr, AccountType type, Guid? parentId,
        bool isPostingAccount, ControlSubledger controlSubledger, bool allowManualPosting)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
        Type = type;
        ParentId = parentId;
        IsPostingAccount = isPostingAccount;
        ControlSubledger = controlSubledger;
        AllowManualPosting = allowManualPosting && controlSubledger == ControlSubledger.None && isPostingAccount;
        Status = AccountStatus.Active;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public AccountType Type { get; private set; }

    public Guid? ParentId { get; private set; }

    /// <summary>Leaf account that accepts journal lines. Group accounts only total their children.</summary>
    public bool IsPostingAccount { get; private set; }

    public ControlSubledger ControlSubledger { get; private set; }

    public bool IsControl => ControlSubledger != ControlSubledger.None;

    public bool AllowManualPosting { get; private set; }

    public AccountStatus Status { get; private set; }

    public CashFlowCategory CashFlowCategory { get; private set; }

    /// <summary>Balance-sheet accounts carry forward across fiscal years; revenue and expense accounts are closed to retained earnings.</summary>
    public bool IsBalanceSheet => Type is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    public NormalBalance NormalBalance => Type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit;

    public static Account Create(Guid companyId, string code, string nameEn, string nameAr, AccountType type, Account? parent,
        bool isPostingAccount, ControlSubledger controlSubledger = ControlSubledger.None, bool allowManualPosting = true)
    {
        if (parent is not null && (parent.IsPostingAccount || parent.Type != type || parent.CompanyId != companyId))
        {
            throw new DomainException(AccountingErrors.ParentNotGroup);
        }

        if (!isPostingAccount && controlSubledger != ControlSubledger.None)
        {
            throw new DomainException(Error.Validation("accounting.control-must-be-posting", "Only posting accounts can be control accounts."));
        }

        return new Account(SequentialGuid.New(), companyId, AccountingErrors.Code(code),
            Guard.MaxLength(Guard.NotEmpty(nameEn, "accounting.name-required", "English name"), 200, "accounting.name-too-long", "English name"),
            Guard.MaxLength(Guard.NotEmpty(nameAr, "accounting.name-required", "Arabic name"), 200, "accounting.name-too-long", "Arabic name"),
            type, parent?.Id, isPostingAccount, controlSubledger, allowManualPosting);
    }

    public void Update(string nameEn, string nameAr, bool allowManualPosting)
    {
        NameEn = Guard.MaxLength(Guard.NotEmpty(nameEn, "accounting.name-required", "English name"), 200, "accounting.name-too-long", "English name");
        NameAr = Guard.MaxLength(Guard.NotEmpty(nameAr, "accounting.name-required", "Arabic name"), 200, "accounting.name-too-long", "Arabic name");
        AllowManualPosting = allowManualPosting && !IsControl && IsPostingAccount;
    }

    public void SetCashFlowCategory(CashFlowCategory category)
    {
        if (!IsBalanceSheet && category != CashFlowCategory.None)
        {
            throw new DomainException(Error.Validation("accounting.cash-flow-category-balance-sheet-only",
                "Only asset, liability and equity accounts have a cash-flow category (revenue and expenses enter through net profit)."));
        }

        CashFlowCategory = category;
    }

    public void Deactivate() => Status = AccountStatus.Inactive;

    public void Activate() => Status = AccountStatus.Active;

    /// <summary>Checks the account can receive a line from the given source module.</summary>
    public Result CanPost(string sourceModule, bool manual)
    {
        if (!IsPostingAccount)
        {
            return AccountingErrors.NotPostingAccount;
        }

        if (Status != AccountStatus.Active)
        {
            return AccountingErrors.AccountInactive;
        }

        if (IsControl && (manual || !ControlAccountRules.Allows(ControlSubledger, sourceModule)))
        {
            return AccountingErrors.ControlAccountRestricted;
        }

        if (manual && !AllowManualPosting)
        {
            return AccountingErrors.ManualPostingNotAllowed;
        }

        return Result.Success();
    }
}

[Audited]
public sealed class CostCenter : AggregateRoot, ICompanyScoped
{
    private CostCenter()
    {
    }

    private CostCenter(Guid id, Guid companyId, string code, string name, Guid? parentId)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
        ParentId = parentId;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public Guid? ParentId { get; private set; }

    public bool IsActive { get; private set; }

    public static CostCenter Create(Guid companyId, string code, string name, Guid? parentId) =>
        new(SequentialGuid.New(), companyId, AccountingErrors.Code(code),
            Guard.MaxLength(Guard.NotEmpty(name, "accounting.name-required", "Name"), 200, "accounting.name-too-long", "Name"), parentId);
}

public enum PeriodStatus
{
    Open = 1,
    SoftClosed = 2,
    Closed = 3,
}

/// <summary>Fiscal year of a legal entity, split into contiguous monthly periods.</summary>
[Audited]
public sealed class FiscalYear : AggregateRoot, ICompanyScoped
{
    private readonly List<FiscalPeriod> _periods = [];

    private FiscalYear()
    {
    }

    private FiscalYear(Guid id, Guid companyId, Guid legalEntityId, DateOnly startDate, DateOnly endDate)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        StartDate = startDate;
        EndDate = endDate;
        Code = endDate.Year == startDate.Year ? $"FY{startDate.Year}" : $"FY{startDate.Year}-{endDate.Year % 100:00}";
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    /// <summary>Calendar year of the end date; used to scope gapless journal numbering.</summary>
    public int NumberingYear => EndDate.Year;

    /// <summary>True once the year-end close has run. All periods are then closed.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>The closing journal (null when the year had no revenue or expense balances to close).</summary>
    public Guid? ClosingJournalId { get; private set; }

    public DateTime? ClosedAtUtc { get; private set; }

    public IReadOnlyList<FiscalPeriod> Periods => _periods.OrderBy(p => p.PeriodNo).ToList();

    public static FiscalYear Create(Guid companyId, Guid legalEntityId, DateOnly startDate, int months = 12)
    {
        if (startDate.Day != 1)
        {
            throw new DomainException(Error.Validation("accounting.fiscal-year-start", "A fiscal year must start on the first day of a month."));
        }

        if (months is < 1 or > 24)
        {
            throw new DomainException(Error.Validation("accounting.fiscal-year-length", "A fiscal year has 1–24 monthly periods."));
        }

        var year = new FiscalYear(SequentialGuid.New(), companyId, legalEntityId, startDate, startDate.AddMonths(months).AddDays(-1));
        for (var i = 0; i < months; i++)
        {
            var start = startDate.AddMonths(i);
            year._periods.Add(new FiscalPeriod(SequentialGuid.New(), year.Id, i + 1, start, start.AddMonths(1).AddDays(-1)));
        }

        return year;
    }

    public bool Overlaps(DateOnly start, DateOnly end) => start <= EndDate && end >= StartDate;

    public FiscalPeriod? FindPeriod(DateOnly date) => _periods.FirstOrDefault(p => p.Contains(date));

    /// <summary>
    /// Changes a period's status. Closing requires every earlier period to be closed; reopening (or soft-closing a
    /// closed period) requires every later period to be open or soft-closed, so the books close in order.
    /// </summary>
    public Result SetPeriodStatus(Guid periodId, PeriodStatus status)
    {
        if (IsClosed)
        {
            return AccountingErrors.FiscalYearClosed;
        }

        var period = _periods.FirstOrDefault(p => p.Id == periodId);
        if (period is null)
        {
            return AccountingErrors.PeriodNotFound;
        }

        if (period.Status == status)
        {
            return Result.Success();
        }

        if (status == PeriodStatus.Closed && _periods.Any(p => p.PeriodNo < period.PeriodNo && p.Status != PeriodStatus.Closed))
        {
            return AccountingErrors.PeriodOrder;
        }

        if (period.Status == PeriodStatus.Closed && _periods.Any(p => p.PeriodNo > period.PeriodNo && p.Status == PeriodStatus.Closed))
        {
            return AccountingErrors.PeriodOrder;
        }

        var previous = period.Status;
        period.SetStatus(status);
        Raise(new FiscalPeriodStatusChanged(Id, period.Id, LegalEntityId, period.PeriodNo, previous.ToString(), status.ToString()));
        return Result.Success();
    }

    public FiscalPeriod LastPeriod => _periods.OrderBy(p => p.PeriodNo).Last();

    /// <summary>
    /// Year-end close is allowed when every period except the last is closed and the last is soft-closed: the closing entry is
    /// dated on the last day and must still be postable, while ordinary users can no longer post there.
    /// </summary>
    public Result CanClose()
    {
        var last = LastPeriod;
        return _periods.Where(p => p.Id != last.Id).All(p => p.Status == PeriodStatus.Closed) && last.Status == PeriodStatus.SoftClosed
            ? Result.Success()
            : AccountingErrors.YearEndPeriodsNotReady;
    }

    /// <summary>Records the year-end close and closes the last period.</summary>
    public void MarkClosed(Guid? closingJournalId, DateTime nowUtc)
    {
        if (IsClosed)
        {
            return;
        }

        var check = CanClose();
        if (check.IsFailure)
        {
            throw new DomainException(check.Error);
        }

        var last = LastPeriod;
        last.SetStatus(PeriodStatus.Closed);
        IsClosed = true;
        ClosingJournalId = closingJournalId;
        ClosedAtUtc = nowUtc;
        Raise(new FiscalPeriodStatusChanged(Id, last.Id, LegalEntityId, last.PeriodNo, PeriodStatus.SoftClosed.ToString(), PeriodStatus.Closed.ToString()));
        Raise(new FiscalYearClosed(Id, LegalEntityId, closingJournalId));
    }

    /// <summary>Undoes the year-end close: the last period becomes soft-closed so the closing entry can be reversed on the last day.</summary>
    public void Reopen()
    {
        if (!IsClosed)
        {
            throw new DomainException(AccountingErrors.FiscalYearNotClosed);
        }

        LastPeriod.SetStatus(PeriodStatus.SoftClosed);
        IsClosed = false;
        ClosingJournalId = null;
        ClosedAtUtc = null;
        Raise(new FiscalYearReopened(Id, LegalEntityId));
    }
}

public sealed class FiscalPeriod : ITenantScoped
{
    private FiscalPeriod()
    {
    }

    internal FiscalPeriod(Guid id, Guid fiscalYearId, int periodNo, DateOnly startDate, DateOnly endDate)
    {
        Id = id;
        FiscalYearId = fiscalYearId;
        PeriodNo = periodNo;
        StartDate = startDate;
        EndDate = endDate;
        Status = PeriodStatus.Open;
    }

    public Guid Id { get; private set; }

    public Guid FiscalYearId { get; private set; }

    public int PeriodNo { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    public PeriodStatus Status { get; private set; }

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;

    internal void SetStatus(PeriodStatus status) => Status = status;
}

public enum JournalStatus
{
    Draft = 1,
    Posted = 2,

    /// <summary>Posted and later cancelled by a reversal journal. Its lines stay in the ledger.</summary>
    Reversed = 3,
}

/// <summary>
/// A double-entry journal. Immutable once posted; corrections are reversals (Part 12.4).
/// Amounts are kept in the transaction currency and converted to the legal entity's functional (base) currency
/// with per-line rounding (MR-6). A residual of at most 0.01 is absorbed by an FX rounding line.
/// </summary>
[Audited]
public sealed class JournalEntry : AggregateRoot, ICompanyScoped
{
    public const decimal MaxRoundingDifference = 0.01m;
    public const string ReversalSuffix = ":reversal";

    private readonly List<JournalLine> _lines = [];

    private JournalEntry()
    {
    }

    private JournalEntry(Guid id, Guid companyId, Guid legalEntityId, DateOnly journalDate, string sourceModule, string sourceDocType,
        Guid sourceDocId, string purpose, string description, string currency, decimal exchangeRate, Guid? reversalOfId)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        JournalDate = journalDate;
        SourceModule = sourceModule;
        SourceDocType = sourceDocType;
        SourceDocId = sourceDocId;
        Purpose = purpose;
        Description = description;
        Currency = currency;
        ExchangeRate = exchangeRate;
        ReversalOfId = reversalOfId;
        Status = JournalStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    /// <summary>Gapless per legal entity and fiscal year, assigned only when posted (ADR-021).</summary>
    public string? Number { get; private set; }

    public DateOnly JournalDate { get; private set; }

    public Guid? FiscalPeriodId { get; private set; }

    public string SourceModule { get; private set; } = string.Empty;

    public string SourceDocType { get; private set; } = string.Empty;

    public Guid SourceDocId { get; private set; }

    public string Purpose { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public JournalStatus Status { get; private set; }

    public Guid? ReversalOfId { get; private set; }

    public Guid? ReversedById { get; private set; }

    public Guid? PostedBy { get; private set; }

    public DateTime? PostedAtUtc { get; private set; }

    public IReadOnlyList<JournalLine> Lines => _lines.OrderBy(l => l.LineNo).ToList();

    public decimal TotalDebit => _lines.Sum(l => l.Debit);

    public decimal TotalCredit => _lines.Sum(l => l.Credit);

    public decimal TotalBaseDebit => _lines.Sum(l => l.BaseDebit);

    public decimal TotalBaseCredit => _lines.Sum(l => l.BaseCredit);

    public static JournalEntry CreateDraft(Guid companyId, Guid legalEntityId, DateOnly journalDate, string sourceModule, string sourceDocType,
        Guid sourceDocId, string purpose, string description, string currency, decimal exchangeRate)
    {
        if (exchangeRate <= 0)
        {
            throw new DomainException(AccountingErrors.InvalidRate);
        }

        return new JournalEntry(SequentialGuid.New(), companyId, legalEntityId, journalDate,
            Guard.MaxLength(Guard.NotEmpty(sourceModule, "accounting.source-required", "Source module"), 50, "accounting.source-too-long", "Source module"),
            Guard.MaxLength(Guard.NotEmpty(sourceDocType, "accounting.source-required", "Source document type"), 50, "accounting.source-too-long", "Source document type"),
            sourceDocId,
            Guard.MaxLength(Guard.NotEmpty(purpose, "accounting.purpose-required", "Purpose"), 60, "accounting.purpose-too-long", "Purpose"),
            Guard.MaxLength(description?.Trim() ?? string.Empty, 500, "accounting.description-too-long", "Description"),
            Guard.NotEmpty(currency, "accounting.currency-required", "Currency").ToUpperInvariant(), exchangeRate, null);
    }

    public static decimal ToBase(decimal amount, decimal rate) => Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);

    public void AddLine(Guid accountId, decimal debit, decimal credit, Guid? costCenterId = null, string? partyType = null, Guid? partyId = null, string? description = null, decimal? baseAmount = null)
    {
        EnsureDraft();
        if (debit < 0 || credit < 0 || (debit > 0) == (credit > 0))
        {
            throw new DomainException(AccountingErrors.LineOneSided);
        }

        if (baseAmount is < 0)
        {
            throw new DomainException(AccountingErrors.LineOneSided);
        }

        // A sub-ledger may fix the base amount (original-rate relief); otherwise convert at the journal rate.
        var fixedBase = baseAmount is null ? (decimal?)null : Math.Round(baseAmount.Value, 2, MidpointRounding.AwayFromZero);
        var baseDebit = debit > 0 ? fixedBase ?? ToBase(debit, ExchangeRate) : 0m;
        var baseCredit = credit > 0 ? fixedBase ?? ToBase(credit, ExchangeRate) : 0m;
        _lines.Add(new JournalLine(SequentialGuid.New(), Id, _lines.Count + 1, accountId, debit, credit,
            baseDebit, baseCredit, costCenterId, partyType, partyId, description));
    }

    public void ClearLines()
    {
        EnsureDraft();
        _lines.Clear();
    }

    public void UpdateHeader(DateOnly journalDate, string description)
    {
        EnsureDraft();
        JournalDate = journalDate;
        Description = Guard.MaxLength(description?.Trim() ?? string.Empty, 500, "accounting.description-too-long", "Description");
    }

    /// <summary>Base-currency difference (debit − credit) left by per-line rounding.</summary>
    public decimal BaseImbalance => TotalBaseDebit - TotalBaseCredit;

    /// <summary>Validates transaction-currency balance and returns whether a base rounding line is needed.</summary>
    public Result<bool> CheckBalance()
    {
        if (_lines.Count < 2)
        {
            return AccountingErrors.TooFewLines;
        }

        if (TotalDebit != TotalCredit)
        {
            return AccountingErrors.Unbalanced;
        }

        var difference = Math.Abs(BaseImbalance);
        if (difference > MaxRoundingDifference)
        {
            return AccountingErrors.Unbalanced;
        }

        return difference != 0m;
    }

    /// <summary>Adds a base-currency-only line that removes a rounding residual of at most 0.01.</summary>
    public void AddRoundingLine(Guid roundingAccountId)
    {
        EnsureDraft();
        var difference = BaseImbalance;
        if (difference == 0m || Math.Abs(difference) > MaxRoundingDifference)
        {
            throw new DomainException(AccountingErrors.Unbalanced);
        }

        _lines.Add(new JournalLine(SequentialGuid.New(), Id, _lines.Count + 1, roundingAccountId, 0m, 0m,
            difference < 0 ? -difference : 0m, difference > 0 ? difference : 0m, null, null, null, "FX rounding difference"));
    }

    public void Post(string number, Guid fiscalPeriodId, Guid? postedBy, DateTime nowUtc)
    {
        EnsureDraft();
        var balance = CheckBalance();
        if (balance.IsFailure || balance.Value || TotalBaseDebit != TotalBaseCredit)
        {
            throw new DomainException(AccountingErrors.Unbalanced);
        }

        Number = Guard.NotEmpty(number, "accounting.number-required", "Number");
        FiscalPeriodId = fiscalPeriodId;
        PostedBy = postedBy;
        PostedAtUtc = nowUtc;
        Status = JournalStatus.Posted;
        Raise(new JournalPosted(Id, LegalEntityId, Number, SourceModule, SourceDocType, SourceDocId, Purpose, TotalBaseDebit));
    }

    /// <summary>Creates the draft reversal: every line with debit and credit swapped, same source document.</summary>
    public JournalEntry CreateReversal(DateOnly reversalDate)
    {
        if (Status == JournalStatus.Reversed)
        {
            throw new DomainException(AccountingErrors.AlreadyReversed);
        }

        if (Status != JournalStatus.Posted)
        {
            throw new DomainException(AccountingErrors.NotPosted);
        }

        var reversal = new JournalEntry(SequentialGuid.New(), CompanyId, LegalEntityId, reversalDate, SourceModule, SourceDocType, SourceDocId,
            Purpose + ReversalSuffix, $"Reversal of {Number}", Currency, ExchangeRate, Id);
        foreach (var line in Lines)
        {
            reversal._lines.Add(new JournalLine(SequentialGuid.New(), reversal.Id, line.LineNo, line.AccountId, line.Credit, line.Debit,
                line.BaseCredit, line.BaseDebit, line.CostCenterId, line.PartyType, line.PartyId, line.Description));
        }

        return reversal;
    }

    public void MarkReversed(Guid reversalId)
    {
        if (Status != JournalStatus.Posted)
        {
            throw new DomainException(Status == JournalStatus.Reversed ? AccountingErrors.AlreadyReversed : AccountingErrors.NotPosted);
        }

        Status = JournalStatus.Reversed;
        ReversedById = reversalId;
    }

    private void EnsureDraft()
    {
        if (Status != JournalStatus.Draft)
        {
            throw new DomainException(AccountingErrors.NotDraft);
        }
    }
}

public sealed class JournalLine : ITenantScoped
{
    private JournalLine()
    {
    }

    internal JournalLine(Guid id, Guid journalEntryId, int lineNo, Guid accountId, decimal debit, decimal credit, decimal baseDebit,
        decimal baseCredit, Guid? costCenterId, string? partyType, Guid? partyId, string? description)
    {
        Id = id;
        JournalEntryId = journalEntryId;
        LineNo = lineNo;
        AccountId = accountId;
        Debit = debit;
        Credit = credit;
        BaseDebit = baseDebit;
        BaseCredit = baseCredit;
        CostCenterId = costCenterId;
        PartyType = partyType;
        PartyId = partyId;
        Description = description is { Length: > 300 } ? description[..300] : description;
    }

    public Guid Id { get; private set; }

    public Guid JournalEntryId { get; private set; }

    public int LineNo { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>Transaction-currency debit.</summary>
    public decimal Debit { get; private set; }

    /// <summary>Transaction-currency credit.</summary>
    public decimal Credit { get; private set; }

    public decimal BaseDebit { get; private set; }

    public decimal BaseCredit { get; private set; }

    public Guid? CostCenterId { get; private set; }

    public string? PartyType { get; private set; }

    public Guid? PartyId { get; private set; }

    public string? Description { get; private set; }
}

/// <summary>
/// Maps a posting role to an account for a company. Optional context keys make a rule more specific; the most specific
/// matching rule wins, then the highest priority (ADR-011).
/// </summary>
[Audited]
public sealed class AccountDetermination : AggregateRoot, ICompanyScoped
{
    private AccountDetermination()
    {
    }

    private AccountDetermination(Guid id, Guid companyId, string role, Guid accountId, Guid? legalEntityId, string? productPostingGroup,
        string? partyPostingGroup, Guid? taxCodeId, Guid? warehouseId, int priority)
        : base(id)
    {
        CompanyId = companyId;
        Role = role;
        AccountId = accountId;
        LegalEntityId = legalEntityId;
        ProductPostingGroup = productPostingGroup;
        PartyPostingGroup = partyPostingGroup;
        TaxCodeId = taxCodeId;
        WarehouseId = warehouseId;
        Priority = priority;
    }

    public Guid CompanyId { get; private set; }

    public string Role { get; private set; } = string.Empty;

    public Guid AccountId { get; private set; }

    public Guid? LegalEntityId { get; private set; }

    public string? ProductPostingGroup { get; private set; }

    public string? PartyPostingGroup { get; private set; }

    public Guid? TaxCodeId { get; private set; }

    public Guid? WarehouseId { get; private set; }

    public int Priority { get; private set; }

    public int Specificity =>
        (LegalEntityId is null ? 0 : 1) + (ProductPostingGroup is null ? 0 : 1) + (PartyPostingGroup is null ? 0 : 1) +
        (TaxCodeId is null ? 0 : 1) + (WarehouseId is null ? 0 : 1);

    public static AccountDetermination Create(Guid companyId, string role, Guid accountId, Guid? legalEntityId = null,
        string? productPostingGroup = null, string? partyPostingGroup = null, Guid? taxCodeId = null, Guid? warehouseId = null, int priority = 0) =>
        new(SequentialGuid.New(), companyId, Guard.NotEmpty(role, "accounting.role-required", "Role"), accountId, legalEntityId,
            Normalize(productPostingGroup), Normalize(partyPostingGroup), taxCodeId, warehouseId, priority);

    public void Retarget(Guid accountId, int priority)
    {
        AccountId = accountId;
        Priority = priority;
    }

    public bool SameKey(Guid? legalEntityId, string? productPostingGroup, string? partyPostingGroup, Guid? taxCodeId, Guid? warehouseId) =>
        LegalEntityId == legalEntityId && ProductPostingGroup == Normalize(productPostingGroup) && PartyPostingGroup == Normalize(partyPostingGroup) &&
        TaxCodeId == taxCodeId && WarehouseId == warehouseId;

    /// <summary>Every key the rule sets must equal the posting's value; unset keys match anything.</summary>
    public bool Matches(Guid legalEntityId, string? productPostingGroup, string? partyPostingGroup, Guid? taxCodeId, Guid? warehouseId) =>
        (LegalEntityId is null || LegalEntityId == legalEntityId) &&
        (ProductPostingGroup is null || ProductPostingGroup == Normalize(productPostingGroup)) &&
        (PartyPostingGroup is null || PartyPostingGroup == Normalize(partyPostingGroup)) &&
        (TaxCodeId is null || TaxCodeId == taxCodeId) &&
        (WarehouseId is null || WarehouseId == warehouseId);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

public static class PostingRuleResolver
{
    public static AccountDetermination? Resolve(IEnumerable<AccountDetermination> rules, string role, Guid legalEntityId,
        string? productPostingGroup, string? partyPostingGroup, Guid? taxCodeId, Guid? warehouseId) =>
        rules.Where(r => r.Role == role && r.Matches(legalEntityId, productPostingGroup, partyPostingGroup, taxCodeId, warehouseId))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.Priority)
            .FirstOrDefault();
}

/// <summary>Running totals per account/period/cost centre/currency, maintained inside the posting transaction.</summary>
public sealed class AccountBalance : ICompanyScoped
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid LegalEntityId { get; set; }

    public Guid AccountId { get; set; }

    public Guid FiscalPeriodId { get; set; }

    /// <summary><see cref="Guid.Empty"/> when the lines carry no cost centre.</summary>
    public Guid CostCenterId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal BaseDebit { get; set; }

    public decimal BaseCredit { get; set; }

    public decimal TxDebit { get; set; }

    public decimal TxCredit { get; set; }
}

public sealed record JournalPosted(Guid JournalId, Guid LegalEntityId, string Number, string SourceModule, string SourceDocType, Guid SourceDocId, string Purpose, decimal TotalBaseDebit) : IDomainEvent;

public sealed record FiscalYearClosed(Guid FiscalYearId, Guid LegalEntityId, Guid? ClosingJournalId) : IDomainEvent;

public sealed record FiscalYearReopened(Guid FiscalYearId, Guid LegalEntityId) : IDomainEvent;

public sealed record OpeningBalancePosted(Guid OpeningBalanceId, Guid LegalEntityId, Guid JournalId) : IDomainEvent;

public sealed record FiscalPeriodStatusChanged(Guid FiscalYearId, Guid PeriodId, Guid LegalEntityId, int PeriodNo, string From, string To) : IDomainEvent;
