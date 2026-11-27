using Erp.Modules.Workflow.Contracts;
using Erp.Modules.Workflow.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class WorkflowDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid FinanceRole = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();

    private static ApprovalFacts Facts(decimal amount, Guid? branch = null, Dictionary<string, decimal>? numeric = null, Dictionary<string, string>? text = null) =>
        new(amount, branch, numeric ?? [], text ?? []);

    private static ApprovalStep RoleStep(int sequence = 1, int quorum = 1, int? escalateHours = null, Guid? escalateTo = null) =>
        new(sequence, ApproverKind.Role, FinanceRole, quorum, escalateHours, escalateHours is null ? null : ApproverKind.User, escalateTo);

    private static ApprovalDefinition Definition(string name, int priority, IEnumerable<ApprovalCondition> conditions, params ApprovalStep[] steps) =>
        ApprovalDefinition.Create(Company, ApprovalDocumentTypes.SalesInvoice, name, priority, conditions, steps.Length == 0 ? [RoleStep()] : steps);

    private static ApprovalRequest Request(decimal amount = 60_000m) =>
        new(ApprovalDocumentTypes.SalesInvoice, Guid.NewGuid(), "DRAFT-1", Company, amount, "EGP", Requester);

    private static ApprovalInstance Start(ApprovalDefinition definition, decimal amount = 60_000m) =>
        ApprovalInstance.Start(definition, Request(amount), amount, "{}", Now);

    [Theory]
    [InlineData(ConditionOperator.GreaterThan, "50000", 50_000.01, true)]
    [InlineData(ConditionOperator.GreaterThan, "50000", 50_000, false)]
    [InlineData(ConditionOperator.GreaterOrEqual, "50000", 50_000, true)]
    [InlineData(ConditionOperator.LessThan, "1000", 999.99, true)]
    [InlineData(ConditionOperator.LessOrEqual, "1000", 1000.01, false)]
    [InlineData(ConditionOperator.Equal, "250", 250, true)]
    [InlineData(ConditionOperator.In, "100, 250, 400", 250, true)]
    public void Amount_conditions_compare_the_base_currency_amount(ConditionOperator op, string value, double amount, bool expected) =>
        new ApprovalCondition("amount", op, value).IsSatisfiedBy(Facts((decimal)amount)).ShouldBe(expected);

    [Fact]
    public void Branch_numeric_and_text_facts_are_evaluated_and_missing_facts_never_match()
    {
        var branch = Guid.NewGuid();
        new ApprovalCondition("branch", ConditionOperator.In, $"{Guid.NewGuid()},{branch}").IsSatisfiedBy(Facts(0, branch)).ShouldBeTrue();
        new ApprovalCondition("branch", ConditionOperator.Equal, branch.ToString()).IsSatisfiedBy(Facts(0)).ShouldBeFalse();
        new ApprovalCondition("discountPercent", ConditionOperator.GreaterThan, "10").IsSatisfiedBy(Facts(0, numeric: new() { ["discountPercent"] = 12.5m })).ShouldBeTrue();
        new ApprovalCondition("discountPercent", ConditionOperator.GreaterThan, "10").IsSatisfiedBy(Facts(0)).ShouldBeFalse();
        new ApprovalCondition("customerGroup", ConditionOperator.In, "VIP, KEY").IsSatisfiedBy(Facts(0, text: new() { ["customerGroup"] = "key" })).ShouldBeTrue();
    }

    [Fact]
    public void Numeric_operators_reject_non_numeric_values() =>
        Should.Throw<DomainException>(() => new ApprovalCondition("amount", ConditionOperator.GreaterThan, "a lot"));

    [Fact]
    public void The_first_matching_active_definition_by_priority_wins()
    {
        var large = Definition("Large", 1, [new ApprovalCondition("amount", ConditionOperator.GreaterThan, "100000")]);
        var medium = Definition("Medium", 2, [new ApprovalCondition("amount", ConditionOperator.GreaterThan, "50000")]);
        var inactive = Definition("Everything", 0, []);
        inactive.Deactivate();
        var all = new[] { medium, large, inactive };

        ApprovalDefinition.SelectFor(all, ApprovalDocumentTypes.SalesInvoice, Facts(150_000)).ShouldBe(large);
        ApprovalDefinition.SelectFor(all, ApprovalDocumentTypes.SalesInvoice, Facts(60_000)).ShouldBe(medium);
        ApprovalDefinition.SelectFor(all, ApprovalDocumentTypes.SalesInvoice, Facts(10_000)).ShouldBeNull();
        ApprovalDefinition.SelectFor(all, ApprovalDocumentTypes.PurchaseOrder, Facts(150_000)).ShouldBeNull();
    }

    [Fact]
    public void Definitions_need_steps_and_steps_are_renumbered()
    {
        Should.Throw<DomainException>(() => ApprovalDefinition.Create(Company, "x", "No steps", 1, [], []));
        var d = Definition("Two", 1, [], RoleStep(5), new ApprovalStep(9, ApproverKind.User, Manager, 1));
        d.Steps.Select(s => s.Sequence).ShouldBe([1, 2]);
        Should.Throw<DomainException>(() => new ApprovalStep(1, ApproverKind.Role, FinanceRole, 1, escalateAfterHours: 4));
    }

    [Fact]
    public void Requester_can_never_approve_their_own_document_even_on_behalf_of_someone()
    {
        var instance = Start(Definition("Any", 1, []));
        instance.Approve(Requester, new Eligibility(true, null), null, Now).Error.ShouldBe(WorkflowErrors.OwnDocument);
        instance.Approve(Manager, new Eligibility(true, Requester), null, Now).Error.ShouldBe(WorkflowErrors.OwnDocument);
        instance.Approve(Manager, Eligibility.No, null, Now).Error.ShouldBe(WorkflowErrors.NotAnApprover);
        instance.Status.ShouldBe(ApprovalStatus.Pending);
    }

    [Fact]
    public void A_step_completes_only_when_its_quorum_of_distinct_approvers_is_reached()
    {
        var instance = Start(Definition("Quorum", 1, [], RoleStep(quorum: 2)));
        var first = Guid.NewGuid();
        instance.Approve(first, new Eligibility(true, null), "ok", Now).IsSuccess.ShouldBeTrue();
        instance.Approve(first, new Eligibility(true, null), "again", Now).Error.ShouldBe(WorkflowErrors.AlreadyActed);
        instance.Status.ShouldBe(ApprovalStatus.Pending);

        // An approval on behalf of the first approver counts for the first approver, not as a second one.
        instance.Approve(Guid.NewGuid(), new Eligibility(true, first), null, Now).Error.ShouldBe(WorkflowErrors.AlreadyActed);

        instance.Approve(Guid.NewGuid(), new Eligibility(true, null), null, Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        instance.Status.ShouldBe(ApprovalStatus.Approved);
        instance.DomainEvents.OfType<ApprovalCompleted>().ShouldHaveSingleItem().Approved.ShouldBeTrue();
    }

    [Fact]
    public void Steps_run_in_order_and_each_assignment_is_announced()
    {
        var instance = Start(Definition("Two steps", 1, [], RoleStep(1), new ApprovalStep(2, ApproverKind.User, Manager, 1)));
        instance.IsDirectApprover(Manager, []).ShouldBeFalse();
        instance.IsDirectApprover(Guid.NewGuid(), [FinanceRole]).ShouldBeTrue();

        instance.Approve(Guid.NewGuid(), new Eligibility(true, null), null, Now).IsSuccess.ShouldBeTrue();
        instance.CurrentStep.ShouldBe(2);
        instance.IsDirectApprover(Manager, []).ShouldBeTrue();
        instance.DomainEvents.OfType<ApprovalStepAssigned>().Select(e => e.StepSequence).ShouldBe([1, 2]);

        instance.Approve(Manager, new Eligibility(true, null), null, Now).IsSuccess.ShouldBeTrue();
        instance.Status.ShouldBe(ApprovalStatus.Approved);
    }

    [Fact]
    public void Rejection_needs_a_reason_and_ends_the_approval()
    {
        var instance = Start(Definition("Any", 1, []));
        instance.Reject(Manager, new Eligibility(true, null), "  ", Now).Error.ShouldBe(WorkflowErrors.ReasonRequired);
        instance.Reject(Manager, new Eligibility(true, null), "Discount too high", Now).IsSuccess.ShouldBeTrue();
        instance.Status.ShouldBe(ApprovalStatus.Rejected);
        var completed = instance.DomainEvents.OfType<ApprovalCompleted>().Single();
        completed.Approved.ShouldBeFalse();
        completed.Reason.ShouldBe("Discount too high");
        instance.Approve(Guid.NewGuid(), new Eligibility(true, null), null, Now).Error.ShouldBe(WorkflowErrors.NotPending);
    }

    [Fact]
    public void Delegating_a_step_makes_the_delegate_an_approver_but_never_the_requester()
    {
        var instance = Start(Definition("Any", 1, []));
        var deputy = Guid.NewGuid();
        instance.Delegate(Manager, new Eligibility(true, null), Requester, null, Now).Error.ShouldBe(WorkflowErrors.CannotDelegateToRequester);
        instance.Delegate(Manager, new Eligibility(true, null), deputy, "on leave", Now).IsSuccess.ShouldBeTrue();
        instance.IsDirectApprover(deputy, []).ShouldBeTrue();
    }

    [Fact]
    public void Standing_delegations_apply_only_inside_their_window_and_document_type()
    {
        var d = ApprovalDelegation.Create(Company, Manager, Requester, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), ApprovalDocumentTypes.SalesInvoice);
        d.AppliesTo(Requester, ApprovalDocumentTypes.SalesInvoice, new DateOnly(2026, 12, 1)).ShouldBeTrue();
        d.AppliesTo(Requester, ApprovalDocumentTypes.SalesInvoice, new DateOnly(2026, 12, 15)).ShouldBeTrue();
        d.AppliesTo(Requester, ApprovalDocumentTypes.SalesInvoice, new DateOnly(2026, 12, 16)).ShouldBeFalse();
        d.AppliesTo(Requester, ApprovalDocumentTypes.PurchaseOrder, new DateOnly(2026, 12, 5)).ShouldBeFalse();
        d.Revoke();
        d.AppliesTo(Requester, ApprovalDocumentTypes.SalesInvoice, new DateOnly(2026, 12, 5)).ShouldBeFalse();
        Should.Throw<DomainException>(() => ApprovalDelegation.Create(Company, Manager, Manager, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null));
        Should.Throw<DomainException>(() => ApprovalDelegation.Create(Company, Manager, Requester, new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1), null));
    }

    [Fact]
    public void Overdue_steps_escalate_once_to_the_configured_target()
    {
        var director = Guid.NewGuid();
        var instance = Start(Definition("Escalating", 1, [], RoleStep(escalateHours: 24, escalateTo: director)));
        instance.IsEscalationDue(Now.AddHours(23).AddMinutes(59)).ShouldBeFalse();
        instance.Escalate(Now.AddHours(23)).ShouldBeFalse();
        instance.IsDirectApprover(director, []).ShouldBeFalse();

        instance.Escalate(Now.AddHours(24)).ShouldBeTrue();
        instance.IsDirectApprover(director, []).ShouldBeTrue();
        instance.Escalate(Now.AddHours(48)).ShouldBeFalse();
        instance.Actions.Count(a => a.Kind == ApprovalActionKind.Escalated).ShouldBe(1);
    }
}
