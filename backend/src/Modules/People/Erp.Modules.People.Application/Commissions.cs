using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.People.Contracts;
using Erp.Modules.People.Domain;
using Erp.Modules.Pos.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.People.Application;

public sealed record CommissionRunLineDto(Guid SalespersonId, string SalespersonCode, string SalespersonName, Guid PlanId, string PlanCode, string Basis,
    decimal BasisTotal, decimal TierRatePercent, decimal Commission);

public sealed record CommissionDetailDto(Guid SalespersonId, string SourceType, Guid DocumentId, string DocumentNumber, DateOnly DocumentDate, decimal BasisAmount,
    decimal RatePercent, decimal Commission);

public sealed record CommissionRunDto(Guid Id, Guid LegalEntityId, DateOnly PeriodFrom, DateOnly PeriodTo, string Status, string? Number, decimal Total,
    IReadOnlyList<CommissionRunLineDto> Lines, IReadOnlyList<CommissionDetailDto> Details, IReadOnlyList<Guid> SkippedSalespersons, DateTime CalculatedAtUtc,
    Guid? ApprovedBy, DateTime? ApprovedAtUtc, Guid? JournalId, DateTime? PostedAtUtc, DateOnly? ReversalDate);

internal static class CommissionMapping
{
    public static async Task<CommissionRunDto> ToDtoAsync(IPeopleDbContext db, CommissionRun r, bool withDetails, CancellationToken ct)
    {
        var ids = r.Lines.Select(l => l.SalespersonId).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return new CommissionRunDto(r.Id, r.LegalEntityId, r.PeriodFrom, r.PeriodTo, r.Status.ToString(), r.Number, r.Total,
            r.Lines.Select(l => new CommissionRunLineDto(l.SalespersonId, people.GetValueOrDefault(l.SalespersonId)?.Code ?? string.Empty,
                people.GetValueOrDefault(l.SalespersonId)?.NameEn ?? string.Empty, l.PlanId, l.PlanCode, l.Basis.ToString(), l.BasisTotal, l.TierRatePercent,
                l.Commission)).ToList(),
            withDetails
                ? r.Details.Select(d => new CommissionDetailDto(d.SalespersonId, d.SourceType, d.DocumentId, d.DocumentNumber, d.DocumentDate, d.BasisAmount,
                    d.RatePercent, d.Commission)).ToList()
                : [],
            r.SkippedSalespersons, r.CalculatedAtUtc, r.ApprovedBy, r.ApprovedAtUtc, r.JournalId, r.PostedAtUtc, r.ReversalDate);
    }
}

/// <summary>
/// Collects the commissionable lines of a legal entity and month: posted sales invoices and credit notes (Sales contract) and completed
/// POS receipts and refunds (POS contract) that carry a salesperson. Product categories come from the catalog for category overrides.
/// </summary>
public sealed class CommissionSourceLoader(IServiceProvider services)
{
    public async Task<IReadOnlyList<CommissionSource>> LoadAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = new List<(CommissionSource Source, Guid ProductId)>();
        if (services.GetService<ISalesReportQueries>() is { } sales)
        {
            var facts = await sales.GetSalesFactsAsync(new SalesReportFilter(from, to, LegalEntityId: legalEntityId), ct);
            rows.AddRange(facts.Where(f => f.SalespersonId is not null && f.LegalEntityId == legalEntityId).Select(f => (
                new CommissionSource(f.SalespersonId!.Value, f.DocumentType, f.DocumentId, f.DocumentNumber, f.Date, null, f.NetBase, f.CostBase,
                    f.DocumentType == SalesDocumentTypes.Return), f.ProductId)));
        }

        if (services.GetService<IPosSalesQueries>() is { } pos)
        {
            var facts = await pos.GetSalesFactsAsync(from, to, legalEntityId, null, ct);
            rows.AddRange(facts.Where(f => f.SalespersonId is not null).Select(f => (
                new CommissionSource(f.SalespersonId!.Value, f.DocumentType, f.ReceiptId, f.Number, f.Date, null, f.NetBase, f.CostBase,
                    f.DocumentType == PosDocumentTypes.Refund), f.ProductId)));
        }

        var categories = new Dictionary<Guid, Guid>();
        if (rows.Count > 0 && services.GetService<ICatalogReportQueries>() is { } catalog)
        {
            foreach (var info in await catalog.GetProductReportInfoAsync(rows.Select(r => r.ProductId).Distinct().ToList(), ct))
            {
                categories[info.ProductId] = info.CategoryId;
            }
        }

        return rows.Select(r => r.Source with { CategoryId = categories.TryGetValue(r.ProductId, out var c) ? c : null }).ToList();
    }
}

/// <summary>Validates the legal entity (active company) and calculates a run's lines from the current sales and plans.</summary>
public sealed class CommissionEngine(IPeopleDbContext db, IOrganizationQueries organization, ITenantContext tenant, CommissionSourceLoader loader, IClock clock)
{
    public async Task<Result<LegalEntitySummary>> LegalEntityAsync(Guid legalEntityId, CancellationToken ct)
    {
        var le = await organization.GetLegalEntityAsync(legalEntityId, ct);
        return le is null || (tenant.CompanyId is { } company && company != le.CompanyId) ? PeopleAppErrors.LegalEntityNotFound : le;
    }

    public async Task CalculateAsync(CommissionRun run, CancellationToken ct)
    {
        var sources = await loader.LoadAsync(run.LegalEntityId, run.PeriodFrom, run.PeriodTo, ct);
        var ids = sources.Select(s => s.SalespersonId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var planIds = employees.Values.Where(e => e.CommissionPlanId != null).Select(e => e.CommissionPlanId!.Value).Distinct().ToList();
        var plans = await db.CommissionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id, ct);
        var (lines, details, skipped) = CommissionCalculator.Calculate(sources, id =>
            employees.TryGetValue(id, out var e) && e.CommissionPlanId is { } planId && plans.TryGetValue(planId, out var plan) ? plan : null);
        run.SetCalculation(lines, details, skipped, clock.UtcNow);
    }
}

public sealed record CalculateCommissionRunCommand(Guid LegalEntityId, DateOnly Period) : ICommand<CommissionRunDto>;

public sealed class CalculateCommissionRunValidator : AbstractValidator<CalculateCommissionRunCommand>
{
    public CalculateCommissionRunValidator() => RuleFor(x => x.LegalEntityId).NotEmpty();
}

/// <summary>Creates the draft run of a legal entity and month and calculates it (one active run per legal entity and month).</summary>
public sealed class CalculateCommissionRunHandler(IPeopleDbContext db, ITenantContext tenant, CommissionEngine engine)
    : ICommandHandler<CalculateCommissionRunCommand, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(CalculateCommissionRunCommand command, CancellationToken cancellationToken)
    {
        var le = await engine.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var period = CommissionRun.MonthStart(command.Period);
        if (await db.CommissionRuns.AnyAsync(r => r.LegalEntityId == command.LegalEntityId && r.PeriodFrom == period &&
                (r.Status == CommissionRunStatus.Draft || r.Status == CommissionRunStatus.Approved || r.Status == CommissionRunStatus.Posted), cancellationToken))
        {
            return PeopleAppErrors.RunExists;
        }

        var run = CommissionRun.Create(tenant.CompanyId!.Value, command.LegalEntityId, period);
        await engine.CalculateAsync(run, cancellationToken);
        db.CommissionRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The filtered unique index (one active run per legal entity and month) caught a concurrent calculation.
            return PeopleAppErrors.RunExists;
        }

        return await CommissionMapping.ToDtoAsync(db, run, true, cancellationToken);
    }
}

public sealed record RecalculateCommissionRunCommand(Guid Id) : ICommand<CommissionRunDto>;

public sealed class RecalculateCommissionRunHandler(IPeopleDbContext db, CommissionEngine engine) : ICommandHandler<RecalculateCommissionRunCommand, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(RecalculateCommissionRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.CommissionRuns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (run is null)
        {
            return PeopleAppErrors.RunNotFound;
        }

        try
        {
            await engine.CalculateAsync(run, cancellationToken);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await CommissionMapping.ToDtoAsync(db, run, true, cancellationToken);
    }
}

public sealed record ApproveCommissionRunCommand(Guid Id) : ICommand<CommissionRunDto>;

public sealed class ApproveCommissionRunHandler(IPeopleDbContext db, ICurrentUser user, IClock clock) : ICommandHandler<ApproveCommissionRunCommand, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(ApproveCommissionRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.CommissionRuns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (run is null)
        {
            return PeopleAppErrors.RunNotFound;
        }

        try
        {
            run.Approve(user.UserId, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await CommissionMapping.ToDtoAsync(db, run, false, cancellationToken);
    }
}

public sealed record CancelCommissionRunCommand(Guid Id) : ICommand<CommissionRunDto>;

public sealed class CancelCommissionRunHandler(IPeopleDbContext db) : ICommandHandler<CancelCommissionRunCommand, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(CancelCommissionRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.CommissionRuns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (run is null)
        {
            return PeopleAppErrors.RunNotFound;
        }

        try
        {
            run.Cancel();
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await CommissionMapping.ToDtoAsync(db, run, false, cancellationToken);
    }
}

public sealed record PostCommissionRunCommand(Guid Id, DateOnly? Date) : ICommand<CommissionRunDto>;

/// <summary>
/// Posts an approved run: per salesperson Dr commission expense / Cr commissions payable (party = the employee), dated the end of the month
/// unless a date is given. Negative commission (net clawback) posts the other way round.
/// </summary>
public sealed class PostCommissionRunHandler(IPeopleDbContext db, CommissionEngine engine, IJournalPostingService journals, IDocumentNumberGenerator numbers, IClock clock)
    : ICommandHandler<PostCommissionRunCommand, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(PostCommissionRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.CommissionRuns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (run is null)
        {
            return PeopleAppErrors.RunNotFound;
        }

        if (run.Status != CommissionRunStatus.Approved)
        {
            return PeopleErrors.InvalidTransition(run.Status.ToString(), "posted");
        }

        var le = await engine.LegalEntityAsync(run.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var date = command.Date ?? run.PeriodTo;
        var lines = new List<JournalLineRequest>();
        foreach (var line in run.Lines.Where(l => l.Commission != 0))
        {
            var amount = Math.Abs(line.Commission);
            var expenseDebit = line.Commission > 0;
            var description = $"Commission {run.PeriodFrom:yyyy-MM}";
            lines.Add(new JournalLineRequest(PostingRoles.CommissionExpense, null, expenseDebit ? amount : 0, expenseDebit ? 0 : amount, null, null,
                PeoplePartyTypes.Employee, line.SalespersonId, description));
            lines.Add(new JournalLineRequest(PostingRoles.CommissionPayable, null, expenseDebit ? 0 : amount, expenseDebit ? amount : 0, null, null,
                PeoplePartyTypes.Employee, line.SalespersonId, description));
        }

        var number = await numbers.NextAsync(new NumberScope(run.CompanyId, PeopleDocumentTypes.CommissionRun, "COM", LegalEntityId: run.LegalEntityId,
            FiscalYear: date.Year), cancellationToken);
        var posted = await journals.PostAsync(new JournalRequest(run.LegalEntityId, date, SourceModules.People, PeopleDocumentTypes.CommissionRun, run.Id, "post",
            $"Sales commissions {run.PeriodFrom:yyyy-MM} ({number})", le.Value.FunctionalCurrency, 1m, lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        run.MarkPosted(number, posted.Value.JournalId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await CommissionMapping.ToDtoAsync(db, run, false, cancellationToken);
    }
}

public sealed record ReverseCommissionRunCommand(Guid Id, DateOnly? Date) : ICommand<CommissionRunDto>;

/// <summary>Reverses a posted run's journal (default: the run's period end) and frees the month for a new run.</summary>
public sealed class ReverseCommissionRunHandler(IPeopleDbContext db, CommissionEngine engine, IJournalPostingService journals)
    : ICommandHandler<ReverseCommissionRunCommand, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(ReverseCommissionRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.CommissionRuns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (run is null)
        {
            return PeopleAppErrors.RunNotFound;
        }

        if (run.Status != CommissionRunStatus.Posted)
        {
            return PeopleErrors.InvalidTransition(run.Status.ToString(), "reversed");
        }

        var le = await engine.LegalEntityAsync(run.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var date = command.Date ?? run.PeriodTo;
        var reversed = await journals.ReverseAsync(run.LegalEntityId, PeopleDocumentTypes.CommissionRun, run.Id, "post", date, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        run.MarkReversed(date);
        await db.SaveChangesAsync(cancellationToken);
        return await CommissionMapping.ToDtoAsync(db, run, false, cancellationToken);
    }
}

public sealed record GetCommissionRunQuery(Guid Id) : IQuery<CommissionRunDto>;

public sealed class GetCommissionRunHandler(IPeopleDbContext db) : IQueryHandler<GetCommissionRunQuery, CommissionRunDto>
{
    public async Task<Result<CommissionRunDto>> Handle(GetCommissionRunQuery query, CancellationToken cancellationToken)
    {
        var run = await db.CommissionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
        return run is null ? PeopleAppErrors.RunNotFound : await CommissionMapping.ToDtoAsync(db, run, true, cancellationToken);
    }
}

public sealed record ListCommissionRunsQuery(Guid? LegalEntityId, string? Status) : IQuery<IReadOnlyList<CommissionRunDto>>;

public sealed class ListCommissionRunsHandler(IPeopleDbContext db) : IQueryHandler<ListCommissionRunsQuery, IReadOnlyList<CommissionRunDto>>
{
    public async Task<Result<IReadOnlyList<CommissionRunDto>>> Handle(ListCommissionRunsQuery query, CancellationToken cancellationToken)
    {
        var source = db.CommissionRuns.AsNoTracking();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(r => r.LegalEntityId == le);
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<CommissionRunStatus>(query.Status, true, out var status))
        {
            source = source.Where(r => r.Status == status);
        }

        var runs = await source.OrderByDescending(r => r.PeriodFrom).ToListAsync(cancellationToken);
        var result = new List<CommissionRunDto>();
        foreach (var run in runs)
        {
            result.Add(await CommissionMapping.ToDtoAsync(db, run, false, cancellationToken));
        }

        return result;
    }
}

/// <summary>Commission statement lines from posted (and reversed, flagged) runs whose month overlaps the range.</summary>
public sealed class CommissionReportQueries(IPeopleDbContext db) : ICommissionReportQueries
{
    public async Task<IReadOnlyList<CommissionStatementLine>> GetStatementAsync(DateOnly from, DateOnly to, Guid? salespersonId, CancellationToken cancellationToken = default)
    {
        var runs = await db.CommissionRuns.AsNoTracking()
            .Where(r => (r.Status == CommissionRunStatus.Posted || r.Status == CommissionRunStatus.Reversed) && r.PeriodFrom <= to && r.PeriodTo >= from)
            .OrderBy(r => r.PeriodFrom)
            .ToListAsync(cancellationToken);
        return runs.SelectMany(r => r.Details
                .Where(d => salespersonId == null || d.SalespersonId == salespersonId)
                .Select(d => new CommissionStatementLine(r.Id, r.Number ?? string.Empty, r.PeriodFrom, r.PeriodTo, d.SalespersonId, d.SourceType, d.DocumentId,
                    d.DocumentNumber, d.DocumentDate, d.BasisAmount, d.RatePercent, d.Commission, r.Status == CommissionRunStatus.Reversed)))
            .ToList();
    }
}
