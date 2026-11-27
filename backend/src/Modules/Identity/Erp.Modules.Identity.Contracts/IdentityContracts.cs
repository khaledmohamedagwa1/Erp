namespace Erp.Modules.Identity.Contracts;

public sealed record TenantAdministratorRequest(Guid CompanyId, string Email, string DisplayName, string Password);

/// <summary>Used by tenant provisioning and company setup. Runs inside the caller's transaction.</summary>
public interface IIdentityProvisioning
{
    /// <summary>Creates the "Administrator" role with every installed permission and the first user.</summary>
    Task<Guid> CreateTenantAdministratorAsync(TenantAdministratorRequest request, CancellationToken cancellationToken = default);

    Task GrantCompanyAccessAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default);
}

public static class IdentityPermissions
{
    public const string UserRead = "identity.user.read";
    public const string UserManage = "identity.user.manage";
    public const string RoleRead = "identity.role.read";
    public const string RoleManage = "identity.role.manage";
}

public sealed record UserSummary(Guid Id, string Email, string DisplayName, bool IsActive);

/// <summary>Read access to users and role membership for other modules (e.g. approval routing and notifications).</summary>
public interface IUserDirectory
{
    Task<IReadOnlyCollection<Guid>> GetRoleIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserSummary>> GetUsersInRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserSummary>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}
