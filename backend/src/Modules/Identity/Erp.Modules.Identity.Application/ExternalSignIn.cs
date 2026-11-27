using System.Security.Cryptography;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Identity.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Identity.Application;

/// <summary>Claims taken from a validated ID token.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, bool? EmailVerified);

/// <summary>OpenID Connect authorization-code flow with PKCE against a tenant's provider (discovery, code redemption, ID-token validation).</summary>
public interface IOidcClient
{
    Task<string> BuildAuthorizationUrlAsync(ExternalIdentityProvider provider, string redirectUri, string state, string nonce, string codeChallenge,
        CancellationToken cancellationToken);

    /// <summary>Redeems the code and validates the ID token (signature, issuer, audience, lifetime, nonce).</summary>
    Task<Result<ExternalIdentity>> RedeemCodeAsync(ExternalIdentityProvider provider, string code, string redirectUri, string codeVerifier, string nonce,
        CancellationToken cancellationToken);
}

/// <summary>What the sign-in round trip must remember. Carried in the protected, time-limited <c>state</c> parameter.</summary>
public sealed record ExternalSignInState(Guid TenantId, Guid ProviderId, string CodeVerifier, string Nonce, string ReturnUrl, string BrowserBinding);

public interface IExternalSignInStateProtector
{
    string Protect(ExternalSignInState state);

    bool TryUnprotect(string? protectedState, out ExternalSignInState? state);
}

internal static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// ---------- Public: providers offered on the sign-in page ----------
public sealed record SignInProviderDto(string Key, string DisplayName);

public sealed record ListSignInProvidersQuery(string? TenantCode) : IQuery<IReadOnlyList<SignInProviderDto>>, IManualTransaction;

public sealed class ListSignInProvidersHandler(IIdentityDbContext db, ITenantDirectory tenants, ITenantContext tenantContext, Microsoft.Extensions.Options.IOptions<AuthOptions> options)
    : IQueryHandler<ListSignInProvidersQuery, IReadOnlyList<SignInProviderDto>>
{
    public async Task<Result<IReadOnlyList<SignInProviderDto>>> Handle(ListSignInProvidersQuery query, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(query.TenantCode) ? options.Value.DefaultTenantCode : query.TenantCode;
        var tenant = string.IsNullOrWhiteSpace(code) ? null : await tenants.FindByCodeAsync(code.Trim().ToUpperInvariant(), cancellationToken);
        if (tenant is not { IsActive: true })
        {
            // Same answer as "no providers" so the endpoint does not reveal which tenant codes exist.
            return Array.Empty<SignInProviderDto>();
        }

        tenantContext.SetTenant(tenant.Id);
        return await db.IdentityProviders.AsNoTracking().Where(p => p.IsEnabled).OrderBy(p => p.DisplayName)
            .Select(p => new SignInProviderDto(p.Key, p.DisplayName)).ToListAsync(cancellationToken);
    }
}

// ---------- Start ----------
public sealed record StartExternalSignInDto(string AuthorizationUrl, string BrowserBinding);

public sealed record StartExternalSignInCommand(string? TenantCode, string ProviderKey, string ReturnUrl, string RedirectUri)
    : ICommand<StartExternalSignInDto>, IManualTransaction;

public sealed class StartExternalSignInHandler(
    IIdentityDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    IOidcClient oidc,
    IExternalSignInStateProtector stateProtector,
    Microsoft.Extensions.Options.IOptions<AuthOptions> options) : ICommandHandler<StartExternalSignInCommand, StartExternalSignInDto>
{
    public async Task<Result<StartExternalSignInDto>> Handle(StartExternalSignInCommand command, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(command.TenantCode) ? options.Value.DefaultTenantCode : command.TenantCode;
        var tenant = string.IsNullOrWhiteSpace(code) ? null : await tenants.FindByCodeAsync(code.Trim().ToUpperInvariant(), cancellationToken);
        if (tenant is not { IsActive: true })
        {
            return FederationErrors.ProviderNotFound;
        }

        tenantContext.SetTenant(tenant.Id);
        var key = command.ProviderKey.Trim().ToLowerInvariant();
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key && p.IsEnabled, cancellationToken);
        if (provider is null)
        {
            return FederationErrors.ProviderNotFound;
        }

        var verifier = Pkce.NewVerifier();
        var nonce = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));
        var binding = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));
        var state = stateProtector.Protect(new ExternalSignInState(tenant.Id, provider.Id, verifier, nonce, command.ReturnUrl, binding));
        var url = await oidc.BuildAuthorizationUrlAsync(provider, command.RedirectUri, state, nonce, Pkce.Challenge(verifier), cancellationToken);
        return new StartExternalSignInDto(url, binding);
    }
}

// ---------- Callback ----------
/// <summary>
/// Outcome of the callback. <see cref="ReturnUrl"/> is known whenever the state was valid, so the browser can be sent back
/// to the application with either a session (refresh cookie), an MFA challenge, or an error code.
/// </summary>
public sealed record ExternalSignInOutcome(string? ReturnUrl, SignInResult? Session, string? MfaChallengeToken, Error? Error);

public sealed record CompleteExternalSignInCommand(string? Code, string? State, string? BrowserBinding, string? ProviderError, string RedirectUri,
    string? UserAgent, string? IpAddress) : ICommand<ExternalSignInOutcome>, IManualTransaction;

public sealed class CompleteExternalSignInHandler(
    IIdentityDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    IOidcClient oidc,
    IExternalSignInStateProtector stateProtector,
    IChallengeTokens challenges,
    ISessionIssuer sessions,
    IClock clock) : ICommandHandler<CompleteExternalSignInCommand, ExternalSignInOutcome>
{
    public async Task<Result<ExternalSignInOutcome>> Handle(CompleteExternalSignInCommand command, CancellationToken cancellationToken)
    {
        if (!stateProtector.TryUnprotect(command.State, out var state) || state is null ||
            string.IsNullOrEmpty(command.BrowserBinding) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state.BrowserBinding), Encoding.ASCII.GetBytes(command.BrowserBinding)))
        {
            return new ExternalSignInOutcome(null, null, null, FederationErrors.ExternalStateInvalid);
        }

        ExternalSignInOutcome Failed(Error error) => new(state.ReturnUrl, null, null, error);

        if (!string.IsNullOrEmpty(command.ProviderError) || string.IsNullOrEmpty(command.Code))
        {
            return Failed(FederationErrors.ExternalSignInFailed);
        }

        if (await tenants.FindByIdAsync(state.TenantId, cancellationToken) is not { IsActive: true })
        {
            return Failed(FederationErrors.ExternalSignInFailed);
        }

        tenantContext.SetTenant(state.TenantId);
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == state.ProviderId && p.IsEnabled, cancellationToken);
        if (provider is null)
        {
            return Failed(FederationErrors.ProviderNotFound);
        }

        var identity = await oidc.RedeemCodeAsync(provider, command.Code, command.RedirectUri, state.CodeVerifier, state.Nonce, cancellationToken);
        if (identity.IsFailure)
        {
            return Failed(identity.Error);
        }

        if (string.IsNullOrWhiteSpace(identity.Value.Email))
        {
            return Failed(FederationErrors.ExternalSignInFailed);
        }

        if (provider.RequireVerifiedEmail && identity.Value.EmailVerified != true)
        {
            return Failed(FederationErrors.ExternalEmailNotVerified);
        }

        var normalized = User.Normalize(identity.Value.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        var now = clock.UtcNow;
        if (user is not { IsActive: true })
        {
            return Failed(FederationErrors.ExternalUserNotFound);
        }

        if (user.IsLockedOut(now))
        {
            return Failed(IdentityErrors.LockedOut);
        }

        if (user.MfaEnabled)
        {
            // The ERP's own second factor still applies; the client completes it with POST /auth/mfa/verify.
            return new ExternalSignInOutcome(state.ReturnUrl, null, challenges.Create(ChallengePurpose.Mfa, state.TenantId, user.Id), null);
        }

        var session = await sessions.IssueAsync(user, state.TenantId, command.UserAgent, command.IpAddress, cancellationToken,
            authenticationMethod: "ext:" + provider.Key);
        return new ExternalSignInOutcome(state.ReturnUrl, session, null, null);
    }
}

// ---------- Administration ----------
public sealed record IdentityProviderDto(Guid Id, string Key, string DisplayName, string Authority, string ClientId, string? ClientSecretName,
    string Scopes, string EmailClaim, bool RequireVerifiedEmail, bool IsEnabled);

internal static class IdentityProviderMapping
{
    public static IdentityProviderDto ToDto(ExternalIdentityProvider p) =>
        new(p.Id, p.Key, p.DisplayName, p.Authority, p.ClientId, p.ClientSecretName, p.Scopes, p.EmailClaim, p.RequireVerifiedEmail, p.IsEnabled);
}

public sealed record ListIdentityProvidersQuery : IQuery<IReadOnlyList<IdentityProviderDto>>;

public sealed class ListIdentityProvidersHandler(IIdentityDbContext db) : IQueryHandler<ListIdentityProvidersQuery, IReadOnlyList<IdentityProviderDto>>
{
    public async Task<Result<IReadOnlyList<IdentityProviderDto>>> Handle(ListIdentityProvidersQuery query, CancellationToken cancellationToken) =>
        (await db.IdentityProviders.AsNoTracking().OrderBy(p => p.Key).ToListAsync(cancellationToken)).Select(IdentityProviderMapping.ToDto).ToList();
}

public sealed record SaveIdentityProviderCommand(Guid? Id, string Key, string DisplayName, string Authority, string ClientId, string? ClientSecretName,
    string? Scopes, string? EmailClaim, bool RequireVerifiedEmail, bool IsEnabled) : ICommand<IdentityProviderDto>;

public sealed class SaveIdentityProviderValidator : AbstractValidator<SaveIdentityProviderCommand>
{
    public SaveIdentityProviderValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9-]+$");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Authority).NotEmpty().MaximumLength(300)
            .Must(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps).WithMessage("Authority must be an absolute https URL.");
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ClientSecretName).MaximumLength(100).Matches("^[A-Za-z0-9_.-]*$").When(x => x.ClientSecretName is not null);
    }
}

public sealed class SaveIdentityProviderHandler(IIdentityDbContext db) : ICommandHandler<SaveIdentityProviderCommand, IdentityProviderDto>
{
    public async Task<Result<IdentityProviderDto>> Handle(SaveIdentityProviderCommand command, CancellationToken cancellationToken)
    {
        ExternalIdentityProvider provider;
        if (command.Id is { } id)
        {
            var existing = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (existing is null)
            {
                return FederationErrors.ProviderNotFound;
            }

            provider = existing;
            provider.Update(command.DisplayName, command.Authority, command.ClientId, command.ClientSecretName, command.Scopes, command.EmailClaim,
                command.RequireVerifiedEmail, command.IsEnabled);
        }
        else
        {
            var key = command.Key.Trim().ToLowerInvariant();
            if (await db.IdentityProviders.AnyAsync(p => p.Key == key, cancellationToken))
            {
                return FederationErrors.ProviderKeyTaken;
            }

            provider = ExternalIdentityProvider.Create(key, command.DisplayName, command.Authority, command.ClientId, command.ClientSecretName,
                command.Scopes, command.EmailClaim, command.RequireVerifiedEmail, command.IsEnabled);
            db.IdentityProviders.Add(provider);
        }

        await db.SaveChangesAsync(cancellationToken);
        return IdentityProviderMapping.ToDto(provider);
    }
}
