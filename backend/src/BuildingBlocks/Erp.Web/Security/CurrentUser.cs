using System.Security.Claims;
using Erp.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Erp.Web.Security;

public static class ErpClaims
{
    public const string Subject = "sub";
    public const string Tenant = "tid";
    public const string Session = "sid";
    public const string Email = "email";
    public const string Name = "name";

    /// <summary>Unix time (seconds) of the last second-factor verification in this session (sign-in or step-up).</summary>
    public const string MfaAt = "mfa_at";

    /// <summary>"true" when the user had two-step verification enabled when the token was issued.</summary>
    public const string MfaEnrolled = "mfa_enrolled";

    /// <summary>Authentication methods (RFC 8176): "pwd", "otp", "ext".</summary>
    public const string AuthenticationMethods = "amr";

    /// <summary>Role claim on platform-operator tokens.</summary>
    public const string Role = "role";
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Parse(ErpClaims.Subject);

    public Guid? SessionId => Parse(ErpClaims.Session);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    private Guid? Parse(string claim) =>
        Guid.TryParse(Principal?.FindFirst(claim)?.Value, out var value) ? value : null;
}
