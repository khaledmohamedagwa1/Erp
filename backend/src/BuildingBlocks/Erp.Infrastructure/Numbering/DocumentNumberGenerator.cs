using System.Data;
using System.Globalization;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Persistence;

namespace Erp.Infrastructure.Numbering;

/// <summary>
/// Allocates the next number of a sequence with an update lock held until the surrounding transaction ends.
/// Because the increment rolls back with the transaction, posted numbers are gapless (ADR-021).
/// </summary>
public sealed class DocumentNumberGenerator(ErpDbSession session, ITenantContext tenantContext) : IDocumentNumberGenerator
{
    public async Task<string> NextAsync(NumberScope scope, CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Document numbers require a tenant context.");
        var transaction = session.Transaction
            ?? throw new InvalidOperationException("Document numbers must be allocated inside a transaction, otherwise gaps are possible.");

        var scopeKey = string.Join('|',
            scope.CompanyId.ToString("N"), scope.LegalEntityId?.ToString("N") ?? "-", scope.BranchId?.ToString("N") ?? "-",
            scope.DocumentType, scope.FiscalYear?.ToString(CultureInfo.InvariantCulture) ?? "-");

        await using var command = AuditWriter.Command(transaction.Connection!, transaction, """
            -- Row locks only: create the sequence row once (a concurrent creator makes us wait, then we ignore the duplicate),
            -- then increment under an exclusive row lock held until the transaction ends (gapless).
            IF NOT EXISTS (SELECT 1 FROM core.NumberSequence WHERE TenantId = @tenantId AND ScopeKey = @scopeKey)
            BEGIN TRY
                INSERT INTO core.NumberSequence (Id, TenantId, ScopeKey, Prefix, Padding, NextValue) VALUES (NEWID(), @tenantId, @scopeKey, @prefix, 6, 1);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
            END CATCH
            UPDATE core.NumberSequence WITH (UPDLOCK, ROWLOCK)
               SET NextValue = NextValue + 1
            OUTPUT deleted.NextValue, deleted.Prefix, deleted.Padding
             WHERE TenantId = @tenantId AND ScopeKey = @scopeKey;
            """);
        AuditWriter.Add(command, "@tenantId", DbType.Guid, tenantId);
        AuditWriter.Add(command, "@scopeKey", DbType.String, scopeKey);
        AuditWriter.Add(command, "@prefix", DbType.String, scope.DefaultPrefix);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Number sequence could not be allocated.");
        }

        var value = reader.GetInt64(0);
        var prefix = reader.GetString(1);
        var padding = reader.GetInt32(2);
        return Format(prefix, scope.BranchCode, scope.FiscalYear, value, padding);
    }

    public static string Format(string prefix, string? branchCode, int? fiscalYear, long value, int padding)
    {
        var parts = new List<string> { prefix };
        if (!string.IsNullOrWhiteSpace(branchCode))
        {
            parts.Add(branchCode);
        }

        if (fiscalYear is not null)
        {
            parts.Add(fiscalYear.Value.ToString(CultureInfo.InvariantCulture));
        }

        parts.Add(value.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0'));
        return string.Join('-', parts);
    }
}
