using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Documents.Contracts;
using Erp.Modules.Identity.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Tax.Contracts;
using Erp.Modules.Workflow.Contracts;
using Erp.Modules.Workflow.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Workflow.Application;

public interface IWorkflowDbContext
{
    DbSet<ApprovalDefinition> Definitions { get; }

    DbSet<ApprovalInstance> Instances { get; }

    DbSet<ApprovalDelegation> Delegations { get; }

    EntityEntry Entry(object entity);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

internal static class FactsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public sealed record Snapshot(IReadOnlyDictionary<string, decimal> Numeric, IReadOnlyDictionary<string, string> Text);

    public static string Write(ApprovalRequest request) => JsonSerializer.Serialize(
        new Snapshot(request.NumericFacts ?? new Dictionary<string, decimal>(), request.TextFacts ?? new Dictionary<string, string>()), Options);

    public static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

// ---------- The engine (Workflow.Contracts) ----------

/// <summary>
/// Evaluates the company's active definitions for the document type in priority order; the first whose conditions all hold
/// starts an approval instance. Runs inside the caller's transaction, so the instance commits with the document.
/// </summary>
public sealed class ApprovalService(
    IWorkflowDbContext db,
    ITenantContext tenant,
    IOrganizationQueries organization,
    IExchangeRateProvider rates,
    IClock clock) : IApprovalService
{
    public async Task<ApprovalDecision> SubmitAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Approvals require a tenant context.");
        var now = clock.UtcNow;
        var amountBase = await ToBaseAsync(request, DateOnly.FromDateTime(now), cancellationToken);
        var facts = new ApprovalFacts(amountBase, request.BranchId,
            request.NumericFacts ?? new Dictionary<string, decimal>(),
            request.TextFacts ?? new Dictionary<string, string>());

        // A resubmission (e.g. the document was edited) replaces the open approval.
        await WithdrawAsync(request.DocumentType, request.DocumentId, cancellationToken);

        // The caller's company may not be the scope's active company (background posting): filter explicitly, tenant included.
        var candidates = await db.Definitions.IgnoreQueryFilters()
            .Where(d => EF.Property<Guid>(d, "TenantId") == tenantId && d.CompanyId == request.CompanyId && d.DocumentType == request.DocumentType && d.IsActive)
            .ToListAsync(cancellationToken);
        var definition = ApprovalDefinition.SelectFor(candidates, request.DocumentType, facts);
        if (definition is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new ApprovalDecision(false, null);
        }

        var instance = ApprovalInstance.Start(definition, request, amountBase, FactsJson.Write(request), now);
        db.Instances.Add(instance);
        await db.SaveChangesAsync(cancellationToken);
        return new ApprovalDecision(true, instance.Id);
    }

    public async Task WithdrawAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Approvals require a tenant context.");
        var open = await db.Instances.IgnoreQueryFilters()
            .Where(i => EF.Property<Guid>(i, "TenantId") == tenantId && i.DocumentType == documentType && i.DocumentId == documentId && i.Status == ApprovalStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var instance in open)
        {
            instance.Withdraw(null, clock.UtcNow);
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Rules compare amounts in the company's base currency; other currencies are converted at today's spot rate.</summary>
    private async Task<decimal> ToBaseAsync(ApprovalRequest request, DateOnly date, CancellationToken cancellationToken)
    {
        var company = (await organization.GetCompaniesAsync([request.CompanyId], cancellationToken)).FirstOrDefault()
            ?? throw new DomainException(Error.NotFound("organization.company-not-found", "The company was not found."));
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (currency == company.BaseCurrency)
        {
            return request.Amount;
        }

        var rate = await rates.GetRateAsync(currency, company.BaseCurrency, date, ExchangeRateKind.Spot, cancellationToken)
            ?? throw new DomainException(WorkflowErrors.ExchangeRateMissing);
        return Math.Round(request.Amount * rate, 4, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Decides whether a user may act on an instance's current step, directly or through a standing delegation.</summary>
public sealed class EligibilityResolver(IWorkflowDbContext db, IUserDirectory users, IClock clock)
{
    public async Task<Eligibility> ResolveAsync(ApprovalInstance instance, Guid userId, CancellationToken cancellationToken)
    {
        if (instance.Status != ApprovalStatus.Pending)
        {
            return Eligibility.No;
        }

        var roles = await users.GetRoleIdsAsync(userId, cancellationToken);
        if (instance.IsDirectApprover(userId, roles))
        {
            return new Eligibility(true, null);
        }

        var today = DateOnly.FromDateTime(clock.UtcNow);
        var delegations = await db.Delegations.AsNoTracking()
            .Where(d => d.ToUserId == userId && d.IsActive && d.ValidFrom <= today && d.ValidTo >= today && d.CompanyId == instance.CompanyId)
            .ToListAsync(cancellationToken);
        foreach (var delegation in delegations.Where(d => d.AppliesTo(userId, instance.DocumentType, today)))
        {
            var delegatorRoles = await users.GetRoleIdsAsync(delegation.FromUserId, cancellationToken);
            if (instance.IsDirectApprover(delegation.FromUserId, delegatorRoles))
            {
                return new Eligibility(true, delegation.FromUserId);
            }
        }

        return Eligibility.No;
    }
}

// ---------- DTOs ----------
public sealed record ConditionDto(string Fact, string Operator, string Value, int Group);

public sealed record StepDto(int Sequence, string ApproverKind, Guid ApproverId, int RequiredApprovals, int? EscalateAfterHours, string? EscalateToKind, Guid? EscalateToId,
    string EscalationMode);

public sealed record DefinitionDto(Guid Id, string DocumentType, string Name, bool IsActive, int Priority, IReadOnlyList<ConditionDto> Conditions,
    IReadOnlyList<StepDto> Steps, byte[] RowVersion);

public sealed record InstanceStepDto(int Sequence, string ApproverKind, Guid ApproverId, int RequiredApprovals, int Approvals, int? EscalateAfterHours, bool Escalated,
    string EscalationMode, bool Reassigned);

public sealed record AssignedApproverDto(int StepSequence, string Kind, Guid ApproverId, string Source, bool Active);

public sealed record ActionDto(string Kind, int StepSequence, Guid? UserId, Guid? OnBehalfOf, Guid? TargetId, string? Reason, DateTime AtUtc);

public sealed record InstanceDto(
    Guid Id,
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    Guid RequestedBy,
    string DefinitionName,
    decimal Amount,
    string Currency,
    decimal AmountBase,
    Guid? BranchId,
    JsonElement Facts,
    string Status,
    int CurrentStep,
    DateTime SubmittedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<InstanceStepDto> Steps,
    IReadOnlyList<AssignedApproverDto> AssignedApprovers,
    IReadOnlyList<ActionDto> History);

public sealed record InboxItemDto(Guid InstanceId, string DocumentType, Guid DocumentId, string DocumentNumber, Guid RequestedBy, decimal Amount, string Currency,
    decimal AmountBase, int CurrentStep, int StepCount, Guid? OnBehalfOf, DateTime SubmittedAtUtc, DateTime StepStartedAtUtc, JsonElement Facts);

public sealed record DelegationDto(Guid Id, Guid FromUserId, Guid ToUserId, DateOnly ValidFrom, DateOnly ValidTo, string? DocumentType, bool IsActive);

/// <param name="Group">OR group: conditions in the same group must all hold; the rule applies when any group holds. Null/0 = group 0.</param>
public sealed record ConditionInput(string Fact, string Operator, string Value, int? Group = null);

/// <param name="EscalationMode">AddApprover (default) or Reassign.</param>
public sealed record StepInput(int Sequence, string ApproverKind, Guid ApproverId, int RequiredApprovals, int? EscalateAfterHours, string? EscalateToKind, Guid? EscalateToId,
    string? EscalationMode = null);

internal static class WorkflowMapping
{
    public static DefinitionDto ToDto(ApprovalDefinition d, byte[] rowVersion) => new(d.Id, d.DocumentType, d.Name, d.IsActive, d.Priority,
        d.Conditions.Select(c => new ConditionDto(c.Fact, c.Operator.ToString(), c.Value, c.GroupKey)).ToList(),
        d.Steps.Select(s => new StepDto(s.Sequence, s.ApproverKind.ToString(), s.ApproverId, s.RequiredApprovals, s.EscalateAfterHours,
            s.EscalateToKind?.ToString(), s.EscalateToId, (s.EscalationMode ?? EscalationMode.AddApprover).ToString())).ToList(),
        rowVersion);

    public static InstanceDto ToDto(ApprovalInstance i) => new(i.Id, i.DocumentType, i.DocumentId, i.DocumentNumber, i.RequestedBy, i.DefinitionName,
        i.Amount, i.Currency, i.AmountBase, i.BranchId, FactsJson.Read(i.FactsJson), i.Status.ToString(), i.CurrentStep, i.SubmittedAtUtc, i.CompletedAtUtc,
        i.Steps.Select(s => new InstanceStepDto(s.Sequence, s.ApproverKind.ToString(), s.ApproverId, s.RequiredApprovals,
            i.Actions.Count(a => a.Kind == ApprovalActionKind.Approved && a.StepSequence == s.Sequence), s.EscalateAfterHours, s.Escalated,
            (s.EscalationMode ?? EscalationMode.AddApprover).ToString(), s.IsReassigned)).ToList(),
        i.AssignedApprovers.Select(a => new AssignedApproverDto(a.StepSequence, a.Kind.ToString(), a.ApproverId, a.Source.ToString(), a.IsActive)).ToList(),
        i.Actions.Select(a => new ActionDto(a.Kind.ToString(), a.StepSequence, a.UserId, a.OnBehalfOf, a.TargetId, a.Reason, a.AtUtc)).ToList());

    public static DelegationDto ToDto(ApprovalDelegation d) => new(d.Id, d.FromUserId, d.ToUserId, d.ValidFrom, d.ValidTo, d.DocumentType, d.IsActive);

    public static IReadOnlyList<ApprovalCondition> Conditions(IEnumerable<ConditionInput>? inputs) =>
        (inputs ?? []).Select(c => new ApprovalCondition(c.Fact, Enum.Parse<ConditionOperator>(c.Operator, true), c.Value, c.Group)).ToList();

    public static IReadOnlyList<ApprovalStep> Steps(IEnumerable<StepInput>? inputs) =>
        (inputs ?? []).Select(s => new ApprovalStep(s.Sequence, Enum.Parse<ApproverKind>(s.ApproverKind, true), s.ApproverId, s.RequiredApprovals,
            s.EscalateAfterHours, s.EscalateToKind is null ? null : Enum.Parse<ApproverKind>(s.EscalateToKind, true), s.EscalateToId,
            string.IsNullOrWhiteSpace(s.EscalationMode) ? null : Enum.Parse<EscalationMode>(s.EscalationMode, true))).ToList();
}

internal static class WorkflowInputRules
{
    public static void Apply<T>(IRuleBuilderInitial<T, IReadOnlyList<ConditionInput>?> conditions, IRuleBuilderInitial<T, IReadOnlyList<StepInput>> steps)
    {
        conditions.Must(list => list is null || list.All(c => Enum.TryParse<ConditionOperator>(c.Operator, true, out _) && !string.IsNullOrWhiteSpace(c.Fact)))
            .WithMessage("Operators: GreaterThan, GreaterOrEqual, LessThan, LessOrEqual, Equal, In.");
        steps.NotEmpty().Must(list => list is not null && list.All(s =>
                Enum.TryParse<ApproverKind>(s.ApproverKind, true, out _) &&
                (s.EscalateToKind is null || Enum.TryParse<ApproverKind>(s.EscalateToKind, true, out _)) &&
                (string.IsNullOrWhiteSpace(s.EscalationMode) || Enum.TryParse<EscalationMode>(s.EscalationMode, true, out _))))
            .WithMessage("Approver kinds: Role, User. Escalation modes: AddApprover, Reassign.");
    }
}

// ---------- Definitions ----------
public sealed record CreateDefinitionCommand(string DocumentType, string Name, int Priority, IReadOnlyList<ConditionInput>? Conditions, IReadOnlyList<StepInput> Steps)
    : ICommand<DefinitionDto>;

public sealed class CreateDefinitionValidator : AbstractValidator<CreateDefinitionCommand>
{
    public CreateDefinitionValidator()
    {
        RuleFor(x => x.DocumentType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        WorkflowInputRules.Apply(RuleFor(x => x.Conditions), RuleFor(x => x.Steps));
    }
}

public sealed class CreateDefinitionHandler(IWorkflowDbContext db, ITenantContext tenant) : ICommandHandler<CreateDefinitionCommand, DefinitionDto>
{
    public async Task<Result<DefinitionDto>> Handle(CreateDefinitionCommand command, CancellationToken cancellationToken)
    {
        var definition = ApprovalDefinition.Create(tenant.CompanyId!.Value, command.DocumentType, command.Name, command.Priority,
            WorkflowMapping.Conditions(command.Conditions), WorkflowMapping.Steps(command.Steps));
        db.Definitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken);
        return WorkflowMapping.ToDto(definition, RowVersion(db, definition));
    }

    internal static byte[] RowVersion(IWorkflowDbContext db, object entity) => (byte[])db.Entry(entity).Property("RowVersion").CurrentValue!;
}

public sealed record UpdateDefinitionCommand(Guid Id, string Name, int Priority, IReadOnlyList<ConditionInput>? Conditions, IReadOnlyList<StepInput> Steps, byte[] ExpectedRowVersion)
    : ICommand<DefinitionDto>;

public sealed class UpdateDefinitionValidator : AbstractValidator<UpdateDefinitionCommand>
{
    public UpdateDefinitionValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        WorkflowInputRules.Apply(RuleFor(x => x.Conditions), RuleFor(x => x.Steps));
    }
}

public sealed class UpdateDefinitionHandler(IWorkflowDbContext db) : ICommandHandler<UpdateDefinitionCommand, DefinitionDto>
{
    public async Task<Result<DefinitionDto>> Handle(UpdateDefinitionCommand command, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (definition is null)
        {
            return WorkflowErrors.DefinitionNotFound;
        }

        if (!CreateDefinitionHandler.RowVersion(db, definition).AsSpan().SequenceEqual(command.ExpectedRowVersion))
        {
            return new Error("concurrency-conflict", "The definition was changed by someone else. Reload and try again.", ErrorType.PreconditionFailed);
        }

        db.Entry(definition).Property("RowVersion").OriginalValue = command.ExpectedRowVersion;
        definition.Update(command.Name, command.Priority, WorkflowMapping.Conditions(command.Conditions), WorkflowMapping.Steps(command.Steps));
        await db.SaveChangesAsync(cancellationToken);
        return WorkflowMapping.ToDto(definition, CreateDefinitionHandler.RowVersion(db, definition));
    }
}

public sealed record SetDefinitionActiveCommand(Guid Id, bool Active) : ICommand<DefinitionDto>;

public sealed class SetDefinitionActiveHandler(IWorkflowDbContext db) : ICommandHandler<SetDefinitionActiveCommand, DefinitionDto>
{
    public async Task<Result<DefinitionDto>> Handle(SetDefinitionActiveCommand command, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (definition is null)
        {
            return WorkflowErrors.DefinitionNotFound;
        }

        if (command.Active)
        {
            definition.Activate();
        }
        else
        {
            definition.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);
        return WorkflowMapping.ToDto(definition, CreateDefinitionHandler.RowVersion(db, definition));
    }
}

/// <summary>Deleting is safe: running instances keep their own snapshot of the steps.</summary>
public sealed record DeleteDefinitionCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteDefinitionHandler(IWorkflowDbContext db) : ICommandHandler<DeleteDefinitionCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteDefinitionCommand command, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (definition is null)
        {
            return WorkflowErrors.DefinitionNotFound;
        }

        db.Definitions.Remove(definition);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

public sealed record ListDefinitionsQuery(string? DocumentType) : IQuery<IReadOnlyList<DefinitionDto>>;

public sealed class ListDefinitionsHandler(IWorkflowDbContext db) : IQueryHandler<ListDefinitionsQuery, IReadOnlyList<DefinitionDto>>
{
    public async Task<Result<IReadOnlyList<DefinitionDto>>> Handle(ListDefinitionsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Definitions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.DocumentType))
        {
            source = source.Where(d => d.DocumentType == query.DocumentType);
        }

        var list = await source.OrderBy(d => d.DocumentType).ThenBy(d => d.Priority).ThenBy(d => d.Name).ToListAsync(cancellationToken);
        return list.Select(d => WorkflowMapping.ToDto(d, CreateDefinitionHandler.RowVersion(db, d))).ToList();
    }
}

public sealed record GetDefinitionQuery(Guid Id) : IQuery<DefinitionDto>;

public sealed class GetDefinitionHandler(IWorkflowDbContext db) : IQueryHandler<GetDefinitionQuery, DefinitionDto>
{
    public async Task<Result<DefinitionDto>> Handle(GetDefinitionQuery query, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.FirstOrDefaultAsync(d => d.Id == query.Id, cancellationToken);
        return definition is null ? WorkflowErrors.DefinitionNotFound : WorkflowMapping.ToDto(definition, CreateDefinitionHandler.RowVersion(db, definition));
    }
}

// ---------- Decisions ----------
public enum DecisionKind
{
    Approve,
    Reject,
    Delegate,
}

public sealed record DecideCommand(Guid InstanceId, DecisionKind Kind, string? Comment, Guid? DelegateToUserId = null) : ICommand<InstanceDto>;

public sealed class DecideHandler(IWorkflowDbContext db, ICurrentUser currentUser, EligibilityResolver eligibility, IClock clock)
    : ICommandHandler<DecideCommand, InstanceDto>
{
    public async Task<Result<InstanceDto>> Handle(DecideCommand command, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == command.InstanceId, cancellationToken);
        if (instance is null)
        {
            return WorkflowErrors.InstanceNotFound;
        }

        var userId = currentUser.UserId!.Value;
        var eligible = await eligibility.ResolveAsync(instance, userId, cancellationToken);
        var now = clock.UtcNow;
        var result = command.Kind switch
        {
            DecisionKind.Approve => instance.Approve(userId, eligible, command.Comment, now),
            DecisionKind.Reject => instance.Reject(userId, eligible, command.Comment, now),
            _ => instance.Delegate(userId, eligible, command.DelegateToUserId ?? Guid.Empty, command.Comment, now),
        };
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return WorkflowMapping.ToDto(instance);
    }
}

// ---------- Instances & inbox ----------
public sealed record GetInstanceQuery(Guid Id) : IQuery<InstanceDto>;

public sealed class GetInstanceHandler(IWorkflowDbContext db) : IQueryHandler<GetInstanceQuery, InstanceDto>
{
    public async Task<Result<InstanceDto>> Handle(GetInstanceQuery query, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == query.Id, cancellationToken);
        return instance is null ? WorkflowErrors.InstanceNotFound : WorkflowMapping.ToDto(instance);
    }
}

public sealed record ListInstancesQuery(PageRequest Page, string? Status, string? DocumentType, Guid? DocumentId) : IQuery<PagedResult<InstanceDto>>;

public sealed class ListInstancesHandler(IWorkflowDbContext db) : IQueryHandler<ListInstancesQuery, PagedResult<InstanceDto>>
{
    public async Task<Result<PagedResult<InstanceDto>>> Handle(ListInstancesQuery query, CancellationToken cancellationToken)
    {
        var source = db.Instances.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<ApprovalStatus>(query.Status, true, out var status))
        {
            source = source.Where(i => i.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.DocumentType))
        {
            source = source.Where(i => i.DocumentType == query.DocumentType);
        }

        if (query.DocumentId is { } documentId)
        {
            source = source.Where(i => i.DocumentId == documentId);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(i => i.SubmittedAtUtc).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<InstanceDto>(items.Select(WorkflowMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

/// <summary>Pending approvals in the active company the current user may decide, directly or on behalf of a delegator.</summary>
public sealed record InboxQuery : IQuery<IReadOnlyList<InboxItemDto>>;

public sealed class InboxHandler(IWorkflowDbContext db, ICurrentUser currentUser, EligibilityResolver eligibility)
    : IQueryHandler<InboxQuery, IReadOnlyList<InboxItemDto>>
{
    public async Task<Result<IReadOnlyList<InboxItemDto>>> Handle(InboxQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var pending = await db.Instances.AsNoTracking()
            .Where(i => i.Status == ApprovalStatus.Pending && i.RequestedBy != userId)
            .OrderBy(i => i.SubmittedAtUtc)
            .ToListAsync(cancellationToken);
        var items = new List<InboxItemDto>();
        foreach (var instance in pending)
        {
            var e = await eligibility.ResolveAsync(instance, userId, cancellationToken);
            if (!e.Eligible || e.OnBehalfOf == instance.RequestedBy)
            {
                continue;
            }

            // Hide steps the user (or their principal) already approved while the quorum is still open.
            var principal = e.OnBehalfOf ?? userId;
            if (instance.Actions.Any(a => a.Kind == ApprovalActionKind.Approved && a.StepSequence == instance.CurrentStep && (a.OnBehalfOf ?? a.UserId) == principal))
            {
                continue;
            }

            items.Add(new InboxItemDto(instance.Id, instance.DocumentType, instance.DocumentId, instance.DocumentNumber, instance.RequestedBy,
                instance.Amount, instance.Currency, instance.AmountBase, instance.CurrentStep, instance.Steps.Count, e.OnBehalfOf,
                instance.SubmittedAtUtc, instance.StepStartedAtUtc, FactsJson.Read(instance.FactsJson)));
        }

        return items;
    }
}

// ---------- Delegations ----------

/// <param name="FromUserId">Defaults to the current user. Delegating for someone else requires workflow.definition.manage (checked by the endpoint).</param>
public sealed record CreateDelegationCommand(Guid? FromUserId, Guid ToUserId, DateOnly ValidFrom, DateOnly ValidTo, string? DocumentType) : ICommand<DelegationDto>;

public sealed class CreateDelegationValidator : AbstractValidator<CreateDelegationCommand>
{
    public CreateDelegationValidator()
    {
        RuleFor(x => x.ToUserId).NotEmpty();
        RuleFor(x => x.ValidTo).GreaterThanOrEqualTo(x => x.ValidFrom);
        RuleFor(x => x.DocumentType).MaximumLength(100);
    }
}

public sealed class CreateDelegationHandler(IWorkflowDbContext db, ITenantContext tenant, ICurrentUser currentUser, IUserDirectory users)
    : ICommandHandler<CreateDelegationCommand, DelegationDto>
{
    public async Task<Result<DelegationDto>> Handle(CreateDelegationCommand command, CancellationToken cancellationToken)
    {
        var from = command.FromUserId ?? currentUser.UserId!.Value;
        var known = await users.GetUsersAsync([from, command.ToUserId], cancellationToken);
        if (known.Count(u => u.IsActive) < 2 && from != command.ToUserId)
        {
            return Error.NotFound("identity.user-not-found", "The user was not found.");
        }

        var delegation = ApprovalDelegation.Create(tenant.CompanyId!.Value, from, command.ToUserId, command.ValidFrom, command.ValidTo, command.DocumentType);
        db.Delegations.Add(delegation);
        await db.SaveChangesAsync(cancellationToken);
        return WorkflowMapping.ToDto(delegation);
    }
}

public sealed record RevokeDelegationCommand(Guid Id, bool CanManageOthers) : ICommand<DelegationDto>;

public sealed class RevokeDelegationHandler(IWorkflowDbContext db, ICurrentUser currentUser) : ICommandHandler<RevokeDelegationCommand, DelegationDto>
{
    public async Task<Result<DelegationDto>> Handle(RevokeDelegationCommand command, CancellationToken cancellationToken)
    {
        var delegation = await db.Delegations.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (delegation is null || (!command.CanManageOthers && delegation.FromUserId != currentUser.UserId))
        {
            return Error.NotFound("workflow.delegation-not-found", "The delegation was not found.");
        }

        delegation.Revoke();
        await db.SaveChangesAsync(cancellationToken);
        return WorkflowMapping.ToDto(delegation);
    }
}

/// <summary>Delegations given by or to the current user (all of the company's for administrators).</summary>
public sealed record ListDelegationsQuery(bool All) : IQuery<IReadOnlyList<DelegationDto>>;

public sealed class ListDelegationsHandler(IWorkflowDbContext db, ICurrentUser currentUser) : IQueryHandler<ListDelegationsQuery, IReadOnlyList<DelegationDto>>
{
    public async Task<Result<IReadOnlyList<DelegationDto>>> Handle(ListDelegationsQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var source = db.Delegations.AsNoTracking();
        if (!query.All)
        {
            source = source.Where(d => d.FromUserId == userId || d.ToUserId == userId);
        }

        var list = await source.OrderByDescending(d => d.ValidFrom).ToListAsync(cancellationToken);
        return list.Select(WorkflowMapping.ToDto).ToList();
    }
}

// ---------- Escalation ----------

/// <param name="AsOfUtc">Evaluation time; null = now. Only trusted callers (tests, jobs) set it — the endpoint never does.</param>
public sealed record EscalateOverdueCommand(DateTime? AsOfUtc = null) : ICommand<int>;

/// <summary>Escalates every overdue pending step of the current tenant (all companies).</summary>
public sealed class EscalateOverdueHandler(IWorkflowDbContext db, ITenantContext tenant, IClock clock) : ICommandHandler<EscalateOverdueCommand, int>
{
    public async Task<Result<int>> Handle(EscalateOverdueCommand command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant context required.");
        var now = command.AsOfUtc ?? clock.UtcNow;
        var pending = await db.Instances.IgnoreQueryFilters()
            .Where(i => EF.Property<Guid>(i, "TenantId") == tenantId && i.Status == ApprovalStatus.Pending)
            .ToListAsync(cancellationToken);
        var escalated = pending.Count(i => i.Escalate(now));
        if (escalated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return escalated;
    }
}

// ---------- Notifications ----------

/// <summary>
/// Notifies the approvers of a step when it is assigned (submission, next step, delegation, escalation): an in-app
/// notification (written in this handler's transaction) and an email. At most once per event.
/// </summary>
public sealed class ApprovalStepAssignedNotifier(
    IUnitOfWork unitOfWork,
    IInbox inbox,
    IOutboxMessageContext message,
    IWorkflowDbContext db,
    ITenantContext tenant,
    IUserDirectory users,
    INotificationSender sender,
    INotificationPublisher inApp) : InboxIntegrationEventHandler<ApprovalStepAssigned>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(ApprovalStepAssigned integrationEvent, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var instance = await db.Instances.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => EF.Property<Guid>(i, "TenantId") == tenantId && i.Id == integrationEvent.InstanceId, cancellationToken);
        if (instance is null || instance.Status != ApprovalStatus.Pending || instance.CurrentStep != integrationEvent.StepSequence)
        {
            return;
        }

        var step = instance.Current;
        // After a reassigning escalation only the active assigned approvers (the escalation target and its delegates) are told.
        var approvers = new List<(ApproverKind Kind, Guid Id)>();
        if (!step.IsReassigned)
        {
            approvers.Add((step.ApproverKind, step.ApproverId));
        }

        approvers.AddRange(instance.AssignedApprovers.Where(a => a.StepSequence == step.Sequence && a.IsActive).Select(a => (a.Kind, a.ApproverId)));

        var recipients = new Dictionary<Guid, UserSummary>();
        foreach (var (kind, id) in approvers.Distinct())
        {
            var found = kind == ApproverKind.User ? await users.GetUsersAsync([id], cancellationToken) : await users.GetUsersInRoleAsync(id, cancellationToken);
            foreach (var user in found.Where(u => u.IsActive && u.Id != instance.RequestedBy))
            {
                recipients[user.Id] = user;
            }
        }

        await inApp.PublishAsync(recipients.Keys.ToList(), new InAppNotification(
            "workflow.approval-requested",
            $"Approval needed: {instance.DocumentNumber}",
            $"{instance.DocumentType} for {instance.Amount:N2} {instance.Currency}, step {step.Sequence} of {instance.Steps.Count}.",
            "/approvals",
            instance.CompanyId), cancellationToken);

        foreach (var user in recipients.Values)
        {
            await sender.SendEmailAsync(new EmailMessage(user.Email,
                $"Approval needed: {instance.DocumentNumber} ({instance.DocumentType})",
                $"Hello {user.DisplayName},\n\n{instance.DocumentNumber} for {instance.Amount:N2} {instance.Currency} is waiting for your approval " +
                $"(step {step.Sequence} of {instance.Steps.Count}, rule \"{instance.DefinitionName}\").\n\nOpen your approvals inbox to decide."), cancellationToken);
        }
    }
}
