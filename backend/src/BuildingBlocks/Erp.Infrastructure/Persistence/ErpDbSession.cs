using System.Data.Common;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence;

/// <summary>
/// One SQL connection and (optionally) one transaction per scope, shared by every module DbContext.
/// This is how a source-document posting touches several modules atomically without distributed transactions (ADR-006).
/// The connection is opened lazily, after the tenant is known, so the session context is always correct.
/// </summary>
public sealed class ErpDbSession(IConnectionStringProvider connectionStrings, ITenantContext tenantContext) : IUnitOfWork, IChangeTrackerReset, IAsyncDisposable
{
    private readonly List<DbContext> _contexts = [];
    private SqlConnection? _connection;

    public DbConnection Connection => _connection ??= new SqlConnection(connectionStrings.GetConnectionString());

    public DbTransaction? Transaction { get; private set; }

    public bool HasActiveTransaction => Transaction is not null;

    internal void Register(DbContext context)
    {
        _contexts.Add(context);
        if (Transaction is not null)
        {
            context.Database.UseTransaction(Transaction);
        }
    }

    public async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = Connection;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            await TenantSessionContext.ApplyAsync(connection, tenantContext.TenantId, cancellationToken);
        }

        return connection;
    }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (Transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active in this scope.");
        }

        var connection = await GetOpenConnectionAsync(cancellationToken);
        Transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        foreach (var context in _contexts)
        {
            await context.Database.UseTransactionAsync(Transaction, cancellationToken);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var transaction = Transaction ?? throw new InvalidOperationException("No active transaction.");
        await transaction.CommitAsync(cancellationToken);
        await EndTransactionAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (Transaction is null)
        {
            return;
        }

        try
        {
            await Transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            // The server already rolled the transaction back (deadlock victim, broken connection). Nothing left to undo;
            // swallowing keeps the original failure visible to the caller instead of this secondary one.
        }
        finally
        {
            await EndTransactionAsync();
        }
    }

    /// <summary>Detaches every tracked entity in every module context of this scope (used before retrying a command).</summary>
    public void ClearTrackedChanges()
    {
        foreach (var context in _contexts)
        {
            context.ChangeTracker.Clear();
        }
    }

    private async Task EndTransactionAsync()
    {
        foreach (var context in _contexts)
        {
            await context.Database.UseTransactionAsync(null);
        }

        if (Transaction is not null)
        {
            await Transaction.DisposeAsync();
            Transaction = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null)
        {
            await Transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
