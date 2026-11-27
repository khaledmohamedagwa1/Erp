using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.People.Domain;

public static class PeopleErrors
{
    public static readonly Error InvalidRate = Error.Validation("people.invalid-rate", "Commission rates must be between 0 and 100 %.");
    public static readonly Error InvalidTiers = Error.Validation("people.invalid-tiers", "Tier thresholds must be zero or positive and unique.");
    public static readonly Error DuplicateCategory = Error.Validation("people.duplicate-category", "A category can have only one override rate.");
    public static readonly Error NoLines = Error.Conflict("people.commission-run-empty", "The commission run has no commission to approve.");

    public static Error InvalidTransition(string from, string action) =>
        Error.Conflict("people.invalid-state-transition", $"A commission run in status {from} cannot be {action}.");

    public static string Code(string code) =>
        Guard.MaxLength(Guard.NotEmpty(code, "people.code-required", "Code"), 30, "people.code-too-long", "Code").Trim().ToUpperInvariant();

    public static string Name(string name, string field) =>
        Guard.MaxLength(Guard.NotEmpty(name, "people.name-required", field), 200, "people.name-too-long", field).Trim();
}

/// <summary>An employee of the company. Salespersons (flag) can be credited with sales and earn commissions under their plan.</summary>
[Audited]
public sealed class Employee : AggregateRoot, ICompanyScoped
{
    private Employee()
    {
    }

    private Employee(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public Guid? BranchId { get; private set; }

    public string? Department { get; private set; }

    public string? JobTitle { get; private set; }

    /// <summary>Optional link to the user account (POS cashiers default to their own employee as salesperson).</summary>
    public Guid? UserId { get; private set; }

    public bool IsSalesperson { get; private set; }

    public Guid? CommissionPlanId { get; private set; }

    public bool IsActive { get; private set; }

    public static Employee Create(Guid companyId, string code, string nameEn, string nameAr, Guid? branchId, string? department, string? jobTitle,
        Guid? userId, bool isSalesperson, Guid? commissionPlanId)
    {
        var employee = new Employee(SequentialGuid.New(), companyId, PeopleErrors.Code(code));
        employee.Update(nameEn, nameAr, branchId, department, jobTitle, userId, isSalesperson, commissionPlanId);
        return employee;
    }

    public void Update(string nameEn, string nameAr, Guid? branchId, string? department, string? jobTitle, Guid? userId, bool isSalesperson,
        Guid? commissionPlanId)
    {
        NameEn = PeopleErrors.Name(nameEn, "NameEn");
        NameAr = PeopleErrors.Name(nameAr, "NameAr");
        BranchId = branchId;
        Department = Optional(department, 100, "Department");
        JobTitle = Optional(jobTitle, 100, "JobTitle");
        UserId = userId;
        IsSalesperson = isSalesperson;
        CommissionPlanId = commissionPlanId;
    }

    public void SetActive(bool active) => IsActive = active;

    private static string? Optional(string? value, int max, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : Guard.MaxLength(value.Trim(), max, "people.value-too-long", field);
}

public enum CommissionBasis
{
    /// <summary>Net sales amount (excluding tax).</summary>
    NetSales = 1,

    /// <summary>Net sales minus the cost of goods sold recorded on the document.</summary>
    GrossMargin = 2,
}

/// <summary>Rate that applies to the whole period when the period basis total reaches <see cref="Threshold"/> (highest reached tier wins).</summary>
public sealed class CommissionTier
{
    /// <summary>For JSON deserialization.</summary>
    public CommissionTier()
    {
    }

    public CommissionTier(decimal threshold, decimal ratePercent)
    {
        Threshold = threshold;
        RatePercent = ratePercent;
    }

    public decimal Threshold { get; init; }

    public decimal RatePercent { get; init; }
}

/// <summary>Rate for products of a catalog category; overrides the plan and tier rate for those lines.</summary>
public sealed class CategoryRate
{
    /// <summary>For JSON deserialization.</summary>
    public CategoryRate()
    {
    }

    public CategoryRate(Guid categoryId, decimal ratePercent)
    {
        CategoryId = categoryId;
        RatePercent = ratePercent;
    }

    public Guid CategoryId { get; init; }

    public decimal RatePercent { get; init; }
}

/// <summary>
/// Commission plan: a base rate on net sales or gross margin, optional tiers by period total, optional per-category overrides and
/// clawback of commission on credit notes (refunds).
/// </summary>
[Audited]
public sealed class CommissionPlan : AggregateRoot, ICompanyScoped
{
    private CommissionPlan()
    {
    }

    private CommissionPlan(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public CommissionBasis Basis { get; private set; }

    public decimal RatePercent { get; private set; }

    /// <summary>Credit notes and refunds reduce the commission (negative basis) instead of being ignored.</summary>
    public bool ClawbackOnReturns { get; private set; }

    public bool IsActive { get; private set; }

    public List<CommissionTier> Tiers { get; private set; } = [];

    public List<CategoryRate> CategoryRates { get; private set; } = [];

    public static CommissionPlan Create(Guid companyId, string code, string nameEn, string nameAr, CommissionBasis basis, decimal ratePercent,
        bool clawbackOnReturns, IEnumerable<CommissionTier> tiers, IEnumerable<CategoryRate> categoryRates)
    {
        var plan = new CommissionPlan(SequentialGuid.New(), companyId, PeopleErrors.Code(code));
        plan.Update(nameEn, nameAr, basis, ratePercent, clawbackOnReturns, tiers, categoryRates, true);
        return plan;
    }

    public void Update(string nameEn, string nameAr, CommissionBasis basis, decimal ratePercent, bool clawbackOnReturns, IEnumerable<CommissionTier> tiers,
        IEnumerable<CategoryRate> categoryRates, bool isActive)
    {
        var tierList = tiers.OrderBy(t => t.Threshold).ToList();
        var categoryList = categoryRates.ToList();
        if (!ValidRate(ratePercent) || tierList.Any(t => !ValidRate(t.RatePercent)) || categoryList.Any(c => !ValidRate(c.RatePercent)))
        {
            throw new DomainException(PeopleErrors.InvalidRate);
        }

        if (tierList.Any(t => t.Threshold < 0) || tierList.Select(t => t.Threshold).Distinct().Count() != tierList.Count)
        {
            throw new DomainException(PeopleErrors.InvalidTiers);
        }

        if (categoryList.Select(c => c.CategoryId).Distinct().Count() != categoryList.Count)
        {
            throw new DomainException(PeopleErrors.DuplicateCategory);
        }

        NameEn = PeopleErrors.Name(nameEn, "NameEn");
        NameAr = PeopleErrors.Name(nameAr, "NameAr");
        Basis = basis;
        RatePercent = ratePercent;
        ClawbackOnReturns = clawbackOnReturns;
        Tiers = tierList;
        CategoryRates = categoryList;
        IsActive = isActive;
    }

    /// <summary>Rate for a period whose basis total is <paramref name="periodTotal"/>: the highest tier reached, otherwise the base rate.</summary>
    public decimal RateFor(decimal periodTotal) =>
        Tiers.Where(t => t.Threshold <= periodTotal).OrderByDescending(t => t.Threshold).Select(t => (decimal?)t.RatePercent).FirstOrDefault() ?? RatePercent;

    public decimal? CategoryRateFor(Guid? categoryId) =>
        categoryId is { } id ? CategoryRates.FirstOrDefault(c => c.CategoryId == id)?.RatePercent : null;

    private static bool ValidRate(decimal rate) => rate is >= 0 and <= 100 && decimal.Round(rate, 6) == rate;
}

public enum CommissionRunStatus
{
    Draft = 1,
    Approved = 2,
    Posted = 3,
    Reversed = 4,
    Cancelled = 5,
}

/// <summary>Commission of one salesperson in a run.</summary>
public sealed class CommissionRunLine
{
    /// <summary>For JSON deserialization.</summary>
    public CommissionRunLine()
    {
    }

    public CommissionRunLine(Guid salespersonId, Guid planId, string planCode, CommissionBasis basis, decimal basisTotal, decimal tierRatePercent, decimal commission)
    {
        SalespersonId = salespersonId;
        PlanId = planId;
        PlanCode = planCode;
        Basis = basis;
        BasisTotal = basisTotal;
        TierRatePercent = tierRatePercent;
        Commission = commission;
    }

    public Guid SalespersonId { get; init; }

    public Guid PlanId { get; init; }

    public string PlanCode { get; init; } = string.Empty;

    public CommissionBasis Basis { get; init; }

    /// <summary>Period basis total (net sales or margin, after clawbacks when the plan claws back).</summary>
    public decimal BasisTotal { get; init; }

    /// <summary>The plan or tier rate applied to lines without a category override.</summary>
    public decimal TierRatePercent { get; init; }

    public decimal Commission { get; init; }
}

/// <summary>Commission per source document and rate (the audit trail of a run line).</summary>
public sealed class CommissionDetail
{
    /// <summary>For JSON deserialization.</summary>
    public CommissionDetail()
    {
    }

    public CommissionDetail(Guid salespersonId, string sourceType, Guid documentId, string documentNumber, DateOnly documentDate, decimal basisAmount,
        decimal ratePercent, decimal commission)
    {
        SalespersonId = salespersonId;
        SourceType = sourceType;
        DocumentId = documentId;
        DocumentNumber = documentNumber;
        DocumentDate = documentDate;
        BasisAmount = basisAmount;
        RatePercent = ratePercent;
        Commission = commission;
    }

    public Guid SalespersonId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public Guid DocumentId { get; init; }

    public string DocumentNumber { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public decimal BasisAmount { get; init; }

    public decimal RatePercent { get; init; }

    public decimal Commission { get; init; }
}

/// <summary>
/// Monthly commission calculation for a legal entity: Draft (recalculable) → Approved → Posted (journal Dr commission expense /
/// Cr commissions payable per salesperson) → Reversed. One active run per legal entity and month.
/// </summary>
[Audited]
public sealed class CommissionRun : AggregateRoot, ICompanyScoped
{
    private CommissionRun()
    {
    }

    private CommissionRun(Guid id, Guid companyId, Guid legalEntityId, DateOnly periodFrom)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        PeriodFrom = periodFrom;
        PeriodTo = periodFrom.AddMonths(1).AddDays(-1);
        Status = CommissionRunStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public CommissionRunStatus Status { get; private set; }

    public string? Number { get; private set; }

    public decimal Total { get; private set; }

    public List<CommissionRunLine> Lines { get; private set; } = [];

    public List<CommissionDetail> Details { get; private set; } = [];

    /// <summary>Salespersons with sales in the period but no (active) commission plan — listed so the omission is visible.</summary>
    public List<Guid> SkippedSalespersons { get; private set; } = [];

    public DateTime CalculatedAtUtc { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTime? ApprovedAtUtc { get; private set; }

    public Guid? JournalId { get; private set; }

    public DateTime? PostedAtUtc { get; private set; }

    public DateOnly? ReversalDate { get; private set; }

    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    public static CommissionRun Create(Guid companyId, Guid legalEntityId, DateOnly period) =>
        new(SequentialGuid.New(), companyId, legalEntityId, MonthStart(period));

    public void SetCalculation(IReadOnlyList<CommissionRunLine> lines, IReadOnlyList<CommissionDetail> details, IReadOnlyList<Guid> skipped, DateTime nowUtc)
    {
        if (Status != CommissionRunStatus.Draft)
        {
            throw new DomainException(PeopleErrors.InvalidTransition(Status.ToString(), "recalculated"));
        }

        Lines = [.. lines];
        Details = [.. details];
        SkippedSalespersons = [.. skipped];
        Total = Lines.Sum(l => l.Commission);
        CalculatedAtUtc = nowUtc;
    }

    public void Approve(Guid? userId, DateTime nowUtc)
    {
        if (Status != CommissionRunStatus.Draft)
        {
            throw new DomainException(PeopleErrors.InvalidTransition(Status.ToString(), "approved"));
        }

        if (Lines.All(l => l.Commission == 0))
        {
            throw new DomainException(PeopleErrors.NoLines);
        }

        Status = CommissionRunStatus.Approved;
        ApprovedBy = userId;
        ApprovedAtUtc = nowUtc;
    }

    public void MarkPosted(string number, Guid journalId, DateTime nowUtc)
    {
        if (Status != CommissionRunStatus.Approved)
        {
            throw new DomainException(PeopleErrors.InvalidTransition(Status.ToString(), "posted"));
        }

        Number = number;
        JournalId = journalId;
        PostedAtUtc = nowUtc;
        Status = CommissionRunStatus.Posted;
    }

    public void MarkReversed(DateOnly date)
    {
        if (Status != CommissionRunStatus.Posted)
        {
            throw new DomainException(PeopleErrors.InvalidTransition(Status.ToString(), "reversed"));
        }

        ReversalDate = date;
        Status = CommissionRunStatus.Reversed;
    }

    public void Cancel()
    {
        if (Status is not (CommissionRunStatus.Draft or CommissionRunStatus.Approved))
        {
            throw new DomainException(PeopleErrors.InvalidTransition(Status.ToString(), "cancelled"));
        }

        Status = CommissionRunStatus.Cancelled;
    }
}

/// <summary>A commissionable line: an invoice or receipt line (positive) or a credit-note or refund line (negative), functional currency.</summary>
public sealed record CommissionSource(
    Guid SalespersonId,
    string SourceType,
    Guid DocumentId,
    string DocumentNumber,
    DateOnly DocumentDate,
    Guid? CategoryId,
    decimal Net,
    decimal Cost,
    bool IsReturn);

/// <summary>Pure commission arithmetic (unit-tested). Commission is rounded per document and rate; a run line is the sum of its details.</summary>
public static class CommissionCalculator
{
    public static (IReadOnlyList<CommissionRunLine> Lines, IReadOnlyList<CommissionDetail> Details, IReadOnlyList<Guid> Skipped) Calculate(
        IEnumerable<CommissionSource> sources, Func<Guid, CommissionPlan?> planFor)
    {
        var lines = new List<CommissionRunLine>();
        var details = new List<CommissionDetail>();
        var skipped = new List<Guid>();
        foreach (var person in sources.GroupBy(s => s.SalespersonId).OrderBy(g => g.Key))
        {
            var plan = planFor(person.Key);
            if (plan is null)
            {
                skipped.Add(person.Key);
                continue;
            }

            var rows = person.Where(s => plan.ClawbackOnReturns || !s.IsReturn).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            decimal Basis(CommissionSource s) => plan.Basis == CommissionBasis.NetSales ? s.Net : s.Net - s.Cost;
            var total = rows.Sum(Basis);
            var tierRate = plan.RateFor(total);
            var personDetails = rows
                .GroupBy(s => new { s.SourceType, s.DocumentId, s.DocumentNumber, s.DocumentDate, Rate = plan.CategoryRateFor(s.CategoryId) ?? tierRate })
                .OrderBy(g => g.Key.DocumentDate).ThenBy(g => g.Key.DocumentNumber, StringComparer.Ordinal).ThenBy(g => g.Key.Rate)
                .Select(g =>
                {
                    var basis = g.Sum(Basis);
                    return new CommissionDetail(person.Key, g.Key.SourceType, g.Key.DocumentId, g.Key.DocumentNumber, g.Key.DocumentDate, basis, g.Key.Rate,
                        Math.Round(basis * g.Key.Rate / 100m, 2, MidpointRounding.AwayFromZero));
                })
                .ToList();
            details.AddRange(personDetails);
            lines.Add(new CommissionRunLine(person.Key, plan.Id, plan.Code, plan.Basis, total, tierRate, personDetails.Sum(d => d.Commission)));
        }

        return (lines, details, skipped);
    }
}
