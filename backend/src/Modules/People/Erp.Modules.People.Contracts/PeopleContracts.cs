namespace Erp.Modules.People.Contracts;

public static class PeoplePermissions
{
    public const string EmployeeRead = "people.employee.read";
    public const string EmployeeManage = "people.employee.manage";
    public const string CommissionRead = "people.commission.read";

    /// <summary>Maintain commission plans and calculate draft commission runs.</summary>
    public const string CommissionManage = "people.commission.manage";

    public const string CommissionApprove = "people.commission.approve";

    /// <summary>Post and reverse approved commission runs (posts journals).</summary>
    public const string CommissionPost = "people.commission.post";
}

public static class PeopleDocumentTypes
{
    public const string CommissionRun = "CommissionRun";
}

/// <summary>Party type used on journal lines that belong to an employee (commission expense and payable).</summary>
public static class PeoplePartyTypes
{
    public const string Employee = "Employee";
}

public sealed record EmployeeInfo(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    Guid? BranchId,
    bool IsActive,
    bool IsSalesperson,
    Guid? UserId,
    Guid? CommissionPlanId)
{
    /// <summary>Only active employees flagged as salespersons can be credited with sales.</summary>
    public bool CanSell => IsActive && IsSalesperson;
}

/// <summary>Employee reads for other modules (sales documents, POS receipts, customers, reports). Scoped to the active company.</summary>
public interface IEmployeeQueries
{
    Task<EmployeeInfo?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmployeeInfo>> GetEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);

    /// <summary>The employee linked to a user account (e.g. the POS cashier), if any.</summary>
    Task<EmployeeInfo?> FindByUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>One commissioned document line of a posted commission run (credit notes and clawbacks are negative).</summary>
public sealed record CommissionStatementLine(
    Guid RunId,
    string RunNumber,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    Guid SalespersonId,
    string SourceType,
    Guid DocumentId,
    string DocumentNumber,
    DateOnly DocumentDate,
    decimal BasisAmount,
    decimal RatePercent,
    decimal Commission,
    bool Reversed);

/// <summary>Posted commission runs for reporting (commission statement per salesperson).</summary>
public interface ICommissionReportQueries
{
    Task<IReadOnlyList<CommissionStatementLine>> GetStatementAsync(DateOnly from, DateOnly to, Guid? salespersonId, CancellationToken cancellationToken = default);
}
