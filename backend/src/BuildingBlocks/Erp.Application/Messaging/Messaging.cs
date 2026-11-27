using Erp.SharedKernel.Results;

namespace Erp.Application.Messaging;

/// <summary>Marker for anything the dispatcher can handle.</summary>
public interface IRequest<TResult>
{
}

/// <summary>Changes state. Runs inside a database transaction unless it implements <see cref="IManualTransaction"/>.</summary>
public interface ICommand<TResult> : IRequest<TResult>
{
}

/// <summary>Reads state. Never opens a transaction and must not modify data.</summary>
public interface IQuery<TResult> : IRequest<TResult>
{
}

/// <summary>
/// The handler controls its own transaction. Used where the tenant is only known inside the handler
/// (login, token refresh, tenant provisioning) — the database session must not open before the tenant is set.
/// </summary>
public interface IManualTransaction
{
}

public interface IRequestHandler<in TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    Task<Result<TResult>> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResult> : IRequestHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
}

public interface IQueryHandler<in TQuery, TResult> : IRequestHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
}

public delegate Task<Result<TResult>> RequestHandlerDelegate<TResult>();

/// <summary>Cross-cutting step around every request (validation, transactions, logging).</summary>
public interface IPipelineBehavior<in TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    Task<Result<TResult>> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken);
}

public interface IDispatcher
{
    Task<Result<TResult>> Send<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default);
}
