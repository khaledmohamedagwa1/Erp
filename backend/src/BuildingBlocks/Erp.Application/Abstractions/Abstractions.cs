namespace Erp.Application.Abstractions;

/// <summary>
/// The trusted tenant/company context of the current scope. Set only by infrastructure from a validated token
/// (or explicitly by trusted platform code). Never populated from request bodies.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? CompanyId { get; }

    bool HasTenant { get; }

    void SetTenant(Guid tenantId);

    void SetCompany(Guid companyId);
}

public interface ICurrentUser
{
    Guid? UserId { get; }

    Guid? SessionId { get; }

    bool IsAuthenticated { get; }
}

public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>One database transaction spanning every module DbContext in the current scope.</summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }

    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public sealed record TenantInfo(Guid Id, string Code, string Name, bool IsActive);

/// <summary>Looks tenants up outside the tenant-scoped database session (used before a tenant is known).</summary>
public interface ITenantDirectory
{
    Task<TenantInfo?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<TenantInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);
}

public sealed record CompanyAccess(Guid CompanyId, IReadOnlyCollection<Guid> BranchIds, IReadOnlyCollection<Guid> WarehouseIds)
{
    /// <summary>Empty branch/warehouse lists mean "all branches/warehouses of the company".</summary>
    public bool AllBranches => BranchIds.Count == 0;

    public bool AllWarehouses => WarehouseIds.Count == 0;
}

public interface IUserAccessService
{
    Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<CompanyAccess?> GetCompanyAccessAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Guid>> GetAccessibleCompanyIdsAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default);
}

public sealed record PermissionDefinition(string Code, string Module, string DescriptionEn, string DescriptionAr, bool IsSensitive = false);

/// <summary>All permissions declared by the installed modules.</summary>
public interface IPermissionCatalog
{
    IReadOnlyCollection<PermissionDefinition> All { get; }

    bool Exists(string code);
}

/// <summary>Which optional modules are switched on for a company.</summary>
public interface IModuleActivationService
{
    Task<bool> IsEnabledAsync(Guid companyId, string moduleCode, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetEnabledModulesAsync(Guid companyId, CancellationToken cancellationToken = default);
}

public sealed record NumberScope(
    Guid CompanyId,
    string DocumentType,
    string DefaultPrefix,
    Guid? LegalEntityId = null,
    Guid? BranchId = null,
    string? BranchCode = null,
    int? FiscalYear = null);

/// <summary>Concurrency-safe, gapless document numbers. Must be called inside the posting transaction.</summary>
public interface IDocumentNumberGenerator
{
    Task<string> NextAsync(NumberScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Handles a domain event delivered from a module outbox (at least once — handlers must be idempotent).</summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : Erp.SharedKernel.Domain.IDomainEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
