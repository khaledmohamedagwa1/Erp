using Erp.Modules.Finance.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Finance.Domain;

public enum WithholdingCertificateKind
{
    /// <summary>Tax we withheld from a supplier on payments; we issue the certificate.</summary>
    Issued = 1,

    /// <summary>Tax a customer withheld from us on receipts; the customer issued the certificate (recorded with its reference).</summary>
    Received = 2,
}

public enum WithholdingCertificateStatus
{
    Active = 1,
    Cancelled = 2,
}

/// <summary>One receipt/payment with tax withheld, as listed on a certificate (settlement currency plus functional-currency equivalents).</summary>
public sealed class WithholdingCertificateLine
{
    private WithholdingCertificateLine()
    {
    }

    public WithholdingCertificateLine(Guid settlementId, string settlementNumber, DateOnly date, Guid? taxGroupId, string currency, decimal exchangeRate,
        decimal taxableAmount, decimal withheldAmount)
    {
        SettlementId = settlementId;
        SettlementNumber = settlementNumber;
        Date = date;
        TaxGroupId = taxGroupId;
        Currency = currency;
        ExchangeRate = exchangeRate;
        TaxableAmount = taxableAmount;
        WithheldAmount = withheldAmount;
        TaxableBase = FinanceMath.ToBase(taxableAmount, exchangeRate);
        WithheldBase = FinanceMath.ToBase(withheldAmount, exchangeRate);
    }

    public Guid SettlementId { get; private set; }

    public string SettlementNumber { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public Guid? TaxGroupId { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal ExchangeRate { get; private set; }

    public decimal TaxableAmount { get; private set; }

    public decimal WithheldAmount { get; private set; }

    public decimal TaxableBase { get; private set; }

    public decimal WithheldBase { get; private set; }
}

/// <summary>
/// A withholding tax certificate for one party and period: the posted receipts/payments with tax withheld that are not on another
/// active certificate. Issued certificates are numbered (gapless, "WHT"); received ones also keep the customer's reference.
/// </summary>
[Audited]
public sealed class WithholdingCertificate : AggregateRoot, ICompanyScoped
{
    private readonly List<WithholdingCertificateLine> _lines = [];

    private WithholdingCertificate()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public WithholdingCertificateKind Kind { get; private set; }

    public PartyKind PartyType => Kind == WithholdingCertificateKind.Issued ? PartyKind.Supplier : PartyKind.Customer;

    public Guid PartyId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public string? ExternalReference { get; private set; }

    public string? Notes { get; private set; }

    public WithholdingCertificateStatus Status { get; private set; }

    public decimal TotalTaxableBase { get; private set; }

    public decimal TotalWithheldBase { get; private set; }

    public IReadOnlyCollection<WithholdingCertificateLine> Lines => _lines.AsReadOnly();

    public static WithholdingCertificate Create(Guid companyId, Guid legalEntityId, WithholdingCertificateKind kind, Guid partyId, string number,
        DateOnly periodFrom, DateOnly periodTo, DateOnly issueDate, string? externalReference, string? notes, IReadOnlyCollection<WithholdingCertificateLine> lines)
    {
        if (periodFrom > periodTo)
        {
            throw new DomainException(Error.Validation("finance.invalid-period", "The period start must not be after its end."));
        }

        if (lines.Count == 0)
        {
            throw new DomainException(Error.Validation("finance.no-withholding", "No posted receipts or payments with tax withheld exist for this party and period."));
        }

        var certificate = new WithholdingCertificate
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            Kind = kind,
            PartyId = partyId,
            Number = Guard.NotEmpty(number, "finance.number-required", "Number"),
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            IssueDate = issueDate,
            ExternalReference = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Status = WithholdingCertificateStatus.Active,
            TotalTaxableBase = lines.Sum(l => l.TaxableBase),
            TotalWithheldBase = lines.Sum(l => l.WithheldBase),
        };
        certificate._lines.AddRange(lines.OrderBy(l => l.Date).ThenBy(l => l.SettlementNumber, StringComparer.Ordinal));
        return certificate;
    }

    public void Cancel()
    {
        if (Status == WithholdingCertificateStatus.Cancelled)
        {
            throw new DomainException(Error.Conflict("finance.certificate-cancelled", "The certificate is already cancelled."));
        }

        Status = WithholdingCertificateStatus.Cancelled;
    }
}
