using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.FixedAssets.Domain;

public enum AssetStatus
{
    Draft = 1,
    Active = 2,
    FullyDepreciated = 3,
    Disposed = 4,
}

public enum AcquisitionMode
{
    /// <summary>The module posts Dr FixedAssetCost / Cr a chosen clearing account.</summary>
    ManualJournal = 1,

    /// <summary>The cost was already debited by a posted purchase invoice line; the asset only links to it (no journal).</summary>
    PurchaseInvoice = 2,

    /// <summary>Received from another legal entity of the company (cost and accumulated depreciation carried over).</summary>
    Transfer = 3,
}

[Audited]
public sealed class AssetCategory : AggregateRoot, ICompanyScoped
{
    private AssetCategory()
    {
    }

    private AssetCategory(Guid id, Guid companyId, string code)
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

    public DepreciationMethod DefaultMethod { get; private set; }

    public int UsefulLifeMonths { get; private set; }

    public decimal SalvagePercent { get; private set; }

    public decimal? DecliningRatePercent { get; private set; }

    /// <summary>Product posting group used as posting-rule context, so each category can map to its own accounts.</summary>
    public string? PostingGroup { get; private set; }

    public bool IsActive { get; private set; }

    public static AssetCategory Create(Guid companyId, string code, string nameEn, string nameAr, DepreciationMethod method, int usefulLifeMonths,
        decimal salvagePercent, decimal? decliningRatePercent, string? postingGroup)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length is 0 or > 30)
        {
            throw new DomainException(AssetErrors.CategoryCodeInvalid);
        }

        var category = new AssetCategory(SequentialGuid.New(), companyId, normalized);
        category.Update(nameEn, nameAr, method, usefulLifeMonths, salvagePercent, decliningRatePercent, postingGroup, true);
        return category;
    }

    public void Update(string nameEn, string nameAr, DepreciationMethod method, int usefulLifeMonths, decimal salvagePercent, decimal? decliningRatePercent,
        string? postingGroup, bool isActive)
    {
        if (usefulLifeMonths is < 1 or > 1200)
        {
            throw new DomainException(AssetErrors.LifeInvalid);
        }

        if (salvagePercent is < 0 or >= 100)
        {
            throw new DomainException(AssetErrors.SalvageInvalid);
        }

        if (method == DepreciationMethod.DecliningBalance && decliningRatePercent is not (> 0 and <= 100))
        {
            throw new DomainException(AssetErrors.RateInvalid);
        }

        NameEn = Guard.NotEmpty(nameEn, "assets.name-required", "English name");
        NameAr = Guard.NotEmpty(nameAr, "assets.name-required", "Arabic name");
        DefaultMethod = method;
        UsefulLifeMonths = usefulLifeMonths;
        SalvagePercent = salvagePercent;
        DecliningRatePercent = method == DepreciationMethod.DecliningBalance ? decliningRatePercent : null;
        PostingGroup = string.IsNullOrWhiteSpace(postingGroup) ? null : postingGroup.Trim().ToUpperInvariant();
        IsActive = isActive;
    }
}

/// <summary>One month of planned depreciation. Posted once a depreciation run picks it up.</summary>
public sealed class ScheduleLine
{
    public DateOnly Period { get; set; }

    public decimal Amount { get; set; }

    public decimal? Units { get; set; }

    public Guid? RunId { get; set; }

    public bool IsPosted => RunId is not null;
}

public sealed class AssetEvent
{
    public DateTime AtUtc { get; set; }

    public DateOnly Date { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Monetary effect of the event (cost for acquisitions, NBV delta for revaluations, proceeds for disposals, amount for depreciation).</summary>
    public decimal? Amount { get; set; }

    public Guid? JournalId { get; set; }

    public Guid? ReferenceId { get; set; }

    /// <summary>Change of the carrying cost (posting book). Null on events written before this field existed: see <see cref="AssetEventKinds.CostEffect"/>.</summary>
    public decimal? CostDelta { get; set; }

    /// <summary>Change of accumulated depreciation that is not a depreciation run (carried over on transfer, removed on partial disposal).</summary>
    public decimal? AccumulatedDelta { get; set; }

    /// <summary>Change of the asset's revaluation reserve (IAS 16).</summary>
    public decimal? ReserveDelta { get; set; }
}

/// <summary>A revaluation or impairment split into its reserve and profit-or-loss parts (positive = increase / income).</summary>
public sealed record ValueChangePlan(decimal Delta, decimal ReserveDelta, decimal ProfitDelta);

/// <summary>The figures removed from the posting book by a partial disposal.</summary>
public sealed record PartialDisposalPlan(decimal Fraction, decimal Cost, decimal Accumulated, decimal Reserve, decimal Proceeds, decimal GainLoss, decimal? Quantity);

public static class AssetEventKinds
{
    public const string Acquired = "Acquired";
    public const string Transferred = "Transferred";
    public const string Revalued = "Revalued";
    public const string Depreciated = "Depreciated";
    public const string DepreciationReversed = "DepreciationReversed";
    public const string UsageRecorded = "UsageRecorded";
    public const string Disposed = "Disposed";
    public const string Impaired = "Impaired";
    public const string PartiallyDisposed = "PartiallyDisposed";
    public const string TransferredOut = "TransferredOut";
    public const string TransferredIn = "TransferredIn";

    /// <summary>Cost effect of an event; events written before <see cref="AssetEvent.CostDelta"/> existed carry it in <see cref="AssetEvent.Amount"/>.</summary>
    public static decimal CostEffect(AssetEvent e) =>
        e.CostDelta ?? (e.Kind is Acquired or Revalued ? e.Amount ?? 0 : 0);
}

[Audited]
public sealed class Asset : AggregateRoot, IBranchScoped
{
    private Asset()
    {
    }

    private Asset(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
        Status = AssetStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    /// <summary>Assigned when the asset is activated (gapless per company).</summary>
    public string? Number { get; private set; }

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public Guid CategoryId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? BranchId { get; private set; }

    public string? Location { get; private set; }

    public Guid? CustodianId { get; private set; }

    public DateOnly AcquisitionDate { get; private set; }

    public DateOnly InServiceDate { get; private set; }

    public decimal AcquisitionCost { get; private set; }

    /// <summary>Carrying cost: acquisition cost plus revaluation adjustments.</summary>
    public decimal Cost { get; private set; }

    public decimal SalvageValue { get; private set; }

    public int UsefulLifeMonths { get; private set; }

    public DepreciationMethod Method { get; private set; }

    public decimal? DecliningRatePercent { get; private set; }

    public decimal? TotalUnits { get; private set; }

    public AssetStatus Status { get; private set; }

    public decimal AccumulatedDepreciation { get; private set; }

    public string? PostingGroup { get; private set; }

    public AcquisitionMode? AcquisitionMode { get; private set; }

    public Guid? AcquisitionJournalId { get; private set; }

    public string? SourceDocType { get; private set; }

    public Guid? SourceDocId { get; private set; }

    public Guid? SourceLineId { get; private set; }

    public DateOnly? DisposalDate { get; private set; }

    public decimal? DisposalProceeds { get; private set; }

    public decimal? DisposalGainLoss { get; private set; }

    public Guid? DisposalJournalId { get; private set; }

    /// <summary>Number of units the asset record covers (partial disposals by quantity reduce it).</summary>
    public decimal Quantity { get; private set; } = 1;

    /// <summary>IAS 16 revaluation surplus attributable to this asset (credit balance in AssetRevaluationReserve).</summary>
    public decimal RevaluationReserve { get; private set; }

    /// <summary>Revaluation decreases and impairments recognised in profit or loss that a later increase reverses first.</summary>
    public decimal RevaluationLossInProfit { get; private set; }

    public List<ScheduleLine> Schedule { get; private set; } = [];

    public List<AssetEvent> History { get; private set; } = [];

    /// <summary>Additional (reporting-only) depreciation books.</summary>
    public List<AssetBook> Books { get; private set; } = [];

    public decimal NetBookValue => Cost - AccumulatedDepreciation;

    public DateOnly FirstDepreciationMonth => AssetMath.FirstDepreciationMonth(InServiceDate);

    public static Asset CreateDraft(Guid companyId, AssetCategory category, Guid legalEntityId, string nameEn, string nameAr, Guid? branchId, string? location,
        Guid? custodianId, DateOnly acquisitionDate, DateOnly inServiceDate, decimal acquisitionCost, decimal? salvageValue, int? usefulLifeMonths,
        DepreciationMethod? method, decimal? decliningRatePercent, decimal? totalUnits, decimal? quantity = null)
    {
        var asset = new Asset(SequentialGuid.New(), companyId) { LegalEntityId = legalEntityId };
        asset.Update(category, nameEn, nameAr, branchId, location, custodianId, acquisitionDate, inServiceDate, acquisitionCost, salvageValue,
            usefulLifeMonths, method, decliningRatePercent, totalUnits, quantity);
        return asset;
    }

    /// <summary>Changes a draft. Missing life, method, rate and salvage default from the category.</summary>
    public void Update(AssetCategory category, string nameEn, string nameAr, Guid? branchId, string? location, Guid? custodianId, DateOnly acquisitionDate,
        DateOnly inServiceDate, decimal acquisitionCost, decimal? salvageValue, int? usefulLifeMonths, DepreciationMethod? method,
        decimal? decliningRatePercent, decimal? totalUnits, decimal? quantity = null)
    {
        EnsureDraft();
        if (quantity is <= 0)
        {
            throw new DomainException(Error.Validation("assets.quantity-invalid", "Quantity must be greater than zero."));
        }

        Quantity = quantity ?? 1;
        var effectiveMethod = method ?? category.DefaultMethod;
        var life = usefulLifeMonths ?? category.UsefulLifeMonths;
        var salvage = salvageValue ?? AssetMath.Money(acquisitionCost * category.SalvagePercent / 100m);
        var rate = effectiveMethod == DepreciationMethod.DecliningBalance ? decliningRatePercent ?? category.DecliningRatePercent : null;
        if (acquisitionCost <= 0)
        {
            throw new DomainException(AssetErrors.CostInvalid);
        }

        if (salvage < 0 || salvage >= acquisitionCost)
        {
            throw new DomainException(AssetErrors.SalvageInvalid);
        }

        if (life is < 1 or > 1200)
        {
            throw new DomainException(AssetErrors.LifeInvalid);
        }

        if (effectiveMethod == DepreciationMethod.DecliningBalance && rate is not (> 0 and <= 100))
        {
            throw new DomainException(AssetErrors.RateInvalid);
        }

        if (effectiveMethod == DepreciationMethod.UnitsOfProduction && totalUnits is not > 0)
        {
            throw new DomainException(AssetErrors.UnitsInvalid);
        }

        if (inServiceDate < acquisitionDate)
        {
            throw new DomainException(AssetErrors.DatesInvalid);
        }

        CategoryId = category.Id;
        PostingGroup = category.PostingGroup;
        NameEn = Guard.NotEmpty(nameEn, "assets.name-required", "English name");
        NameAr = Guard.NotEmpty(nameAr, "assets.name-required", "Arabic name");
        BranchId = branchId;
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        CustodianId = custodianId;
        AcquisitionDate = acquisitionDate;
        InServiceDate = inServiceDate;
        AcquisitionCost = AssetMath.Money(acquisitionCost);
        Cost = AcquisitionCost;
        SalvageValue = AssetMath.Money(salvage);
        UsefulLifeMonths = life;
        Method = effectiveMethod;
        DecliningRatePercent = rate;
        TotalUnits = effectiveMethod == DepreciationMethod.UnitsOfProduction ? totalUnits : null;
    }

    public void Activate(string number, AcquisitionMode mode, Guid? journalId, string? sourceDocType, Guid? sourceDocId, Guid? sourceLineId, DateTime nowUtc)
    {
        EnsureDraft();
        Number = number;
        AcquisitionMode = mode;
        AcquisitionJournalId = journalId;
        SourceDocType = sourceDocType;
        SourceDocId = sourceDocId;
        SourceLineId = sourceLineId;
        Status = AssetStatus.Active;
        RegenerateSchedule();
        AddEvent(nowUtc, AcquisitionDate, AssetEventKinds.Acquired,
            mode == Domain.AcquisitionMode.PurchaseInvoice ? $"Acquired via {sourceDocType} {sourceDocId}" : "Acquired (manual journal)", AcquisitionCost, journalId, null,
            costDelta: AcquisitionCost);
    }

    /// <summary>
    /// Creates the receiving asset of a transfer between legal entities: same cost, accumulated depreciation, salvage and method, with the
    /// remaining useful life, depreciating from the month of the transfer (the sender depreciated up to the month before).
    /// Additional books are carried over with their values at the transfer date.
    /// </summary>
    public static Asset CreateTransferredIn(Guid id, Asset source, Guid legalEntityId, Guid? branchId, DateOnly date, string number, Guid journalId, DateTime nowUtc)
    {
        var month = AssetMath.MonthStart(date);
        var postedMonths = source.Schedule.Count(s => s.IsPosted);
        var asset = new Asset(id, source.CompanyId)
        {
            LegalEntityId = legalEntityId,
            Number = number,
            NameEn = source.NameEn,
            NameAr = source.NameAr,
            CategoryId = source.CategoryId,
            PostingGroup = source.PostingGroup,
            BranchId = branchId,
            Location = source.Location,
            CustodianId = source.CustodianId,
            AcquisitionDate = date,
            InServiceDate = month,
            AcquisitionCost = source.Cost,
            Cost = source.Cost,
            SalvageValue = source.SalvageValue,
            AccumulatedDepreciation = source.AccumulatedDepreciation,
            UsefulLifeMonths = source.Method == DepreciationMethod.UnitsOfProduction ? source.UsefulLifeMonths : Math.Max(1, source.UsefulLifeMonths - postedMonths),
            Method = source.Method,
            DecliningRatePercent = source.DecliningRatePercent,
            TotalUnits = source.TotalUnits,
            Quantity = source.Quantity,
            Status = AssetStatus.Active,
            AcquisitionMode = Domain.AcquisitionMode.Transfer,
            AcquisitionJournalId = journalId,
            SourceDocType = "AssetTransfer",
            SourceDocId = source.Id,
        };
        asset.Books = source.Books.Select(b => AssetBook.Create(b.BookId, b.Method,
                Math.Max(1, b.UsefulLifeMonths - Math.Max(0, ((month.Year - b.StartMonth.Year) * 12) + month.Month - b.StartMonth.Month)), b.SalvageValue,
                b.DecliningRatePercent, b.CostAsOf(date), b.AccumulatedBeforeMonthOf(date), month, date, AssetEventKinds.TransferredIn))
            .ToList();
        asset.RegenerateSchedule();
        asset.AddEvent(nowUtc, date, AssetEventKinds.TransferredIn, $"Received from {source.Number} (legal entity {source.LegalEntityId})", asset.Cost, journalId, source.Id,
            costDelta: asset.Cost, accumulatedDelta: asset.AccumulatedDepreciation);
        return asset;
    }

    /// <summary>Hands the asset to another legal entity at net book value (no gain or loss). The asset leaves this register like a disposal.</summary>
    public void TransferOut(DateOnly date, Guid journalId, Guid targetAssetId, string targetNumber, DateTime nowUtc)
    {
        DisposalResult(date, NetBookValue);
        if (RevaluationReserve != 0)
        {
            throw new DomainException(Error.Conflict("assets.transfer-with-reserve",
                "Assets with a revaluation reserve cannot be transferred between legal entities; transfer the reserve to retained earnings first by disposal."));
        }

        Schedule = Schedule.Where(s => s.IsPosted).ToList();
        foreach (var book in Books)
        {
            book.DropFrom(date);
        }

        DisposalDate = date;
        DisposalProceeds = NetBookValue;
        DisposalGainLoss = 0;
        DisposalJournalId = journalId;
        Status = AssetStatus.Disposed;
        AddEvent(nowUtc, date, AssetEventKinds.TransferredOut, $"Transferred to {targetNumber} at net book value", NetBookValue, journalId, targetAssetId);
    }

    /// <summary>Adds or replaces an additional (reporting-only) book. A book that has partial-disposal adjustments cannot be redefined.</summary>
    public void SetBook(Guid bookId, DepreciationMethod method, int usefulLifeMonths, decimal? salvage, decimal? rate, decimal? cost, decimal? openingAccumulated)
    {
        EnsureNotDisposed();
        var existing = Books.FirstOrDefault(b => b.BookId == bookId);
        if (existing is not null && existing.Adjustments.Any(a => a.Kind == AssetEventKinds.PartiallyDisposed))
        {
            throw new DomainException(Error.Conflict("assets.book-locked", "This book has partial disposals and can no longer be redefined."));
        }

        var bookCost = cost ?? AcquisitionCost;
        var book = AssetBook.Create(bookId, method, usefulLifeMonths, salvage ?? 0, rate, bookCost, openingAccumulated ?? 0, FirstDepreciationMonth, AcquisitionDate,
            AssetEventKinds.Acquired);
        Books = Books.Where(b => b.BookId != bookId).Append(book).ToList();
    }

    public void RemoveBook(Guid bookId) => Books = Books.Where(b => b.BookId != bookId).ToList();

    /// <summary>
    /// Rebuilds the unposted part of the schedule from the current cost, salvage and accumulated depreciation over the
    /// remaining useful life. Posted lines never change. Units-of-production schedules are driven by recorded usage.
    /// </summary>
    public void RegenerateSchedule()
    {
        if (Method == DepreciationMethod.UnitsOfProduction)
        {
            return;
        }

        var posted = Schedule.Where(s => s.IsPosted).OrderBy(s => s.Period).ToList();
        var first = posted.Count == 0 ? FirstDepreciationMonth : posted[^1].Period.AddMonths(1);
        var remainingMonths = UsefulLifeMonths - posted.Count;
        var planned = DepreciationCalculator.Build(new ScheduleInput(Method, Cost, SalvageValue, AccumulatedDepreciation, first, remainingMonths, DecliningRatePercent));
        Schedule = posted.Concat(planned.Select(p => new ScheduleLine { Period = p.Period, Amount = p.Amount })).ToList();
        RefreshStatus();
    }

    public void RecordUsage(DateOnly period, decimal units, DateTime nowUtc)
    {
        EnsureInService();
        if (Method != DepreciationMethod.UnitsOfProduction)
        {
            throw new DomainException(AssetErrors.NotUnitsOfProduction);
        }

        var month = AssetMath.MonthStart(period);
        if (month < FirstDepreciationMonth)
        {
            throw new DomainException(AssetErrors.PeriodBeforeService);
        }

        var existing = Schedule.FirstOrDefault(s => s.Period == month);
        if (existing is { IsPosted: true })
        {
            throw new DomainException(AssetErrors.PeriodPosted);
        }

        if (existing is not null)
        {
            Schedule.Remove(existing);
        }

        var stillDepreciable = Cost - SalvageValue - AccumulatedDepreciation - Schedule.Where(s => !s.IsPosted).Sum(s => s.Amount);
        var amount = DepreciationCalculator.UnitsOfProduction(Cost, SalvageValue, TotalUnits ?? 0, units, stillDepreciable);
        Schedule.Add(new ScheduleLine { Period = month, Amount = amount, Units = units });
        Schedule = Schedule.OrderBy(s => s.Period).ToList();
        AddEvent(nowUtc, AssetMath.MonthEnd(month), AssetEventKinds.UsageRecorded, $"{units} units in {month:yyyy-MM}", amount, null, null);
    }

    /// <summary>Unposted lines up to and including <paramref name="period"/> (catch-up included).</summary>
    public IReadOnlyList<ScheduleLine> DueLines(DateOnly period) =>
        Status == AssetStatus.Active ? Schedule.Where(s => !s.IsPosted && s.Amount > 0 && s.Period <= AssetMath.MonthStart(period)).ToList() : [];

    public decimal PostDepreciation(DateOnly period, Guid runId, Guid journalId, DateTime nowUtc)
    {
        var due = DueLines(period);
        var amount = due.Sum(l => l.Amount);
        foreach (var line in due)
        {
            line.RunId = runId;
        }

        AccumulatedDepreciation += amount;
        RefreshStatus();
        if (amount > 0)
        {
            AddEvent(nowUtc, AssetMath.MonthEnd(period), AssetEventKinds.Depreciated, $"Depreciation to {AssetMath.MonthStart(period):yyyy-MM}", amount, journalId, runId);
        }

        return amount;
    }

    public decimal ReverseDepreciation(Guid runId, DateOnly reversalDate, Guid? journalId, DateTime nowUtc)
    {
        var lines = Schedule.Where(s => s.RunId == runId).ToList();
        var amount = lines.Sum(l => l.Amount);
        foreach (var line in lines)
        {
            line.RunId = null;
        }

        AccumulatedDepreciation -= amount;
        if (Status == AssetStatus.FullyDepreciated)
        {
            Status = AssetStatus.Active;
        }

        if (amount > 0)
        {
            AddEvent(nowUtc, reversalDate, AssetEventKinds.DepreciationReversed, "Depreciation run reversed", amount, journalId, runId);
        }

        return amount;
    }

    public void Transfer(DateOnly date, Guid? branchId, string? location, Guid? custodianId, string? note, DateTime nowUtc)
    {
        EnsureInService();
        var description = $"Branch {BranchId?.ToString() ?? "-"} → {branchId?.ToString() ?? "-"}; location {Location ?? "-"} → {location ?? "-"}; custodian {CustodianId?.ToString() ?? "-"} → {custodianId?.ToString() ?? "-"}";
        BranchId = branchId;
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        CustodianId = custodianId;
        AddEvent(nowUtc, date, AssetEventKinds.Transferred, string.IsNullOrWhiteSpace(note) ? description : $"{note.Trim()} ({description})", null, null, null);
    }

    /// <summary>
    /// IAS 16 split of a revaluation to <paramref name="newNetBookValue"/>: an increase first reverses earlier decreases recognised in profit or
    /// loss, the rest goes to the revaluation reserve; a decrease first uses up the asset's reserve, the rest is an expense.
    /// </summary>
    public ValueChangePlan PlanValueChange(decimal newNetBookValue, int? newUsefulLifeMonths, bool impairment = false)
    {
        decimal delta;
        if (impairment)
        {
            EnsureInService();
            var value = AssetMath.Money(newNetBookValue);
            if (value < 0 || value >= NetBookValue)
            {
                throw new DomainException(Error.Validation("assets.impairment-invalid", "The recoverable amount must be below the net book value and not negative."));
            }

            delta = value - NetBookValue;
        }
        else
        {
            delta = ValidateRevaluation(newNetBookValue, newUsefulLifeMonths);
        }

        if (delta >= 0)
        {
            var reversal = Math.Min(delta, RevaluationLossInProfit);
            return new ValueChangePlan(delta, delta - reversal, reversal);
        }

        var fromReserve = Math.Min(-delta, Math.Max(0, RevaluationReserve));
        return new ValueChangePlan(delta, -fromReserve, delta + fromReserve);
    }

    /// <returns>The change in net book value (positive = increase), already applied to <see cref="Cost"/>.</returns>
    public decimal Revalue(DateOnly date, decimal newNetBookValue, int? newUsefulLifeMonths, Guid journalId, Guid eventId, DateTime nowUtc,
        ValueChangePlan? plan = null)
    {
        EnsureInService();
        var delta = ValidateRevaluation(newNetBookValue, newUsefulLifeMonths);
        Cost += delta;
        if (newUsefulLifeMonths is { } life)
        {
            UsefulLifeMonths = life;
        }

        TrackReserve(plan);
        if (Status == AssetStatus.FullyDepreciated && NetBookValue > SalvageValue)
        {
            Status = AssetStatus.Active;
        }

        RegenerateSchedule();
        AddEvent(nowUtc, date, AssetEventKinds.Revalued, $"Net book value revalued to {newNetBookValue:0.00}", delta, journalId, eventId,
            costDelta: delta, reserveDelta: plan?.ReserveDelta);
        return delta;
    }

    /// <summary>Writes the asset down to its recoverable amount (IAS 36); the remaining life is unchanged and the schedule is rebuilt.</summary>
    public void Impair(DateOnly date, decimal recoverableAmount, ValueChangePlan plan, Guid journalId, Guid eventId, DateTime nowUtc)
    {
        Cost += plan.Delta;
        if (SalvageValue > NetBookValue)
        {
            SalvageValue = NetBookValue;
        }

        TrackReserve(plan);
        RegenerateSchedule();
        AddEvent(nowUtc, date, AssetEventKinds.Impaired, $"Impaired to recoverable amount {recoverableAmount:0.00}", plan.Delta, journalId, eventId,
            costDelta: plan.Delta, reserveDelta: plan.ReserveDelta);
    }

    private void TrackReserve(ValueChangePlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        RevaluationReserve += plan.ReserveDelta;
        RevaluationLossInProfit -= plan.ProfitDelta;
    }

    /// <summary>Checks and computes a partial disposal of <paramref name="fraction"/> of the asset (0 &lt; fraction &lt; 1).</summary>
    public PartialDisposalPlan PlanPartialDisposal(DateOnly date, decimal fraction, decimal proceeds)
    {
        DisposalResult(date, proceeds);
        if (fraction is <= 0 or >= 1)
        {
            throw new DomainException(Error.Validation("assets.fraction-invalid", "A partial disposal must remove more than 0% and less than 100% of the asset."));
        }

        var cost = AssetMath.Money(Cost * fraction);
        var accumulated = AssetMath.Money(AccumulatedDepreciation * fraction);
        var reserve = AssetMath.Money(RevaluationReserve * fraction);
        var money = AssetMath.Money(proceeds);
        return new PartialDisposalPlan(fraction, cost, accumulated, reserve, money, money - (cost - accumulated), Quantity * fraction);
    }

    public void DisposePartially(DateOnly date, PartialDisposalPlan plan, Guid journalId, DateTime nowUtc)
    {
        var f = plan.Fraction;
        Cost -= plan.Cost;
        AccumulatedDepreciation -= plan.Accumulated;
        RevaluationReserve -= plan.Reserve;
        RevaluationLossInProfit -= AssetMath.Money(RevaluationLossInProfit * f);
        AcquisitionCost -= AssetMath.Money(AcquisitionCost * f);
        SalvageValue -= AssetMath.Money(SalvageValue * f);
        Quantity = Math.Round(Quantity - (plan.Quantity ?? 0), 6);
        if (TotalUnits is { } units)
        {
            TotalUnits = Math.Round(units * (1 - f), 6);
        }

        foreach (var book in Books)
        {
            var month = AssetMath.MonthStart(date);
            var bookCost = AssetMath.Money(book.Cost * f);
            var bookAccumulated = AssetMath.Money(book.AccumulatedBeforeMonthOf(date) * f);
            book.Adjustments.Add(new BookAdjustment { Date = date, Kind = AssetEventKinds.PartiallyDisposed, CostDelta = -bookCost, AccumulatedDelta = -bookAccumulated });
            book.Cost -= bookCost;
            book.SalvageValue -= AssetMath.Money(book.SalvageValue * f);
            book.RegenerateFrom(month);
        }

        RegenerateSchedule();
        AddEvent(nowUtc, date, AssetEventKinds.PartiallyDisposed,
            $"{f:P2} disposed: cost {plan.Cost:0.00}, accumulated depreciation {plan.Accumulated:0.00}, {(plan.GainLoss >= 0 ? "gain" : "loss")} {Math.Abs(plan.GainLoss):0.00}",
            plan.Proceeds, journalId, null, costDelta: -plan.Cost, accumulatedDelta: -plan.Accumulated, reserveDelta: plan.Reserve == 0 ? null : -plan.Reserve);
    }

    /// <summary>Checks a revaluation without changing the asset; returns the NBV delta.</summary>
    public decimal ValidateRevaluation(decimal newNetBookValue, int? newUsefulLifeMonths)
    {
        EnsureInService();
        var value = AssetMath.Money(newNetBookValue);
        if (value < SalvageValue)
        {
            throw new DomainException(AssetErrors.ValueBelowSalvage);
        }

        if (newUsefulLifeMonths is { } life && life is < 1 or > 1200)
        {
            throw new DomainException(AssetErrors.LifeInvalid);
        }

        var postedMonths = Schedule.Count(s => s.IsPosted);
        if (Method != DepreciationMethod.UnitsOfProduction && (newUsefulLifeMonths ?? UsefulLifeMonths) - postedMonths <= 0 && value > SalvageValue)
        {
            throw new DomainException(AssetErrors.NoRemainingLife);
        }

        return value - NetBookValue;
    }

    /// <summary>Gain (positive) or loss (negative) if disposed now for <paramref name="proceeds"/>.</summary>
    public decimal DisposalResult(DateOnly date, decimal proceeds)
    {
        EnsureInService();
        if (proceeds < 0)
        {
            throw new DomainException(Error.Validation("assets.proceeds-invalid", "Proceeds cannot be negative."));
        }

        if (Method != DepreciationMethod.UnitsOfProduction && Schedule.Any(s => !s.IsPosted && s.Amount > 0 && s.Period < AssetMath.MonthStart(date)))
        {
            throw new DomainException(AssetErrors.DepreciationNotUpToDate);
        }

        return AssetMath.Money(proceeds) - NetBookValue;
    }

    public void Dispose(DateOnly date, decimal proceeds, Guid journalId, DateTime nowUtc)
    {
        var gainLoss = DisposalResult(date, proceeds);
        Schedule = Schedule.Where(s => s.IsPosted).ToList();
        foreach (var book in Books)
        {
            book.DropFrom(date);
        }

        var reserve = RevaluationReserve;
        RevaluationReserve = 0;
        DisposalDate = date;
        DisposalProceeds = AssetMath.Money(proceeds);
        DisposalGainLoss = gainLoss;
        DisposalJournalId = journalId;
        Status = AssetStatus.Disposed;
        AddEvent(nowUtc, date, AssetEventKinds.Disposed, gainLoss >= 0 ? $"Disposed with a gain of {gainLoss:0.00}" : $"Disposed with a loss of {-gainLoss:0.00}",
            DisposalProceeds, journalId, null, reserveDelta: reserve == 0 ? null : -reserve);
    }

    private void RefreshStatus()
    {
        if (Status is AssetStatus.Active or AssetStatus.FullyDepreciated)
        {
            Status = NetBookValue <= SalvageValue ? AssetStatus.FullyDepreciated : AssetStatus.Active;
        }
    }

    private void EnsureDraft()
    {
        if (Status != AssetStatus.Draft)
        {
            throw new DomainException(AssetErrors.NotDraft);
        }
    }

    private void EnsureNotDisposed()
    {
        if (Status == AssetStatus.Disposed)
        {
            throw new DomainException(AssetErrors.Disposed);
        }
    }

    private void EnsureInService()
    {
        if (Status == AssetStatus.Disposed)
        {
            throw new DomainException(AssetErrors.Disposed);
        }

        if (Status == AssetStatus.Draft)
        {
            throw new DomainException(AssetErrors.NotActive);
        }
    }

    private void AddEvent(DateTime nowUtc, DateOnly date, string kind, string description, decimal? amount, Guid? journalId, Guid? referenceId,
        decimal? costDelta = null, decimal? accumulatedDelta = null, decimal? reserveDelta = null) =>
        History.Add(new AssetEvent
        {
            AtUtc = nowUtc, Date = date, Kind = kind, Description = description, Amount = amount, JournalId = journalId, ReferenceId = referenceId,
            CostDelta = costDelta, AccumulatedDelta = accumulatedDelta, ReserveDelta = reserveDelta,
        });
}

public enum DepreciationRunStatus
{
    Posted = 1,
    Reversed = 2,
}

public sealed class DepreciationRunLine
{
    public Guid AssetId { get; set; }

    public string AssetNumber { get; set; } = string.Empty;

    public Guid CategoryId { get; set; }

    public string? PostingGroup { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>One month's depreciation for a legal entity, posted as a single journal (grouped by posting group).</summary>
[Audited]
public sealed class DepreciationRun : AggregateRoot, ICompanyScoped
{
    private DepreciationRun()
    {
    }

    private DepreciationRun(Guid id, Guid companyId, Guid legalEntityId, DateOnly period)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        Period = AssetMath.MonthStart(period);
        Status = DepreciationRunStatus.Posted;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public DateOnly Period { get; private set; }

    public DepreciationRunStatus Status { get; private set; }

    public decimal Total { get; private set; }

    public Guid? JournalId { get; private set; }

    public DateOnly? ReversalDate { get; private set; }

    public Guid? ReversalJournalId { get; private set; }

    public List<DepreciationRunLine> Lines { get; private set; } = [];

    public static DepreciationRun Create(Guid id, Guid companyId, Guid legalEntityId, DateOnly period) => new(id, companyId, legalEntityId, period);

    public void SetPosted(IEnumerable<DepreciationRunLine> lines, Guid journalId)
    {
        Lines = lines.ToList();
        Total = Lines.Sum(l => l.Amount);
        JournalId = journalId;
    }

    public void MarkReversed(DateOnly date, Guid journalId)
    {
        if (Status != DepreciationRunStatus.Posted)
        {
            throw new DomainException(Error.Conflict("assets.run-not-posted", "Only posted runs can be reversed."));
        }

        Status = DepreciationRunStatus.Reversed;
        ReversalDate = date;
        ReversalJournalId = journalId;
    }
}
