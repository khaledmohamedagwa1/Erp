using Erp.Modules.Manufacturing.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class ManufacturingCostingTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 3, 2);

    private static (ProductionOrder Order, Guid X, Guid Y) PlannedOrder(decimal quantity = 10)
    {
        var (fg, x, y) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var version = ManufacturingBomTests.Version(fg, new DateOnly(2026, 1, 1), 1, 100, ManufacturingBomTests.Line(x, 2), ManufacturingBomTests.Line(y, 3));
        version.Lines[0].Alternatives = [Guid.NewGuid()];
        var routing = Routing.Create(Company, fg, "R", "R", [new RoutingOperation { Sequence = 10, Name = "Op", WorkCenterId = Guid.NewGuid(), RunHoursPerUnit = 1 }]);
        var order = ProductionOrder.Create(Company, "MO-1", fg, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity, Day, Day.AddDays(5));
        order.Plan(version, new Dictionary<Guid, decimal>(), routing);
        return (order, x, y);
    }

    private static Guid LineOf(ProductionOrder order, Guid product) => order.Components.Single(c => c.ProductId == product && c.SubstituteForLineId is null).LineId;

    [Fact]
    public void Variant_conditional_bom_lines_explode_only_for_matching_variants()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var large = ManufacturingBomTests.Line(b, 1);
        large.AppliesTo = new Dictionary<string, string> { ["Size"] = "L" };
        var version = ManufacturingBomTests.Version(Guid.NewGuid(), new DateOnly(2026, 1, 1), 1, 100, ManufacturingBomTests.Line(a, 2), large);

        BomCalculator.Explode(version, 5, new Dictionary<string, string> { ["size"] = "l", ["Colour"] = "Red" }).Select(r => r.ProductId).ShouldBe([a, b]);
        BomCalculator.Explode(version, 5, new Dictionary<string, string> { ["Size"] = "S" }).Select(r => r.ProductId).ShouldBe([a]);
        BomCalculator.Explode(version, 5).Select(r => r.ProductId).ShouldBe([a]);
    }

    [Fact]
    public void Standard_order_receives_at_standard_and_splits_the_residual_into_price_usage_and_conversion()
    {
        var (order, x, y) = PlannedOrder();
        order.UseStandardCost(Guid.NewGuid(), new StandardCostItem { ProductId = order.ProductId, Material = 16, Labor = 3, Machine = 4, Overhead = 2 },
            new Dictionary<Guid, decimal> { [x] = 5, [y] = 2 });
        order.Release();
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialIssue, LineOf(order, x), 22, 121, Day, null);
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialIssue, LineOf(order, y), 30, 60, Day, null);
        order.RecordOperation(Guid.NewGuid(), 10, 10, 10, 35, 40, 20, Day, null);

        order.ReceiptUnitCost(10).ShouldBe(25m);
        order.RecordReceipt(Guid.NewGuid(), 10, 250, Day, null);
        order.WipBalance.ShouldBe(26m);

        var split = StandardCosting.Split(order);
        split.ShouldBe(new StandardVariances(MaterialPrice: 11, MaterialUsage: 10, Conversion: 5, Other: 0));
        split.Total.ShouldBe(order.WipBalance);

        order.Close(Guid.NewGuid(), Day, null, split);
        order.WipBalance.ShouldBe(0m);
        (order.MaterialPriceVariance, order.MaterialUsageVariance, order.ConversionVariance).ShouldBe((11m, 10m, 5m));
    }

    [Fact]
    public void Components_without_a_standard_have_no_price_variance_and_the_rest_goes_to_other()
    {
        var (order, x, y) = PlannedOrder();
        // Only X has a component standard; the product's material standard (17) differs from 2×5 + 3×2 = 16.
        order.UseStandardCost(Guid.NewGuid(), new StandardCostItem { ProductId = order.ProductId, Material = 17 }, new Dictionary<Guid, decimal> { [x] = 5 });
        order.Release();
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialIssue, LineOf(order, x), 20, 100, Day, null);
        order.RecordMaterial(Guid.NewGuid(), ProductionTransactionKind.MaterialIssue, LineOf(order, y), 33, 66, Day, null);
        order.RecordReceipt(Guid.NewGuid(), 10, 170, Day, null);

        var split = StandardCosting.Split(order);
        split.MaterialPrice.ShouldBe(0m);
        split.MaterialUsage.ShouldBe(6m); // Y: 3 extra at its actual 2.00
        split.Conversion.ShouldBe(0m);
        split.Other.ShouldBe(-10m);       // 166 actual material − 170 relieved − 6 usage
        split.Total.ShouldBe(order.WipBalance);
    }

    [Fact]
    public void Substitution_moves_the_share_of_the_standard_quantity_to_the_alternative()
    {
        var (order, x, _) = PlannedOrder();
        var alternative = order.Component(LineOf(order, x)).Alternatives.Single();
        order.Release();
        var substitute = order.Substitute(LineOf(order, x), alternative, Guid.NewGuid(), 5, Day, automatic: true, alternativeStandardCost: null);

        var original = order.Component(LineOf(order, x));
        original.RequiredQuantity.ShouldBe(15m);
        substitute.RequiredQuantity.ShouldBe(5m);
        (original.QuantityPerUnit + substitute.QuantityPerUnit).ShouldBe(2m);
        substitute.SubstituteForLineId.ShouldBe(original.LineId);
        order.Substitutions.Single().Automatic.ShouldBeTrue();

        Should.Throw<DomainException>(() => order.Substitute(LineOf(order, x), Guid.NewGuid(), Guid.NewGuid(), 1, Day, false, null))
            .Error.Code.ShouldBe("manufacturing.not-an-alternative");
        Should.Throw<DomainException>(() => order.Substitute(LineOf(order, x), alternative, Guid.NewGuid(), 16, Day, false, null))
            .Error.Code.ShouldBe("manufacturing.substitution-quantity-invalid");
    }

    [Fact]
    public void Capacity_planner_flags_overloaded_days_and_proposes_a_later_finish()
    {
        var wc = new CapacityWorkCenter(Guid.NewGuid(), "ASM", 8, WorkingDays.FromNames(["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday"]));
        var monday = new DateOnly(2026, 3, 2); // a Monday
        var loads = new[]
        {
            new CapacityLoad("ProductionOrder", Guid.NewGuid(), "MO-1", wc.Id, 16, monday, monday.AddDays(1)), // Mon–Tue, fits exactly
            new CapacityLoad("ProductionOrder", Guid.NewGuid(), "MO-2", wc.Id, 12, monday, monday.AddDays(1)), // same days → overload
        };

        var result = CapacityPlanner.Plan([wc], loads, monday, monday.AddDays(6));
        var monday0 = result.Buckets.Single(b => b.Date == monday);
        monday0.Load.ShouldBe(14m);
        monday0.Overloaded.ShouldBeTrue();
        result.Buckets.ShouldNotContain(b => b.Date == monday.AddDays(4)); // Friday: no capacity, no load

        var first = result.Proposals.Single(p => p.Reference == "MO-1");
        first.Late.ShouldBeFalse();
        var second = result.Proposals.Single(p => p.Reference == "MO-2");
        second.Late.ShouldBeTrue();
        second.ProposedFinish.ShouldBe(monday.AddDays(3)); // Wed 8 h + Thu 4 h
    }

    [Fact]
    public void A_cost_version_activates_only_when_approved_and_effective_and_supersedes_the_previous_one()
    {
        var old = CostVersion.Create(Company, "STD-1", "2026", new DateOnly(2026, 1, 1));
        old.SetItems([new StandardCostItem { ProductId = Guid.NewGuid(), Material = 1 }]);
        old.Approve(null);
        old.Activate(null, new DateOnly(2026, 1, 1));

        var next = CostVersion.Create(Company, "STD-2", "2026 H2", new DateOnly(2026, 7, 1));
        Should.Throw<DomainException>(() => next.Approve(null)).Error.Code.ShouldBe("manufacturing.cost-version-empty");
        next.SetItems([new StandardCostItem { ProductId = Guid.NewGuid(), Material = 1.1234567m, Labor = 2 }]);
        next.Items.Single().UnitCost.ShouldBe(3.123457m);
        Should.Throw<DomainException>(() => next.Activate(old, new DateOnly(2026, 7, 1))); // not approved
        next.Approve(null);
        Should.Throw<DomainException>(() => next.Activate(old, new DateOnly(2026, 6, 30))).Error.Code.ShouldBe("manufacturing.cost-version-not-effective");
        next.Activate(old, new DateOnly(2026, 7, 1));
        (old.Status, next.Status).ShouldBe((CostVersionStatus.Superseded, CostVersionStatus.Active));
        Should.Throw<DomainException>(() => next.SetItems([])); // only drafts are editable
    }

    [Fact]
    public void Working_days_round_trip_and_rework_orders_refuse_standard_costing()
    {
        WorkingDays.ToNames(WorkingDays.FromNames(["saturday", "Sunday"])).ShouldBe(["Sunday", "Saturday"]);
        WorkingDays.IsWorking(WorkingDays.All, new DateOnly(2026, 3, 6)).ShouldBeTrue();
        Should.Throw<DomainException>(() => WorkingDays.FromNames(["Funday"]));

        var product = Guid.NewGuid();
        var rework = ProductionOrder.Create(Company, "MO-R", product, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, Day, Day, orderType: ProductionOrderType.Rework);
        var line = new ComponentRequirement(Guid.NewGuid(), product, Guid.NewGuid(), IssueMethod.Manual, 1, 3);
        rework.PlanRework([line], new Dictionary<Guid, decimal>(), null);
        rework.Components.Single().ProductId.ShouldBe(product);
        Should.Throw<DomainException>(() => rework.UseStandardCost(Guid.NewGuid(), new StandardCostItem { ProductId = product }, new Dictionary<Guid, decimal>()))
            .Error.Code.ShouldBe("manufacturing.rework-actual-only");
    }
}
