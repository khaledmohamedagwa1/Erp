using System.Globalization;
using Erp.Modules.Workflow.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Workflow.Domain;

public static class WorkflowErrors
{
    public static readonly Error DefinitionNotFound = Error.NotFound("workflow.definition-not-found", "The approval definition was not found.");
    public static readonly Error InstanceNotFound = Error.NotFound("workflow.instance-not-found", "The approval instance was not found.");
    public static readonly Error NotPending = Error.Conflict("workflow.not-pending", "The approval is no longer pending.");
    public static readonly Error NotAnApprover = Error.Forbidden("workflow.not-an-approver", "You are not an approver of the current step.");
    public static readonly Error OwnDocument = Error.Forbidden("workflow.own-document", "You cannot approve a document you requested (segregation of duties).");
    public static readonly Error AlreadyActed = Error.Conflict("workflow.already-approved", "You have already approved this step.");
    public static readonly Error ReasonRequired = Error.Validation("workflow.reason-required", "A reason is required when rejecting.");
    public static readonly Error NoSteps = Error.Validation("workflow.steps-required", "An approval definition needs at least one step.");
    public static readonly Error InvalidStep = Error.Validation("workflow.step-invalid", "Each step needs an approver and at least one required approval.");
    public static readonly Error InvalidCondition = Error.Validation("workflow.condition-invalid", "A condition is invalid.");
    public static readonly Error InvalidDelegation = Error.Validation("workflow.delegation-invalid", "A delegation needs a different user and a valid date range.");
    public static readonly Error CannotDelegateToRequester = Error.Validation("workflow.delegate-to-requester", "An approval cannot be delegated to the person who requested it.");
    public static readonly Error ExchangeRateMissing = Error.Validation("workflow.exchange-rate-missing", "No exchange rate exists to convert the document amount to the company's base currency.");
}

public enum ApproverKind
{
    Role = 1,
    User = 2,
}

/// <summary>What happens when a step is escalated.</summary>
public enum EscalationMode
{
    /// <summary>The escalation target becomes an additional approver; the original approvers can still decide.</summary>
    AddApprover = 1,

    /// <summary>The step is reassigned: only the escalation target (and whoever it delegates to) can decide from then on.</summary>
    Reassign = 2,
}

public enum ConditionOperator
{
    GreaterThan = 1,
    GreaterOrEqual = 2,
    LessThan = 3,
    LessOrEqual = 4,
    Equal = 5,
    In = 6,
}

/// <summary>The facts a rule is evaluated against. Amount is always in the company's base currency.</summary>
public sealed record ApprovalFacts(
    decimal AmountBase,
    Guid? BranchId,
    IReadOnlyDictionary<string, decimal> Numeric,
    IReadOnlyDictionary<string, string> Text);

/// <summary>
/// One condition. <see cref="Fact"/> is "amount", "branch", or the name of a numeric/text fact supplied by the module
/// (e.g. "discountPercent"). For <see cref="ConditionOperator.In"/> the value is a comma-separated list.
/// A fact that is not present makes the condition false.
/// <see cref="Group"/> builds OR logic: conditions in the same group must all hold (all-of), and the definition applies when
/// any group holds (any-of). Conditions without a group form group 0, so a definition without groups keeps plain AND logic.
/// </summary>
public sealed class ApprovalCondition
{
    public const string AmountFact = "amount";
    public const string BranchFact = "branch";

    private ApprovalCondition()
    {
    }

    public ApprovalCondition(string fact, ConditionOperator op, string value, int? group = null)
    {
        if (group is < 0 or > 100)
        {
            throw new DomainException(WorkflowErrors.InvalidCondition with { Message = "Condition group must be between 0 and 100." });
        }

        Group = group is null or 0 ? null : group;
        Fact = Guard.MaxLength(Guard.NotEmpty(fact, "workflow.condition-invalid", "Fact"), 100, "workflow.condition-invalid", "Fact");
        Operator = op;
        Value = Guard.MaxLength(Guard.NotEmpty(value, "workflow.condition-invalid", "Value"), 500, "workflow.condition-invalid", "Value");
        if (op is not ConditionOperator.Equal and not ConditionOperator.In && !TryNumber(Value, out _))
        {
            throw new DomainException(WorkflowErrors.InvalidCondition with { Message = $"'{Value}' must be a number for operator {op}." });
        }
    }

    public string Fact { get; private set; } = string.Empty;

    public ConditionOperator Operator { get; private set; }

    public string Value { get; private set; } = string.Empty;

    /// <summary>OR group (null = group 0).</summary>
    public int? Group { get; private set; }

    public int GroupKey => Group ?? 0;

    public bool IsSatisfiedBy(ApprovalFacts facts)
    {
        if (string.Equals(Fact, AmountFact, StringComparison.OrdinalIgnoreCase))
        {
            return CompareNumber(facts.AmountBase);
        }

        if (string.Equals(Fact, BranchFact, StringComparison.OrdinalIgnoreCase))
        {
            return facts.BranchId is { } branch && CompareText(branch.ToString());
        }

        if (facts.Numeric.TryGetValue(Fact, out var number))
        {
            return CompareNumber(number);
        }

        return facts.Text.TryGetValue(Fact, out var text) && CompareText(text);
    }

    private bool CompareNumber(decimal actual)
    {
        if (Operator == ConditionOperator.In)
        {
            return Values().Any(v => TryNumber(v, out var n) && n == actual);
        }

        if (!TryNumber(Value, out var expected))
        {
            return false;
        }

        return Operator switch
        {
            ConditionOperator.GreaterThan => actual > expected,
            ConditionOperator.GreaterOrEqual => actual >= expected,
            ConditionOperator.LessThan => actual < expected,
            ConditionOperator.LessOrEqual => actual <= expected,
            ConditionOperator.Equal => actual == expected,
            _ => false,
        };
    }

    private bool CompareText(string actual) => Operator switch
    {
        ConditionOperator.Equal => string.Equals(actual, Value.Trim(), StringComparison.OrdinalIgnoreCase),
        ConditionOperator.In => Values().Any(v => string.Equals(actual, v, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private IEnumerable<string> Values() => Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryNumber(string value, out decimal number) =>
        decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
}

/// <summary>A step of a definition: who approves, how many approvals are needed, and where it escalates.</summary>
public sealed class ApprovalStep
{
    private ApprovalStep()
    {
    }

    public ApprovalStep(int sequence, ApproverKind approverKind, Guid approverId, int requiredApprovals, int? escalateAfterHours = null,
        ApproverKind? escalateToKind = null, Guid? escalateToId = null, EscalationMode? escalationMode = null)
    {
        if (approverId == Guid.Empty || requiredApprovals < 1 || requiredApprovals > 20 || escalateAfterHours is < 1 ||
            (escalateAfterHours is not null && (escalateToKind is null || escalateToId is null || escalateToId == Guid.Empty)))
        {
            throw new DomainException(WorkflowErrors.InvalidStep);
        }

        Sequence = sequence;
        ApproverKind = approverKind;
        ApproverId = approverId;
        RequiredApprovals = requiredApprovals;
        EscalateAfterHours = escalateAfterHours;
        EscalateToKind = escalateAfterHours is null ? null : escalateToKind;
        EscalateToId = escalateAfterHours is null ? null : escalateToId;
        EscalationMode = escalateAfterHours is null || escalationMode is null or Domain.EscalationMode.AddApprover ? null : escalationMode;
    }

    public int Sequence { get; private set; }

    public ApproverKind ApproverKind { get; private set; }

    public Guid ApproverId { get; private set; }

    /// <summary>Null = <see cref="Domain.EscalationMode.AddApprover"/> (the behaviour before the option existed).</summary>
    public EscalationMode? EscalationMode { get; private set; }

    /// <summary>Quorum: distinct approvers needed before the step completes.</summary>
    public int RequiredApprovals { get; private set; }

    public int? EscalateAfterHours { get; private set; }

    public ApproverKind? EscalateToKind { get; private set; }

    public Guid? EscalateToId { get; private set; }
}

/// <summary>When an approval is needed for a document type and who must give it (Part 16).</summary>
[Audited]
public sealed class ApprovalDefinition : AggregateRoot, ICompanyScoped
{
    private readonly List<ApprovalCondition> _conditions = [];
    private readonly List<ApprovalStep> _steps = [];

    private ApprovalDefinition()
    {
    }

    private ApprovalDefinition(Guid id, Guid companyId, string documentType)
        : base(id)
    {
        CompanyId = companyId;
        DocumentType = documentType;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string DocumentType { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    /// <summary>Lower numbers are evaluated first; the first matching definition wins.</summary>
    public int Priority { get; private set; }

    /// <summary>Any condition group must hold, with every condition of that group (see <see cref="ApprovalCondition.Group"/>). No conditions = always applies.</summary>
    public IReadOnlyList<ApprovalCondition> Conditions => _conditions.AsReadOnly();

    public IReadOnlyList<ApprovalStep> Steps => _steps.OrderBy(s => s.Sequence).ToList();

    public static ApprovalDefinition Create(Guid companyId, string documentType, string name, int priority,
        IEnumerable<ApprovalCondition> conditions, IEnumerable<ApprovalStep> steps)
    {
        var definition = new ApprovalDefinition(SequentialGuid.New(),
            companyId,
            Guard.MaxLength(Guard.NotEmpty(documentType, "workflow.document-type-required", "Document type"), 100, "workflow.document-type-too-long", "Document type"));
        definition.Update(name, priority, conditions, steps);
        return definition;
    }

    public void Update(string name, int priority, IEnumerable<ApprovalCondition> conditions, IEnumerable<ApprovalStep> steps)
    {
        var stepList = steps.ToList();
        if (stepList.Count == 0)
        {
            throw new DomainException(WorkflowErrors.NoSteps);
        }

        Name = Guard.MaxLength(Guard.NotEmpty(name, "workflow.name-required", "Name"), 200, "workflow.name-too-long", "Name");
        Priority = priority;
        _conditions.Clear();
        _conditions.AddRange(conditions);
        _steps.Clear();

        // Renumber 1..n in the given order so sequences are always contiguous.
        var ordered = stepList.OrderBy(s => s.Sequence).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            _steps.Add(new ApprovalStep(i + 1, s.ApproverKind, s.ApproverId, s.RequiredApprovals, s.EscalateAfterHours, s.EscalateToKind, s.EscalateToId, s.EscalationMode));
        }
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    public bool Matches(ApprovalFacts facts) =>
        IsActive && (_conditions.Count == 0 || _conditions.GroupBy(c => c.GroupKey).Any(group => group.All(c => c.IsSatisfiedBy(facts))));

    /// <summary>The first active definition (by priority, then name) whose conditions all hold.</summary>
    public static ApprovalDefinition? SelectFor(IEnumerable<ApprovalDefinition> candidates, string documentType, ApprovalFacts facts) =>
        candidates
            .Where(d => d.DocumentType == documentType)
            .OrderBy(d => d.Priority).ThenBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id)
            .FirstOrDefault(d => d.Matches(facts));
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Withdrawn = 4,
}

public enum ApprovalActionKind
{
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    Delegated = 4,
    Escalated = 5,
    Withdrawn = 6,
    StepCompleted = 7,
}

/// <summary>A copy of a definition step taken at submission, so later definition edits never change an in-flight approval.</summary>
public sealed class InstanceStep
{
    private InstanceStep()
    {
    }

    internal InstanceStep(ApprovalStep step)
    {
        Sequence = step.Sequence;
        ApproverKind = step.ApproverKind;
        ApproverId = step.ApproverId;
        RequiredApprovals = step.RequiredApprovals;
        EscalateAfterHours = step.EscalateAfterHours;
        EscalateToKind = step.EscalateToKind;
        EscalateToId = step.EscalateToId;
        EscalationMode = step.EscalationMode;
    }

    public int Sequence { get; private set; }

    public ApproverKind ApproverKind { get; private set; }

    public Guid ApproverId { get; private set; }

    public int RequiredApprovals { get; private set; }

    public int? EscalateAfterHours { get; private set; }

    public ApproverKind? EscalateToKind { get; private set; }

    public Guid? EscalateToId { get; private set; }

    public EscalationMode? EscalationMode { get; private set; }

    public bool Escalated { get; internal set; }

    /// <summary>True once a <see cref="Domain.EscalationMode.Reassign"/> escalation took the step away from its original approvers.</summary>
    public bool IsReassigned => Escalated && EscalationMode == Domain.EscalationMode.Reassign;
}

/// <summary>An extra approver added to a step by delegation or escalation.</summary>
public sealed class AssignedApprover
{
    private AssignedApprover()
    {
    }

    internal AssignedApprover(int stepSequence, ApproverKind kind, Guid approverId, ApprovalActionKind source)
    {
        StepSequence = stepSequence;
        Kind = kind;
        ApproverId = approverId;
        Source = source;
    }

    public int StepSequence { get; private set; }

    public ApproverKind Kind { get; private set; }

    public Guid ApproverId { get; private set; }

    public ApprovalActionKind Source { get; private set; }

    /// <summary>Set when a reassigning escalation removed this approver from the step (null = still assigned).</summary>
    public bool? Superseded { get; internal set; }

    public bool IsActive => Superseded != true;
}

public sealed class ApprovalAction
{
    private ApprovalAction()
    {
    }

    internal ApprovalAction(ApprovalActionKind kind, int stepSequence, Guid? userId, Guid? onBehalfOf, Guid? targetId, string? reason, DateTime atUtc)
    {
        Kind = kind;
        StepSequence = stepSequence;
        UserId = userId;
        OnBehalfOf = onBehalfOf;
        TargetId = targetId;
        Reason = reason;
        AtUtc = atUtc;
    }

    public ApprovalActionKind Kind { get; private set; }

    public int StepSequence { get; private set; }

    /// <summary>Who acted (null for system actions such as escalation).</summary>
    public Guid? UserId { get; private set; }

    /// <summary>Set when the user acted under a standing delegation from another approver.</summary>
    public Guid? OnBehalfOf { get; private set; }

    /// <summary>Delegate or escalation target (user or role id).</summary>
    public Guid? TargetId { get; private set; }

    public string? Reason { get; private set; }

    public DateTime AtUtc { get; private set; }
}

/// <summary>Who may act on the current step for a given user: directly (user/role) or on behalf of a delegator.</summary>
/// <param name="Eligible">The user may approve.</param>
/// <param name="OnBehalfOf">Delegator, when eligibility comes from a standing delegation.</param>
public sealed record Eligibility(bool Eligible, Guid? OnBehalfOf)
{
    public static readonly Eligibility No = new(false, null);
}

/// <summary>One run of an approval for one document.</summary>
[Audited]
public sealed class ApprovalInstance : AggregateRoot, ICompanyScoped
{
    private readonly List<InstanceStep> _steps = [];
    private readonly List<AssignedApprover> _assigned = [];
    private readonly List<ApprovalAction> _actions = [];

    private ApprovalInstance()
    {
    }

    private ApprovalInstance(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
    }

    public Guid CompanyId { get; private set; }

    public string DocumentType { get; private set; } = string.Empty;

    public Guid DocumentId { get; private set; }

    public string DocumentNumber { get; private set; } = string.Empty;

    public Guid RequestedBy { get; private set; }

    public Guid DefinitionId { get; private set; }

    public string DefinitionName { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal AmountBase { get; private set; }

    public Guid? BranchId { get; private set; }

    /// <summary>JSON snapshot of the module-supplied numeric and text facts.</summary>
    public string FactsJson { get; private set; } = "{}";

    public ApprovalStatus Status { get; private set; }

    public int CurrentStep { get; private set; }

    public DateTime StepStartedAtUtc { get; private set; }

    public DateTime SubmittedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public IReadOnlyList<InstanceStep> Steps => _steps.OrderBy(s => s.Sequence).ToList();

    public IReadOnlyList<AssignedApprover> AssignedApprovers => _assigned.AsReadOnly();

    public IReadOnlyList<ApprovalAction> Actions => _actions.OrderBy(a => a.AtUtc).ToList();

    public InstanceStep Current => _steps.Single(s => s.Sequence == CurrentStep);

    public static ApprovalInstance Start(ApprovalDefinition definition, ApprovalRequest request, decimal amountBase, string factsJson, DateTime nowUtc)
    {
        var instance = new ApprovalInstance(SequentialGuid.New(), request.CompanyId)
        {
            DocumentType = request.DocumentType,
            DocumentId = request.DocumentId,
            DocumentNumber = Guard.MaxLength(request.DocumentNumber?.Trim() ?? string.Empty, 50, "workflow.document-number-too-long", "Document number"),
            RequestedBy = request.RequestedBy,
            DefinitionId = definition.Id,
            DefinitionName = definition.Name,
            Amount = request.Amount,
            Currency = request.Currency.ToUpperInvariant(),
            AmountBase = amountBase,
            BranchId = request.BranchId,
            FactsJson = factsJson,
            Status = ApprovalStatus.Pending,
            CurrentStep = 1,
            StepStartedAtUtc = nowUtc,
            SubmittedAtUtc = nowUtc,
        };
        instance._steps.AddRange(definition.Steps.Select(s => new InstanceStep(s)));
        instance._actions.Add(new ApprovalAction(ApprovalActionKind.Submitted, 1, request.RequestedBy, null, null, null, nowUtc));
        instance.Raise(new ApprovalStepAssigned(instance.Id, instance.CompanyId, instance.DocumentType, instance.DocumentNumber, 1));
        return instance;
    }

    /// <summary>Direct eligibility for the current step: named user, role membership, or an assigned (delegated/escalated) approver.</summary>
    public bool IsDirectApprover(Guid userId, IReadOnlyCollection<Guid> roleIds)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return false;
        }

        var step = Current;
        return (!step.IsReassigned && Matches(step.ApproverKind, step.ApproverId, userId, roleIds)) ||
               _assigned.Any(a => a.StepSequence == CurrentStep && a.IsActive && Matches(a.Kind, a.ApproverId, userId, roleIds));
    }

    private static bool Matches(ApproverKind kind, Guid approverId, Guid userId, IReadOnlyCollection<Guid> roleIds) =>
        kind == ApproverKind.User ? approverId == userId : roleIds.Contains(approverId);

    public Result Approve(Guid userId, Eligibility eligibility, string? comment, DateTime nowUtc)
    {
        var check = CheckActor(userId, eligibility);
        if (check.IsFailure)
        {
            return check;
        }

        // Quorum counts distinct principals: an approval on behalf of someone counts for that someone.
        var principal = eligibility.OnBehalfOf ?? userId;
        if (_actions.Any(a => a.Kind == ApprovalActionKind.Approved && a.StepSequence == CurrentStep && (a.OnBehalfOf ?? a.UserId) == principal))
        {
            return WorkflowErrors.AlreadyActed;
        }

        _actions.Add(new ApprovalAction(ApprovalActionKind.Approved, CurrentStep, userId, eligibility.OnBehalfOf, null, Trim(comment), nowUtc));
        var approvals = _actions.Count(a => a.Kind == ApprovalActionKind.Approved && a.StepSequence == CurrentStep);
        if (approvals < Current.RequiredApprovals)
        {
            return Result.Success();
        }

        _actions.Add(new ApprovalAction(ApprovalActionKind.StepCompleted, CurrentStep, null, null, null, null, nowUtc));
        if (CurrentStep == _steps.Max(s => s.Sequence))
        {
            Status = ApprovalStatus.Approved;
            CompletedAtUtc = nowUtc;
            Raise(new ApprovalCompleted(DocumentType, DocumentId, true, userId, null));
        }
        else
        {
            CurrentStep++;
            StepStartedAtUtc = nowUtc;
            Raise(new ApprovalStepAssigned(Id, CompanyId, DocumentType, DocumentNumber, CurrentStep));
        }

        return Result.Success();
    }

    public Result Reject(Guid userId, Eligibility eligibility, string? reason, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return WorkflowErrors.ReasonRequired;
        }

        var check = CheckActor(userId, eligibility);
        if (check.IsFailure)
        {
            return check;
        }

        _actions.Add(new ApprovalAction(ApprovalActionKind.Rejected, CurrentStep, userId, eligibility.OnBehalfOf, null, Trim(reason), nowUtc));
        Status = ApprovalStatus.Rejected;
        CompletedAtUtc = nowUtc;
        Raise(new ApprovalCompleted(DocumentType, DocumentId, false, userId, Trim(reason)));
        return Result.Success();
    }

    /// <summary>An eligible approver hands the current step to another user (who then becomes an approver of this step).</summary>
    public Result Delegate(Guid userId, Eligibility eligibility, Guid toUserId, string? comment, DateTime nowUtc)
    {
        var check = CheckActor(userId, eligibility);
        if (check.IsFailure)
        {
            return check;
        }

        if (toUserId == Guid.Empty || toUserId == userId)
        {
            return WorkflowErrors.InvalidDelegation;
        }

        if (toUserId == RequestedBy)
        {
            return WorkflowErrors.CannotDelegateToRequester;
        }

        _assigned.Add(new AssignedApprover(CurrentStep, ApproverKind.User, toUserId, ApprovalActionKind.Delegated));
        _actions.Add(new ApprovalAction(ApprovalActionKind.Delegated, CurrentStep, userId, eligibility.OnBehalfOf, toUserId, Trim(comment), nowUtc));
        Raise(new ApprovalStepAssigned(Id, CompanyId, DocumentType, DocumentNumber, CurrentStep));
        return Result.Success();
    }

    /// <summary>True when the current step has waited longer than its escalation time and has not been escalated yet.</summary>
    public bool IsEscalationDue(DateTime nowUtc) =>
        Status == ApprovalStatus.Pending && Current is { EscalateAfterHours: { } hours, Escalated: false } &&
        nowUtc - StepStartedAtUtc >= TimeSpan.FromHours(hours);

    /// <summary>
    /// Adds the step's escalation target as an approver of the current step or, with <see cref="EscalationMode.Reassign"/>, hands the step
    /// to the target alone (the original and previously assigned approvers can no longer decide). Approvals already given still count
    /// towards the quorum. Returns false when not due.
    /// </summary>
    public bool Escalate(DateTime nowUtc)
    {
        if (!IsEscalationDue(nowUtc))
        {
            return false;
        }

        var step = Current;
        step.Escalated = true;
        if (step.IsReassigned)
        {
            foreach (var assigned in _assigned.Where(a => a.StepSequence == CurrentStep))
            {
                assigned.Superseded = true;
            }
        }

        _assigned.Add(new AssignedApprover(CurrentStep, step.EscalateToKind!.Value, step.EscalateToId!.Value, ApprovalActionKind.Escalated));
        _actions.Add(new ApprovalAction(ApprovalActionKind.Escalated, CurrentStep, null, null, step.EscalateToId, step.IsReassigned ? $"Reassigned: no decision after {step.EscalateAfterHours} h" : $"No decision after {step.EscalateAfterHours} h", nowUtc));
        Raise(new ApprovalStepAssigned(Id, CompanyId, DocumentType, DocumentNumber, CurrentStep));
        return true;
    }

    public void Withdraw(Guid? userId, DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return;
        }

        Status = ApprovalStatus.Withdrawn;
        CompletedAtUtc = nowUtc;
        _actions.Add(new ApprovalAction(ApprovalActionKind.Withdrawn, CurrentStep, userId, null, null, null, nowUtc));
    }

    private Result CheckActor(Guid userId, Eligibility eligibility)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return WorkflowErrors.NotPending;
        }

        // Segregation of duties: the requester can never decide, not even on behalf of someone else, and nobody decides on the requester's behalf.
        if (userId == RequestedBy || eligibility.OnBehalfOf == RequestedBy)
        {
            return WorkflowErrors.OwnDocument;
        }

        return eligibility.Eligible ? Result.Success() : WorkflowErrors.NotAnApprover;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Trim().Length > 1000 ? value.Trim()[..1000] : value.Trim());
}

/// <summary>A standing delegation: while active, <see cref="ToUserId"/> may approve whatever <see cref="FromUserId"/> may approve.</summary>
[Audited]
public sealed class ApprovalDelegation : AggregateRoot, ICompanyScoped
{
    private ApprovalDelegation()
    {
    }

    private ApprovalDelegation(Guid id, Guid companyId, Guid fromUserId, Guid toUserId, DateOnly validFrom, DateOnly validTo, string? documentType)
        : base(id)
    {
        CompanyId = companyId;
        FromUserId = fromUserId;
        ToUserId = toUserId;
        ValidFrom = validFrom;
        ValidTo = validTo;
        DocumentType = documentType;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public Guid FromUserId { get; private set; }

    public Guid ToUserId { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>Inclusive.</summary>
    public DateOnly ValidTo { get; private set; }

    /// <summary>Null = all document types.</summary>
    public string? DocumentType { get; private set; }

    public bool IsActive { get; private set; }

    public static ApprovalDelegation Create(Guid companyId, Guid fromUserId, Guid toUserId, DateOnly validFrom, DateOnly validTo, string? documentType)
    {
        if (fromUserId == Guid.Empty || toUserId == Guid.Empty || fromUserId == toUserId || validTo < validFrom)
        {
            throw new DomainException(WorkflowErrors.InvalidDelegation);
        }

        return new ApprovalDelegation(SequentialGuid.New(), companyId, fromUserId, toUserId, validFrom, validTo,
            string.IsNullOrWhiteSpace(documentType) ? null : documentType.Trim());
    }

    public bool AppliesTo(Guid toUserId, string documentType, DateOnly date) =>
        IsActive && ToUserId == toUserId && date >= ValidFrom && date <= ValidTo &&
        (DocumentType is null || DocumentType == documentType);

    public void Revoke() => IsActive = false;
}

/// <summary>Raised when a step gets (new) approvers: submission, next step, delegation, escalation. Drives notifications.</summary>
public sealed record ApprovalStepAssigned(Guid InstanceId, Guid CompanyId, string DocumentType, string DocumentNumber, int StepSequence) : IDomainEvent;
