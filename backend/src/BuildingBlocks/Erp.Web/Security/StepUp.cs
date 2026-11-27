using System.Security.Claims;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Erp.Web.Security;

/// <summary>Section <c>Auth:StepUp</c>: re-authentication with the second factor before sensitive actions.</summary>
public sealed class StepUpOptions
{
    public const string SectionName = "Auth:StepUp";

    public bool Enabled { get; set; } = true;

    /// <summary>How long a second-factor verification counts as "recent".</summary>
    public int MaxAgeMinutes { get; set; } = 10;

    /// <summary>
    /// Policy for users without two-step verification. false (default): they must enroll before performing sensitive actions.
    /// true: sensitive actions are allowed on the password alone for those users (weaker; for evaluation installs).
    /// </summary>
    public bool AllowWithoutMfa { get; set; }
}

public enum StepUpDecision
{
    Allowed,
    StepUpRequired,
    EnrollmentRequired,
}

public static class StepUpPolicy
{
    public const string ErrorCode = "identity.step-up-required";

    public static StepUpDecision Evaluate(ClaimsPrincipal user, StepUpOptions options, DateTimeOffset now, int? maxAgeMinutes = null)
    {
        if (!options.Enabled)
        {
            return StepUpDecision.Allowed;
        }

        var maxAge = TimeSpan.FromMinutes(maxAgeMinutes ?? options.MaxAgeMinutes);
        if (long.TryParse(user.FindFirst(ErpClaims.MfaAt)?.Value, out var mfaAt))
        {
            var age = now - DateTimeOffset.FromUnixTimeSeconds(mfaAt);
            if (age <= maxAge && age >= TimeSpan.FromMinutes(-5))
            {
                return StepUpDecision.Allowed;
            }
        }

        if (string.Equals(user.FindFirst(ErpClaims.MfaEnrolled)?.Value, "true", StringComparison.Ordinal))
        {
            return StepUpDecision.StepUpRequired;
        }

        return options.AllowWithoutMfa ? StepUpDecision.Allowed : StepUpDecision.EnrollmentRequired;
    }
}

public static class StepUpEndpointExtensions
{
    /// <summary>
    /// The access token must show a second-factor verification within the last few minutes (POST /api/v1/auth/step-up).
    /// Otherwise 403 <c>identity.step-up-required</c>; <c>mfaEnrollmentRequired</c> tells the client whether to prompt for a
    /// code or send the user to enroll first.
    /// </summary>
    public static TBuilder RequireStepUp<TBuilder>(this TBuilder builder, int? maxAgeMinutes = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresStepUpMetadata());
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var options = http.RequestServices.GetRequiredService<IOptions<StepUpOptions>>().Value;
            var decision = StepUpPolicy.Evaluate(http.User, options, DateTimeOffset.UtcNow, maxAgeMinutes);
            if (decision == StepUpDecision.Allowed)
            {
                return await next(context);
            }

            var enrollment = decision == StepUpDecision.EnrollmentRequired;
            var message = enrollment
                ? "Turn on two-step verification to perform this action."
                : "Confirm it is you: enter a code from your authenticator app.";
            return Results.Problem(
                title: ErrorLocalizer.Title(StepUpPolicy.ErrorCode, message),
                detail: message,
                statusCode: StatusCodes.Status403Forbidden,
                type: $"https://errors.erp.local/{StepUpPolicy.ErrorCode}",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = StepUpPolicy.ErrorCode,
                    ["mfaEnrollmentRequired"] = enrollment,
                    ["maxAgeMinutes"] = maxAgeMinutes ?? options.MaxAgeMinutes,
                });
        });
        return builder;
    }
}

/// <summary>Marks endpoints that require a recent second-factor verification.</summary>
public sealed class RequiresStepUpMetadata;
