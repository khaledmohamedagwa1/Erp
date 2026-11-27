using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Erp.Application.Abstractions;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Domain;
using Erp.Web.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Modules.Identity.Infrastructure;

/// <summary>PBKDF2 hashing from ASP.NET Core Identity (versioned format, upgradable iteration count).</summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();

    public string Hash(string password) => Hasher.HashPassword(Subject, password);

    public bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
}

/// <summary>
/// Holds the RS256 signing key. Outside Development/Testing a configured key is mandatory; in Development an
/// ephemeral key is generated (tokens then stop working after a restart, which is acceptable locally).
/// </summary>
public sealed class SigningKeyProvider
{
    public SigningKeyProvider(IOptions<AuthOptions> options, IHostEnvironment environment, ILogger<SigningKeyProvider> logger)
    {
        var rsa = RSA.Create();
        var pem = options.Value.SigningKeyPem;
        if (!string.IsNullOrWhiteSpace(pem))
        {
            rsa.ImportFromPem(pem);
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            rsa.KeySize = 2048;
            logger.LogWarning("Auth:SigningKeyPem is not configured; using an ephemeral development signing key.");
        }
        else
        {
            throw new InvalidOperationException("Auth:SigningKeyPem must be configured outside Development (see deploy docs).");
        }

        Key = new RsaSecurityKey(rsa) { KeyId = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16] };
    }

    public RsaSecurityKey Key { get; }
}

public sealed class JwtTokenService(SigningKeyProvider keys, IOptions<AuthOptions> options, IClock clock) : ITokenService
{
    private static readonly JsonWebTokenHandler Handler = new();

    public AccessToken CreateAccessToken(User user, Guid tenantId, UserSession session)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(Claims(user, tenantId, session)),
        });
        return new AccessToken(token, expires);
    }

    private static List<Claim> Claims(User user, Guid tenantId, UserSession session)
    {
        var claims = new List<Claim>
        {
            new(ErpClaims.Subject, user.Id.ToString()),
            new(ErpClaims.Tenant, tenantId.ToString()),
            new(ErpClaims.Session, session.Id.ToString()),
            new(ErpClaims.Email, user.Email),
            new(ErpClaims.Name, user.DisplayName),
            new(ErpClaims.MfaEnrolled, user.MfaEnabled ? "true" : "false"),
            new(ErpClaims.AuthenticationMethods, session.AuthenticationMethod.StartsWith("ext", StringComparison.Ordinal) ? "ext" : "pwd"),
        };
        if (session.LastMfaAtUtc is { } mfaAt)
        {
            claims.Add(new Claim(ErpClaims.MfaAt, new DateTimeOffset(DateTime.SpecifyKind(mfaAt, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
            claims.Add(new Claim(ErpClaims.AuthenticationMethods, "otp"));
        }

        return claims;
    }
}

public sealed class ConfigureJwtBearer(SigningKeyProvider keys, IOptions<AuthOptions> options) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions jwt)
    {
        var settings = options.Value;
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = name == PlatformAuth.Scheme ? settings.PlatformAudience : settings.Audience,
            IssuerSigningKey = keys.Key,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ErpClaims.Name,
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);
}

/// <summary>
/// Caches session status, company access and permissions for a short time. Keys always include the tenant.
/// Session status and invalidation versions live in <see cref="IDistributedCache"/> (Redis when several instances run), so a
/// revoked session or a permission change takes effect on every instance at once; the bulky access/permission lists are
/// cached per instance under keys that include those versions.
/// </summary>
public sealed class IdentityCache(IDistributedCache shared, ITenantContext tenantContext) : IIdentityCache
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);
    private static readonly DistributedCacheEntryOptions VersionLifetime = new() { SlidingExpiration = TimeSpan.FromDays(1) };

    private Guid Tenant => tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context required.");

    public string SessionKey(Guid sessionId) => $"t:{Tenant}:session:{sessionId}";

    public async Task<string> UserKeyAsync(Guid userId, string part, CancellationToken cancellationToken = default) =>
        $"t:{Tenant}:user:{userId}:{part}:{await VersionAsync(UserVersionKey(userId), cancellationToken)}:{await VersionAsync(PermissionVersionKey, cancellationToken)}";

    public async Task<bool?> GetSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await shared.GetStringAsync(SessionKey(sessionId), cancellationToken) switch
        {
            "1" => true,
            "0" => false,
            _ => null,
        };

    public Task SetSessionActiveAsync(Guid sessionId, bool active, CancellationToken cancellationToken) =>
        shared.SetStringAsync(SessionKey(sessionId), active ? "1" : "0", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Duration }, cancellationToken);

    public Task InvalidateSessionAsync(Guid sessionId) => shared.RemoveAsync(SessionKey(sessionId));

    // A fresh random version (not an increment) avoids lost updates between instances without a distributed lock.
    public Task InvalidateUserAsync(Guid userId) => shared.SetStringAsync(UserVersionKey(userId), NewVersion(), VersionLifetime);

    public Task InvalidateAllPermissionsAsync() => shared.SetStringAsync(PermissionVersionKey, NewVersion(), VersionLifetime);

    private string UserVersionKey(Guid userId) => $"t:{Tenant}:user:{userId}:v";

    private string PermissionVersionKey => $"t:{Tenant}:perm:v";

    private static string NewVersion() => Guid.NewGuid().ToString("N")[..12];

    private async Task<string> VersionAsync(string key, CancellationToken cancellationToken) =>
        await shared.GetStringAsync(key, cancellationToken) ?? "0";
}

public sealed class UserAccessService(IdentityDbContext db, IdentityCache keys, IMemoryCache cache, IClock clock) : IUserAccessService
{
    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (await keys.GetSessionActiveAsync(sessionId, cancellationToken) is { } cached)
        {
            return cached;
        }

        var now = clock.UtcNow;
        var active = await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.RevokedAtUtc == null && s.AbsoluteExpiresAtUtc > now, cancellationToken);
        await keys.SetSessionActiveAsync(sessionId, active, cancellationToken);
        return active;
    }

    public async Task<CompanyAccess?> GetCompanyAccessAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default)
    {
        var all = await LoadAsync(userId, cancellationToken);
        return all.FirstOrDefault(a => a.CompanyId == companyId);
    }

    public async Task<IReadOnlyCollection<Guid>> GetAccessibleCompanyIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await LoadAsync(userId, cancellationToken)).Select(a => a.CompanyId).ToList();

    private async Task<IReadOnlyList<CompanyAccess>> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(await keys.UserKeyAsync(userId, "access", cancellationToken), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = IdentityCache.Duration;
            var rows = await db.Users.AsNoTracking().Where(u => u.Id == userId && u.IsActive)
                .SelectMany(u => u.CompanyAccess).ToListAsync(cancellationToken);
            return (IReadOnlyList<CompanyAccess>)rows.Select(r => new CompanyAccess(r.CompanyId, r.BranchIds, r.WarehouseIds)).ToList();
        }))!;
}

public sealed class PermissionService(IdentityDbContext db, IdentityCache keys, IMemoryCache cache) : IPermissionService
{
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await cache.GetOrCreateAsync(await keys.UserKeyAsync(userId, "permissions", cancellationToken), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = IdentityCache.Duration;
            var roleIds = await db.Users.AsNoTracking().Where(u => u.Id == userId && u.IsActive)
                .SelectMany(u => u.Roles.Select(r => r.RoleId)).ToListAsync(cancellationToken);
            var permissionLists = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => r.Permissions).ToListAsync(cancellationToken);
            return (IReadOnlySet<string>)permissionLists.SelectMany(p => p).ToHashSet(StringComparer.Ordinal);
        }))!;

    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default) =>
        (await GetPermissionsAsync(userId, cancellationToken)).Contains(permission);
}
