using Erp.Modules.Manufacturing.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class ManufacturingBomTests
{
    private static readonly Guid Company = Guid.NewGuid();

    internal static BomVersion Version(Guid product, DateOnly from, decimal output = 1, decimal yieldPercent = 100, params BomLine[] lines)
    {
        var bom = BillOfMaterials.Create(Company, product, "B" + Guid.NewGuid().ToString("N")[..6], "Bom");
        return BomVersion.Create(bom, 1, from, null, output, yieldPercent, lines);
    }

    internal static BomLine Line(Guid component, decimal qty, decimal scrap = 0, IssueMethod method = IssueMethod.Manual) =>
        new() { ComponentProductId = component, QuantityPerOutput = qty, UomId = Guid.NewGuid(), ScrapPercent = scrap, IssueMethod = method };

    [Fact]
    public void Explosion_applies_scrap_and_yield_per_output_unit()
    {
        var x = Guid.NewGuid();
        var version = Version(Guid.NewGuid(), new DateOnly(2026, 1, 1), output: 2, yieldPercent: 90, Line(x, 4, scrap: 10));
        var requirement = BomCalculator.Explode(version, 100).Single();
        // 4 per 2 units = 2 per unit; × 1.10 scrap ÷ 0.90 yield = 2.444444…
        requirement.Quantity.ShouldBe(244.444444m);
        requirement.QuantityPerUnit.ShouldBe(2m * 1.1m / 0.9m);
    }

    [Fact]
    public void J9_structure_explodes_to_2_3_1_per_unit()
    {
        var (x, y, z) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var version = Version(Guid.NewGuid(), new DateOnly(2026, 1, 1), 1, 100, Line(x, 2), Line(y, 3), Line(z, 1));
        BomCalculator.Explode(version, 100).Select(r => r.Quantity).ShouldBe([200m, 300m, 100m]);
    }

    [Fact]
    public void A_component_equal_to_the_output_is_rejected() =>
        Should.Throw<DomainException>(() =>
        {
            var p = Guid.NewGuid();
            Version(p, new DateOnly(2026, 1, 1), 1, 100, Line(p, 1));
        });

    [Fact]
    public void Multi_level_cycles_are_detected()
    {
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var graph = new Dictionary<Guid, IReadOnlyCollection<Guid>> { [a] = [b], [b] = [c] };
        BomCalculator.HasCycle(new Dictionary<Guid, IReadOnlyCollection<Guid>>(graph) { [c] = [a] }, c).ShouldBeTrue();
        BomCalculator.HasCycle(new Dictionary<Guid, IReadOnlyCollection<Guid>>(graph) { [c] = [d] }, c).ShouldBeFalse();
    }

    [Fact]
    public void Low_level_codes_follow_the_deepest_use()
    {
        var (fg, sub, raw) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // raw is used directly by fg and through sub → its code is 2.
        var graph = new Dictionary<Guid, IReadOnlyCollection<Guid>> { [fg] = [sub, raw], [sub] = [raw] };
        var codes = BomCalculator.LowLevelCodes(graph, [fg]);
        codes[fg].ShouldBe(0);
        codes[sub].ShouldBe(1);
        codes[raw].ShouldBe(2);
    }

    [Fact]
    public void Activating_a_newer_version_supersedes_the_open_ended_one_and_effective_dates_select_correctly()
    {
        var product = Guid.NewGuid();
        var bom = BillOfMaterials.Create(Company, product, "FG", "FG");
        var v1 = BomVersion.Create(bom, 1, new DateOnly(2026, 1, 1), null, 1, 100, [Line(Guid.NewGuid(), 1)]);
        v1.Activate([]);
        var v2 = BomVersion.Create(bom, 2, new DateOnly(2026, 6, 1), null, 1, 100, [Line(Guid.NewGuid(), 2)]);
        v2.Activate([v1]);

        v1.Status.ShouldBe(BomVersionStatus.Superseded);
        v1.EffectiveTo.ShouldBe(new DateOnly(2026, 5, 31));
        BomCalculator.Effective([v1, v2], new DateOnly(2026, 3, 1)).ShouldBe(v1);
        BomCalculator.Effective([v1, v2], new DateOnly(2026, 7, 1)).ShouldBe(v2);
        BomCalculator.Effective([v1, v2], new DateOnly(2025, 12, 31)).ShouldBeNull();

        var overlapping = BomVersion.Create(bom, 3, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), 1, 100, [Line(Guid.NewGuid(), 1)]);
        Should.Throw<DomainException>(() => overlapping.Activate([v1, v2])).Error.ShouldBe(ManufacturingErrors.VersionOverlap);
    }

    [Fact]
    public void Work_center_cost_uses_its_overhead_basis()
    {
        var wc = WorkCenter.Create(Company, "ASM", "Assembly", "تجميع", 8, 30, 50, 25, OverheadBasis.MachineHours);
        wc.Cost(10, 8).ShouldBe((300m, 400m, 200m));
        wc.Update("Assembly", "تجميع", 8, 30, 50, 25, OverheadBasis.LaborHours);
        wc.Cost(10, 8).Overhead.ShouldBe(250m);
    }
}

public class ProductionOrderTests
{
    private static readonly DateOnly D = new(2026, 3, 1);

    private static ProductionOrder Released(params BomLine[] lines)
    {
        var product = Guid.NewGuid();
        var version = ManufacturingBomTests.Version(product, new DateOnly(2026, 1, 1), 1, 100, lines);
        var order = ProductionOrder.Create(Guid.NewGuid(), "MO-1", product, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, D, D);
        order.Plan(version, version.Lines.ToDictionary(l => l.LineId, _ => 1m), null);
        order.Release();
        return order;
    }

    [Fact]
    public void Receipt_cost_spreads_remaining_wip_over_remaining_output_and_the_last_receipt_relieves_everything()
    {
        var x = ManufacturingBomTests.Line(Guid.NewGuid(), 2);
        var order = Released(x);
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialIssue, x.LineId, 200, 2000, D, null);
        order.Status.ShouldBe(ProductionOrderStatus.InProgress);
        order.WipBalance.ShouldBe(2000);

        order.ReceiptUnitCost(60).ShouldBe(20m);
        order.RecordReceipt(Guid.NewGuid(), 60, 1200, D, null);
        order.Status.ShouldBe(ProductionOrderStatus.PartiallyCompleted);
        order.WipBalance.ShouldBe(800);

        // Normal scrap: 10 units lost, the remaining 800 is spread over the 30 good units still to come.
        order.RecordScrap(Guid.NewGuid(), 10, D, "damaged");
        order.ReceiptUnitCost(30).ShouldBe(26.666667m);
        order.RecordReceipt(Guid.NewGuid(), 30, 800, D, null);
        order.Status.ShouldBe(ProductionOrderStatus.Completed);
        order.WipBalance.ShouldBe(0);
    }

    [Fact]
    public void Backflush_follows_output_and_never_exceeds_the_requirement()
    {
        var manual = ManufacturingBomTests.Line(Guid.NewGuid(), 2);
        var flushed = ManufacturingBomTests.Line(Guid.NewGuid(), 1, method: IssueMethod.Backflush);
        var order = Released(manual, flushed);
        order.BackflushDue(60).Single().Quantity.ShouldBe(60);
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.Backflush, flushed.LineId, 60, 240, D, null);
        order.BackflushDue(60).ShouldBeEmpty();
        order.BackflushDue(120).Single().Quantity.ShouldBe(40);
    }

    [Fact]
    public void Returns_cannot_exceed_issues_and_closing_records_the_residual_as_variance()
    {
        var x = ManufacturingBomTests.Line(Guid.NewGuid(), 2);
        var order = Released(x);
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialIssue, x.LineId, 10, 100, D, null);
        Should.Throw<DomainException>(() => order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialReturn, x.LineId, -11, -110, D, null));
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialReturn, x.LineId, -4, -40, D, null);
        order.WipBalance.ShouldBe(60);

        order.Close(Guid.NewGuid(), D, null);
        order.VarianceAmount.ShouldBe(60);
        order.WipBalance.ShouldBe(0);
        order.Status.ShouldBe(ProductionOrderStatus.Closed);
    }

    [Fact]
    public void Work_is_refused_before_release_and_cancel_only_without_transactions()
    {
        var product = Guid.NewGuid();
        var order = ProductionOrder.Create(Guid.NewGuid(), "MO-2", product, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, D, D);
        Should.Throw<DomainException>(() => order.RecordScrap(Guid.NewGuid(), 1, D, null));
        order.Cancel();
        order.Status.ShouldBe(ProductionOrderStatus.Cancelled);
    }
}

public class MrpEngineTests
{
    private static readonly DateOnly Run = new(2026, 3, 1);

    [Theory]
    [InlineData(LotSizeRule.LotForLot, 0, 17, 17)]
    [InlineData(LotSizeRule.FixedMinimum, 50, 17, 50)]
    [InlineData(LotSizeRule.FixedMinimum, 50, 70, 70)]
    [InlineData(LotSizeRule.Multiple, 50, 17, 50)]
    [InlineData(LotSizeRule.Multiple, 50, 101, 150)]
    public void Lot_sizing(LotSizeRule rule, decimal size, decimal need, decimal expected) => MrpEngine.LotSize(need, rule, size).ShouldBe(expected);

    [Fact]
    public void Nets_parent_first_explodes_dependent_demand_and_offsets_by_lead_time()
    {
        var (fg, x) = (Guid.NewGuid(), Guid.NewGuid());
        var graph = new Dictionary<Guid, IReadOnlyCollection<Guid>> { [fg] = [x] };
        var items = new Dictionary<Guid, MrpItem>
        {
            [fg] = new(fg, 10, 0, 2, LotSizeRule.LotForLot, 0, ProcurementType.Make),
            [x] = new(x, 20, 0, 7, LotSizeRule.Multiple, 50, ProcurementType.Buy),
        };
        var input = new MrpInput(Run, Run.AddDays(56), items,
            [new MrpRequirement(fg, 30, Run.AddDays(20), "SalesOrder", "SO-1")], [],
            BomCalculator.LowLevelCodes(graph, [fg]),
            (product, qty, date) => product == fg ? [(x, qty * 2)] : null);

        var result = MrpEngine.Run(input);
        var produce = result.Suggestions.Single(s => s.ProductId == fg);
        produce.Action.ShouldBe(SuggestionAction.Produce);
        produce.Quantity.ShouldBe(20);
        produce.DueDate.ShouldBe(Run.AddDays(20));
        produce.OrderByDate.ShouldBe(Run.AddDays(18));
        produce.Pegging.Single().SourceReference.ShouldBe("SO-1");

        // 40 of X needed on day 18; 20 available → 20 short, rounded up to a multiple of 50, ordered 7 days earlier.
        var buy = result.Suggestions.Single(s => s.ProductId == x);
        buy.Action.ShouldBe(SuggestionAction.Purchase);
        buy.Quantity.ShouldBe(50);
        buy.DueDate.ShouldBe(Run.AddDays(18));
        buy.OrderByDate.ShouldBe(Run.AddDays(11));
        buy.Pegging.Single().SourceType.ShouldBe("PlannedProduction");
        result.Shortages.ShouldBeEmpty();
    }

    [Fact]
    public void Scheduled_receipts_and_safety_stock_are_respected_and_late_orders_are_flagged()
    {
        var p = Guid.NewGuid();
        var items = new Dictionary<Guid, MrpItem> { [p] = new(p, 5, 10, 14, LotSizeRule.LotForLot, 0, ProcurementType.Buy) };
        var input = new MrpInput(Run, Run.AddDays(56), items,
            [new MrpRequirement(p, 20, Run.AddDays(30), "SalesOrder", "SO-2")],
            [new MrpReceipt(p, 15, Run.AddDays(10))],
            new Dictionary<Guid, int>(), (_, _, _) => null);

        var result = MrpEngine.Run(input);
        // Day 0: 5 on hand < safety 10 → 5 now (late: 14-day lead time). Day 10: +15 → 25. Day 30: −20 → 5 < 10 → 5 more.
        result.Suggestions.Select(s => (s.Quantity, s.DueDate, s.IsLate)).ShouldBe([(5m, Run, true), (5m, Run.AddDays(30), false)]);
        result.Shortages.Single().Reason.ShouldContain("lead time");
    }

    [Fact]
    public void Demand_beyond_the_horizon_is_ignored()
    {
        var p = Guid.NewGuid();
        var input = new MrpInput(Run, Run.AddDays(10), new Dictionary<Guid, MrpItem>(),
            [new MrpRequirement(p, 20, Run.AddDays(30), "SalesOrder", "SO-3")], [], new Dictionary<Guid, int>(), (_, _, _) => null);
        MrpEngine.Run(input).Suggestions.ShouldBeEmpty();
    }
}
