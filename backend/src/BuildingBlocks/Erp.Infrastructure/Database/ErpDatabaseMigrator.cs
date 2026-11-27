using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Database;

/// <summary>A module DbContext, in migration order.</summary>
public sealed record ModuleDbContextRegistration(Type ContextType, string Schema, int Order);

public sealed class DestructiveMigrationException(string message) : Exception(message);

/// <summary>
/// Applies every module's migrations, then (re)installs the row-level security policy.
/// Destructive operations (dropping tables/columns/schemas) are refused unless explicitly approved (Part 20.5).
/// </summary>
public sealed class ErpDatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ModuleDbContextRegistration> registrations,
    IConnectionStringProvider connectionStrings,
    ILogger<ErpDatabaseMigrator> logger)
{
    public async Task MigrateAsync(bool allowDestructive = false, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseExistsAsync(cancellationToken);
        await RowLevelSecurityInstaller.DropPolicyAsync(connectionStrings.GetConnectionString(), cancellationToken);

        foreach (var registration in registrations.OrderBy(r => r.Order))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("{Context}: up to date", registration.ContextType.Name);
                continue;
            }

            if (!allowDestructive)
            {
                EnsureNotDestructive(context, pending);
            }

            logger.LogInformation("{Context}: applying {Migrations}", registration.ContextType.Name, string.Join(", ", pending));
            await context.Database.MigrateAsync(cancellationToken);
        }

        await RowLevelSecurityInstaller.InstallPolicyAsync(connectionStrings.GetConnectionString(), cancellationToken);
        logger.LogInformation("Row-level security policy installed");
    }

    private static void EnsureNotDestructive(DbContext context, IEnumerable<string> pending)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        foreach (var id in pending)
        {
            var migrationType = assembly.Migrations[id];
            var migration = assembly.CreateMigration(migrationType, context.Database.ProviderName!);
            var destructive = migration.UpOperations.Where(o => o is DropTableOperation or DropColumnOperation or DropSchemaOperation).ToList();
            if (destructive.Count > 0)
            {
                throw new DestructiveMigrationException(
                    $"Migration {id} contains destructive operations ({string.Join(", ", destructive.Select(o => o.GetType().Name))}). " +
                    "Take a backup and re-run with --allow-destructive after approval.");
            }
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionStrings.GetConnectionString());
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "IF DB_ID(@db) IS NULL EXEC('CREATE DATABASE [' + @db + ']');";
        command.Parameters.AddWithValue("@db", database);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Database {Database} is present", database);
    }
}

/// <summary>
/// Creates one security policy covering every table that has a <c>TenantId</c> column (except the infrastructure
/// tables listed below), so new tables are protected automatically on the next migration run.
/// </summary>
public static class RowLevelSecurityInstaller
{
    public const string PolicyName = "security.TenantIsolationPolicy";

    private static readonly string[] Excluded = ["OutboxMessage", "InboxMessage", "Tenant", "__EFMigrationsHistory"];

    public static async Task DropPolicyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connectionString, """
            IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = 'TenantIsolationPolicy' AND SCHEMA_NAME(schema_id) = 'security')
                DROP SECURITY POLICY security.TenantIsolationPolicy;
            """, cancellationToken);
    }

    public static async Task InstallPolicyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await DropPolicyAsync(connectionString, cancellationToken);
        await ExecuteAsync(connectionString, "IF SCHEMA_ID('security') IS NULL EXEC('CREATE SCHEMA security');", cancellationToken);
        await ExecuteAsync(connectionString, """
            IF OBJECT_ID('security.fn_TenantAccessPredicate') IS NULL
            EXEC('CREATE FUNCTION security.fn_TenantAccessPredicate(@TenantId uniqueidentifier)
                  RETURNS TABLE WITH SCHEMABINDING AS
                  RETURN SELECT 1 AS AccessResult
                  WHERE @TenantId = CAST(SESSION_CONTEXT(N''TenantId'') AS uniqueidentifier)');
            """, cancellationToken);

        var excluded = string.Join(",", Excluded.Select(e => $"'{e}'"));
        await ExecuteAsync(connectionString, $"""
            DECLARE @predicates nvarchar(max);
            SELECT @predicates = STRING_AGG(CAST(
                     'ADD FILTER PREDICATE security.fn_TenantAccessPredicate(TenantId) ON ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ', ' +
                     'ADD BLOCK PREDICATE security.fn_TenantAccessPredicate(TenantId) ON ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ' AFTER INSERT, ' +
                     'ADD BLOCK PREDICATE security.fn_TenantAccessPredicate(TenantId) ON ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ' AFTER UPDATE'
                   AS nvarchar(max)), ', ')
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id AND c.name = 'TenantId'
            WHERE t.name NOT IN ({excluded}) AND s.name <> 'security';
            IF @predicates IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE SECURITY POLICY security.TenantIsolationPolicy ' + @predicates + N' WITH (STATE = ON, SCHEMABINDING = ON);';
                EXEC sp_executesql @sql;
            END
            """, cancellationToken);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
