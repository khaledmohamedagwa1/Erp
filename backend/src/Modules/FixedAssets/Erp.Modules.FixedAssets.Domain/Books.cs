using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.FixedAssets.Domain;

/// <summary>
/// An additional depreciation book (e.g. TAX, IFRS). The asset's own figures are the posting book — the only one that posts
/// journals. Additional books are reporting-only: their depreciation is a schedule, read as of a date, never posted.
/// </summary>
[Audited]
public sealed class DepreciationBook : AggregateRoot, ICompanyScoped
{
    private DepreciationBook()
    {
    }

    private DepreciationBook(Guid id, Guid companyId, string code)
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

    public bool IsActive { get; private set; }

    public static DepreciationBook Create(Guid companyId, string code, string nameEn, string nameAr)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length is 0 or > 30)
        {
            throw new DomainException(Error.Validation("assets.book-code-invalid", "Book code is required (max 30 characters)."));
        }

        var book = new DepreciationBook(SequentialGuid.New(), companyId, normalized);
        book.Update(nameEn, nameAr, true);
        return book;
    }

    public void Update(string nameEn, string nameAr, bool isActive)
    {
        NameEn = Guard.NotEmpty(nameEn, "assets.name-required", "English name");
        NameAr = Guard.NotEmpty(nameAr, "assets.name-required", "Arabic name");
        IsActive = isActive;
    }
}

public sealed class BookLine
{
    public DateOnly Period { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>A dated change of a book's cost and accumulated depreciation that is not monthly depreciation (acquisition, transfer, partial disposal).</summary>
public sealed class BookAdjustment
{
    public DateOnly Date { get; set; }

    public string Kind { get; set; } = string.Empty;

    public decimal CostDelta { get; set; }

    public decimal AccumulatedDelta { get; set; }
}

/// <summary>The asset's parameters and schedule in one additional (reporting-only) book.</summary>
public sealed class AssetBook
{
    public Guid BookId { get; set; }

    public DepreciationMethod Method { get; set; }

    public int UsefulLifeMonths { get; set; }

    public decimal SalvageValue { get; set; }

    public decimal? DecliningRatePercent { get; set; }

    /// <summary>Current book cost (after partial disposals).</summary>
    public decimal Cost { get; set; }

    /// <summary>First month of this book's life (the useful life counts from here).</summary>
    public DateOnly StartMonth { get; set; }

    public List<BookLine> Schedule { get; set; } = [];

    public List<BookAdjustment> Adjustments { get; set; } = [];

    public decimal CostAsOf(DateOnly asOf) => Adjustments.Where(a => a.Date <= asOf).Sum(a => a.CostDelta);

    /// <summary>Accumulated depreciation as of a date: schedule months up to the date's month plus dated adjustments.</summary>
    public decimal AccumulatedAsOf(DateOnly asOf) =>
        Schedule.Where(l => l.Period <= AssetMath.MonthStart(asOf)).Sum(l => l.Amount) + Adjustments.Where(a => a.Date <= asOf).Sum(a => a.AccumulatedDelta);

    /// <summary>Accumulated depreciation before the month of <paramref name="date"/> (the month an event takes effect in).</summary>
    public decimal AccumulatedBeforeMonthOf(DateOnly date)
    {
        var month = AssetMath.MonthStart(date);
        return Schedule.Where(l => l.Period < month).Sum(l => l.Amount) + Adjustments.Sum(a => a.AccumulatedDelta);
    }

    /// <summary>Rebuilds the schedule from <paramref name="fromMonth"/> on; earlier months never change.</summary>
    public void RegenerateFrom(DateOnly fromMonth)
    {
        var month = AssetMath.MonthStart(fromMonth);
        var accumulated = AccumulatedBeforeMonthOf(month);
        var elapsed = Math.Max(0, ((month.Year - StartMonth.Year) * 12) + month.Month - StartMonth.Month);
        var planned = DepreciationCalculator.Build(new ScheduleInput(Method, Cost, SalvageValue, accumulated, month, UsefulLifeMonths - elapsed, DecliningRatePercent));
        Schedule = Schedule.Where(l => l.Period < month).Concat(planned.Select(p => new BookLine { Period = p.Period, Amount = p.Amount })).ToList();
    }

    public void DropFrom(DateOnly date)
    {
        var month = AssetMath.MonthStart(date);
        Schedule = Schedule.Where(l => l.Period < month).ToList();
    }

    public static AssetBook Create(Guid bookId, DepreciationMethod method, int usefulLifeMonths, decimal salvage, decimal? rate, decimal cost,
        decimal openingAccumulated, DateOnly startMonth, DateOnly costDate, string kind)
    {
        if (method == DepreciationMethod.UnitsOfProduction)
        {
            throw new DomainException(Error.Validation("assets.book-method-invalid", "Additional books support StraightLine and DecliningBalance only."));
        }

        if (usefulLifeMonths is < 1 or > 1200)
        {
            throw new DomainException(AssetErrors.LifeInvalid);
        }

        if (method == DepreciationMethod.DecliningBalance && rate is not (> 0 and <= 100))
        {
            throw new DomainException(AssetErrors.RateInvalid);
        }

        if (cost <= 0 || salvage < 0 || salvage >= cost || openingAccumulated < 0 || openingAccumulated > cost - salvage)
        {
            throw new DomainException(AssetErrors.SalvageInvalid with { Message = "Book cost, salvage and opening accumulated depreciation are inconsistent." });
        }

        var book = new AssetBook
        {
            BookId = bookId, Method = method, UsefulLifeMonths = usefulLifeMonths, SalvageValue = AssetMath.Money(salvage),
            DecliningRatePercent = method == DepreciationMethod.DecliningBalance ? rate : null, Cost = AssetMath.Money(cost), StartMonth = AssetMath.MonthStart(startMonth),
            Adjustments = [new BookAdjustment { Date = costDate, Kind = kind, CostDelta = AssetMath.Money(cost), AccumulatedDelta = AssetMath.Money(openingAccumulated) }],
        };
        book.RegenerateFrom(book.StartMonth);
        return book;
    }
}

/// <summary>Per-company fixed-asset settings: automatic monthly depreciation.</summary>
[Audited]
public sealed class AssetSettings : AggregateRoot, ICompanyScoped
{
    private AssetSettings()
    {
    }

    private AssetSettings(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
        RunOnDay = 1;
    }

    public Guid CompanyId { get; private set; }

    /// <summary>When on, the worker posts last month's depreciation for every legal entity once <see cref="RunOnDay"/> is reached.</summary>
    public bool AutoDepreciationEnabled { get; private set; }

    /// <summary>Day of the month (1–28) from which last month's run is posted.</summary>
    public int RunOnDay { get; private set; }

    public DateTime? LastRunAtUtc { get; private set; }

    public string? LastRunSummary { get; private set; }

    public static AssetSettings Create(Guid companyId) => new(SequentialGuid.New(), companyId);

    public void Update(bool autoDepreciationEnabled, int runOnDay)
    {
        if (runOnDay is < 1 or > 28)
        {
            throw new DomainException(Error.Validation("assets.run-day-invalid", "The run day must be between 1 and 28."));
        }

        AutoDepreciationEnabled = autoDepreciationEnabled;
        RunOnDay = runOnDay;
    }

    /// <summary>The period whose depreciation is due on <paramref name="today"/> (last month once the run day is reached), or null.</summary>
    public DateOnly? DuePeriod(DateOnly today) =>
        AutoDepreciationEnabled && today.Day >= RunOnDay ? AssetMath.MonthStart(today).AddMonths(-1) : null;

    public void RecordRun(DateTime nowUtc, string summary)
    {
        LastRunAtUtc = nowUtc;
        LastRunSummary = summary.Length > 2000 ? summary[..2000] : summary;
    }
}
