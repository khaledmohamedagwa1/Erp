using Erp.Application.Abstractions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Erp.Web.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.Web.Http;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 100)
        {
            correlationId = context.TraceIdentifier;
        }

        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

/// <summary>
/// Establishes the trusted tenant (from the signed token only), checks the session is still active,
/// and validates the requested company against the user's access (Part 4: never trust client tenant ids).
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string CompanyHeader = "X-Company-Id";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantDirectory tenants,
        IUserAccessService access,
        ICurrentUser currentUser,
        ILogger<TenantResolutionMiddleware> logger)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.User.FindFirst(ErpClaims.Tenant)?.Value, out var tenantId) ||
            currentUser.UserId is null || currentUser.SessionId is not { } sessionId)
        {
            await Write(context, Error.Unauthorized("unauthorized", "The access token is missing required claims."));
            return;
        }

        var tenant = await tenants.FindByIdAsync(tenantId, context.RequestAborted);
        if (tenant is null || !tenant.IsActive)
        {
            await Write(context, Error.Unauthorized("unauthorized", "The tenant is not active."));
            return;
        }

        tenantContext.SetTenant(tenantId);

        if (!await access.IsSessionActiveAsync(sessionId, context.RequestAborted))
        {
            await Write(context, Error.Unauthorized("auth.session-revoked", "The session is no longer active."));
            return;
        }

        var companyHeader = context.Request.Headers[CompanyHeader].ToString();
        if (!string.IsNullOrEmpty(companyHeader))
        {
            if (!Guid.TryParse(companyHeader, out var companyId) ||
                await access.GetCompanyAccessAsync(currentUser.UserId.Value, companyId, context.RequestAborted) is null)
            {
                await Write(context, Error.Forbidden("company.access-denied", "You do not have access to this company."));
                return;
            }

            tenantContext.SetCompany(companyId);
            await ApplyDataScopeAsync(context, currentUser.UserId.Value, companyId, access);
        }

        using (logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = tenantId, ["UserId"] = currentUser.UserId }))
        {
            await next(context);
        }
    }

    /// <summary>Permission that exempts a user from branch/warehouse scopes (declared by the Identity module).</summary>
    public const string AllDataScopesPermission = "identity.scope.all";

    private static async Task ApplyDataScopeAsync(HttpContext context, Guid userId, Guid companyId, IUserAccessService access)
    {
        var companyAccess = await access.GetCompanyAccessAsync(userId, companyId, context.RequestAborted);
        if (companyAccess is null || (companyAccess.AllBranches && companyAccess.AllWarehouses))
        {
            return;
        }

        var permissions = context.RequestServices.GetRequiredService<IPermissionService>();
        if (await permissions.HasPermissionAsync(userId, AllDataScopesPermission, context.RequestAborted))
        {
            return;
        }

        context.RequestServices.GetRequiredService<IDataScope>().Restrict(companyAccess);
    }

    private static Task Write(HttpContext context, Error error) => error.ToProblem().ExecuteAsync(context);
}

/// <summary>Turns unhandled exceptions into problem details without leaking internals.</summary>
public sealed class ErpExceptionHandler(ILogger<ErpExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var error = exception switch
        {
            DomainException domain => domain.Error,
            DbUpdateConcurrencyException => Error.Conflict("concurrency-conflict", "The record was changed by someone else."),
            BadHttpRequestException bad => Error.Validation("validation-failed", bad.Message),
            _ => null,
        };

        if (error is null)
        {
            logger.LogError(exception, "Unhandled exception");
            error = Error.Failure("internal-error", "An unexpected error occurred.");
            await Results.Problem(
                title: "An unexpected error occurred.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = httpContext.TraceIdentifier })
                .ExecuteAsync(httpContext);
            return true;
        }

        await error.ToProblem().ExecuteAsync(httpContext);
        return true;
    }
}
