using System.Collections.Concurrent;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Application.Messaging;

/// <summary>
/// Small in-house mediator (ADR-012). Resolves the handler and wraps it with the registered behaviors,
/// first-registered outermost.
/// </summary>
public sealed class Dispatcher(IServiceProvider serviceProvider) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Wrappers = new();

    public Task<Result<TResult>> Send<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = (RequestWrapper<TResult>)Wrappers.GetOrAdd(
            request.GetType(),
            static type => Activator.CreateInstance(typeof(RequestWrapper<,>).MakeGenericType(type, typeof(TResult)))!);
        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }

    private abstract class RequestWrapper<TResult>
    {
        public abstract Task<Result<TResult>> Handle(IRequest<TResult> request, IServiceProvider services, CancellationToken ct);
    }

    private sealed class RequestWrapper<TRequest, TResult> : RequestWrapper<TResult>
        where TRequest : IRequest<TResult>
    {
        public override Task<Result<TResult>> Handle(IRequest<TResult> request, IServiceProvider services, CancellationToken ct)
        {
            var typed = (TRequest)request;
            var handler = services.GetService<IRequestHandler<TRequest, TResult>>()
                ?? throw new InvalidOperationException($"No handler registered for {typeof(TRequest).Name}.");

            RequestHandlerDelegate<TResult> pipeline = () => handler.Handle(typed, ct);
            foreach (var behavior in services.GetServices<IPipelineBehavior<TRequest, TResult>>().Reverse())
            {
                var next = pipeline;
                pipeline = () => behavior.Handle(typed, next, ct);
            }

            return pipeline();
        }
    }
}
