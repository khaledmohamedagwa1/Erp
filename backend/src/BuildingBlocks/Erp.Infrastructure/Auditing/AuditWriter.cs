using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Domain;

namespace Erp.Infrastructure.Auditing;

public sealed record AuditEntry(string EntityType, string EntityId, string Action, string ChangesJson);

public interface IAuditWriter
{
    Task WriteAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken);
}

/// <summary>
/// Appends audit records in the business transaction. Each record carries the SHA-256 of the previous record of the
/// same tenant, so any later edit or deletion breaks the chain and is detected by <see cref="AuditChainVerifier"/>.
/// The chain head row is locked (UPDLOCK) so concurrent writers serialize per tenant.
/// </summary>
public sealed class AuditWriter(ErpDbSession session, ITenantContext tenantContext, ICurrentUser currentUser, IClock clock) : IAuditWriter
{
    public async Task WriteAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Audit records require a tenant context.");
        var transaction = session.Transaction ?? throw new InvalidOperationException("Audit records must be written inside a transaction.");
        var connection = transaction.Connection!;

        long sequence;
        string previousHash;
        await using (var head = Command(connection, transaction, """
            SELECT LastSequence, LastHash FROM audit.AuditChainHead WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @tenantId;
            """))
        {
            Add(head, "@tenantId", DbType.Guid, tenantId);
            await using var reader = await head.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                sequence = reader.GetInt64(0);
                previousHash = reader.GetString(1);
            }
            else
            {
                sequence = 0;
                previousHash = new string('0', 64);
            }
        }

        // Stored as datetime2(3): hash the value exactly as it will be read back.
        var utc = clock.UtcNow;
        var now = new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        var userId = currentUser.UserId;
        var correlation = System.Diagnostics.Activity.Current?.TraceId.ToString();
        foreach (var entry in entries)
        {
            sequence++;
            var hash = AuditChainVerifier.ComputeHash(previousHash, tenantId, sequence, now, userId, entry);
            await using var insert = Command(connection, transaction, """
                INSERT INTO audit.AuditLog (Id, TenantId, Sequence, OccurredAtUtc, UserId, Action, EntityType, EntityId, Changes, CorrelationId, PreviousHash, Hash)
                VALUES (@id, @tenantId, @sequence, @at, @userId, @action, @entityType, @entityId, @changes, @correlation, @previous, @hash);
                """);
            Add(insert, "@id", DbType.Guid, SequentialGuid.New());
            Add(insert, "@tenantId", DbType.Guid, tenantId);
            Add(insert, "@sequence", DbType.Int64, sequence);
            Add(insert, "@at", DbType.DateTime2, now);
            Add(insert, "@userId", DbType.Guid, userId);
            Add(insert, "@action", DbType.String, entry.Action);
            Add(insert, "@entityType", DbType.String, entry.EntityType);
            Add(insert, "@entityId", DbType.String, entry.EntityId);
            Add(insert, "@changes", DbType.String, entry.ChangesJson);
            Add(insert, "@correlation", DbType.String, correlation);
            Add(insert, "@previous", DbType.String, previousHash);
            Add(insert, "@hash", DbType.String, hash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            previousHash = hash;
        }

        await using var upsert = Command(connection, transaction, """
            UPDATE audit.AuditChainHead SET LastSequence = @sequence, LastHash = @hash WHERE TenantId = @tenantId;
            IF @@ROWCOUNT = 0 INSERT INTO audit.AuditChainHead (TenantId, LastSequence, LastHash) VALUES (@tenantId, @sequence, @hash);
            """);
        Add(upsert, "@tenantId", DbType.Guid, tenantId);
        Add(upsert, "@sequence", DbType.Int64, sequence);
        Add(upsert, "@hash", DbType.String, previousHash);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Recomputes a tenant's audit hash chain; any mismatch means the history was altered.</summary>
public sealed class AuditChainVerifier(ErpDbSession session, ITenantContext tenantContext)
{
    public static string ComputeHash(string previousHash, Guid tenantId, long sequence, DateTime at, Guid? userId, AuditEntry entry)
    {
        var material = string.Join('|',
            previousHash, tenantId.ToString("N"), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            at.ToString("O", System.Globalization.CultureInfo.InvariantCulture), userId?.ToString("N") ?? "-",
            entry.Action, entry.EntityType, entry.EntityId, entry.ChangesJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <returns>The sequence number of the first broken record, or null when the chain is intact.</returns>
    public async Task<long?> FindFirstBrokenRecordAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context required.");
        var connection = await session.GetOpenConnectionAsync(cancellationToken);
        await using var command = AuditWriter.Command(connection, session.Transaction, """
            SELECT Sequence, OccurredAtUtc, UserId, Action, EntityType, EntityId, Changes, PreviousHash, Hash
            FROM audit.AuditLog WHERE TenantId = @tenantId ORDER BY Sequence;
            """);
        AuditWriter.Add(command, "@tenantId", DbType.Guid, tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var expectedPrevious = new string('0', 64);
        long expectedSequence = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            expectedSequence++;
            var sequence = reader.GetInt64(0);
            var at = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            Guid? userId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            var entry = new AuditEntry(reader.GetString(4), reader.GetString(5), reader.GetString(3), reader.GetString(6));
            var previous = reader.GetString(7);
            var hash = reader.GetString(8);
            if (sequence != expectedSequence || previous != expectedPrevious ||
                ComputeHash(previous, tenantId, sequence, at, userId, entry) != hash)
            {
                return sequence;
            }

            expectedPrevious = hash;
        }

        return null;
    }
}
