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
