using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Identity.Domain;

public static class FederationErrors
{
    public static readonly Error ProviderNotFound = Error.NotFound("identity.provider-not-found", "The identity provider was not found.");
    public static readonly Error ProviderKeyTaken = Error.Conflict("identity.provider-key-taken", "An identity provider with this key already exists.");
    public static readonly Error ExternalSignInFailed = Error.Unauthorized("auth.external-sign-in-failed", "Sign-in with the external identity provider failed.");
    public static readonly Error ExternalStateInvalid = Error.Unauthorized("auth.external-state-invalid", "The sign-in request has expired or was not started in this browser. Try again.");
    public static readonly Error ExternalEmailNotVerified = Error.Unauthorized("auth.external-email-not-verified", "The identity provider did not confirm the email address.");
    public static readonly Error ExternalUserNotFound = Error.Unauthorized("auth.external-user-not-found", "No active user with this email exists in the organisation.");
    public static readonly Error InvalidReturnUrl = Error.Validation("auth.invalid-return-url", "The return URL is not allowed.");
}

/// <summary>
/// An external OpenID Connect provider a tenant signs in with (Microsoft Entra ID, Google Workspace, Okta, Keycloak...).
/// Users are matched by verified email; the ERP keeps its own users, roles and sessions. The client secret is never stored
/// here — only the name of a secret supplied to the server (Auth:Oidc:Secrets:{name}, e.g. a file in /run/secrets).
/// </summary>
[Audited]
public sealed class ExternalIdentityProvider : AggregateRoot, ITenantScoped
{
    private ExternalIdentityProvider()
    {
    }

    private ExternalIdentityProvider(Guid id, string key)
        : base(id)
    {
        Key = key;
    }

    /// <summary>URL-safe identifier used in the sign-in URL, e.g. "entra".</summary>
    public string Key { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Issuer URL; the discovery document is read from {Authority}/.well-known/openid-configuration.</summary>
    public string Authority { get; private set; } = string.Empty;

    public string ClientId { get; private set; } = string.Empty;

    /// <summary>Name of the server-side secret holding the client secret; null for public clients (PKCE only).</summary>
    public string? ClientSecretName { get; private set; }

    public string Scopes { get; private set; } = "openid email profile";

    /// <summary>ID-token claim holding the email used to match ERP users.</summary>
    public string EmailClaim { get; private set; } = "email";

    /// <summary>Require <c>email_verified=true</c> in the ID token (turn off only for providers that never send it, such as Entra ID).</summary>
    public bool RequireVerifiedEmail { get; private set; } = true;

    public bool IsEnabled { get; private set; }

    public static ExternalIdentityProvider Create(string key, string displayName, string authority, string clientId, string? clientSecretName,
        string? scopes, string? emailClaim, bool requireVerifiedEmail, bool enabled)
    {
        var normalizedKey = Guard.MaxLength(Guard.NotEmpty(key, "identity.provider-key-required", "Key"), 40, "identity.provider-key-too-long", "Key")
            .Trim().ToLowerInvariant();
        if (!normalizedKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException(Error.Validation("identity.provider-key-invalid", "Key may contain only letters, digits and hyphens."));
        }

        var provider = new ExternalIdentityProvider(SequentialGuid.New(), normalizedKey);
        provider.Update(displayName, authority, clientId, clientSecretName, scopes, emailClaim, requireVerifiedEmail, enabled);
        return provider;
    }

    public void Update(string displayName, string authority, string clientId, string? clientSecretName, string? scopes, string? emailClaim,
        bool requireVerifiedEmail, bool enabled)
    {
        DisplayName = Guard.MaxLength(Guard.NotEmpty(displayName, "identity.provider-name-required", "Display name"), 100, "identity.provider-name-too-long", "Display name");
        Authority = Guard.MaxLength(Guard.NotEmpty(authority, "identity.provider-authority-required", "Authority"), 300, "identity.provider-authority-too-long", "Authority").TrimEnd('/');
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainException(Error.Validation("identity.provider-authority-invalid", "Authority must be an absolute https URL."));
        }

        ClientId = Guard.MaxLength(Guard.NotEmpty(clientId, "identity.provider-client-required", "Client id"), 200, "identity.provider-client-too-long", "Client id");
        ClientSecretName = string.IsNullOrWhiteSpace(clientSecretName) ? null
            : Guard.MaxLength(clientSecretName.Trim(), 100, "identity.provider-secret-name-too-long", "Client secret name");
        Scopes = string.IsNullOrWhiteSpace(scopes) ? "openid email profile" : Guard.MaxLength(scopes.Trim(), 300, "identity.provider-scopes-too-long", "Scopes");
        if (!Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal))
        {
            Scopes = "openid " + Scopes;
        }

        EmailClaim = string.IsNullOrWhiteSpace(emailClaim) ? "email" : Guard.MaxLength(emailClaim.Trim(), 100, "identity.provider-claim-too-long", "Email claim");
        RequireVerifiedEmail = requireVerifiedEmail;
        IsEnabled = enabled;
    }
}

/// <summary>
/// A SaaS platform operator (provisions tenants). Not tenant-scoped and separate from tenant users: operators sign in with
/// password + mandatory TOTP and receive tokens for the <c>erp-platform</c> audience only, which tenant APIs reject.
/// </summary>
public sealed class PlatformOperator : AggregateRoot
{
    private PlatformOperator()
    {
    }

    private PlatformOperator(Guid id, string email, string displayName, string passwordHash, string mfaSecretProtected, bool isBootstrap)
        : base(id)
    {
        Email = email;
        NormalizedEmail = User.Normalize(email);
        DisplayName = displayName;
        PasswordHash = passwordHash;
        MfaSecretProtected = mfaSecretProtected;
        IsActive = true;
        IsBootstrap = isBootstrap;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    [Sensitive]
    public string PasswordHash { get; private set; } = string.Empty;

    [Sensitive]
    public string MfaSecretProtected { get; private set; } = string.Empty;

    public long? MfaLastTimeStep { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>The operator created with the static bootstrap key. At most one exists (unique filtered index).</summary>
    public bool IsBootstrap { get; private set; }

    public int AccessFailedCount { get; private set; }

    public DateTime? LockoutEndUtc { get; private set; }

    public DateTime? LastLoginAtUtc { get; private set; }

    public static PlatformOperator Create(string email, string displayName, string passwordHash, string mfaSecretProtected, bool isBootstrap) =>
        new(SequentialGuid.New(),
            Guard.MaxLength(Guard.NotEmpty(email, "identity.email-required", "Email"), 256, "identity.email-too-long", "Email").Trim(),
            Guard.MaxLength(Guard.NotEmpty(displayName, "identity.name-required", "Display name"), 200, "identity.name-too-long", "Display name"),
            Guard.NotEmpty(passwordHash, "identity.password-required", "Password"),
            Guard.NotEmpty(mfaSecretProtected, "identity.mfa-secret-required", "MFA secret"),
            isBootstrap);

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

    public bool AcceptTimeStep(long timeStep)
    {
        if (MfaLastTimeStep is { } last && timeStep <= last)
        {
            return false;
        }

        MfaLastTimeStep = timeStep;
        return true;
    }

    public void Deactivate() => IsActive = false;
}

/// <summary>Append-only record of platform-operator actions (sign-ins, provisioning, operator changes).</summary>
public sealed class PlatformAuditEntry
{
    private PlatformAuditEntry()
    {
    }

    public PlatformAuditEntry(DateTime atUtc, Guid? operatorId, string action, string? details, string? ipAddress)
    {
        AtUtc = atUtc;
        OperatorId = operatorId;
        Action = action.Length > 100 ? action[..100] : action;
        Details = details is { Length: > 2000 } ? details[..2000] : details;
        IpAddress = ipAddress;
    }

    public long Id { get; private set; }

    public DateTime AtUtc { get; private set; }

    public Guid? OperatorId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string? Details { get; private set; }

    public string? IpAddress { get; private set; }
}
