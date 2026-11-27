using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Domain;
using Erp.SharedKernel.Results;
using Erp.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Modules.Identity.Infrastructure;

/// <summary>
/// OpenID Connect client: discovery (cached per authority), authorization URL with PKCE (S256), code redemption with
/// client_secret_post, and ID-token validation against the provider's JWKS. Uses the named HttpClient "erp-oidc".
/// </summary>
public sealed class OidcClient(IHttpClientFactory httpClients, IConfiguration configuration, ILogger<OidcClient> logger) : IOidcClient
{
    public const string HttpClientName = "erp-oidc";
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);
    private static readonly JsonWebTokenHandler TokenHandler = new();

    public async Task<string> BuildAuthorizationUrlAsync(ExternalIdentityProvider provider, string redirectUri, string state, string nonce, string codeChallenge,
        CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(provider, cancellationToken);
        var message = new OpenIdConnectMessage
        {
            IssuerAddress = discovery.AuthorizationEndpoint,
            ClientId = provider.ClientId,
            RedirectUri = redirectUri,
            ResponseType = OpenIdConnectResponseType.Code,
            ResponseMode = OpenIdConnectResponseMode.Query,
            Scope = provider.Scopes,
            State = state,
            Nonce = nonce,
        };
        message.Parameters["code_challenge"] = codeChallenge;
        message.Parameters["code_challenge_method"] = "S256";
        return message.CreateAuthenticationRequestUrl();
    }

    public async Task<Result<ExternalIdentity>> RedeemCodeAsync(ExternalIdentityProvider provider, string code, string redirectUri, string codeVerifier, string nonce,
        CancellationToken cancellationToken)
    {
        try
        {
            var discovery = await DiscoverAsync(provider, cancellationToken);
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = provider.ClientId,
                ["code_verifier"] = codeVerifier,
            };
            if (provider.ClientSecretName is { } secretName)
            {
                var secret = configuration[$"Auth:Oidc:Secrets:{secretName}"];
                if (string.IsNullOrEmpty(secret))
                {
                    logger.LogError("Client secret Auth:Oidc:Secrets:{SecretName} for identity provider {Provider} is not configured.", secretName, provider.Key);
                    return FederationErrors.ExternalSignInFailed;
                }

                form["client_secret"] = secret;
            }

            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsync(discovery.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Identity provider {Provider} rejected the code redemption with {Status}.", provider.Key, (int)response.StatusCode);
                return FederationErrors.ExternalSignInFailed;
            }

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!body.RootElement.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.GetString() is not { } idToken)
            {
                return FederationErrors.ExternalSignInFailed;
            }

            var validation = await TokenHandler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = discovery.Issuer,
                ValidAudience = provider.ClientId,
                IssuerSigningKeys = discovery.SigningKeys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                RequireSignedTokens = true,
            });
            if (!validation.IsValid)
            {
                logger.LogWarning(validation.Exception, "ID token from identity provider {Provider} failed validation.", provider.Key);
                return FederationErrors.ExternalSignInFailed;
            }

            var claims = validation.ClaimsIdentity;
            if (!string.Equals(claims.FindFirst("nonce")?.Value, nonce, StringComparison.Ordinal))
            {
                logger.LogWarning("ID token from identity provider {Provider} has a wrong nonce.", provider.Key);
                return FederationErrors.ExternalSignInFailed;
            }

            var subject = claims.FindFirst("sub")?.Value ?? string.Empty;
            var email = claims.FindFirst(provider.EmailClaim)?.Value;
            bool? verified = claims.FindFirst("email_verified")?.Value is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : null;
            return new ExternalIdentity(subject, email, verified);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException or IOException)
        {
            logger.LogWarning(ex, "Sign-in with identity provider {Provider} failed.", provider.Key);
            return FederationErrors.ExternalSignInFailed;
        }
    }

    private Task<OpenIdConnectConfiguration> DiscoverAsync(ExternalIdentityProvider provider, CancellationToken cancellationToken)
    {
        var manager = _managers.GetOrAdd(provider.Authority, authority => new ConfigurationManager<OpenIdConnectConfiguration>(
            authority + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClients.CreateClient(HttpClientName)) { RequireHttps = true }));
        return manager.GetConfigurationAsync(cancellationToken);
    }
}

/// <summary>Protects the OIDC <c>state</c> (10 minutes, purpose-bound) so the callback can trust what it carries.</summary>
public sealed class DataProtectionExternalStateProtector(IDataProtectionProvider provider) : IExternalSignInStateProtector
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private ITimeLimitedDataProtector Protector => provider.CreateProtector("Erp.Identity.OidcState.v1").ToTimeLimitedDataProtector();

    public string Protect(ExternalSignInState state) => Protector.Protect(JsonSerializer.Serialize(state), Lifetime);

    public bool TryUnprotect(string? protectedState, out ExternalSignInState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(protectedState))
        {
            return false;
        }

        try
        {
            state = JsonSerializer.Deserialize<ExternalSignInState>(Protector.Unprotect(protectedState));
            return state is not null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }
}

public sealed class PlatformTokenService(SigningKeyProvider keys, IOptions<AuthOptions> options, IClock clock) : IPlatformTokenService
{
    private static readonly JsonWebTokenHandler Handler = new();

    public AccessToken CreateOperatorToken(PlatformOperator op)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(settings.PlatformTokenMinutes);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.PlatformAudience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(ErpClaims.Subject, op.Id.ToString()),
                new Claim(ErpClaims.Email, op.Email),
                new Claim(ErpClaims.Name, op.DisplayName),
                new Claim(ErpClaims.Role, PlatformAuth.OperatorRole),
                new Claim(ErpClaims.AuthenticationMethods, "pwd"),
                new Claim(ErpClaims.AuthenticationMethods, "otp"),
            ]),
        });
        return new AccessToken(token, expires);
    }
}

/// <summary>Operator status and the platform audit log. Runs outside any tenant (the tables carry no TenantId).</summary>
public sealed class PlatformOperatorDirectory(IdentityDbContext db, IClock clock) : IPlatformOperatorDirectory
{
    public Task<bool> IsActiveAsync(Guid operatorId, CancellationToken cancellationToken = default) =>
        db.PlatformOperators.AsNoTracking().AnyAsync(o => o.Id == operatorId && o.IsActive, cancellationToken);

    public async Task RecordAsync(Guid? operatorId, string action, string? details, string? ipAddress, CancellationToken cancellationToken = default)
    {
        db.PlatformAudit.Add(new PlatformAuditEntry(clock.UtcNow, operatorId, action, details, ipAddress));
        await db.SaveChangesAsync(cancellationToken);
    }
}
