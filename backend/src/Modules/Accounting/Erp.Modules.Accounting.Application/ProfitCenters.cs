using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

// ---------- Profit centers (ADR-036) ----------
public sealed record ProfitCenterDto(Guid Id, string Code, string Name, Guid? BranchId, bool IsActive);

public static class ProfitCenterErrors
{
    public static readonly Error NotFound = Error.NotFound("accounting.profit-center-not-found", "The profit center was not found.");
    public static readonly Error CodeTaken = Error.Conflict("accounting.profit-center-code-taken", "A profit center with this code already exists.");
    public static readonly Error BranchNotFound = Error.Validation("accounting.branch-not-found", "The branch was not found in the active company.");
    public static readonly Error BranchMapped = Error.Conflict("accounting.branch-already-mapped", "The branch is already mapped to another profit center.");
}

public sealed record CreateProfitCenterCommand(string Code, string Name, Guid? BranchId) : ICommand<ProfitCenterDto>;

public sealed record UpdateProfitCenterCommand(Guid Id, string Name, Guid? BranchId, bool IsActive) : ICommand<ProfitCenterDto>;

public sealed class CreateProfitCenterValidator : AbstractValidator<CreateProfitCenterCommand>
{
    public CreateProfitCenterValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class UpdateProfitCenterValidator : AbstractValidator<UpdateProfitCenterCommand>
{
    public UpdateProfitCenterValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
}

internal static class ProfitCenterRules
{
    public static async Task<Error?> CheckBranchAsync(IAccountingDbContext db, IOrganizationReportQueries organization, Guid? branchId, Guid? self, CancellationToken ct)
    {
        if (branchId is not { } id)
        {
            return null;
        }

        if ((await organization.GetBranchesAsync([id], ct)).Count == 0)
        {
            return ProfitCenterErrors.BranchNotFound;
        }

        return await db.ProfitCenters.AnyAsync(p => p.BranchId == id && p.Id != self, ct) ? ProfitCenterErrors.BranchMapped : null;
    }

    public static ProfitCenterDto ToDto(ProfitCenter p) => new(p.Id, p.Code, p.Name, p.BranchId, p.IsActive);
}

public sealed class CreateProfitCenterHandler(IAccountingDbContext db, ITenantContext tenant, IOrganizationReportQueries organization)
    : ICommandHandler<CreateProfitCenterCommand, ProfitCenterDto>
{
    public async Task<Result<ProfitCenterDto>> Handle(CreateProfitCenterCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.ProfitCenters.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return ProfitCenterErrors.CodeTaken;
        }

        if (await ProfitCenterRules.CheckBranchAsync(db, organization, command.BranchId, null, cancellationToken) is { } error)
        {
            return error;
        }

        var center = ProfitCenter.Create(tenant.CompanyId!.Value, code, command.Name, command.BranchId);
        db.ProfitCenters.Add(center);
        await db.SaveChangesAsync(cancellationToken);
        return ProfitCenterRules.ToDto(center);
    }
}

public sealed class UpdateProfitCenterHandler(IAccountingDbContext db, IOrganizationReportQueries organization)
    : ICommandHandler<UpdateProfitCenterCommand, ProfitCenterDto>
{
    public async Task<Result<ProfitCenterDto>> Handle(UpdateProfitCenterCommand command, CancellationToken cancellationToken)
    {
        var center = await db.ProfitCenters.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (center is null)
        {
            return ProfitCenterErrors.NotFound;
        }

        if (await ProfitCenterRules.CheckBranchAsync(db, organization, command.BranchId, center.Id, cancellationToken) is { } error)
        {
            return error;
        }

        center.Update(command.Name, command.BranchId, command.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return ProfitCenterRules.ToDto(center);
    }
}

public sealed record ListProfitCentersQuery : IQuery<IReadOnlyList<ProfitCenterDto>>;

public sealed class ListProfitCentersHandler(IAccountingDbContext db) : IQueryHandler<ListProfitCentersQuery, IReadOnlyList<ProfitCenterDto>>
{
    public async Task<Result<IReadOnlyList<ProfitCenterDto>>> Handle(ListProfitCentersQuery query, CancellationToken cancellationToken) =>
        (await db.ProfitCenters.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken)).Select(ProfitCenterRules.ToDto).ToList();
}

// ---------- Profit and loss by profit center ----------
public sealed record ProfitCenterPnlRowDto(Guid? ProfitCenterId, string Code, string Name, decimal NetRevenue, decimal CostOfSales, decimal GrossProfit,
    decimal OtherIncome, decimal OperatingExpenses, decimal NetProfit);

public sealed record ProfitCenterPnlDto(Guid LegalEntityId, string Currency, DateOnly From, DateOnly To, IReadOnlyList<ProfitCenterPnlRowDto> Rows,
    ProfitCenterPnlRowDto Total);

/// <summary>
/// Income-statement figures per profit center for a range (closing entries excluded). Lines without a profit center are shown as
/// "(unassigned)" so the columns add up to the company income statement.
/// </summary>
public sealed record ProfitCenterPnlQuery(Guid LegalEntityId, DateOnly From, DateOnly To) : IQuery<ProfitCenterPnlDto>;

public sealed class ProfitCenterPnlValidator : AbstractValidator<ProfitCenterPnlQuery>
{
    public ProfitCenterPnlValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x).Must(x => x.From <= x.To).WithMessage("The start date must not be after the end date.");
    }
}

public sealed class ProfitCenterPnlHandler(IAccountingDbContext db, FinancialStatementService statements, LedgerQueries ledger, CompanyLegalEntities legalEntities)
    : IQueryHandler<ProfitCenterPnlQuery, ProfitCenterPnlDto>
{
    public async Task<Result<ProfitCenterPnlDto>> Handle(ProfitCenterPnlQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var accounts = await statements.AccountsAsync(query.LegalEntityId, cancellationToken);
        var (from, to) = (query.From, query.To);
        var amounts = await ledger.PostedLines(query.LegalEntityId)
            .Where(r => r.JournalDate >= from && r.JournalDate <= to && r.SourceDocType != AccountingDocumentTypes.YearEndClose)
            .GroupBy(r => new { r.ProfitCenterId, r.AccountId })
            .Select(g => new { g.Key.ProfitCenterId, g.Key.AccountId, Amount = g.Sum(r => r.BaseDebit - r.BaseCredit) })
            .ToListAsync(cancellationToken);
        var centers = await db.ProfitCenters.AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);

        ProfitCenterPnlRowDto Row(Guid? id, string code, string name, IReadOnlyDictionary<Guid, decimal> byAccount)
        {
            var (sections, netRevenue, gross, net) = FinancialStatementBuilder.IncomeStatement(accounts, byAccount, null, null);
            decimal Total(string key) => sections.First(s => s.Key == key).Total;
            return new ProfitCenterPnlRowDto(id, code, name, netRevenue.Amount, Total("cost-of-sales"), gross.Amount, Total("other-income"),
                Total("operating-expenses"), net.Amount);
        }

        var rows = amounts.GroupBy(a => a.ProfitCenterId)
            .Select(g =>
            {
                var byAccount = g.ToDictionary(a => a.AccountId, a => a.Amount);
                return g.Key is { } id && centers.TryGetValue(id, out var pc)
                    ? Row(id, pc.Code, pc.Name, byAccount)
                    : Row(null, string.Empty, "(unassigned)", byAccount);
            })
            .Where(r => r.NetRevenue != 0 || r.CostOfSales != 0 || r.OtherIncome != 0 || r.OperatingExpenses != 0)
            .OrderBy(r => r.ProfitCenterId is null ? 1 : 0).ThenBy(r => r.Code, StringComparer.Ordinal)
            .ToList();
        var total = Row(null, string.Empty, "Total", amounts.GroupBy(a => a.AccountId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount)));
        return new ProfitCenterPnlDto(query.LegalEntityId, le.Value.FunctionalCurrency, query.From, query.To, rows, total);
    }
}
