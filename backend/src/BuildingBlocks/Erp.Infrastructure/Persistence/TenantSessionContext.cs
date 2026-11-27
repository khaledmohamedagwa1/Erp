using System.Data;
using System.Data.Common;
using Erp.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Erp.Infrastructure.Persistence;

/// <summary>
/// Writes the tenant into SQL Server SESSION_CONTEXT so the row-level security policy (ADR-004) can filter rows.
/// Set read-only, so code running on the same connection cannot switch tenant.
/// </summary>
public static class TenantSessionContext
{
    public const string Key = "TenantId";

    public static void Apply(DbConnection connection, Guid? tenantId)
    {
        if (tenantId is null)
        {
            return;
        }

        using var command = CreateCommand(connection, tenantId.Value);
        command.ExecuteNonQuery();
    }

    public static async Task ApplyAsync(DbConnection connection, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (tenantId is null)
        {
            return;
        }

        await using var command = CreateCommand(connection, tenantId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand CreateCommand(DbConnection connection, Guid tenantId)
    {
        var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenantId";
        parameter.DbType = DbType.Guid;
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);
        return command;
    }
}

/// <summary>Applies the tenant session context whenever EF opens a connection.</summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        TenantSessionContext.Apply(connection, tenantContext.TenantId);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
        TenantSessionContext.ApplyAsync(connection, tenantContext.TenantId, cancellationToken);
}
