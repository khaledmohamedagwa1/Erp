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

        // Claim by inserting: the primary key serializes concurrent deliveries of the same message without key-range locks.
        await using var command = AuditWriter.Command(transaction.Connection!, transaction, """
            BEGIN TRY
                INSERT INTO core.InboxMessage (MessageId, Consumer, ProcessedAtUtc) VALUES (@id, @consumer, SYSUTCDATETIME());
                SELECT 1;
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
                SELECT 0;
            END CATCH
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
    /// <summary>
    /// Claims the key for this request by inserting a placeholder in the current transaction. Returns null when claimed
    /// (process the request), or the stored response of an earlier request with the same key. A concurrent duplicate
    /// waits on the key until the first request commits (then it gets the stored response) or rolls back (then it claims).
    /// </summary>
    Task<StoredResponse?> ClaimAsync(string key, string endpoint, string requestHash, CancellationToken cancellationToken);

    /// <summary>Stores the final response on the claimed record.</summary>
    Task SaveAsync(string key, string endpoint, StoredResponse response, CancellationToken cancellationToken);
}

/// <summary>Stores idempotent responses in <c>core.IdempotencyRecord</c> inside the request's transaction.</summary>
public sealed class SqlIdempotencyStore(ErpDbSession session, ITenantContext tenant, IClock clock) : IIdempotencyStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<StoredResponse?> ClaimAsync(string key, string endpoint, string requestHash, CancellationToken cancellationToken)
    {
        var transaction = RequireTransaction();
        var now = clock.UtcNow;
        await using (var claim = AuditWriter.Command(transaction.Connection!, transaction, """
            DELETE FROM core.IdempotencyRecord WHERE TenantId = @tenantId AND [Key] = @key AND Endpoint = @endpoint AND ExpiresAtUtc <= @now;
            BEGIN TRY
                INSERT INTO core.IdempotencyRecord (Id, TenantId, [Key], Endpoint, RequestHash, StatusCode, ResponseBody, ContentType, CreatedAtUtc, ExpiresAtUtc)
                VALUES (NEWID(), @tenantId, @key, @endpoint, @hash, 0, NULL, NULL, @now, @expires);
                SELECT 1;
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
                SELECT 0;
            END CATCH
            """))
        {
            AddKey(claim, key, endpoint);
            AuditWriter.Add(claim, "@hash", DbType.String, requestHash);
            AuditWriter.Add(claim, "@now", DbType.DateTime2, now);
            AuditWriter.Add(claim, "@expires", DbType.DateTime2, now.Add(Retention));
            if (Convert.ToInt32(await claim.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                return null;
            }
        }

        await using var read = AuditWriter.Command(transaction.Connection!, transaction, """
            SELECT RequestHash, StatusCode, ResponseBody, ContentType
            FROM core.IdempotencyRecord
            WHERE TenantId = @tenantId AND [Key] = @key AND Endpoint = @endpoint;
            """);
        AddKey(read, key, endpoint);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Idempotency record disappeared after a duplicate-key conflict.");
        }

        return new StoredResponse(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task SaveAsync(string key, string endpoint, StoredResponse response, CancellationToken cancellationToken)
    {
        var transaction = RequireTransaction();
        await using var command = AuditWriter.Command(transaction.Connection!, transaction, """
            UPDATE core.IdempotencyRecord
               SET RequestHash = @hash, StatusCode = @status, ResponseBody = @body, ContentType = @contentType
             WHERE TenantId = @tenantId AND [Key] = @key AND Endpoint = @endpoint;
            """);
        AddKey(command, key, endpoint);
        AuditWriter.Add(command, "@hash", DbType.String, response.RequestHash);
        AuditWriter.Add(command, "@status", DbType.Int32, response.StatusCode);
        AuditWriter.Add(command, "@body", DbType.String, response.Body);
        AuditWriter.Add(command, "@contentType", DbType.String, response.ContentType);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The idempotency key was not claimed in this transaction.");
        }
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
