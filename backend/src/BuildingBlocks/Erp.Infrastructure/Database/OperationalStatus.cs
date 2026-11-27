using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Database;

/// <summary>Lists migrations that exist in the deployed code but are not yet applied to the database.</summary>
public interface IPendingMigrationsReader
{
    /// <returns>"Context: Migration" entries; empty when the schema is current.</returns>
    Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class PendingMigrationsReader(IServiceScopeFactory scopeFactory, IEnumerable<ModuleDbContextRegistration> registrations) : IPendingMigrationsReader
{
    public async Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = new List<string>();
        foreach (var registration in registrations.OrderBy(r => r.Order))
        {
            // A fresh scope without a tenant: migration history tables are not tenant-scoped.
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            pending.AddRange((await context.Database.GetPendingMigrationsAsync(cancellationToken)).Select(m => $"{registration.ContextType.Name}: {m}"));
        }

        return pending;
    }
}

/// <summary>Age of the oldest undelivered outbox message across all module outboxes (all tenants; outboxes are exempt from RLS).</summary>
public interface IOutboxLagReader
{
    Task<TimeSpan> GetOldestPendingAgeAsync(CancellationToken cancellationToken = default);
}

public sealed class OutboxLagReader(IConnectionStringProvider connectionStrings, IEnumerable<OutboxSource> sources, IOptions<OutboxOptions> options) : IOutboxLagReader
{
    public async Task<TimeSpan> GetOldestPendingAgeAsync(CancellationToken cancellationToken = default)
    {
        var schemas = sources.Select(s => s.Schema).Distinct().ToList();
        if (schemas.Count == 0)
        {
            return TimeSpan.Zero;
        }

        // Schema names come from module registrations (code), never from input; they are still bracket-quoted.
        var union = string.Join(" UNION ALL ", schemas.Select(s =>
            $"SELECT MIN(OccurredAtUtc) AS Oldest FROM [{s.Replace("]", "]]", StringComparison.Ordinal)}].[OutboxMessage] WHERE ProcessedAtUtc IS NULL AND Attempts < @maxAttempts"));
        await using var connection = new SqlConnection(connectionStrings.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DATEDIFF_BIG(SECOND, MIN(Oldest), SYSUTCDATETIME()) FROM ({union}) AS o;";
        command.Parameters.AddWithValue("@maxAttempts", options.Value.MaxAttempts);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }
}
