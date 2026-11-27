using System.Security.Cryptography;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Identity.Contracts;
using Erp.Modules.Identity.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Identity.Application;

public interface IIdentityDbContext
{
    DbSet<User> Users { get; }

    DbSet<Role> Roles { get; }

    DbSet<UserSession> Sessions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user, Guid tenantId, Guid sessionId);
}

/// <summary>Invalidates cached session, access and permission data after changes.</summary>
public interface IIdentityCache
{
    void InvalidateSession(Guid sessionId);

    void InvalidateUser(Guid userId);

    void InvalidateAllPermissions();
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "erp";

    public string Audience { get; set; } = "erp-api";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 14;

    public int SessionAbsoluteDays { get; set; } = 30;

    /// <summary>PEM-encoded RSA private key used to sign access tokens (RS256). Required outside Development.</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Single-tenant (on-prem) installations can omit the tenant code at sign-in.</summary>
    public string? DefaultTenantCode { get; set; }

    public bool SecureCookies { get; set; } = true;

    /// <summary>Issuer name shown in authenticator apps.</summary>
    public string MfaIssuer { get; set; } = "ERP";
}

/// <summary>
/// Refresh tokens are "{tenantId:N}.{256 random bits}". The tenant prefix lets the server set the tenant (and therefore
/// row-level security) before looking the token up; only its SHA-256 hash is stored.
/// </summary>
public static class RefreshTokens
{
    public static string Create(Guid tenantId) =>
        tenantId.ToString("N") + "." + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string rawToken) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public static bool TryReadTenant(string? rawToken, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        var dot = rawToken.IndexOf('.', StringComparison.Ordinal);
        return dot == 32 && Guid.TryParseExact(rawToken[..dot], "N", out tenantId);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record SignInResult(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    Guid UserId,
    Guid TenantId,
    string DisplayName);

// ---------- Login ----------
/// <summary>Result of a password sign-in: a session, an MFA challenge, or a forced password change.</summary>
public sealed record LoginOutcome(SignInResult? Session, string? MfaChallengeToken, string? PasswordChangeToken)
{
    public bool MfaRequired => MfaChallengeToken is not null;

    public bool PasswordChangeRequired => PasswordChangeToken is not null;
}

public sealed record LoginCommand(string? TenantCode, string Email, string Password, string? UserAgent, string? IpAddress)
    : ICommand<LoginOutcome>, IManualTransaction;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
        RuleFor(x => x.TenantCode).MaximumLength(30);
    }
}

public sealed class LoginHandler(
    IIdentityDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    IPasswordHasher hasher,
    ISessionIssuer sessions,
    IChallengeTokens challenges,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<AuthOptions> options) : ICommandHandler<LoginCommand, LoginOutcome>
{
    // Verified when the user does not exist so response time does not reveal which emails are registered.
    private static string? _dummyHash;

    private string DummyHash => _dummyHash ??= hasher.Hash(Guid.NewGuid().ToString());

    public async Task<Result<LoginOutcome>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var tenantCode = string.IsNullOrWhiteSpace(command.TenantCode) ? settings.DefaultTenantCode : command.TenantCode;
        if (string.IsNullOrWhiteSpace(tenantCode))
        {
            return IdentityErrors.InvalidCredentials;
        }

        var tenant = await tenants.FindByCodeAsync(tenantCode.Trim().ToUpperInvariant(), cancellationToken);
        if (tenant is not { IsActive: true })
        {
            hasher.Verify(DummyHash, command.Password);
            return IdentityErrors.InvalidCredentials;
        }

        tenantContext.SetTenant(tenant.Id);
        var now = clock.UtcNow;
        var normalized = User.Normalize(command.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is null || !user.IsActive)
        {
            hasher.Verify(DummyHash, command.Password);
            return IdentityErrors.InvalidCredentials;
        }

        if (user.IsLockedOut(now))
        {
            return IdentityErrors.LockedOut;
        }

        if (!hasher.Verify(user.PasswordHash, command.Password))
        {
            user.RegisterFailedLogin(now);
            await db.SaveChangesAsync(cancellationToken);
            return user.IsLockedOut(now) ? IdentityErrors.LockedOut : IdentityErrors.InvalidCredentials;
        }

        if (user.MustChangePassword)
        {
            user.ResetFailedAttempts();
            await db.SaveChangesAsync(cancellationToken);
            return new LoginOutcome(null, null, challenges.Create(ChallengePurpose.PasswordChange, tenant.Id, user.Id));
        }

        if (user.MfaEnabled)
        {
            // Password failures stay counted until the second factor also succeeds.
            return new LoginOutcome(null, challenges.Create(ChallengePurpose.Mfa, tenant.Id, user.Id), null);
        }

        var session = await sessions.IssueAsync(user, tenant.Id, command.UserAgent, command.IpAddress, cancellationToken);
        return new LoginOutcome(session, null, null);
    }
}

// ---------- Refresh ----------
public sealed record RefreshSessionCommand(string? RefreshToken) : ICommand<SignInResult>, IManualTransaction;

public sealed class RefreshSessionHandler(
    IIdentityDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    ITokenService tokens,
    IIdentityCache cache,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<AuthOptions> options) : ICommandHandler<RefreshSessionCommand, SignInResult>
{
    public async Task<Result<SignInResult>> Handle(RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        if (!RefreshTokens.TryReadTenant(command.RefreshToken, out var tenantId))
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        tenantContext.SetTenant(tenantId);
        var hash = RefreshTokens.Hash(command.RefreshToken!);
        var session = await db.Sessions
            .Include(s => s.Tokens.Where(t => t.TokenHash == hash))
            .FirstOrDefaultAsync(s => s.Tokens.Any(t => t.TokenHash == hash), cancellationToken);
        if (session is null)
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        var now = clock.UtcNow;
        var rawRefresh = RefreshTokens.Create(tenantId);
        var rotation = session.Rotate(hash, RefreshTokens.Hash(rawRefresh), now, TimeSpan.FromDays(options.Value.RefreshTokenDays));

        // Persist even on failure: a detected reuse revokes the session and that revocation must stick.
        await db.SaveChangesAsync(cancellationToken);
        if (rotation.IsFailure)
        {
            cache.InvalidateSession(session.Id);
            return rotation.Error;
        }

        var user = await db.Users.FirstAsync(u => u.Id == session.UserId, cancellationToken);
        if (!user.IsActive)
        {
            session.Revoke("user-deactivated", now);
            await db.SaveChangesAsync(cancellationToken);
            cache.InvalidateSession(session.Id);
            return IdentityErrors.SessionRevoked;
        }

        var access = tokens.CreateAccessToken(user, tenantId, session.Id);
        var newest = session.Tokens.Single(t => t.UsedAtUtc is null);
        return new SignInResult(access.Token, access.ExpiresAtUtc, rawRefresh, newest.ExpiresAtUtc, user.Id, tenantId, user.DisplayName);
    }
}

// ---------- Logout ----------
public sealed record LogoutCommand : ICommand<Unit>;

public sealed class LogoutHandler(IIdentityDbContext db, ICurrentUser currentUser, IIdentityCache cache, IClock clock) : ICommandHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.SessionId is not { } sessionId)
        {
            return Unit.Value;
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is not null)
        {
            session.Revoke("logout", clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            cache.InvalidateSession(sessionId);
        }

        return Unit.Value;
    }
}

// ---------- Me ----------
public sealed record MeDto(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid TenantId,
    string TenantCode,
    string TenantName,
    Guid? ActiveCompanyId,
    IReadOnlyList<CompanySummary> Companies,
    IReadOnlyCollection<string> Permissions);

public sealed record GetMeQuery : IQuery<MeDto>;

public sealed class GetMeHandler(
    IIdentityDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    ITenantDirectory tenants,
    IUserAccessService access,
    IPermissionService permissions,
    IOrganizationQueries organization) : IQueryHandler<GetMeQuery, MeDto>
{
    public async Task<Result<MeDto>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var tenant = (await tenants.FindByIdAsync(tenantContext.TenantId!.Value, cancellationToken))!;
        var companyIds = await access.GetAccessibleCompanyIdsAsync(userId, cancellationToken);
        var companies = await organization.GetCompaniesAsync(companyIds, cancellationToken);
        var granted = await permissions.GetPermissionsAsync(userId, cancellationToken);
        return new MeDto(user.Id, user.Email, user.DisplayName, tenant.Id, tenant.Code, tenant.Name, tenantContext.CompanyId,
            companies, granted.Order(StringComparer.Ordinal).ToList());
    }
}

// ---------- Users ----------
public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsActive, DateTime? LastLoginAtUtc, IReadOnlyCollection<Guid> RoleIds, IReadOnlyCollection<Guid> CompanyIds);

public sealed record CreateUserCommand(string Email, string DisplayName, string Password, IReadOnlyCollection<Guid> RoleIds, IReadOnlyCollection<Guid> CompanyIds)
    : ICommand<UserDto>;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10).WithMessage("Password must be at least 10 characters.");
    }
}

public sealed class CreateUserHandler(IIdentityDbContext db, IPasswordHasher hasher, ICurrentUser currentUser, IUserAccessService access)
    : ICommandHandler<CreateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(command.Email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken))
        {
            return IdentityErrors.EmailTaken;
        }

        var roleIds = command.RoleIds ?? [];
        if (await db.Roles.CountAsync(r => roleIds.Contains(r.Id), cancellationToken) != roleIds.Distinct().Count())
        {
            return IdentityErrors.RoleNotFound;
        }

        // An administrator can only grant access to companies they can access themselves.
        var grantable = await access.GetAccessibleCompanyIdsAsync(currentUser.UserId!.Value, cancellationToken);
        var companyIds = command.CompanyIds ?? [];
        if (companyIds.Any(c => !grantable.Contains(c)))
        {
            return Error.Forbidden("company.access-denied", "You cannot grant access to a company you cannot access.");
        }

        var user = User.Create(command.Email, command.DisplayName, hasher.Hash(command.Password));
        user.SetRoles(roleIds);
        foreach (var companyId in companyIds)
        {
            user.GrantCompanyAccess(companyId);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityMapping.ToDto(user);
    }
}

public sealed record SetUserRolesCommand(Guid UserId, IReadOnlyCollection<Guid> RoleIds) : ICommand<UserDto>;

public sealed class SetUserRolesHandler(IIdentityDbContext db, IIdentityCache cache) : ICommandHandler<SetUserRolesCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetUserRolesCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(u => u.Roles).Include(u => u.CompanyAccess).FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var roleIds = command.RoleIds ?? [];
        if (await db.Roles.CountAsync(r => roleIds.Contains(r.Id), cancellationToken) != roleIds.Distinct().Count())
        {
            return IdentityErrors.RoleNotFound;
        }

        user.SetRoles(roleIds);
        await db.SaveChangesAsync(cancellationToken);
        cache.InvalidateUser(user.Id);
        return IdentityMapping.ToDto(user);
    }
}

public sealed record SetUserCompanyAccessCommand(Guid UserId, Guid CompanyId, bool Granted, IReadOnlyCollection<Guid>? BranchIds, IReadOnlyCollection<Guid>? WarehouseIds)
    : ICommand<UserDto>;

public sealed class SetUserCompanyAccessHandler(IIdentityDbContext db, IIdentityCache cache, ICurrentUser currentUser, IUserAccessService access)
    : ICommandHandler<SetUserCompanyAccessCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetUserCompanyAccessCommand command, CancellationToken cancellationToken)
    {
        if (await access.GetCompanyAccessAsync(currentUser.UserId!.Value, command.CompanyId, cancellationToken) is null)
        {
            return Error.Forbidden("company.access-denied", "You cannot grant access to a company you cannot access.");
        }

        var user = await db.Users.Include(u => u.Roles).Include(u => u.CompanyAccess).FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        if (command.Granted)
        {
            user.GrantCompanyAccess(command.CompanyId, command.BranchIds, command.WarehouseIds);
        }
        else
        {
            user.RevokeCompanyAccess(command.CompanyId);
        }

        await db.SaveChangesAsync(cancellationToken);
        cache.InvalidateUser(user.Id);
        return IdentityMapping.ToDto(user);
    }
}

public sealed record ListUsersQuery(PageRequest Page, string? Search) : IQuery<PagedResult<UserDto>>;

public sealed class ListUsersHandler(IIdentityDbContext db) : IQueryHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<Result<PagedResult<UserDto>>> Handle(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var source = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(u => u.DisplayName.Contains(term) || u.Email.Contains(term));
        }

        var total = await source.CountAsync(cancellationToken);
        var users = await source.Include(u => u.Roles).Include(u => u.CompanyAccess)
            .OrderBy(u => u.DisplayName).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<UserDto>(users.Select(IdentityMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ---------- Roles ----------
public sealed record RoleDto(Guid Id, string Name, bool IsSystem, IReadOnlyCollection<string> Permissions);

public sealed record CreateRoleCommand(string Name, IReadOnlyCollection<string> Permissions) : ICommand<RoleDto>;

public sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}

public sealed class CreateRoleHandler(IIdentityDbContext db, IPermissionCatalog catalog) : ICommandHandler<CreateRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        var permissions = command.Permissions ?? [];
        if (permissions.Any(p => !catalog.Exists(p)))
        {
            return IdentityErrors.UnknownPermission;
        }

        var name = command.Name.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == name, cancellationToken))
        {
            return IdentityErrors.RoleNameTaken;
        }

        var role = Role.Create(name, permissions);
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityMapping.ToDto(role);
    }
}

public sealed record SetRolePermissionsCommand(Guid RoleId, IReadOnlyCollection<string> Permissions) : ICommand<RoleDto>;

public sealed class SetRolePermissionsHandler(IIdentityDbContext db, IPermissionCatalog catalog, IIdentityCache cache)
    : ICommandHandler<SetRolePermissionsCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(SetRolePermissionsCommand command, CancellationToken cancellationToken)
    {
        var permissions = command.Permissions ?? [];
        if (permissions.Any(p => !catalog.Exists(p)))
        {
            return IdentityErrors.UnknownPermission;
        }

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == command.RoleId, cancellationToken);
        if (role is null)
        {
            return IdentityErrors.RoleNotFound;
        }

        if (role.IsSystem)
        {
            return IdentityErrors.SystemRoleLocked;
        }

        role.SetPermissions(permissions);
        await db.SaveChangesAsync(cancellationToken);
        cache.InvalidateAllPermissions();
        return IdentityMapping.ToDto(role);
    }
}

public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

public sealed class ListRolesHandler(IIdentityDbContext db) : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery query, CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        return roles.Select(IdentityMapping.ToDto).ToList();
    }
}

public sealed record PermissionDto(string Code, string Module, string DescriptionEn, string DescriptionAr, bool IsSensitive);

public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

public sealed class ListPermissionsHandler(IPermissionCatalog catalog) : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<PermissionDto> list = catalog.All.OrderBy(p => p.Code, StringComparer.Ordinal)
            .Select(p => new PermissionDto(p.Code, p.Module, p.DescriptionEn, p.DescriptionAr, p.IsSensitive)).ToList();
        return Task.FromResult(Result.Success(list));
    }
}

// ---------- Provisioning contract ----------
public sealed class IdentityProvisioning(IIdentityDbContext db, IPasswordHasher hasher, IPermissionCatalog catalog, IIdentityCache cache)
    : IIdentityProvisioning
{
    public async Task<Guid> CreateTenantAdministratorAsync(TenantAdministratorRequest request, CancellationToken cancellationToken = default)
    {
        var role = Role.CreateAdministrator(catalog.All.Select(p => p.Code));
        var user = User.Create(request.Email, request.DisplayName, hasher.Hash(request.Password));
        user.SetRoles([role.Id]);
        user.GrantCompanyAccess(request.CompanyId);
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    public async Task GrantCompanyAccessAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.Include(u => u.CompanyAccess).FirstAsync(u => u.Id == userId, cancellationToken);
        user.GrantCompanyAccess(companyId);
        await db.SaveChangesAsync(cancellationToken);
        cache.InvalidateUser(userId);
    }
}

internal static class IdentityMapping
{
    public static UserDto ToDto(User u) => new(u.Id, u.Email, u.DisplayName, u.IsActive, u.LastLoginAtUtc,
        u.Roles.Select(r => r.RoleId).ToList(), u.CompanyAccess.Select(a => a.CompanyId).ToList());

    public static RoleDto ToDto(Role r) => new(r.Id, r.Name, r.IsSystem, r.Permissions);
}
