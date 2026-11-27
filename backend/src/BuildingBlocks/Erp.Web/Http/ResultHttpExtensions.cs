using Erp.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Erp.Web.Http;

/// <summary>Maps <see cref="Result"/> to HTTP responses; failures become RFC 9457 problem details with a stable <c>code</c>.</summary>
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke(result.Value) ?? Results.Ok(result.Value)) : result.Error.ToProblem();

    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();

    public static IResult ToProblem(this Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status422UnprocessableEntity,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            ErrorType.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
            _ => StatusCodes.Status400BadRequest,
        };

        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Details is not null)
        {
            extensions["errors"] = error.Details;
        }

        return Results.Problem(
            title: ErrorLocalizer.Title(error.Code, error.Message),
            detail: error.Message,
            statusCode: status,
            type: $"https://errors.erp.local/{error.Code}",
            extensions: extensions);
    }
}
