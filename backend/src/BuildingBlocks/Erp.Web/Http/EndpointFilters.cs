using Erp.Application.Abstractions;
using Erp.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Web.Http;

/// <summary>Server-side checks on every endpoint. The UI may hide things; these decide (Part 25, rule 10).</summary>
public static class EndpointFilterExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var user = services.GetRequiredService<ICurrentUser>();
            if (user.UserId is not { } userId)
            {
                return Error.Unauthorized("unauthorized", "Authentication is required.").ToProblem();
            }

            var permissions = services.GetRequiredService<IPermissionService>();
            if (!await permissions.HasPermissionAsync(userId, permission, context.HttpContext.RequestAborted))
            {
                return Error.Forbidden("forbidden", $"Missing permission '{permission}'.").ToProblem();
            }

            return await next(context);
        });
        return builder;
    }

    /// <summary>The request must carry a company (X-Company-Id) the user has access to.</summary>
    public static TBuilder RequireCompany<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresCompanyMetadata());
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenant.CompanyId is null
                ? Error.Validation("company.required", "This endpoint requires the X-Company-Id header.").ToProblem()
                : await next(context);
        });
        return builder;
    }

    /// <summary>Hides a module's endpoints (404) when the module is not enabled for the active company.</summary>
    public static TBuilder RequireModule<TBuilder>(this TBuilder builder, string moduleCode)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresCompanyMetadata());
        builder.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var tenant = services.GetRequiredService<ITenantContext>();
            if (tenant.CompanyId is not { } companyId)
            {
                return Error.Validation("company.required", "This endpoint requires the X-Company-Id header.").ToProblem();
            }

            var modules = services.GetRequiredService<IModuleActivationService>();
            return await modules.IsEnabledAsync(companyId, moduleCode, context.HttpContext.RequestAborted)
                ? await next(context)
                : Error.NotFound("module-disabled", $"Module '{moduleCode}' is not enabled.").ToProblem();
        });
        return builder;
    }
}

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Key for platform-operator endpoints. When empty, those endpoints are disabled (404).</summary>
    public string? ApiKey { get; set; }
}

public static class PlatformEndpointExtensions
{
    public const string HeaderName = "X-Platform-Key";

    public static TBuilder RequirePlatformKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlatformOptions>>().Value;
            if (string.IsNullOrEmpty(options.ApiKey))
            {
                return Results.NotFound();
            }

            var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
            var expected = System.Text.Encoding.UTF8.GetBytes(options.ApiKey);
            var actual = System.Text.Encoding.UTF8.GetBytes(presented);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual)
                ? await next(context)
                : Error.Unauthorized("unauthorized", "A valid platform key is required.").ToProblem();
        });
        return builder;
    }
}

public static class ConcurrencyHeaders
{
    public static string ToETag(byte[] rowVersion) => $"\"{Convert.ToBase64String(rowVersion)}\"";

    /// <summary>Parses If-Match into a rowversion; returns an error when missing or malformed.</summary>
    public static Result<byte[]> ReadIfMatch(HttpRequest request)
    {
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return new Error("precondition-required", "The If-Match header is required.", ErrorType.PreconditionRequired);
        }

        try
        {
            return Convert.FromBase64String(header.Trim().Trim('"').Replace("W/", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return Error.Validation("validation-failed", "If-Match is not a valid version.");
        }
    }
}
