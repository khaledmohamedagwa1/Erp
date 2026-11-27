using Erp.Modules.FixedAssets.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class DepreciationCalculatorTests
{
    private static readonly DateOnly Jan = new(2026, 1, 1);

    [Fact]
    public void J12_straight_line_120000_over_10_years_is_1000_a_month()
    {
        var lines = DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.StraightLine, 120_000m, 0, 0, Jan, 120));
        lines.Count.ShouldBe(120);
        lines.ShouldAllBe(l => l.Amount == 1000m);
        lines[0].Period.ShouldBe(Jan);
        lines[^1].Period.ShouldBe(new DateOnly(2035, 12, 1));
        lines.Sum(l => l.Amount).ShouldBe(120_000m);
    }

    [Fact]
    public void Straight_line_respects_salvage()
    {
        var lines = DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.StraightLine, 10_000m, 1_000m, 0, Jan, 36));
        lines.ShouldAllBe(l => l.Amount == 250m);
        lines.Sum(l => l.Amount).ShouldBe(9_000m);
    }

    [Fact]
    public void Rounding_residual_goes_to_the_final_period()
    {
        var lines = DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.StraightLine, 1_000m, 0, 0, Jan, 3));
        lines.Select(l => l.Amount).ShouldBe([333.33m, 333.33m, 333.34m]);
    }

    [Fact]
    public void Remaining_schedule_starts_from_accumulated_depreciation()
    {
        var lines = DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.StraightLine, 12_000m, 0, 6_000m, Jan, 12));
        lines.ShouldAllBe(l => l.Amount == 500m);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(15, 3)]
    [InlineData(16, 4)]
    [InlineData(31, 4)]
    public void Mid_month_convention(int day, int expectedMonth)
    {
        AssetMath.FirstDepreciationMonth(new DateOnly(2026, 3, Math.Min(day, 31))).ShouldBe(new DateOnly(2026, expectedMonth, 1));
    }

    [Fact]
    public void Declining_balance_switches_to_straight_line_and_ends_at_salvage()
    {
        const decimal cost = 10_000m, salvage = 1_000m, rate = 40m;
        var lines = DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.DecliningBalance, cost, salvage, 0, Jan, 60, rate));
        lines.Count.ShouldBe(60);
        lines[0].Amount.ShouldBe(333.33m); // 10,000 × 40% ÷ 12
        lines.Sum(l => l.Amount).ShouldBe(cost - salvage);

        // Find the switch: from some month on, the straight-line amount beats the declining amount.
        var nbv = cost;
        var switchedAt = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var declining = AssetMath.Money(nbv * rate / 100m / 12m);
            if (switchedAt < 0 && lines[i].Amount > declining)
            {
                switchedAt = i;
            }

            nbv -= lines[i].Amount;
            nbv.ShouldBeGreaterThanOrEqualTo(salvage);
        }

        switchedAt.ShouldBeGreaterThan(0);
        switchedAt.ShouldBeLessThan(59);
        var tail = lines.Skip(switchedAt).Select(l => l.Amount).ToList();
        (tail.Max() - tail.Min()).ShouldBeLessThanOrEqualTo(0.02m);
        lines.Take(switchedAt).Select(l => l.Amount).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public void Declining_balance_never_goes_below_salvage_with_a_high_rate()
    {
        var lines = DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.DecliningBalance, 5_000m, 2_000m, 0, Jan, 24, 100m));
        lines.Sum(l => l.Amount).ShouldBe(3_000m);
        lines.ShouldAllBe(l => l.Amount >= 0);
    }

    [Fact]
    public void Units_of_production_is_proportional_and_capped()
    {
        DepreciationCalculator.UnitsOfProduction(50_000m, 5_000m, 100_000m, 10_000m, 45_000m).ShouldBe(4_500m);
        DepreciationCalculator.UnitsOfProduction(50_000m, 5_000m, 100_000m, 10_000m, 1_000m).ShouldBe(1_000m);
        DepreciationCalculator.UnitsOfProduction(50_000m, 5_000m, 100_000m, 0, 1_000m).ShouldBe(0);
        DepreciationCalculator.Build(new ScheduleInput(DepreciationMethod.UnitsOfProduction, 50_000m, 0, 0, Jan, 12)).ShouldBeEmpty();
    }
}

public class AssetLifecycleTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AssetCategory Category(DepreciationMethod method = DepreciationMethod.StraightLine, int life = 120, decimal salvagePercent = 0, decimal? rate = null) =>
        AssetCategory.Create(Company, "veh", "Vehicles", "مركبات", method, life, salvagePercent, rate, "VEH");

    private static Asset Active(decimal cost = 120_000m, DateOnly? inService = null, AssetCategory? category = null, decimal? salvage = null,
        DepreciationMethod? method = null, decimal? totalUnits = null)
    {
        var date = inService ?? new DateOnly(2026, 1, 10);
        var asset = Asset.CreateDraft(Company, category ?? Category(), Guid.NewGuid(), "Truck", "شاحنة", null, null, null, date, date, cost, salvage, null,
            method, null, totalUnits);
        asset.Activate("FA-1", AcquisitionMode.ManualJournal, Guid.NewGuid(), null, null, null, Now);
        return asset;
    }

    private static void PostThrough(Asset asset, DateOnly period) => asset.PostDepreciation(period, Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public void Draft_takes_defaults_from_the_category()
    {
        var category = Category(salvagePercent: 10);
        category.Code.ShouldBe("VEH");
        var asset = Asset.CreateDraft(Company, category, Guid.NewGuid(), "Truck", "شاحنة", null, null, null, new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 1), 50_000m, null, null, null, null, null);
        asset.Status.ShouldBe(AssetStatus.Draft);
        asset.SalvageValue.ShouldBe(5_000m);
        asset.UsefulLifeMonths.ShouldBe(120);
        asset.Method.ShouldBe(DepreciationMethod.StraightLine);
        asset.PostingGroup.ShouldBe("VEH");
        asset.Schedule.ShouldBeEmpty();
    }

    [Fact]
    public void Draft_validation_rejects_bad_values()
    {
        var category = Category();
        Should.Throw<DomainException>(() => Asset.CreateDraft(Company, category, Guid.NewGuid(), "A", "A", null, null, null, new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 1), 1_000m, 1_000m, null, null, null, null)).Error.Code.ShouldBe("assets.salvage-invalid");
        Should.Throw<DomainException>(() => Asset.CreateDraft(Company, category, Guid.NewGuid(), "A", "A", null, null, null, new DateOnly(2026, 2, 1),
            new DateOnly(2026, 1, 1), 1_000m, null, null, null, null, null)).Error.Code.ShouldBe("assets.dates-invalid");
        Should.Throw<DomainException>(() => Asset.CreateDraft(Company, category, Guid.NewGuid(), "A", "A", null, null, null, new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 1), 1_000m, null, null, DepreciationMethod.DecliningBalance, null, null)).Error.Code.ShouldBe("assets.rate-invalid");
        Should.Throw<DomainException>(() => Asset.CreateDraft(Company, category, Guid.NewGuid(), "A", "A", null, null, null, new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 1), 1_000m, null, null, DepreciationMethod.UnitsOfProduction, null, null)).Error.Code.ShouldBe("assets.units-invalid");
    }

    [Fact]
    public void Activation_builds_the_schedule_from_the_in_service_month()
    {
        var asset = Active(inService: new DateOnly(2026, 1, 20));
        asset.Status.ShouldBe(AssetStatus.Active);
        asset.Schedule.Count.ShouldBe(120);
        asset.Schedule[0].Period.ShouldBe(new DateOnly(2026, 2, 1));
        asset.Schedule.ShouldAllBe(s => s.Amount == 1000m && !s.IsPosted);
        asset.History.Single().Kind.ShouldBe(AssetEventKinds.Acquired);
        Should.Throw<DomainException>(() => asset.Activate("FA-2", AcquisitionMode.ManualJournal, null, null, null, null, Now));
    }

    [Fact]
    public void Posting_catches_up_overdue_months_and_reversal_restores_them()
    {
        var asset = Active();
        var run = Guid.NewGuid();
        asset.PostDepreciation(new DateOnly(2026, 3, 1), run, Guid.NewGuid(), Now).ShouldBe(3_000m);
        asset.AccumulatedDepreciation.ShouldBe(3_000m);
        asset.NetBookValue.ShouldBe(117_000m);
        asset.DueLines(new DateOnly(2026, 3, 1)).ShouldBeEmpty();

        asset.ReverseDepreciation(run, new DateOnly(2026, 3, 31), Guid.NewGuid(), Now).ShouldBe(3_000m);
        asset.AccumulatedDepreciation.ShouldBe(0);
        asset.DueLines(new DateOnly(2026, 3, 1)).Count.ShouldBe(3);
    }

    [Fact]
    public void Fully_posted_asset_becomes_fully_depreciated_at_salvage()
    {
        var asset = Active(cost: 12_000m, salvage: 1_200m, category: Category(life: 12));
        PostThrough(asset, new DateOnly(2026, 12, 1));
        asset.Status.ShouldBe(AssetStatus.FullyDepreciated);
        asset.NetBookValue.ShouldBe(1_200m);
        asset.DueLines(new DateOnly(2030, 1, 1)).ShouldBeEmpty();
    }

    [Fact]
    public void Revaluation_reschedules_the_remaining_life_and_keeps_posted_lines()
    {
        var asset = Active();
        PostThrough(asset, new DateOnly(2030, 12, 1)); // 60 months
        asset.NetBookValue.ShouldBe(60_000m);

        asset.Revalue(new DateOnly(2030, 12, 31), 90_000m, null, Guid.NewGuid(), Guid.NewGuid(), Now).ShouldBe(30_000m);
        asset.Cost.ShouldBe(150_000m);
        asset.NetBookValue.ShouldBe(90_000m);
        asset.Schedule.Count(s => s.IsPosted).ShouldBe(60);
        var future = asset.Schedule.Where(s => !s.IsPosted).ToList();
        future.Count.ShouldBe(60);
        future.ShouldAllBe(s => s.Amount == 1_500m);
        future[0].Period.ShouldBe(new DateOnly(2031, 1, 1));

        // A new useful life spreads the value over the new remaining months.
        asset.Revalue(new DateOnly(2030, 12, 31), 90_000m, 90, Guid.NewGuid(), Guid.NewGuid(), Now).ShouldBe(0);
        asset.Schedule.Where(s => !s.IsPosted).ShouldAllBe(s => s.Amount == 3_000m);
        asset.Schedule.Count(s => !s.IsPosted).ShouldBe(30);
    }

    [Fact]
    public void Revaluation_below_salvage_is_rejected()
    {
        var asset = Active(salvage: 10_000m);
        Should.Throw<DomainException>(() => asset.ValidateRevaluation(9_000m, null)).Error.Code.ShouldBe("assets.value-below-salvage");
    }

    [Fact]
    public void Declining_balance_asset_reschedules_after_revaluation()
    {
        var asset = Active(cost: 10_000m, salvage: 1_000m, category: Category(DepreciationMethod.DecliningBalance, 60, rate: 40));
        asset.Schedule.Sum(s => s.Amount).ShouldBe(9_000m);
        PostThrough(asset, new DateOnly(2026, 6, 1));
        asset.Revalue(new DateOnly(2026, 6, 30), asset.NetBookValue - 500m, null, Guid.NewGuid(), Guid.NewGuid(), Now);
        (asset.AccumulatedDepreciation + asset.Schedule.Where(s => !s.IsPosted).Sum(s => s.Amount)).ShouldBe(asset.Cost - asset.SalvageValue);
    }

    [Fact]
    public void Units_of_production_depreciates_from_recorded_usage()
    {
        var asset = Active(cost: 50_000m, salvage: 5_000m, method: DepreciationMethod.UnitsOfProduction, totalUnits: 100_000m);
        asset.Schedule.ShouldBeEmpty();
        asset.RecordUsage(new DateOnly(2026, 1, 1), 10_000m, Now);
        asset.RecordUsage(new DateOnly(2026, 1, 1), 20_000m, Now); // replaces the unposted month
        asset.Schedule.Single().Amount.ShouldBe(9_000m);
        PostThrough(asset, new DateOnly(2026, 1, 1));
        Should.Throw<DomainException>(() => asset.RecordUsage(new DateOnly(2026, 1, 1), 1m, Now)).Error.Code.ShouldBe("assets.period-posted");
        asset.RecordUsage(new DateOnly(2026, 2, 1), 500_000m, Now); // capped at what is left
        asset.Schedule.Single(s => !s.IsPosted).Amount.ShouldBe(36_000m);
    }

    [Fact]
    public void Disposal_requires_depreciation_up_to_date_and_computes_gain_or_loss()
    {
        var asset = Active();
        Should.Throw<DomainException>(() => asset.DisposalResult(new DateOnly(2026, 4, 10), 0)).Error.Code.ShouldBe("assets.depreciation-not-up-to-date");
        PostThrough(asset, new DateOnly(2026, 3, 1));
        asset.DisposalResult(new DateOnly(2026, 4, 10), 120_000m).ShouldBe(3_000m);
        asset.DisposalResult(new DateOnly(2026, 4, 10), 100_000m).ShouldBe(-17_000m);

        asset.Dispose(new DateOnly(2026, 4, 10), 100_000m, Guid.NewGuid(), Now);
        asset.Status.ShouldBe(AssetStatus.Disposed);
        asset.DisposalGainLoss.ShouldBe(-17_000m);
        asset.Schedule.ShouldAllBe(s => s.IsPosted);
        asset.DueLines(new DateOnly(2027, 1, 1)).ShouldBeEmpty();
        Should.Throw<DomainException>(() => asset.Transfer(new DateOnly(2026, 5, 1), null, "X", null, null, Now)).Error.Code.ShouldBe("assets.disposed");
    }

    [Fact]
    public void Transfer_records_history_without_changing_values()
    {
        var asset = Active();
        var branch = Guid.NewGuid();
        asset.Transfer(new DateOnly(2026, 2, 1), branch, "Warehouse 2", null, "Moved", Now);
        asset.BranchId.ShouldBe(branch);
        asset.Location.ShouldBe("Warehouse 2");
        asset.History[^1].Kind.ShouldBe(AssetEventKinds.Transferred);
        asset.NetBookValue.ShouldBe(120_000m);
    }
}
