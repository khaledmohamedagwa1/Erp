using Erp.Modules.FixedAssets.Domain;
using Erp.Modules.Reporting.Application;
using Erp.Modules.Reporting.Domain;
using Erp.Modules.Workflow.Contracts;
using Erp.Modules.Workflow.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class CronExpressionTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0 6 * * *", "2026-11-27T05:59", "2026-11-27T06:00")]
    [InlineData("0 6 * * *", "2026-11-27T06:00", "2026-11-28T06:00")]
    [InlineData("*/15 * * * *", "2026-11-27T10:07", "2026-11-27T10:15")]
    [InlineData("30 8 * * 1-5", "2026-11-27T09:00", "2026-11-30T08:30")] // Friday after the slot → Monday
    [InlineData("0 0 1 * *", "2026-11-27T00:00", "2026-12-01T00:00")]
    [InlineData("0 7 1 1 *", "2026-11-27T00:00", "2027-01-01T07:00")]
    [InlineData("0 9 13 * 5", "2026-11-14T00:00", "2026-11-20T09:00")] // day-of-month OR day-of-week (Friday 20th comes before the 13th)
    [InlineData("0 12 * * 7", "2026-11-27T00:00", "2026-11-29T12:00")] // 7 = Sunday
    public void Next_finds_the_following_matching_minute(string cron, string after, string expected)
    {
        var next = CronExpression.Parse(cron).Next(DateTime.SpecifyKind(DateTime.Parse(after, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc));
        next.ShouldBe(DateTime.SpecifyKind(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("every day")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("* * * *")]
    public void Invalid_expressions_are_rejected(string cron) => CronExpression.TryParse(cron, out _).ShouldBeFalse();

    [Fact]
    public void A_schedule_claims_its_occurrence_by_moving_the_next_run()
    {
        var created = Utc(2026, 11, 27, 5);
        var schedule = ReportSchedule.Create(Guid.NewGuid(), Guid.NewGuid(), "Daily", "sales/summary", new Dictionary<string, string>(), "CSV", null, "0 6 * * *",
            ["a@b.example", "A@B.example", " "], true, created);
        schedule.Recipients.ShouldBe(["a@b.example"]);
        schedule.Format.ShouldBe("csv");
        schedule.NextRunAtUtc.ShouldBe(Utc(2026, 11, 27, 6));
        schedule.IsDue(Utc(2026, 11, 27, 5, 59)).ShouldBeFalse();
        schedule.IsDue(Utc(2026, 11, 27, 6, 1)).ShouldBeTrue();
        schedule.Advance(Utc(2026, 11, 27, 6, 1));
        schedule.NextRunAtUtc.ShouldBe(Utc(2026, 11, 28, 6));
        Should.Throw<DomainException>(() => schedule.Update("x", "sales/summary", new Dictionary<string, string>(), "docx", null, "0 6 * * *", [], true, created));
    }
}

public class ReportParameterParserTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    [Theory]
    [InlineData("today", "2026-11-27")]
    [InlineData("yesterday", "2026-11-26")]
    [InlineData("month-start", "2026-11-01")]
    [InlineData("year-start", "2026-01-01")]
    [InlineData("-30d", "2026-10-28")]
    [InlineData("-3m", "2026-08-27")]
    [InlineData("2026-02-03", "2026-02-03")]
    public void Relative_and_absolute_dates_resolve_against_today(string value, string expected) =>
        ReportParameterParser.ParseDate(value, Today).ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Parameters_map_to_a_report_request_and_unknown_or_invalid_ones_fail()
    {
        var warehouse = Guid.NewGuid();
        var request = ReportParameterParser.ToRequest(new Dictionary<string, string> { ["from"] = "-7d", ["warehouseId"] = warehouse.ToString(), ["groupBy"] = "day" }, Today, 25);
        request.IsSuccess.ShouldBeTrue();
        request.Value.From.ShouldBe(new DateOnly(2026, 11, 20));
        request.Value.WarehouseId.ShouldBe(warehouse);
        request.Value.GroupBy.ShouldBe("day");
        request.Value.PageSize.ShouldBe(25);

        ReportParameterParser.ToRequest(new Dictionary<string, string> { ["colour"] = "red" }, Today, 1).Error.Code.ShouldBe("reporting.parameter-invalid");
        ReportParameterParser.ToRequest(new Dictionary<string, string> { ["customerId"] = "abc" }, Today, 1).Error.Code.ShouldBe("reporting.parameter-invalid");
        ReportParameterParser.ToRequest(new Dictionary<string, string> { ["to"] = "next week" }, Today, 1).IsFailure.ShouldBeTrue();
    }
}

public class DashboardDomainTests
{
    [Fact]
    public void Widgets_get_ids_and_must_fit_the_grid_and_sharing_is_by_role()
    {
        var owner = Guid.NewGuid();
        var role = Guid.NewGuid();
        var dashboard = Dashboard.Create(Guid.NewGuid(), owner, "Sales", null, [role, role, Guid.Empty],
            [new DashboardWidget { Title = "Revenue", ReportKey = "Sales/Summary", Visualization = WidgetVisualization.Kpi, RowLimit = 10_000 }]);
        dashboard.SharedWithRoleIds.ShouldBe([role]);
        dashboard.Widgets[0].WidgetId.ShouldNotBe(Guid.Empty);
        dashboard.Widgets[0].ReportKey.ShouldBe("sales/summary");
        dashboard.Widgets[0].RowLimit.ShouldBe(500);
        dashboard.IsVisibleTo(owner, []).ShouldBeTrue();
        dashboard.IsVisibleTo(Guid.NewGuid(), [role]).ShouldBeTrue();
        dashboard.IsVisibleTo(Guid.NewGuid(), [Guid.NewGuid()]).ShouldBeFalse();
        Should.Throw<DomainException>(() => dashboard.Update("Sales", null, [], [new DashboardWidget { Title = "Wide", ReportKey = "sales/summary", Width = 13 }]));
    }
}

public class WorkflowConditionGroupTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Role = Guid.NewGuid();

    private static ApprovalFacts Facts(decimal amount, Dictionary<string, string>? text = null) => new(amount, null, new Dictionary<string, decimal>(), text ?? []);

    [Fact]
    public void Groups_are_or_ed_and_conditions_within_a_group_are_and_ed()
    {
        var definition = ApprovalDefinition.Create(Company, ApprovalDocumentTypes.SalesInvoice, "Mixed", 1,
        [
            new ApprovalCondition("amount", ConditionOperator.GreaterThan, "50000", 1),
            new ApprovalCondition("channel", ConditionOperator.Equal, "export", 1),
            new ApprovalCondition("amount", ConditionOperator.GreaterOrEqual, "200000", 2),
        ],
        [new ApprovalStep(1, ApproverKind.Role, Role, 1)]);

        definition.Matches(Facts(60_000m, new Dictionary<string, string> { ["channel"] = "export" })).ShouldBeTrue();
        definition.Matches(Facts(60_000m, new Dictionary<string, string> { ["channel"] = "local" })).ShouldBeFalse();
        definition.Matches(Facts(250_000m, new Dictionary<string, string> { ["channel"] = "local" })).ShouldBeTrue();
    }

    [Fact]
    public void Ungrouped_conditions_keep_and_logic()
    {
        var definition = ApprovalDefinition.Create(Company, ApprovalDocumentTypes.SalesInvoice, "And", 1,
            [new ApprovalCondition("amount", ConditionOperator.GreaterThan, "1000"), new ApprovalCondition("amount", ConditionOperator.LessThan, "2000")],
            [new ApprovalStep(1, ApproverKind.Role, Role, 1)]);
        definition.Matches(Facts(1500m)).ShouldBeTrue();
        definition.Matches(Facts(2500m)).ShouldBeFalse();
        Should.Throw<DomainException>(() => new ApprovalCondition("amount", ConditionOperator.GreaterThan, "1", 101));
    }

    [Fact]
    public void Reassigning_escalation_removes_the_original_and_delegated_approvers()
    {
        var target = Guid.NewGuid();
        var delegateUser = Guid.NewGuid();
        var member = Guid.NewGuid();
        var now = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        var definition = ApprovalDefinition.Create(Company, ApprovalDocumentTypes.SalesInvoice, "Reassign", 1, [],
            [new ApprovalStep(1, ApproverKind.Role, Role, 1, 4, ApproverKind.User, target, EscalationMode.Reassign)]);
        var instance = ApprovalInstance.Start(definition, new ApprovalRequest(ApprovalDocumentTypes.SalesInvoice, Guid.NewGuid(), "D-1", Company, 10m, "EGP", Guid.NewGuid()),
            10m, "{}", now);
        instance.Delegate(member, new Eligibility(true, null), delegateUser, null, now).IsSuccess.ShouldBeTrue();
        instance.IsDirectApprover(member, [Role]).ShouldBeTrue();
        instance.IsDirectApprover(delegateUser, []).ShouldBeTrue();

        instance.Escalate(now.AddHours(5)).ShouldBeTrue();
        instance.IsDirectApprover(member, [Role]).ShouldBeFalse();
        instance.IsDirectApprover(delegateUser, []).ShouldBeFalse();
        instance.IsDirectApprover(target, []).ShouldBeTrue();
        instance.Current.IsReassigned.ShouldBeTrue();
    }
}

public class AssetWave6Tests
{
    private static Asset ActiveAsset(decimal cost = 12_000m, int life = 12, decimal quantity = 1)
    {
        var category = AssetCategory.Create(Guid.NewGuid(), "VEH", "Vehicles", "مركبات", DepreciationMethod.StraightLine, life, 0, null, null);
        var asset = Asset.CreateDraft(Guid.NewGuid(), category, Guid.NewGuid(), "Truck", "شاحنة", null, null, null, new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5),
            cost, 0, life, null, null, null, quantity);
        asset.Activate("FA-1", AcquisitionMode.ManualJournal, Guid.NewGuid(), null, null, null, DateTime.UtcNow);
        return asset;
    }

    [Fact]
    public void Value_changes_split_between_reserve_and_profit_or_loss()
    {
        var asset = ActiveAsset();
        var up = asset.PlanValueChange(14_000m, null);
        up.ShouldBe(new ValueChangePlan(2_000m, 2_000m, 0m));
        asset.Revalue(new DateOnly(2026, 1, 31), 14_000m, null, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, up);

        var down = asset.PlanValueChange(11_000m, null);
        down.ShouldBe(new ValueChangePlan(-3_000m, -2_000m, -1_000m));
        asset.Revalue(new DateOnly(2026, 1, 31), 11_000m, null, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, down);
        asset.RevaluationReserve.ShouldBe(0m);
        asset.RevaluationLossInProfit.ShouldBe(1_000m);

        asset.PlanValueChange(12_500m, null).ShouldBe(new ValueChangePlan(1_500m, 500m, 1_000m));
        Should.Throw<DomainException>(() => asset.PlanValueChange(11_000m, null, impairment: true));
    }

    [Fact]
    public void Partial_disposal_removes_proportional_cost_depreciation_and_quantity()
    {
        var asset = ActiveAsset(quantity: 10);
        asset.PostDepreciation(new DateOnly(2026, 3, 1), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);
        var plan = asset.PlanPartialDisposal(new DateOnly(2026, 4, 1), 0.25m, 2_000m);
        plan.Cost.ShouldBe(3_000m);
        plan.Accumulated.ShouldBe(750m);
        plan.GainLoss.ShouldBe(-250m);
        asset.DisposePartially(new DateOnly(2026, 4, 1), plan, Guid.NewGuid(), DateTime.UtcNow);
        asset.Quantity.ShouldBe(7.5m);
        asset.Cost.ShouldBe(9_000m);
        asset.AccumulatedDepreciation.ShouldBe(2_250m);
        asset.Schedule.Where(s => !s.IsPosted).Sum(s => s.Amount).ShouldBe(6_750m);
        Should.Throw<DomainException>(() => asset.PlanPartialDisposal(new DateOnly(2026, 4, 1), 1m, 0m));
    }

    [Fact]
    public void An_additional_book_has_its_own_schedule_and_values_as_of_a_date()
    {
        var asset = ActiveAsset();
        var bookId = Guid.NewGuid();
        asset.SetBook(bookId, DepreciationMethod.StraightLine, 6, null, null, null, null);
        var book = asset.Books.Single();
        book.Schedule.Count.ShouldBe(6);
        book.AccumulatedAsOf(new DateOnly(2026, 2, 28)).ShouldBe(4_000m);
        book.CostAsOf(new DateOnly(2026, 2, 28)).ShouldBe(12_000m);
        book.CostAsOf(new DateOnly(2025, 12, 31)).ShouldBe(0m);
        Should.Throw<DomainException>(() => asset.SetBook(bookId, DepreciationMethod.UnitsOfProduction, 6, null, null, null, null));
    }

    [Fact]
    public void A_transferred_asset_keeps_cost_and_depreciation_with_the_remaining_life()
    {
        var source = ActiveAsset();
        source.PostDepreciation(new DateOnly(2026, 3, 1), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);
        var target = Asset.CreateTransferredIn(Guid.NewGuid(), source, Guid.NewGuid(), null, new DateOnly(2026, 4, 1), "FA-2", Guid.NewGuid(), DateTime.UtcNow);
        target.AccumulatedDepreciation.ShouldBe(3_000m);
        target.UsefulLifeMonths.ShouldBe(9);
        target.Schedule.Count.ShouldBe(9);
        target.Schedule[0].Period.ShouldBe(new DateOnly(2026, 4, 1));
        source.TransferOut(new DateOnly(2026, 4, 1), Guid.NewGuid(), target.Id, "FA-2", DateTime.UtcNow);
        source.Status.ShouldBe(AssetStatus.Disposed);
        source.DisposalGainLoss.ShouldBe(0m);
    }
}

public class AssetSettingsTests
{
    [Fact]
    public void Last_month_is_due_from_the_run_day()
    {
        var settings = AssetSettings.Create(Guid.NewGuid());
        settings.DuePeriod(new DateOnly(2026, 2, 3)).ShouldBeNull();
        settings.Update(true, 3);
        settings.DuePeriod(new DateOnly(2026, 2, 2)).ShouldBeNull();
        settings.DuePeriod(new DateOnly(2026, 2, 3)).ShouldBe(new DateOnly(2026, 1, 1));
        Should.Throw<DomainException>(() => settings.Update(true, 29));
    }
}
