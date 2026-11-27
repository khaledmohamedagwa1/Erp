using Erp.Application.Abstractions;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Web.Security;

/// <summary>Platform-operator authentication: a separate bearer scheme whose tokens carry the <c>erp-platform</c> audience.</summary>
public static class PlatformAuth
{
    public const string Scheme = "platform";
    public const string OperatorRole = "platform-operator";

    /// <summary>
    /// Requires a valid platform-operator token (password + TOTP sign-in at POST /api/platform/auth/login) for an active
    /// operator. Tenant user tokens are rejected (different audience). Each call is written to the platform audit log.
    /// </summary>
    public static TBuilder RequirePlatformOperator<TBuilder>(this TBuilder builder, string? auditAction = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var result = await http.AuthenticateAsync(Scheme);
            if (!result.Succeeded || result.Principal is null ||
                !string.Equals(result.Principal.FindFirst(ErpClaims.Role)?.Value, OperatorRole, StringComparison.Ordinal) ||
                !Guid.TryParse(result.Principal.FindFirst(ErpClaims.Subject)?.Value, out var operatorId))
            {
                return Error.Unauthorized("unauthorized", "A platform-operator token is required.").ToProblem();
            }

            var directory = http.RequestServices.GetRequiredService<IPlatformOperatorDirectory>();
            if (!await directory.IsActiveAsync(operatorId, http.RequestAborted))
            {
                return Error.Unauthorized("unauthorized", "The platform operator is not active.").ToProblem();
            }

            // Handlers see the operator as the current user (audit stamps CreatedBy with the operator id).
            http.User = result.Principal;
            var response = await next(context);
            var status = response is IStatusCodeHttpResult { StatusCode: { } code } ? code : http.Response.StatusCode;
            await directory.RecordAsync(operatorId, auditAction ?? $"{http.Request.Method} {http.Request.Path}",
                $"status={status}", http.Connection.RemoteIpAddress?.ToString(), CancellationToken.None);
            return response;
        });
        return builder;
    }
}
