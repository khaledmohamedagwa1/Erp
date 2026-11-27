using Erp.Modules.Finance.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Finance.Domain;

public static class FinanceErrors
{
    public static readonly Error OverAllocation = Error.Validation("finance.over-allocation", "Allocations exceed the amount available.");
    public static readonly Error AllocationExceedsOpen = Error.Validation("finance.allocation-exceeds-open", "An allocation is larger than the open amount of the item.");
    public static readonly Error OpenItemAllocated = Error.Conflict("finance.open-item-allocated", "The document has allocations; undo them before cancelling.");
    public static readonly Error OpenItemClosed = Error.Conflict("finance.open-item-closed", "The open item is settled or cancelled.");
    public static readonly Error CurrencyMismatch = Error.Validation("finance.currency-mismatch", "Allocations must be in the same currency as the open item.");
    public static readonly Error NotDraft = Error.Conflict("finance.not-draft", "Only draft documents can be changed or posted.");
    public static readonly Error InvalidAmount = Error.Validation("finance.invalid-amount", "Amounts must be positive.");
}

/// <summary>Pure money arithmetic shared by documents, revaluation and reports. Base = functional currency, 2 decimals.</summary>
public static class FinanceMath
{
    public const int BaseDecimals = 2;

    public static decimal Round(decimal value, int decimals = BaseDecimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static decimal ToBase(decimal amount, decimal rate) => Round(amount * rate);

    /// <summary>Base value relieved when <paramref name="amount"/> of an item is settled, at the item's historical rate.</summary>
    public static decimal Relief(decimal openAmount, decimal openBase, decimal amount) =>
        amount == openAmount ? openBase : Round(openBase * amount / openAmount);

    /// <summary>Realized exchange difference in base currency; positive is a gain.</summary>
    public static decimal RealizedFx(PartyKind party, decimal relievedBase, decimal settlementBase) =>
        party == PartyKind.Customer ? settlementBase - relievedBase : relievedBase - settlementBase;

    /// <summary>Unrealized difference for an open item revalued at a closing rate; positive is a gain.</summary>
    public static decimal Unrealized(PartyKind party, decimal historicalBase, decimal revaluedBase) =>
        party == PartyKind.Customer ? revaluedBase - historicalBase : historicalBase - revaluedBase;

    /// <summary>Aging bucket index 0..4 (current, 1–30, 31–60, 61–90, over 90 days past due).</summary>
    public static int AgingBucket(DateOnly dueDate, DateOnly asOf)
    {
        var overdue = asOf.DayNumber - dueDate.DayNumber;
        return overdue switch
        {
            <= 0 => 0,
            <= 30 => 1,
            <= 60 => 2,
            <= 90 => 3,
            _ => 4,
        };
    }

    /// <summary>Splits a base total over installments proportionally to their amounts; the last absorbs rounding.</summary>
    public static IReadOnlyList<decimal> SplitBase(IReadOnlyList<decimal> amounts, decimal totalBase)
    {
        var total = amounts.Sum();
        var result = new List<decimal>();
        var used = 0m;
        for (var i = 0; i < amounts.Count; i++)
        {
            var share = i == amounts.Count - 1 ? totalBase - used : (total == 0 ? 0 : Round(totalBase * amounts[i] / total));
            result.Add(share);
            used += share;
        }

        return result;
    }
}

public enum OpenItemStatus
{
    Open = 1,
    PartiallySettled = 2,
    Settled = 3,
    Cancelled = 4,
}

/// <summary>
/// One installment of a receivable or payable. Amounts are kept positive; <see cref="Sign"/> is +1 for invoices and −1 for credit
/// or debit notes. Base amounts are at the document's historical rate; realized differences are booked at settlement and
/// unrealized revaluation never changes the item (it is reversed the next day).
/// </summary>
[Audited]
public sealed class OpenItem : AggregateRoot, ICompanyScoped
{
    private OpenItem()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public PartyKind PartyType { get; private set; }

    public Guid PartyId { get; private set; }

    public string PartyPostingGroup { get; private set; } = string.Empty;

    public string SourceModule { get; private set; } = string.Empty;

    public string SourceDocType { get; private set; } = string.Empty;

    public Guid SourceDocId { get; private set; }

    public string DocumentNumber { get; private set; } = string.Empty;

    public DateOnly DocumentDate { get; private set; }

    public int Installment { get; private set; }

    public DateOnly DueDate { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public int Sign { get; private set; }

    public decimal OriginalAmount { get; private set; }

    public decimal OpenAmount { get; private set; }

    public decimal OriginalBase { get; private set; }

    public decimal OpenBase { get; private set; }

    public OpenItemStatus Status { get; private set; }

    public bool IsCredit => Sign < 0;

    public static OpenItem Create(Guid companyId, Guid legalEntityId, PartyKind partyType, Guid partyId, string partyPostingGroup,
        string sourceModule, string sourceDocType, Guid sourceDocId, string documentNumber, DateOnly documentDate, int installment,
        DateOnly dueDate, string currency, decimal exchangeRate, int sign, decimal amount, decimal baseAmount)
    {
        if (amount <= 0 || baseAmount < 0)
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        return new OpenItem
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            PartyType = partyType,
            PartyId = partyId,
            PartyPostingGroup = partyPostingGroup,
            SourceModule = sourceModule,
            SourceDocType = sourceDocType,
            SourceDocId = sourceDocId,
            DocumentNumber = documentNumber,
            DocumentDate = documentDate,
            Installment = installment,
            DueDate = dueDate,
            Currency = currency,
            ExchangeRate = exchangeRate,
            Sign = sign >= 0 ? 1 : -1,
            OriginalAmount = amount,
            OpenAmount = amount,
            OriginalBase = baseAmount,
            OpenBase = baseAmount,
            Status = OpenItemStatus.Open,
        };
    }

    /// <summary>Settles part of the item; returns the base value relieved at the historical rate.</summary>
    public decimal Settle(decimal amount)
    {
        if (Status is OpenItemStatus.Settled or OpenItemStatus.Cancelled)
        {
            throw new DomainException(FinanceErrors.OpenItemClosed);
        }

        if (amount <= 0)
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        if (amount > OpenAmount)
        {
            throw new DomainException(FinanceErrors.AllocationExceedsOpen);
        }

        var relieved = FinanceMath.Relief(OpenAmount, OpenBase, amount);
        OpenAmount -= amount;
        OpenBase -= relieved;
        Status = OpenAmount == 0 ? OpenItemStatus.Settled : OpenItemStatus.PartiallySettled;
        return relieved;
    }

    public void Cancel()
    {
        if (OpenAmount != OriginalAmount)
        {
            throw new DomainException(FinanceErrors.OpenItemAllocated);
        }

        Status = OpenItemStatus.Cancelled;
        OpenAmount = 0;
        OpenBase = 0;
    }

    /// <summary>Adds the document-level settlement event to this item's outbox (the item that changed last carries it).</summary>
    public void Announce(string documentStatus, decimal documentOpenAmount) =>
        Raise(new InvoiceSettled(SourceModule, SourceDocType, SourceDocId, documentStatus, documentOpenAmount));
}

public enum SettlementKind
{
    Receipt = 1,
    Payment = 2,
    CreditNote = 3,
    Advance = 4,
}

/// <summary>Immutable record of an amount settled on an open item and the realized exchange difference it produced.</summary>
public sealed class Allocation : ICompanyScoped
{
    private Allocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid OpenItemId { get; private set; }

    public PartyKind PartyType { get; private set; }

    public Guid PartyId { get; private set; }

    public SettlementKind SettlementKind { get; private set; }

    /// <summary>Receipt/payment id, or the credit note's open item id.</summary>
    public Guid SettlementId { get; private set; }

    public string SettlementReference { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public decimal Amount { get; private set; }

    public decimal BaseRelieved { get; private set; }

    public decimal SettlementBase { get; private set; }

    public decimal RealizedFxBase { get; private set; }

    public static Allocation Create(OpenItem item, SettlementKind kind, Guid settlementId, string reference, DateOnly date,
        decimal amount, decimal baseRelieved, decimal settlementBase) => new()
        {
            Id = SequentialGuid.New(),
            CompanyId = item.CompanyId,
            LegalEntityId = item.LegalEntityId,
            OpenItemId = item.Id,
            PartyType = item.PartyType,
            PartyId = item.PartyId,
            SettlementKind = kind,
            SettlementId = settlementId,
            SettlementReference = reference,
            Date = date,
            Amount = amount,
            BaseRelieved = baseRelieved,
            SettlementBase = settlementBase,
            RealizedFxBase = FinanceMath.RealizedFx(item.PartyType, baseRelieved, settlementBase),
        };
}

public enum CashBankKind
{
    Cash = 1,
    Bank = 2,
}

/// <summary>A cash drawer/safe or bank account. Posts to its own GL account when one is set, otherwise to the CashAccount/BankAccount role.</summary>
[Audited]
public sealed class CashBankAccount : AggregateRoot, ICompanyScoped
{
    private CashBankAccount()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public CashBankKind Kind { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    public Guid? GlAccountId { get; private set; }

    public string? BankName { get; private set; }

    public string? AccountNumber { get; private set; }

    public string? Iban { get; private set; }

    public bool IsActive { get; private set; }

    public static CashBankAccount Create(Guid companyId, Guid legalEntityId, CashBankKind kind, string code, string name, string currency,
        Guid? glAccountId, string? bankName, string? accountNumber, string? iban) => new()
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            Kind = kind,
            Code = Guard.MaxLength(Guard.NotEmpty(code, "finance.code-required", "Code"), 30, "finance.code-too-long", "Code").ToUpperInvariant(),
            Name = Guard.MaxLength(Guard.NotEmpty(name, "finance.name-required", "Name"), 200, "finance.name-too-long", "Name"),
            Currency = Guard.NotEmpty(currency, "finance.currency-required", "Currency").ToUpperInvariant(),
            GlAccountId = glAccountId,
            BankName = bankName,
            AccountNumber = accountNumber,
            Iban = iban,
            IsActive = true,
        };

    public string Role => Kind == CashBankKind.Cash ? "CashAccount" : "BankAccount";

    public void Deactivate() => IsActive = false;
}

public enum DocumentStatus
{
    Draft = 1,
    Posted = 2,
}

public enum PaymentMethod
{
    Cash = 1,
    BankTransfer = 2,
    Cheque = 3,
    Card = 4,
}

public sealed class RequestedAllocation
{
    private RequestedAllocation()
    {
    }

    public RequestedAllocation(Guid openItemId, decimal amount)
    {
        OpenItemId = openItemId;
        Amount = amount;
    }

    public Guid OpenItemId { get; private set; }

    public decimal Amount { get; private set; }
}

public enum SettlementDirection
{
    /// <summary>Money received from a customer.</summary>
    Receipt = 1,

    /// <summary>Money paid to a supplier.</summary>
    Payment = 2,
}

/// <summary>
/// A customer receipt or supplier payment. The settlement value is the cash amount plus any tax withheld; it is allocated to open
/// items and the remainder becomes an advance that can be allocated later.
/// </summary>
[Audited]
public sealed class Settlement : AggregateRoot, ICompanyScoped
{
    private readonly List<RequestedAllocation> _allocations = [];

    private Settlement()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public SettlementDirection Direction { get; private set; }

    public Guid PartyId { get; private set; }

    public string? Number { get; private set; }

    public DateOnly Date { get; private set; }

    public PaymentMethod Method { get; private set; }

    public Guid CashBankAccountId { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public decimal Amount { get; private set; }

    public Guid? WithholdingTaxGroupId { get; private set; }

    public decimal WithholdingBase { get; private set; }

    public decimal WithholdingAmount { get; private set; }

    public string? Reference { get; private set; }

    public string? Description { get; private set; }

    public DocumentStatus Status { get; private set; }

    public decimal UnallocatedAmount { get; private set; }

    public Guid? JournalId { get; private set; }

    public IReadOnlyCollection<RequestedAllocation> Allocations => _allocations.AsReadOnly();

    public PartyKind PartyType => Direction == SettlementDirection.Receipt ? PartyKind.Customer : PartyKind.Supplier;

    public decimal SettlementValue => Amount + WithholdingAmount;

    public static Settlement Create(Guid companyId, Guid legalEntityId, SettlementDirection direction, Guid partyId, DateOnly date,
        PaymentMethod method, Guid cashBankAccountId, string currency, decimal exchangeRate, decimal amount, string? reference, string? description)
    {
        if (amount <= 0 || exchangeRate <= 0)
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        return new Settlement
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            Direction = direction,
            PartyId = partyId,
            Date = date,
            Method = method,
            CashBankAccountId = cashBankAccountId,
            Currency = currency.ToUpperInvariant(),
            ExchangeRate = exchangeRate,
            Amount = amount,
            Reference = reference,
            Description = description,
            Status = DocumentStatus.Draft,
        };
    }

    public void SetWithholding(Guid? taxGroupId, decimal baseAmount, decimal amount)
    {
        EnsureDraft();
        WithholdingTaxGroupId = taxGroupId;
        WithholdingBase = taxGroupId is null ? 0 : baseAmount;
        WithholdingAmount = taxGroupId is null ? 0 : amount;
    }

    public void SetAllocations(IEnumerable<RequestedAllocation> allocations)
    {
        EnsureDraft();
        var list = allocations.ToList();
        if (list.Any(a => a.Amount <= 0))
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        if (list.Sum(a => a.Amount) > SettlementValue)
        {
            throw new DomainException(FinanceErrors.OverAllocation);
        }

        _allocations.Clear();
        _allocations.AddRange(list.GroupBy(a => a.OpenItemId).Select(g => new RequestedAllocation(g.Key, g.Sum(a => a.Amount))));
    }

    public void MarkPosted(string number, Guid journalId, decimal unallocated)
    {
        EnsureDraft();
        Number = number;
        JournalId = journalId;
        UnallocatedAmount = unallocated;
        Status = DocumentStatus.Posted;
    }

    /// <summary>Consumes part of the posted advance (unallocated remainder) for a later allocation.</summary>
    public void ConsumeAdvance(decimal amount)
    {
        if (Status != DocumentStatus.Posted || amount <= 0 || amount > UnallocatedAmount)
        {
            throw new DomainException(FinanceErrors.OverAllocation);
        }

        UnallocatedAmount -= amount;
    }

    private void EnsureDraft()
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new DomainException(FinanceErrors.NotDraft);
        }
    }
}

/// <summary>Money moved between two cash/bank accounts, possibly in different currencies; bank charges optional.</summary>
[Audited]
public sealed class InternalTransfer : AggregateRoot, ICompanyScoped
{
    private InternalTransfer()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public string? Number { get; private set; }

    public DateOnly Date { get; private set; }

    public Guid FromAccountId { get; private set; }

    public decimal FromAmount { get; private set; }

    public decimal FromRate { get; private set; }

    public Guid ToAccountId { get; private set; }

    public decimal ToAmount { get; private set; }

    public decimal ToRate { get; private set; }

    public decimal Charges { get; private set; }

    public string? Description { get; private set; }

    public DocumentStatus Status { get; private set; }

    public Guid? JournalId { get; private set; }

    public static InternalTransfer Create(Guid companyId, Guid legalEntityId, DateOnly date, Guid fromAccountId, decimal fromAmount, decimal fromRate,
        Guid toAccountId, decimal toAmount, decimal toRate, decimal charges, string? description)
    {
        if (fromAccountId == toAccountId)
        {
            throw new DomainException(Error.Validation("finance.same-account", "Choose two different accounts."));
        }

        if (fromAmount <= 0 || toAmount <= 0 || fromRate <= 0 || toRate <= 0 || charges < 0)
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        return new InternalTransfer
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            Date = date,
            FromAccountId = fromAccountId,
            FromAmount = fromAmount,
            FromRate = fromRate,
            ToAccountId = toAccountId,
            ToAmount = toAmount,
            ToRate = toRate,
            Charges = charges,
            Description = description,
            Status = DocumentStatus.Draft,
        };
    }

    public void MarkPosted(string number, Guid journalId)
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new DomainException(FinanceErrors.NotDraft);
        }

        Number = number;
        JournalId = journalId;
        Status = DocumentStatus.Posted;
    }
}

public sealed class ExpenseLine
{
    private ExpenseLine()
    {
    }

    public ExpenseLine(Guid accountId, string? description, decimal netAmount, Guid? taxGroupId, Guid? costCenterId)
    {
        if (netAmount <= 0)
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        Uid = SequentialGuid.New();
        AccountId = accountId;
        Description = description;
        NetAmount = netAmount;
        TaxGroupId = taxGroupId;
        CostCenterId = costCenterId;
    }

    public Guid Uid { get; private set; }

    public Guid AccountId { get; private set; }

    public string? Description { get; private set; }

    public decimal NetAmount { get; private set; }

    public Guid? TaxGroupId { get; private set; }

    public Guid? CostCenterId { get; private set; }

    /// <summary>Recoverable input tax (posted to InputTax).</summary>
    public decimal RecoverableTax { get; private set; }

    /// <summary>Non-recoverable tax, added to the expense (OQ-PUR-5).</summary>
    public decimal NonRecoverableTax { get; private set; }

    /// <summary>Tax code of the recoverable tax, for the InputTax posting context (first recoverable component).</summary>
    public Guid? RecoverableTaxCodeId { get; private set; }

    public decimal Gross => NetAmount + RecoverableTax + NonRecoverableTax;

    public void SetTax(decimal recoverable, decimal nonRecoverable, Guid? recoverableTaxCodeId)
    {
        RecoverableTax = recoverable;
        NonRecoverableTax = nonRecoverable;
        RecoverableTaxCodeId = recoverableTaxCodeId;
    }
}

public enum ExpensePayee
{
    /// <summary>Paid immediately from a cash/bank account.</summary>
    CashBank = 1,

    /// <summary>Owed to a supplier (creates a payable).</summary>
    Supplier = 2,
}

[Audited]
public sealed class Expense : AggregateRoot, ICompanyScoped
{
    private readonly List<ExpenseLine> _lines = [];

    private Expense()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public string? Number { get; private set; }

    public DateOnly Date { get; private set; }

    public ExpensePayee Payee { get; private set; }

    public Guid? CashBankAccountId { get; private set; }

    public Guid? SupplierId { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public string? Reference { get; private set; }

    public string? Description { get; private set; }

    public DocumentStatus Status { get; private set; }

    public Guid? JournalId { get; private set; }

    public IReadOnlyCollection<ExpenseLine> Lines => _lines.AsReadOnly();

    public decimal Total => _lines.Sum(l => l.Gross);

    public static Expense Create(Guid companyId, Guid legalEntityId, DateOnly date, ExpensePayee payee, Guid? cashBankAccountId, Guid? supplierId,
        string currency, decimal exchangeRate, string? reference, string? description, IEnumerable<ExpenseLine> lines)
    {
        if (exchangeRate <= 0)
        {
            throw new DomainException(FinanceErrors.InvalidAmount);
        }

        if ((payee == ExpensePayee.CashBank && cashBankAccountId is null) || (payee == ExpensePayee.Supplier && supplierId is null))
        {
            throw new DomainException(Error.Validation("finance.expense-payee-required", "Choose the cash/bank account or the supplier."));
        }

        var expense = new Expense
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            Date = date,
            Payee = payee,
            CashBankAccountId = payee == ExpensePayee.CashBank ? cashBankAccountId : null,
            SupplierId = payee == ExpensePayee.Supplier ? supplierId : null,
            Currency = currency.ToUpperInvariant(),
            ExchangeRate = exchangeRate,
            Reference = reference,
            Description = description,
            Status = DocumentStatus.Draft,
        };
        expense._lines.AddRange(lines);
        if (expense._lines.Count == 0)
        {
            throw new DomainException(Error.Validation("finance.expense-lines-required", "An expense needs at least one line."));
        }

        return expense;
    }

    public void MarkPosted(string number, Guid journalId)
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new DomainException(FinanceErrors.NotDraft);
        }

        Number = number;
        JournalId = journalId;
        Status = DocumentStatus.Posted;
    }
}

/// <summary>Money movement on a cash/bank account (account currency, signed: + in, − out), used for balances and reconciliation.</summary>
public sealed class CashMovement : ICompanyScoped
{
    private CashMovement()
    {
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid CashBankAccountId { get; private set; }

    public DateOnly Date { get; private set; }

    public decimal Amount { get; private set; }

    public decimal BaseAmount { get; private set; }

    public string SourceDocType { get; private set; } = string.Empty;

    public Guid SourceDocId { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid? ReconciliationId { get; private set; }

    public static CashMovement Create(CashBankAccount account, DateOnly date, decimal amount, decimal baseAmount, string sourceDocType, Guid sourceDocId,
        string reference, string? description) => new()
        {
            Id = SequentialGuid.New(),
            CompanyId = account.CompanyId,
            LegalEntityId = account.LegalEntityId,
            CashBankAccountId = account.Id,
            Date = date,
            Amount = amount,
            BaseAmount = baseAmount,
            SourceDocType = sourceDocType,
            SourceDocId = sourceDocId,
            Reference = reference,
            Description = description,
        };

    public void MarkReconciled(Guid reconciliationId) => ReconciliationId = reconciliationId;
}

public sealed class BankStatementLine
{
    private BankStatementLine()
    {
    }

    public BankStatementLine(DateOnly date, decimal amount, string? description, string? reference)
    {
        Uid = SequentialGuid.New();
        Date = date;
        Amount = amount;
        Description = description;
        Reference = reference;
    }

    public Guid Uid { get; private set; }

    public DateOnly Date { get; private set; }

    /// <summary>Signed: + money in, − money out (account currency).</summary>
    public decimal Amount { get; private set; }

    public string? Description { get; private set; }

    public string? Reference { get; private set; }

    public Guid? MatchedMovementId { get; private set; }

    public void Match(Guid? movementId) => MatchedMovementId = movementId;
}

public enum ReconciliationStatus
{
    Draft = 1,
    Completed = 2,
}

[Audited]
public sealed class BankReconciliation : AggregateRoot, ICompanyScoped
{
    private readonly List<BankStatementLine> _lines = [];

    private BankReconciliation()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid CashBankAccountId { get; private set; }

    public DateOnly StatementDate { get; private set; }

    public decimal StatementBalance { get; private set; }

    public ReconciliationStatus Status { get; private set; }

    public IReadOnlyCollection<BankStatementLine> Lines => _lines.AsReadOnly();

    public static BankReconciliation Create(Guid companyId, Guid cashBankAccountId, DateOnly statementDate, decimal statementBalance, IEnumerable<BankStatementLine> lines)
    {
        var rec = new BankReconciliation
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            CashBankAccountId = cashBankAccountId,
            StatementDate = statementDate,
            StatementBalance = statementBalance,
            Status = ReconciliationStatus.Draft,
        };
        rec._lines.AddRange(lines);
        return rec;
    }

    /// <summary>
    /// Matches unmatched statement lines to unreconciled movements of the same amount, preferring equal reference, then the
    /// closest date within <paramref name="dayTolerance"/> days. Each movement is used once. Returns the number matched.
    /// </summary>
    public int AutoMatch(IReadOnlyCollection<CashMovement> candidates, int dayTolerance = 3)
    {
        EnsureDraft();
        var used = _lines.Where(l => l.MatchedMovementId is not null).Select(l => l.MatchedMovementId!.Value).ToHashSet();
        var matched = 0;
        foreach (var line in _lines.Where(l => l.MatchedMovementId is null))
        {
            var best = candidates
                .Where(m => !used.Contains(m.Id) && m.Amount == line.Amount && Math.Abs(m.Date.DayNumber - line.Date.DayNumber) <= dayTolerance)
                .OrderByDescending(m => !string.IsNullOrEmpty(line.Reference) && string.Equals(m.Reference, line.Reference, StringComparison.OrdinalIgnoreCase))
                .ThenBy(m => Math.Abs(m.Date.DayNumber - line.Date.DayNumber))
                .FirstOrDefault();
            if (best is not null)
            {
                line.Match(best.Id);
                used.Add(best.Id);
                matched++;
            }
        }

        return matched;
    }

    public void MatchManually(Guid lineUid, Guid? movementId)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.Uid == lineUid)
            ?? throw new DomainException(Error.NotFound("finance.statement-line-not-found", "Statement line not found."));
        if (movementId is not null && _lines.Any(l => l.Uid != lineUid && l.MatchedMovementId == movementId))
        {
            throw new DomainException(Error.Conflict("finance.movement-already-matched", "The movement is matched to another line."));
        }

        line.Match(movementId);
    }

    /// <summary>Completes when every line is matched and the book balance equals the statement balance.</summary>
    public Result Complete(decimal bookBalance)
    {
        EnsureDraft();
        if (_lines.Any(l => l.MatchedMovementId is null))
        {
            return Error.Conflict("finance.reconciliation-unmatched", "Some statement lines are not matched.");
        }

        if (bookBalance != StatementBalance)
        {
            return Error.Conflict("finance.reconciliation-difference", $"Book balance {bookBalance} differs from statement balance {StatementBalance}.");
        }

        Status = ReconciliationStatus.Completed;
        return Result.Success();
    }

    private void EnsureDraft()
    {
        if (Status != ReconciliationStatus.Draft)
        {
            throw new DomainException(FinanceErrors.NotDraft);
        }
    }
}

public sealed class RevaluationLine
{
    private RevaluationLine()
    {
    }

    public RevaluationLine(string kind, Guid referenceId, string currency, decimal openAmount, decimal historicalBase, decimal revaluedBase, decimal difference)
    {
        Kind = kind;
        ReferenceId = referenceId;
        Currency = currency;
        OpenAmount = openAmount;
        HistoricalBase = historicalBase;
        RevaluedBase = revaluedBase;
        Difference = difference;
    }

    /// <summary>Receivable, Payable or CashBank.</summary>
    public string Kind { get; private set; } = string.Empty;

    public Guid ReferenceId { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal OpenAmount { get; private set; }

    public decimal HistoricalBase { get; private set; }

    public decimal RevaluedBase { get; private set; }

    /// <summary>Positive = unrealized gain.</summary>
    public decimal Difference { get; private set; }
}

/// <summary>Month-end unrealized FX revaluation (OQ-ACC-5): one journal on the as-of date, reversed on the next day.</summary>
[Audited]
public sealed class FxRevaluationRun : AggregateRoot, ICompanyScoped
{
    private readonly List<RevaluationLine> _lines = [];

    private FxRevaluationRun()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public DateOnly AsOf { get; private set; }

    public DateOnly ReversalDate { get; private set; }

    public decimal TotalGain { get; private set; }

    public decimal TotalLoss { get; private set; }

    public Guid? JournalId { get; private set; }

    public Guid? ReversalJournalId { get; private set; }

    public IReadOnlyCollection<RevaluationLine> Lines => _lines.AsReadOnly();

    public static FxRevaluationRun Create(Guid companyId, Guid legalEntityId, DateOnly asOf, IEnumerable<RevaluationLine> lines)
    {
        var run = new FxRevaluationRun
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            AsOf = asOf,
            ReversalDate = asOf.AddDays(1),
        };
        run._lines.AddRange(lines.Where(l => l.Difference != 0));
        run.TotalGain = run._lines.Where(l => l.Difference > 0).Sum(l => l.Difference);
        run.TotalLoss = -run._lines.Where(l => l.Difference < 0).Sum(l => l.Difference);
        return run;
    }

    public void MarkPosted(Guid? journalId, Guid? reversalJournalId)
    {
        JournalId = journalId;
        ReversalJournalId = reversalJournalId;
    }
}
