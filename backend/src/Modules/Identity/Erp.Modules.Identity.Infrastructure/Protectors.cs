using System.Security.Cryptography;
using Erp.Modules.Identity.Application;
using Microsoft.AspNetCore.DataProtection;

namespace Erp.Modules.Identity.Infrastructure;

/// <summary>MFA secrets encrypted with ASP.NET Core Data Protection (keys stored in core.DataProtectionKey).</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Erp.Identity.MfaSecret.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}

/// <summary>Time-limited, purpose-bound tokens for pending sign-in steps (MFA: 5 minutes, forced password change: 10 minutes).</summary>
public sealed class DataProtectionChallengeTokens(IDataProtectionProvider provider) : IChallengeTokens
{
    public static TimeSpan Lifetime(string purpose) => purpose == ChallengePurpose.Mfa ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(10);

    private ITimeLimitedDataProtector Protector(string purpose) =>
        provider.CreateProtector("Erp.Identity.Challenge.v1", purpose).ToTimeLimitedDataProtector();

    public string Create(string purpose, Guid tenantId, Guid userId) =>
        Protector(purpose).Protect($"{tenantId:N}|{userId:N}", Lifetime(purpose));

    public bool TryRead(string purpose, string? token, out Guid tenantId, out Guid userId)
    {
        tenantId = Guid.Empty;
        userId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var parts = Protector(purpose).Unprotect(token).Split('|');
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out tenantId) && Guid.TryParseExact(parts[1], "N", out userId);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
