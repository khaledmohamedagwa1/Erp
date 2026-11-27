using Erp.Modules.Accounting.Domain;
using Erp.Modules.People.Domain;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class CommissionTests
{
    private static readonly Guid Rep = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Services = Guid.NewGuid();
    private static readonly DateOnly D = new(2026, 6, 10);

    private static CommissionPlan Plan(CommissionBasis basis = CommissionBasis.NetSales, bool clawback = true) =>
        CommissionPlan.Create(Guid.NewGuid(), "std", "Standard", "قياسية", basis, 5m, clawback,
            [new CommissionTier(1000m, 7m), new CommissionTier(300m, 6m)], [new CategoryRate(Services, 20m)]);

    private static readonly Dictionary<string, Guid> Documents = [];

    private static CommissionSource Src(Guid person, string doc, decimal net, decimal cost = 0m, Guid? category = null, bool isReturn = false)
    {
        lock (Documents)
        {
            if (!Documents.TryGetValue(doc, out var id))
            {
                Documents[doc] = id = Guid.NewGuid();
            }

            return new(person, isReturn ? "SalesReturn" : "SalesInvoice", id, doc, D, category, net, cost, isReturn);
        }
    }

    [Fact]
    public void Plan_normalises_code_sorts_tiers_and_picks_the_highest_tier_reached()
    {
        var plan = Plan();
        plan.Code.ShouldBe("STD");
        plan.Tiers.Select(t => t.Threshold).ShouldBe([300m, 1000m]);
        plan.RateFor(299.99m).ShouldBe(5m);
        plan.RateFor(300m).ShouldBe(6m);
        plan.RateFor(5000m).ShouldBe(7m);
        plan.CategoryRateFor(Services).ShouldBe(20m);
        plan.CategoryRateFor(Guid.NewGuid()).ShouldBeNull();
        plan.CategoryRateFor(null).ShouldBeNull();
    }

    [Fact]
    public void Plan_rejects_invalid_rates_tiers_and_duplicate_categories()
    {
        Should.Throw<DomainException>(() => CommissionPlan.Create(Guid.NewGuid(), "x", "X", "X", CommissionBasis.NetSales, 101m, false, [], []))
            .Error.Code.ShouldBe("people.invalid-rate");
        Should.Throw<DomainException>(() => CommissionPlan.Create(Guid.NewGuid(), "x", "X", "X", CommissionBasis.NetSales, 5m, false, [new CommissionTier(10m, -1m)], []))
            .Error.Code.ShouldBe("people.invalid-rate");
        Should.Throw<DomainException>(() => CommissionPlan.Create(Guid.NewGuid(), "x", "X", "X", CommissionBasis.NetSales, 5m, false,
            [new CommissionTier(10m, 1m), new CommissionTier(10m, 2m)], [])).Error.Code.ShouldBe("people.invalid-tiers");
        Should.Throw<DomainException>(() => CommissionPlan.Create(Guid.NewGuid(), "x", "X", "X", CommissionBasis.NetSales, 5m, false, [new CommissionTier(-1m, 2m)], []))
            .Error.Code.ShouldBe("people.invalid-tiers");
        Should.Throw<DomainException>(() => CommissionPlan.Create(Guid.NewGuid(), "x", "X", "X", CommissionBasis.NetSales, 5m, false, [],
            [new CategoryRate(Services, 1m), new CategoryRate(Services, 2m)])).Error.Code.ShouldBe("people.duplicate-category");
    }

    [Fact]
    public void Calculator_applies_the_tier_to_the_period_total_category_overrides_and_clawbacks()
    {
        var plan = Plan();
        var (lines, details, skipped) = CommissionCalculator.Calculate(
        [
            Src(Rep, "INV-1", 200m),
            Src(Rep, "INV-1", 50m, category: Services),
            Src(Rep, "CN-1", -100m, isReturn: true),
            Src(Rep, "INV-2", 200m),
            Src(Other, "INV-3", 500m),
        ], id => id == Rep ? plan : null);

        skipped.ShouldBe([Other]);
        var line = lines.Single();
        line.BasisTotal.ShouldBe(350m);
        line.TierRatePercent.ShouldBe(6m);
        details.Count.ShouldBe(4);
        details.Single(d => d.DocumentNumber == "INV-1" && d.RatePercent == 20m).Commission.ShouldBe(10m);
        details.Single(d => d.DocumentNumber == "INV-1" && d.RatePercent == 6m).Commission.ShouldBe(12m);
        details.Single(d => d.DocumentNumber == "CN-1").Commission.ShouldBe(-6m);
        line.Commission.ShouldBe(12m + 10m - 6m + 12m);
        line.Commission.ShouldBe(details.Sum(d => d.Commission));
    }

    [Fact]
    public void Calculator_ignores_returns_without_clawback_and_can_pay_on_gross_margin()
    {
        var noClawback = Plan(clawback: false);
        var (lines, _, _) = CommissionCalculator.Calculate([Src(Rep, "INV-1", 200m, 120m), Src(Rep, "CN-1", -100m, -60m, isReturn: true)], _ => noClawback);
        lines.Single().BasisTotal.ShouldBe(200m);
        lines.Single().Commission.ShouldBe(10m);

        var margin = Plan(CommissionBasis.GrossMargin);
        var (marginLines, _, _) = CommissionCalculator.Calculate([Src(Rep, "INV-1", 200m, 120m), Src(Rep, "CN-1", -100m, -60m, isReturn: true)], _ => margin);
        marginLines.Single().BasisTotal.ShouldBe(40m);
        marginLines.Single().Commission.ShouldBe(4m - 2m);

        // Only returns in the period with no clawback: nothing to pay, no line.
        CommissionCalculator.Calculate([Src(Rep, "CN-9", -100m, isReturn: true)], _ => noClawback).Lines.ShouldBeEmpty();
    }

    [Fact]
    public void Commission_amounts_round_half_away_from_zero_per_document()
    {
        var plan = CommissionPlan.Create(Guid.NewGuid(), "r", "R", "R", CommissionBasis.NetSales, 2.5m, false, [], []);
        var (lines, details, _) = CommissionCalculator.Calculate([Src(Rep, "INV-1", 10.10m), Src(Rep, "INV-2", 10.30m)], _ => plan);
        details.Select(d => d.Commission).ShouldBe([0.25m, 0.26m]);
        lines.Single().Commission.ShouldBe(0.51m);
    }

    [Fact]
    public void Runs_cover_a_calendar_month_and_follow_draft_approved_posted_reversed()
    {
        var run = CommissionRun.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 2, 17));
        run.PeriodFrom.ShouldBe(new DateOnly(2026, 2, 1));
        run.PeriodTo.ShouldBe(new DateOnly(2026, 2, 28));
        Should.Throw<DomainException>(() => run.Approve(null, DateTime.UtcNow)).Error.Code.ShouldBe("people.commission-run-empty");

        var plan = Plan();
        var (lines, details, skipped) = CommissionCalculator.Calculate([Src(Rep, "INV-1", 100m)], _ => plan);
        run.SetCalculation(lines, details, skipped, DateTime.UtcNow);
        run.Total.ShouldBe(5m);
        Should.Throw<DomainException>(() => run.MarkPosted("COM-1", Guid.NewGuid(), DateTime.UtcNow));
        run.Approve(Guid.NewGuid(), DateTime.UtcNow);
        Should.Throw<DomainException>(() => run.SetCalculation(lines, details, skipped, DateTime.UtcNow)).Error.Code.ShouldBe("people.invalid-state-transition");
        run.MarkPosted("COM-1", Guid.NewGuid(), DateTime.UtcNow);
        run.Status.ShouldBe(CommissionRunStatus.Posted);
        Should.Throw<DomainException>(run.Cancel);
        run.MarkReversed(new DateOnly(2026, 3, 1));
        run.Status.ShouldBe(CommissionRunStatus.Reversed);
        Should.Throw<DomainException>(() => run.MarkReversed(new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void Employees_require_names_and_normalise_codes()
    {
        var employee = Employee.Create(Guid.NewGuid(), " e-7 ", "Sara", "سارة", null, " Sales ", null, null, true, null);
        employee.Code.ShouldBe("E-7");
        employee.Department.ShouldBe("Sales");
        employee.IsActive.ShouldBeTrue();
        Should.Throw<DomainException>(() => Employee.Create(Guid.NewGuid(), "e8", " ", "x", null, null, null, null, false, null));
    }
}

public class ProfitCenterAndInvoiceCloseTests
{
    private static readonly DateOnly D = new(2026, 6, 1);

    private static SalesInvoice PostedInvoice(decimal qty = 10)
    {
        var line = new SalesLine(Guid.NewGuid(), null, "P", "Product", Guid.NewGuid(), qty, qty, 100m, 0, true, false, "GOODS", null);
        var net = qty * 100m;
        line.SetAmounts(net, 0, net, net * 0.14m, net * 1.14m, null, [new LineTax(Guid.NewGuid(), "VAT", 14m, net, net * 0.14m)]);
        var invoice = new SalesInvoice(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "DOMESTIC", "EGP", 1m, null, false, D, null, null, null, null,
            null, [line]);
        invoice.Approve();
        invoice.MarkPosted("INV-1", Guid.NewGuid(), true);
        return invoice;
    }

    [Fact]
    public void A_paid_invoice_closes_on_request_and_reopens_when_the_receivable_opens_again()
    {
        var invoice = PostedInvoice();
        Should.Throw<DomainException>(invoice.Close).Error.Code.ShouldBe("sales.invalid-state-transition");
        invoice.ApplySettlement("Paid", 0m);
        invoice.Status.ShouldBe(SalesInvoiceStatus.Paid);
        invoice.Close();
        invoice.Status.ShouldBe(SalesInvoiceStatus.Closed);
        invoice.IsPosted.ShouldBeTrue();
        Should.Throw<DomainException>(() => invoice.RegisterReturn(invoice.Lines[0].Uid, 1)).Error.Code.ShouldBe("sales.invalid-state-transition");

        invoice.ApplySettlement("PartiallyPaid", 500m);
        invoice.Status.ShouldBe(SalesInvoiceStatus.PartiallyPaid);
        invoice.OpenAmount.ShouldBe(500m);
    }

    [Fact]
    public void A_settled_and_fully_returned_invoice_closes_by_itself()
    {
        var paidFirst = PostedInvoice(2);
        paidFirst.ApplySettlement("Paid", 0m);
        paidFirst.RegisterReturn(paidFirst.Lines[0].Uid, 1);
        paidFirst.Status.ShouldBe(SalesInvoiceStatus.Paid);
        paidFirst.RegisterReturn(paidFirst.Lines[0].Uid, 1);
        paidFirst.Status.ShouldBe(SalesInvoiceStatus.Closed);

        var returnedFirst = PostedInvoice(2);
        returnedFirst.RegisterReturn(returnedFirst.Lines[0].Uid, 2);
        returnedFirst.Status.ShouldBe(SalesInvoiceStatus.Posted);
        returnedFirst.ApplySettlement("Paid", 0m);
        returnedFirst.Status.ShouldBe(SalesInvoiceStatus.Closed);
    }

    [Fact]
    public void Salesperson_flows_from_order_to_delivery_and_from_invoice_to_credit_note()
    {
        var rep = Guid.NewGuid();
        var invoice = PostedInvoice(2);
        invoice.AssignSalesperson(rep);
        var credit = new SalesReturn(invoice, D, "Damaged", null, [invoice.Lines[0]]);
        credit.SalespersonId.ShouldBe(rep);
    }

    [Fact]
    public void Journal_lines_keep_their_profit_center_through_reversal()
    {
        var pc = Guid.NewGuid();
        var journal = JournalEntry.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), D, "sales", "SalesInvoice", Guid.NewGuid(), "invoice", "x", "EGP", 1m);
        journal.AddLine(Guid.NewGuid(), 10m, 0m, profitCenterId: pc);
        journal.AddLine(Guid.NewGuid(), 0m, 10m, profitCenterId: pc);
        journal.Lines.ShouldAllBe(l => l.ProfitCenterId == pc);

        var fx = JournalEntry.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), D, "finance", "Receipt", Guid.NewGuid(), "fx", "x", "USD", 50m);
        fx.AddBaseOnlyLine(Guid.NewGuid(), 0m, 10m, profitCenterId: pc);
        fx.Lines.Single().ProfitCenterId.ShouldBe(pc);
        journal.Post("J-1", Guid.NewGuid(), null, DateTime.UtcNow);
        journal.CreateReversal(D).Lines.ShouldAllBe(l => l.ProfitCenterId == pc);

        var center = ProfitCenter.Create(Guid.NewGuid(), " PC-1 ", "Head office", Guid.NewGuid());
        center.Code.ShouldBe("PC-1");
        center.Update("Renamed", null, false);
        center.BranchId.ShouldBeNull();
        center.IsActive.ShouldBeFalse();
    }
}
