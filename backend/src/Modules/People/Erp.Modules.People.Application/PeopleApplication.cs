using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Identity.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.People.Contracts;
using Erp.Modules.People.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.People.Application;

public interface IPeopleDbContext
{
    DbSet<Employee> Employees { get; }

    DbSet<CommissionPlan> CommissionPlans { get; }

    DbSet<CommissionRun> CommissionRuns { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class PeopleAppErrors
{
    public static readonly Error EmployeeNotFound = Error.NotFound("people.employee-not-found", "The employee was not found.");
    public static readonly Error EmployeeCodeTaken = Error.Conflict("people.employee-code-taken", "An employee with this code already exists.");
    public static readonly Error UserLinked = Error.Conflict("people.user-already-linked", "The user is already linked to another employee.");
    public static readonly Error UserNotFound = Error.Validation("people.user-not-found", "The user was not found.");
    public static readonly Error BranchNotFound = Error.Validation("people.branch-not-found", "The branch was not found in the active company.");
    public static readonly Error PlanNotFound = Error.NotFound("people.plan-not-found", "The commission plan was not found or is inactive.");
    public static readonly Error PlanCodeTaken = Error.Conflict("people.plan-code-taken", "A commission plan with this code already exists.");
    public static readonly Error CategoryNotFound = Error.Validation("people.category-not-found", "A category of a category rate was not found.");
    public static readonly Error RunNotFound = Error.NotFound("people.commission-run-not-found", "The commission run was not found.");
    public static readonly Error RunExists = Error.Conflict("people.commission-run-exists",
        "A commission run for this legal entity and month already exists (cancel or reverse it first).");
    public static readonly Error LegalEntityNotFound = Error.NotFound("people.legal-entity-not-found", "The legal entity was not found in this company.");
}

// ======================= DTOs =======================
public sealed record EmployeeDto(Guid Id, string Code, string NameEn, string NameAr, Guid? BranchId, string? Department, string? JobTitle, Guid? UserId,
    bool IsSalesperson, Guid? CommissionPlanId, bool IsActive);

public sealed record CommissionTierDto(decimal Threshold, decimal RatePercent);

public sealed record CategoryRateDto(Guid CategoryId, decimal RatePercent);

public sealed record CommissionPlanDto(Guid Id, string Code, string NameEn, string NameAr, string Basis, decimal RatePercent, bool ClawbackOnReturns,
    IReadOnlyList<CommissionTierDto> Tiers, IReadOnlyList<CategoryRateDto> CategoryRates, bool IsActive);

public static class PeopleMapping
{
    public static EmployeeDto ToDto(Employee e) => new(e.Id, e.Code, e.NameEn, e.NameAr, e.BranchId, e.Department, e.JobTitle, e.UserId, e.IsSalesperson,
        e.CommissionPlanId, e.IsActive);

    public static CommissionPlanDto ToDto(CommissionPlan p) => new(p.Id, p.Code, p.NameEn, p.NameAr, p.Basis.ToString(), p.RatePercent, p.ClawbackOnReturns,
        p.Tiers.Select(t => new CommissionTierDto(t.Threshold, t.RatePercent)).ToList(),
        p.CategoryRates.Select(c => new CategoryRateDto(c.CategoryId, c.RatePercent)).ToList(), p.IsActive);

    public static EmployeeInfo ToInfo(Employee e) => new(e.Id, e.Code, e.NameEn, e.NameAr, e.BranchId, e.IsActive, e.IsSalesperson, e.UserId, e.CommissionPlanId);
}

// ======================= Employees =======================
public sealed record EmployeeInput(string NameEn, string NameAr, Guid? BranchId, string? Department, string? JobTitle, Guid? UserId, bool IsSalesperson,
    Guid? CommissionPlanId);

public sealed class EmployeeInputValidator : AbstractValidator<EmployeeInput>
{
    public EmployeeInputValidator()
    {
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Department).MaximumLength(100);
        RuleFor(x => x.JobTitle).MaximumLength(100);
    }
}

public sealed record CreateEmployeeCommand(string Code, EmployeeInput Details) : ICommand<EmployeeDto>;

public sealed class CreateEmployeeValidator : AbstractValidator<CreateEmployeeCommand>
{
    public CreateEmployeeValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Details).NotNull().SetValidator(new EmployeeInputValidator());
    }
}

public sealed record UpdateEmployeeCommand(Guid Id, EmployeeInput Details, bool IsActive) : ICommand<EmployeeDto>;

public sealed class UpdateEmployeeValidator : AbstractValidator<UpdateEmployeeCommand>
{
    public UpdateEmployeeValidator() => RuleFor(x => x.Details).NotNull().SetValidator(new EmployeeInputValidator());
}

/// <summary>Checks the references of an employee: branch of the active company, existing user (linked to one employee only), active plan.</summary>
public sealed class EmployeeReferences(IPeopleDbContext db, IOrganizationReportQueries organization, IUserDirectory users)
{
    public async Task<Error?> CheckAsync(EmployeeInput d, Guid? self, CancellationToken ct)
    {
        if (d.BranchId is { } branchId && (await organization.GetBranchesAsync([branchId], ct)).Count == 0)
        {
            return PeopleAppErrors.BranchNotFound;
        }

        if (d.UserId is { } userId)
        {
            if ((await users.GetUsersAsync([userId], ct)).Count == 0)
            {
                return PeopleAppErrors.UserNotFound;
            }

            if (await db.Employees.AnyAsync(e => e.UserId == userId && e.Id != self, ct))
            {
                return PeopleAppErrors.UserLinked;
            }
        }

        if (d.CommissionPlanId is { } planId && !await db.CommissionPlans.AnyAsync(p => p.Id == planId && p.IsActive, ct))
        {
            return PeopleAppErrors.PlanNotFound;
        }

        return null;
    }
}

public sealed class CreateEmployeeHandler(IPeopleDbContext db, ITenantContext tenant, EmployeeReferences references) : ICommandHandler<CreateEmployeeCommand, EmployeeDto>
{
    public async Task<Result<EmployeeDto>> Handle(CreateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Employees.AnyAsync(e => e.Code == code, cancellationToken))
        {
            return PeopleAppErrors.EmployeeCodeTaken;
        }

        var d = command.Details;
        if (await references.CheckAsync(d, null, cancellationToken) is { } error)
        {
            return error;
        }

        try
        {
            var employee = Employee.Create(tenant.CompanyId!.Value, code, d.NameEn, d.NameAr, d.BranchId, d.Department, d.JobTitle, d.UserId, d.IsSalesperson,
                d.CommissionPlanId);
            db.Employees.Add(employee);
            await db.SaveChangesAsync(cancellationToken);
            return PeopleMapping.ToDto(employee);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed class UpdateEmployeeHandler(IPeopleDbContext db, EmployeeReferences references) : ICommandHandler<UpdateEmployeeCommand, EmployeeDto>
{
    public async Task<Result<EmployeeDto>> Handle(UpdateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken);
        if (employee is null)
        {
            return PeopleAppErrors.EmployeeNotFound;
        }

        var d = command.Details;
        if (await references.CheckAsync(d, employee.Id, cancellationToken) is { } error)
        {
            return error;
        }

        try
        {
            employee.Update(d.NameEn, d.NameAr, d.BranchId, d.Department, d.JobTitle, d.UserId, d.IsSalesperson, d.CommissionPlanId);
            employee.SetActive(command.IsActive);
            await db.SaveChangesAsync(cancellationToken);
            return PeopleMapping.ToDto(employee);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed record GetEmployeeQuery(Guid Id) : IQuery<EmployeeDto>;

public sealed class GetEmployeeHandler(IPeopleDbContext db) : IQueryHandler<GetEmployeeQuery, EmployeeDto>
{
    public async Task<Result<EmployeeDto>> Handle(GetEmployeeQuery query, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == query.Id, cancellationToken);
        return employee is null ? PeopleAppErrors.EmployeeNotFound : PeopleMapping.ToDto(employee);
    }
}

public sealed record ListEmployeesQuery(bool? Salespersons, bool? Active, Guid? BranchId, string? Search) : IQuery<IReadOnlyList<EmployeeDto>>;

public sealed class ListEmployeesHandler(IPeopleDbContext db) : IQueryHandler<ListEmployeesQuery, IReadOnlyList<EmployeeDto>>
{
    public async Task<Result<IReadOnlyList<EmployeeDto>>> Handle(ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        var source = db.Employees.AsNoTracking();
        if (query.Salespersons is { } salespersons)
        {
            source = source.Where(e => e.IsSalesperson == salespersons);
        }

        if (query.Active is { } active)
        {
            source = source.Where(e => e.IsActive == active);
        }

        if (query.BranchId is { } branch)
        {
            source = source.Where(e => e.BranchId == branch);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(e => e.Code.Contains(term) || e.NameEn.Contains(term) || e.NameAr.Contains(term));
        }

        return (await source.OrderBy(e => e.Code).ToListAsync(cancellationToken)).Select(PeopleMapping.ToDto).ToList();
    }
}

// ======================= Commission plans =======================
public sealed record CommissionPlanInput(string NameEn, string NameAr, string Basis, decimal RatePercent, bool ClawbackOnReturns,
    IReadOnlyList<CommissionTierDto>? Tiers, IReadOnlyList<CategoryRateDto>? CategoryRates);

public sealed class CommissionPlanInputValidator : AbstractValidator<CommissionPlanInput>
{
    public CommissionPlanInputValidator()
    {
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Basis).Must(b => Enum.TryParse<CommissionBasis>(b, true, out _)).WithMessage("Basis must be NetSales or GrossMargin.");
    }
}

public sealed record CreateCommissionPlanCommand(string Code, CommissionPlanInput Details) : ICommand<CommissionPlanDto>;

public sealed class CreateCommissionPlanValidator : AbstractValidator<CreateCommissionPlanCommand>
{
    public CreateCommissionPlanValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Details).NotNull().SetValidator(new CommissionPlanInputValidator());
    }
}

public sealed record UpdateCommissionPlanCommand(Guid Id, CommissionPlanInput Details, bool IsActive) : ICommand<CommissionPlanDto>;

public sealed class UpdateCommissionPlanValidator : AbstractValidator<UpdateCommissionPlanCommand>
{
    public UpdateCommissionPlanValidator() => RuleFor(x => x.Details).NotNull().SetValidator(new CommissionPlanInputValidator());
}

internal static class PlanInputs
{
    public static async Task<Error?> CheckCategoriesAsync(ICatalogReportQueries? catalog, CommissionPlanInput d, CancellationToken ct)
    {
        if (d.CategoryRates is not { Count: > 0 } rates)
        {
            return null;
        }

        if (catalog is null)
        {
            return PeopleAppErrors.CategoryNotFound;
        }

        foreach (var categoryId in rates.Select(r => r.CategoryId).Distinct())
        {
            if (!await catalog.CategoryExistsAsync(categoryId, ct))
            {
                return PeopleAppErrors.CategoryNotFound;
            }
        }

        return null;
    }

    public static IEnumerable<CommissionTier> Tiers(CommissionPlanInput d) => (d.Tiers ?? []).Select(t => new CommissionTier(t.Threshold, t.RatePercent));

    public static IEnumerable<CategoryRate> Categories(CommissionPlanInput d) => (d.CategoryRates ?? []).Select(c => new CategoryRate(c.CategoryId, c.RatePercent));
}

public sealed class CreateCommissionPlanHandler(IPeopleDbContext db, ITenantContext tenant, IServiceProvider services)
    : ICommandHandler<CreateCommissionPlanCommand, CommissionPlanDto>
{
    public async Task<Result<CommissionPlanDto>> Handle(CreateCommissionPlanCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.CommissionPlans.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return PeopleAppErrors.PlanCodeTaken;
        }

        var d = command.Details;
        if (await PlanInputs.CheckCategoriesAsync(services.GetService(typeof(ICatalogReportQueries)) as ICatalogReportQueries, d, cancellationToken) is { } error)
        {
            return error;
        }

        try
        {
            var plan = CommissionPlan.Create(tenant.CompanyId!.Value, code, d.NameEn, d.NameAr, Enum.Parse<CommissionBasis>(d.Basis, true), d.RatePercent,
                d.ClawbackOnReturns, PlanInputs.Tiers(d), PlanInputs.Categories(d));
            db.CommissionPlans.Add(plan);
            await db.SaveChangesAsync(cancellationToken);
            return PeopleMapping.ToDto(plan);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed class UpdateCommissionPlanHandler(IPeopleDbContext db, IServiceProvider services) : ICommandHandler<UpdateCommissionPlanCommand, CommissionPlanDto>
{
    public async Task<Result<CommissionPlanDto>> Handle(UpdateCommissionPlanCommand command, CancellationToken cancellationToken)
    {
        var plan = await db.CommissionPlans.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (plan is null)
        {
            return PeopleAppErrors.PlanNotFound;
        }

        var d = command.Details;
        if (await PlanInputs.CheckCategoriesAsync(services.GetService(typeof(ICatalogReportQueries)) as ICatalogReportQueries, d, cancellationToken) is { } error)
        {
            return error;
        }

        try
        {
            plan.Update(d.NameEn, d.NameAr, Enum.Parse<CommissionBasis>(d.Basis, true), d.RatePercent, d.ClawbackOnReturns, PlanInputs.Tiers(d),
                PlanInputs.Categories(d), command.IsActive);
            await db.SaveChangesAsync(cancellationToken);
            return PeopleMapping.ToDto(plan);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed record ListCommissionPlansQuery : IQuery<IReadOnlyList<CommissionPlanDto>>;

public sealed class ListCommissionPlansHandler(IPeopleDbContext db) : IQueryHandler<ListCommissionPlansQuery, IReadOnlyList<CommissionPlanDto>>
{
    public async Task<Result<IReadOnlyList<CommissionPlanDto>>> Handle(ListCommissionPlansQuery query, CancellationToken cancellationToken) =>
        (await db.CommissionPlans.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken)).Select(PeopleMapping.ToDto).ToList();
}

// ======================= Contract implementations =======================
public sealed class EmployeeQueries(IPeopleDbContext db) : IEmployeeQueries
{
    public async Task<EmployeeInfo?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        return employee is null ? null : PeopleMapping.ToInfo(employee);
    }

    public async Task<IReadOnlyList<EmployeeInfo>> GetEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        var ids = employeeIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken)).Select(PeopleMapping.ToInfo).ToList();
    }

    public async Task<EmployeeInfo?> FindByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId, cancellationToken);
        return employee is null ? null : PeopleMapping.ToInfo(employee);
    }
}
