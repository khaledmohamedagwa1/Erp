using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Accounting.Domain;

public enum OpeningBalanceStatus
{
    Draft = 1,
    Posted = 2,
    Reversed = 3,
}

/// <summary>
/// General-ledger opening balances of a legal entity, entered when its books start in this system. The difference between
/// the lines is booked to the OpeningBalanceEquity role when posted, so the journal always balances. Control accounts
/// (receivables, payables, inventory, GRNI…) are refused: their openings come from the Finance / Inventory opening documents,
/// which post with party or stock detail so the sub-ledgers agree with the control accounts.
/// At most one posted (not reversed) document per legal entity.
/// </summary>
[Audited]
public sealed class OpeningBalance : AggregateRoot, ICompanyScoped
{
    public static readonly Error ControlAccountNotAllowed = Error.Validation("accounting.opening-control-account",
        "Control accounts cannot be opened from the general ledger. Use the Finance (receivables/payables) or Inventory opening documents so the sub-ledger matches.");

    public static readonly Error AlreadyOpened = Error.Conflict("accounting.opening-balance-exists",
        "This legal entity already has posted opening balances. Reverse them first.");

    private readonly List<OpeningBalanceLine> _lines = [];

    private OpeningBalance()
    {
    }

    private OpeningBalance(Guid id, Guid companyId, Guid legalEntityId, DateOnly openingDate, string description)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        OpeningDate = openingDate;
        Description = description;
        Status = OpeningBalanceStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public DateOnly OpeningDate { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public OpeningBalanceStatus Status { get; private set; }

    public Guid? JournalId { get; private set; }

    public IReadOnlyList<OpeningBalanceLine> Lines => _lines.OrderBy(l => l.LineNo).ToList();

    public decimal TotalDebit => _lines.Sum(l => l.Debit);

    public decimal TotalCredit => _lines.Sum(l => l.Credit);

    /// <summary>Amount booked to opening balance equity: positive = credit to equity, negative = debit.</summary>
    public decimal EquityDifference => TotalDebit - TotalCredit;

    public static OpeningBalance Create(Guid companyId, Guid legalEntityId, DateOnly openingDate, string? description) =>
        new(SequentialGuid.New(), companyId, legalEntityId, openingDate,
            Guard.MaxLength(string.IsNullOrWhiteSpace(description) ? "Opening balances" : description.Trim(), 500, "accounting.description-too-long", "Description"));

    public void AddLine(Account account, decimal debit, decimal credit, string? description)
    {
        EnsureDraft();
        if (account.IsControl)
        {
            throw new DomainException(ControlAccountNotAllowed with { Message = $"{ControlAccountNotAllowed.Message} ({account.Code})" });
        }

        if (!account.IsPostingAccount)
        {
            throw new DomainException(AccountingErrors.NotPostingAccount);
        }

        if (debit < 0 || credit < 0 || (debit > 0) == (credit > 0))
        {
            throw new DomainException(AccountingErrors.LineOneSided);
        }

        _lines.Add(new OpeningBalanceLine(SequentialGuid.New(), Id, _lines.Count + 1, account.Id, debit, credit,
            description is { Length: > 300 } ? description[..300] : description));
    }

    public void MarkPosted(Guid journalId)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new DomainException(AccountingErrors.TooFewLines);
        }

        Status = OpeningBalanceStatus.Posted;
        JournalId = journalId;
        Raise(new OpeningBalancePosted(Id, LegalEntityId, journalId));
    }

    public void MarkReversed()
    {
        if (Status != OpeningBalanceStatus.Posted)
        {
            throw new DomainException(AccountingErrors.NotPosted);
        }

        Status = OpeningBalanceStatus.Reversed;
    }

    private void EnsureDraft()
    {
        if (Status != OpeningBalanceStatus.Draft)
        {
            throw new DomainException(AccountingErrors.NotDraft);
        }
    }
}

public sealed class OpeningBalanceLine : ITenantScoped
{
    private OpeningBalanceLine()
    {
    }

    internal OpeningBalanceLine(Guid id, Guid openingBalanceId, int lineNo, Guid accountId, decimal debit, decimal credit, string? description)
    {
        Id = id;
        OpeningBalanceId = openingBalanceId;
        LineNo = lineNo;
        AccountId = accountId;
        Debit = debit;
        Credit = credit;
        Description = description;
    }

    public Guid Id { get; private set; }

    public Guid OpeningBalanceId { get; private set; }

    public int LineNo { get; private set; }

    public Guid AccountId { get; private set; }

    public decimal Debit { get; private set; }

    public decimal Credit { get; private set; }

    public string? Description { get; private set; }
}
