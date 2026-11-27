using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Identity.Domain;

public static class IdentityErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid-credentials", "The email or password is incorrect.");
    public static readonly Error LockedOut = Error.Unauthorized("auth.locked-out", "The account is temporarily locked after repeated failed sign-ins.");
    public static readonly Error RefreshTokenInvalid = Error.Unauthorized("auth.refresh-token-invalid", "The refresh token is invalid or expired.");
    public static readonly Error RefreshTokenReused = Error.Unauthorized("auth.refresh-token-reused", "A refresh token was reused; the session has been revoked.");
    public static readonly Error SessionRevoked = Error.Unauthorized("auth.session-revoked", "The session is no longer active.");
    public static readonly Error EmailTaken = Error.Conflict("identity.email-taken", "A user with this email already exists.");
    public static readonly Error UserNotFound = Error.NotFound("identity.user-not-found", "The user was not found.");
    public static readonly Error RoleNotFound = Error.NotFound("identity.role-not-found", "The role was not found.");
    public static readonly Error RoleNameTaken = Error.Conflict("identity.role-name-taken", "A role with this name already exists.");
    public static readonly Error SystemRoleLocked = Error.Conflict("identity.system-role-locked", "System roles cannot be changed.");
    public static readonly Error UnknownPermission = Error.Validation("identity.unknown-permission", "One or more permissions do not exist.");
    public static readonly Error MfaAlreadyEnabled = Error.Conflict("identity.mfa-already-enabled", "Two-step verification is already enabled.");
    public static readonly Error MfaNotEnrolling = Error.Conflict("identity.mfa-not-enrolling", "Start enrollment before confirming a code.");
    public static readonly Error MfaNotEnabled = Error.Conflict("identity.mfa-not-enabled", "Two-step verification is not enabled.");
    public static readonly Error MfaInvalidCode = Error.Unauthorized("auth.mfa-invalid-code", "The verification code is invalid or was already used.");
    public static readonly Error MfaChallengeInvalid = Error.Unauthorized("auth.mfa-challenge-invalid", "The sign-in challenge has expired. Sign in again.");
    public static readonly Error PasswordChangeRequired = Error.Forbidden("auth.password-change-required", "You must choose a new password before signing in.");
    public static readonly Error PasswordChangeTokenInvalid = Error.Unauthorized("auth.password-change-token-invalid", "The password-change link has expired. Sign in again.");
    public static readonly Error InvalidCurrentPassword = Error.Validation("identity.invalid-current-password", "The current password is incorrect.");
    public static readonly Error CannotDeactivateSelf = Error.Conflict("identity.cannot-deactivate-self", "You cannot deactivate your own account.");
}

public static class LoginPolicy
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
}

[Audited]
public sealed class User : AggregateRoot, ITenantScoped
{
    private readonly List<UserRole> _roles = [];
    private readonly List<UserCompanyAccess> _companyAccess = [];

    private User()
    {
    }

    private User(Guid id, string email, string displayName, string passwordHash)
        : base(id)
    {
        Email = email;
        NormalizedEmail = Normalize(email);
        DisplayName = displayName;
        PasswordHash = passwordHash;
        IsActive = true;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    [Sensitive]
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>Set by an administrator's password reset: the user must choose a new password before signing in.</summary>
    public bool MustChangePassword { get; private set; }

    public bool MfaEnabled { get; private set; }

    /// <summary>TOTP secret, encrypted with ASP.NET Core Data Protection.</summary>
    [Sensitive]
    public string? MfaSecretProtected { get; private set; }

    /// <summary>Secret generated at enrollment, active only after the user confirms a code.</summary>
    [Sensitive]
    public string? PendingMfaSecretProtected { get; private set; }

    /// <summary>Last accepted TOTP time step — a code can be used only once.</summary>
    public long? MfaLastTimeStep { get; private set; }

    /// <summary>SHA-256 hashes of unused recovery codes.</summary>
    [Sensitive]
    public List<string> RecoveryCodeHashes { get; private set; } = [];

    public bool IsActive { get; private set; }

    public int AccessFailedCount { get; private set; }

    public DateTime? LockoutEndUtc { get; private set; }

    public DateTime? LastLoginAtUtc { get; private set; }

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public IReadOnlyCollection<UserCompanyAccess> CompanyAccess => _companyAccess.AsReadOnly();

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public static User Create(string email, string displayName, string passwordHash)
    {
        var user = new User(
            SequentialGuid.New(),
            Guard.MaxLength(Guard.NotEmpty(email, "identity.email-required", "Email"), 256, "identity.email-too-long", "Email"),
            Guard.MaxLength(Guard.NotEmpty(displayName, "identity.name-required", "Display name"), 200, "identity.name-too-long", "Display name"),
            Guard.NotEmpty(passwordHash, "identity.password-required", "Password"));
        user.Raise(new UserCreated(user.Id, user.Email));
        return user;
    }

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    public void RegisterFailedLogin(DateTime nowUtc)
    {
        AccessFailedCount++;
        if (AccessFailedCount >= LoginPolicy.MaxFailedAttempts)
        {
            LockoutEndUtc = nowUtc.Add(LoginPolicy.LockoutDuration);
            AccessFailedCount = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTime nowUtc)
    {
        AccessFailedCount = 0;
        LockoutEndUtc = null;
        LastLoginAtUtc = nowUtc;
    }

    public void ResetFailedAttempts() => AccessFailedCount = 0;

    /// <summary>The user chose a new password (also clears a pending forced change).</summary>
    public void ChangePasswordHash(string passwordHash)
    {
        PasswordHash = Guard.NotEmpty(passwordHash, "identity.password-required", "Password");
        MustChangePassword = false;
        Raise(new PasswordChanged(Id));
    }

    /// <summary>Administrator reset: temporary password, unlocks the account, forces a change at next sign-in.</summary>
    public void SetTemporaryPassword(string passwordHash)
    {
        PasswordHash = Guard.NotEmpty(passwordHash, "identity.password-required", "Password");
        MustChangePassword = true;
        AccessFailedCount = 0;
        LockoutEndUtc = null;
        Raise(new PasswordResetByAdministrator(Id));
    }

    public void Deactivate() => IsActive = false;

    public void Activate()
    {
        IsActive = true;
        AccessFailedCount = 0;
        LockoutEndUtc = null;
    }

    public void BeginMfaEnrollment(string protectedSecret)
    {
        if (MfaEnabled)
        {
            throw new DomainException(IdentityErrors.MfaAlreadyEnabled);
        }

        PendingMfaSecretProtected = Guard.NotEmpty(protectedSecret, "identity.mfa-secret-required", "MFA secret");
    }

    public void ConfirmMfa(IEnumerable<string> recoveryCodeHashes)
    {
        if (PendingMfaSecretProtected is null)
        {
            throw new DomainException(IdentityErrors.MfaNotEnrolling);
        }

        MfaSecretProtected = PendingMfaSecretProtected;
        PendingMfaSecretProtected = null;
        MfaEnabled = true;
        RecoveryCodeHashes = recoveryCodeHashes.ToList();
        Raise(new MfaStatusChanged(Id, true));
    }

    public void DisableMfa()
    {
        if (!MfaEnabled)
        {
            throw new DomainException(IdentityErrors.MfaNotEnabled);
        }

        MfaEnabled = false;
        MfaSecretProtected = null;
        PendingMfaSecretProtected = null;
        MfaLastTimeStep = null;
        RecoveryCodeHashes = [];
        Raise(new MfaStatusChanged(Id, false));
    }

    /// <summary>Accepts a TOTP time step only if it is newer than the last one used (blocks code replay).</summary>
    public bool AcceptTimeStep(long timeStep)
    {
        if (MfaLastTimeStep is { } last && timeStep <= last)
        {
            return false;
        }

        MfaLastTimeStep = timeStep;
        return true;
    }

    /// <summary>Consumes a recovery code (single use).</summary>
    public bool UseRecoveryCode(string codeHash)
    {
        if (!RecoveryCodeHashes.Contains(codeHash))
        {
            return false;
        }

        RecoveryCodeHashes = RecoveryCodeHashes.Where(h => h != codeHash).ToList();
        return true;
    }

    public void SetRoles(IEnumerable<Guid> roleIds)
    {
        var wanted = roleIds.Distinct().ToHashSet();
        _roles.RemoveAll(r => !wanted.Contains(r.RoleId));
        foreach (var roleId in wanted.Where(id => _roles.All(r => r.RoleId != id)))
        {
            _roles.Add(new UserRole(Id, roleId));
        }
    }

    /// <summary>Grants (or replaces) access to a company. Empty scopes mean all branches / all warehouses.</summary>
    public void GrantCompanyAccess(Guid companyId, IEnumerable<Guid>? branchIds = null, IEnumerable<Guid>? warehouseIds = null)
    {
        var existing = _companyAccess.FirstOrDefault(a => a.CompanyId == companyId);
        if (existing is null)
        {
            _companyAccess.Add(new UserCompanyAccess(Id, companyId, branchIds, warehouseIds));
        }
        else
        {
            existing.SetScopes(branchIds, warehouseIds);
        }
    }

    public void RevokeCompanyAccess(Guid companyId) => _companyAccess.RemoveAll(a => a.CompanyId == companyId);
}

public sealed class UserRole : ITenantScoped
{
    private UserRole()
    {
    }

    internal UserRole(Guid userId, Guid roleId)
    {
        UserId = userId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }
}

public sealed class UserCompanyAccess : ITenantScoped
{
    private UserCompanyAccess()
    {
    }

    internal UserCompanyAccess(Guid userId, Guid companyId, IEnumerable<Guid>? branchIds, IEnumerable<Guid>? warehouseIds)
    {
        UserId = userId;
        CompanyId = companyId;
        SetScopes(branchIds, warehouseIds);
    }

    public Guid UserId { get; private set; }

    public Guid CompanyId { get; private set; }

    public List<Guid> BranchIds { get; private set; } = [];

    public List<Guid> WarehouseIds { get; private set; } = [];

    internal void SetScopes(IEnumerable<Guid>? branchIds, IEnumerable<Guid>? warehouseIds)
    {
        BranchIds = branchIds?.Distinct().ToList() ?? [];
        WarehouseIds = warehouseIds?.Distinct().ToList() ?? [];
    }
}

[Audited]
public sealed class Role : AggregateRoot, ITenantScoped
{
    public const string AdministratorName = "Administrator";

    private Role()
    {
    }

    private Role(Guid id, string name, bool isSystem, IEnumerable<string> permissions)
        : base(id)
    {
        Name = name;
        IsSystem = isSystem;
        Permissions = permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>System roles (Administrator) are maintained by the platform and cannot be edited.</summary>
    public bool IsSystem { get; private set; }

    public List<string> Permissions { get; private set; } = [];

    public static Role Create(string name, IEnumerable<string> permissions) =>
        new(SequentialGuid.New(), Guard.MaxLength(Guard.NotEmpty(name, "identity.role-name-required", "Role name"), 100, "identity.role-name-too-long", "Role name"), false, permissions);

    public static Role CreateAdministrator(IEnumerable<string> allPermissions) => new(SequentialGuid.New(), AdministratorName, true, allPermissions);

    public void SetPermissions(IEnumerable<string> permissions)
    {
        if (IsSystem)
        {
            throw new DomainException(IdentityErrors.SystemRoleLocked);
        }

        Permissions = permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Raise(new RolePermissionsChanged(Id));
    }
}

/// <summary>
/// A sign-in. Refresh tokens rotate on every use; presenting an already-used token means it was stolen,
/// so the whole session is revoked (reuse detection).
/// </summary>
public sealed class UserSession : AggregateRoot, ITenantScoped
{
    private readonly List<RefreshToken> _tokens = [];

    private UserSession()
    {
    }

    private UserSession(Guid id, Guid userId, DateTime nowUtc, DateTime absoluteExpiresAtUtc, string? userAgent, string? ipAddress)
        : base(id)
    {
        UserId = userId;
        StartedAtUtc = nowUtc;
        AbsoluteExpiresAtUtc = absoluteExpiresAtUtc;
        UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent;
        IpAddress = ipAddress;
    }

    public Guid UserId { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    public DateTime AbsoluteExpiresAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? UserAgent { get; private set; }

    public string? IpAddress { get; private set; }

    public IReadOnlyCollection<RefreshToken> Tokens => _tokens.AsReadOnly();

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && AbsoluteExpiresAtUtc > nowUtc;

    public static UserSession Start(Guid userId, DateTime nowUtc, TimeSpan absoluteLifetime, string? userAgent, string? ipAddress) =>
        new(SequentialGuid.New(), userId, nowUtc, nowUtc.Add(absoluteLifetime), userAgent, ipAddress);

    public RefreshToken IssueRefreshToken(string tokenHash, DateTime nowUtc, TimeSpan lifetime)
    {
        var expires = nowUtc.Add(lifetime);
        var token = new RefreshToken(SequentialGuid.New(), Id, tokenHash, nowUtc, expires < AbsoluteExpiresAtUtc ? expires : AbsoluteExpiresAtUtc);
        _tokens.Add(token);
        return token;
    }

    /// <summary>Exchanges the presented token (must be loaded into <see cref="Tokens"/>) for a new one.</summary>
    public Result Rotate(string presentedHash, string newHash, DateTime nowUtc, TimeSpan lifetime)
    {
        if (!IsActive(nowUtc))
        {
            return IdentityErrors.SessionRevoked;
        }

        var presented = _tokens.FirstOrDefault(t => t.TokenHash == presentedHash);
        if (presented is null)
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        if (presented.UsedAtUtc is not null)
        {
            Revoke("refresh-token-reuse", nowUtc);
            return IdentityErrors.RefreshTokenReused;
        }

        if (presented.ExpiresAtUtc <= nowUtc)
        {
            return IdentityErrors.RefreshTokenInvalid;
        }

        presented.MarkUsed(nowUtc);
        IssueRefreshToken(newHash, nowUtc, lifetime);
        return Result.Success();
    }

    public void Revoke(string reason, DateTime nowUtc)
    {
        if (RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = nowUtc;
        RevokedReason = reason;
        Raise(new SessionRevoked(Id, UserId, reason));
    }
}

public sealed class RefreshToken : ITenantScoped
{
    private RefreshToken()
    {
    }

    internal RefreshToken(Guid id, Guid sessionId, string tokenHash, DateTime createdAtUtc, DateTime expiresAtUtc)
    {
        Id = id;
        SessionId = sessionId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    /// <summary>SHA-256 of the token. The raw token is never stored.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? UsedAtUtc { get; private set; }

    internal void MarkUsed(DateTime nowUtc) => UsedAtUtc = nowUtc;
}

public sealed record UserCreated(Guid UserId, string Email) : IDomainEvent;

public sealed record RolePermissionsChanged(Guid RoleId) : IDomainEvent;

public sealed record SessionRevoked(Guid SessionId, Guid UserId, string Reason) : IDomainEvent;

public sealed record PasswordChanged(Guid UserId) : IDomainEvent;

public sealed record PasswordResetByAdministrator(Guid UserId) : IDomainEvent;

public sealed record MfaStatusChanged(Guid UserId, bool Enabled) : IDomainEvent;
