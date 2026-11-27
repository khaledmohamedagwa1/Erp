using Erp.Application.Abstractions;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Erp.Application.Messaging;

public sealed class LoggingBehavior<TRequest, TResult>(ILogger<LoggingBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    public async Task<Result<TResult>> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await next();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (result.IsFailure)
        {
            logger.LogInformation("{Request} failed with {ErrorCode} in {ElapsedMs:0} ms", typeof(TRequest).Name, result.Error.Code, elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogDebug("{Request} succeeded in {ElapsedMs:0} ms", typeof(TRequest).Name, elapsed.TotalMilliseconds);
        }

        return result;
    }
}

/// <summary>Runs FluentValidation validators; failures become a 422 with field-level details.</summary>
public sealed class ValidationBehavior<TRequest, TResult>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    public async Task<Result<TResult>> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await next();
        }

        var details = failures
            .GroupBy(f => ToCamelCase(f.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
        return Error.Validation("validation-failed", "One or more fields are invalid.") with { Details = details };
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>Wraps commands in one database transaction shared by every module touched (ADR-006).</summary>
public sealed class TransactionBehavior<TRequest, TResult>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    public async Task<Result<TResult>> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (request is not ICommand<TResult> || request is IManualTransaction || unitOfWork.HasActiveTransaction)
        {
            return await next();
        }

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await next();
            if (result.IsSuccess)
            {
                await unitOfWork.CommitAsync(cancellationToken);
            }
            else
            {
                await unitOfWork.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
