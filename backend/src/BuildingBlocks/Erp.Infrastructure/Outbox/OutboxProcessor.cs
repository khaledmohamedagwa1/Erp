using System.Data;
using System.Reflection;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Tenancy;
using Erp.SharedKernel.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Outbox;

/// <summary>An assembly containing domain events that may appear in an outbox. Registered by each module.</summary>
public sealed record EventAssembly(Assembly Assembly);

/// <summary>Maps outbox type names back to CLR event types (only types from registered module assemblies).</summary>
public sealed class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public EventTypeRegistry(IEnumerable<EventAssembly> assemblies)
    {
        foreach (var type in assemblies.SelectMany(a => a.Assembly.GetTypes())
                     .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
        {
            _types[type.FullName!] = type;
        }
    }

    public Type? Resolve(string name) => _types.GetValueOrDefault(name);
}

public sealed record OutboxSource(string Schema);

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 50;

    public int PollIntervalMilliseconds { get; set; } = 1000;

    public int MaxAttempts { get; set; } = 10;
}

public interface IOutboxProcessor
{
    /// <returns>Number of messages handled (successfully or not).</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Delivers outbox messages to in-process handlers. Each message runs in its own DI scope with the message's tenant
/// set, so handlers see exactly the tenant that produced the event. Delivery is at-least-once.
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IConnectionStringProvider connectionStrings,
    IEnumerable<OutboxSource> sources,
    EventTypeRegistry registry,
    Microsoft.Extensions.Options.IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        foreach (var source in sources)
        {
            total += await ProcessSourceAsync(source, cancellationToken);
        }

        return total;
    }

    private async Task<int> ProcessSourceAsync(OutboxSource source, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = new SqlConnection(connectionStrings.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var batch = new List<(Guid Id, Guid? TenantId, string Type, string Payload)>();
        await using (var select = AuditWriter.Command(connection, transaction, $"""
            SELECT TOP (@batch) Id, TenantId, Type, Payload
            FROM [{source.Schema}].[OutboxMessage] WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ProcessedAtUtc IS NULL AND Attempts < @maxAttempts
            ORDER BY OccurredAtUtc;
            """))
        {
            AuditWriter.Add(select, "@batch", DbType.Int32, settings.BatchSize);
            AuditWriter.Add(select, "@maxAttempts", DbType.Int32, settings.MaxAttempts);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                batch.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        foreach (var message in batch)
        {
            string? error = null;
            try
            {
                await DispatchAsync(message.Id, message.TenantId, message.Type, message.Payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message;
                logger.LogWarning(ex, "Outbox message {MessageId} ({Type}) failed", message.Id, message.Type);
            }

            await using var update = AuditWriter.Command(connection, transaction, $"""
                UPDATE [{source.Schema}].[OutboxMessage]
                   SET Attempts = Attempts + 1,
                       ProcessedAtUtc = CASE WHEN @error IS NULL THEN SYSUTCDATETIME() ELSE NULL END,
                       Error = @error
                 WHERE Id = @id;
                """);
            AuditWriter.Add(update, "@id", DbType.Guid, message.Id);
            AuditWriter.Add(update, "@error", DbType.String, error);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }

    private async Task DispatchAsync(Guid messageId, Guid? tenantId, string typeName, string payload, CancellationToken cancellationToken)
    {
        var type = registry.Resolve(typeName) ?? throw new InvalidOperationException($"Unknown event type '{typeName}'.");
        var domainEvent = JsonSerializer.Deserialize(payload, type) ?? throw new InvalidOperationException("Empty event payload.");

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOutboxMessageContext>().Set(messageId);
        if (tenantId is not null)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId.Value);
        }

        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(type);
        var method = handlerType.GetMethod(nameof(IIntegrationEventHandler<IDomainEvent>.HandleAsync))!;
        foreach (var handler in scope.ServiceProvider.GetServices(handlerType))
        {
            await (Task)method.Invoke(handler, [domainEvent, cancellationToken])!;
        }
    }
}

/// <summary>Polls the outboxes. Runs only when the process role is <c>worker</c> or <c>all</c>.</summary>
public sealed class OutboxBackgroundService(
    IOutboxProcessor processor,
    Microsoft.Extensions.Options.IOptions<OutboxOptions> options,
    ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.ProcessPendingAsync(stoppingToken);
                if (processed > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox polling failed");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
