using System.Reflection;
using Erp.Application.Messaging;
using FluentValidation;
using Erp.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddErpApplication(this IServiceCollection services)
    {
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(Observability.MetricsBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransientRetryBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        services.TryAddSingleton<ITransientFailureDetector, NeverTransientFailureDetector>();
        services.TryAddScoped<IOutboxMessageContext, OutboxMessageContext>();
        return services;
    }

    /// <summary>Registers every request handler and validator found in a module's application assembly.</summary>
    public static IServiceCollection AddHandlersFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        var handlerInterface = typeof(IRequestHandler<,>);
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var iface in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface))
            {
                services.AddScoped(iface, type);
            }

            foreach (var iface in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
            {
                services.AddScoped(iface, type);
            }

            foreach (var iface in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
            {
                services.AddScoped(iface, type);
            }
        }

        return services;
    }
}
