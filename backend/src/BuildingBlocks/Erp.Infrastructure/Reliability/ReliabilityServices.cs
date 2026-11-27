using System.Data;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace Erp.Infrastructure.Reliability;

/// <summary>SQL Server error numbers that are worth retrying: deadlock victim, lock timeout, snapshot conflicts, connection drops.</summary>
public sealed class SqlTransientFailureDetector : ITransientFailureDetector
{
    private static readonly HashSet<int> TransientNumbers =
    [
        1205,  // deadlock victim
        1222,  // lock request timeout
        3960,  // snapshot isolation update conflict
        -2,    // client timeout
        4060, 40197, 40501, 40613, 49918, 49919, 49920, // service busy / unavailable (Azure SQL)
        10928, 10929, 233, 64, 10053, 10054, 10060,      // resource limits / transport-level errors
    ];

    public bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Records processed (message, consumer) pairs in <c>core.InboxMessage</c>, inside the caller's transaction.</summary>
public sealed class SqlInbox(ErpDbSession session) : IInbox
{
    public async Task<bool> TryBeginAsync(Guid messageId, string consumer, CancellationToken cancellationToken = default)
    {
        var transaction = session.Transaction
            ?? throw new InvalidOperationException("The inbox must be used inside a transaction so the claim commits with the work.");

        await using var command = AuditWriter.Command(transaction.Connection!, transaction, """
            IF EXISTS (SELECT 1 FROM core.InboxMessage WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = @id AND Consumer = @consumer)
                SELECT 0;
            ELSE
            BEGIN
                INSERT INTO core.InboxMessage (MessageId, Consumer, ProcessedAtUtc) VALUES (@id, @consumer, SYSUTCDATETIME());
                SELECT 1;
            END
            """);
        AuditWriter.Add(command, "@id", DbType.Guid, messageId);
        AuditWriter.Add(command, "@consumer", DbType.String, consumer);
        try
        {
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }
}

public sealed record StoredResponse(string RequestHash, int StatusCode, string? Body, string? ContentType);

public interface IIdempotencyStore
{
    /// <summary>Reads (and locks until the transaction ends) the record for this key, ignoring expired records.</summary>
    Task<StoredResponse?> FindAsync(string key, string endpoint, CancellationToken cancellationToken);

    Task SaveAsync(string key, string endpoint, StoredResponse response, CancellationToken cancellationToken);
}

/// <summary>Stores idempotent responses in <c>core.IdempotencyRecord</c> inside the request's transaction.</summary>
public sealed class SqlIdempotencyStore(ErpDbSession session, ITenantContext tenant, IClock clock) : IIdempotencyStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<StoredResponse?> FindAsync(string key, string endpoint, CancellationToken cancellationToken)
    {
        var transaction = RequireTransaction();
        await using var command = AuditWriter.Command(transaction.Connection!, transaction, """
            DELETE FROM core.IdempotencyRecord WHERE TenantId = @tenantId AND [Key] = @key AND Endpoint = @endpoint AND ExpiresAtUtc <= @now;
            SELECT RequestHash, StatusCode, ResponseBody, ContentType
            FROM core.IdempotencyRecord WITH (UPDLOCK, HOLDLOCK)
            WHERE TenantId = @tenantId AND [Key] = @key AND Endpoint = @endpoint;
            """);
        AddKey(command, key, endpoint);
        AuditWriter.Add(command, "@now", DbType.DateTime2, clock.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredResponse(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task SaveAsync(string key, string endpoint, StoredResponse response, CancellationToken cancellationToken)
    {
        var transaction = RequireTransaction();
        await using var command = AuditWriter.Command(transaction.Connection!, transaction, """
            INSERT INTO core.IdempotencyRecord (Id, TenantId, [Key], Endpoint, RequestHash, StatusCode, ResponseBody, ContentType, CreatedAtUtc, ExpiresAtUtc)
            VALUES (NEWID(), @tenantId, @key, @endpoint, @hash, @status, @body, @contentType, @now, @expires);
            """);
        AddKey(command, key, endpoint);
        var now = clock.UtcNow;
        AuditWriter.Add(command, "@hash", DbType.String, response.RequestHash);
        AuditWriter.Add(command, "@status", DbType.Int32, response.StatusCode);
        AuditWriter.Add(command, "@body", DbType.String, response.Body);
        AuditWriter.Add(command, "@contentType", DbType.String, response.ContentType);
        AuditWriter.Add(command, "@now", DbType.DateTime2, now);
        AuditWriter.Add(command, "@expires", DbType.DateTime2, now.Add(Retention));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private System.Data.Common.DbTransaction RequireTransaction() =>
        session.Transaction ?? throw new InvalidOperationException("Idempotency records are written inside the request transaction.");

    private void AddKey(System.Data.Common.DbCommand command, string key, string endpoint)
    {
        AuditWriter.Add(command, "@tenantId", DbType.Guid, tenant.TenantId ?? throw new InvalidOperationException("Tenant context required."));
        AuditWriter.Add(command, "@key", DbType.String, key);
        AuditWriter.Add(command, "@endpoint", DbType.String, endpoint);
    }
}
