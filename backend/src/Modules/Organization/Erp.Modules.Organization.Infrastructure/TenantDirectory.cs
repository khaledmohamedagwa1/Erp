using Erp.Application.Abstractions;
using Erp.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace Erp.Modules.Organization.Infrastructure;

/// <summary>
/// Reads the tenant table on its own short-lived connection (outside the tenant-scoped session), because it is used
/// to establish the tenant in the first place. The Tenant table is exempt from row-level security.
/// </summary>
public sealed class TenantDirectory(IConnectionStringProvider connectionStrings, IMemoryCache cache) : ITenantDirectory
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public Task<TenantInfo?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        cache.GetOrCreateAsync($"tenant:id:{tenantId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return QueryAsync("Id = @value", tenantId, cancellationToken);
        });

    public async Task<TenantInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        // Not cached: used at sign-in and provisioning, where a stale "not found" would be wrong.
        return await QueryAsync("Code = @value", code, cancellationToken);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionStrings.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Code, Name, Status FROM organization.Tenant WHERE Status = 'Active' ORDER BY Code;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<TenantInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new TenantInfo(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), true));
        }

        return tenants;
    }

    private async Task<TenantInfo?> QueryAsync(string predicate, object value, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionStrings.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Code, Name, Status FROM organization.Tenant WHERE {predicate};";
        command.Parameters.AddWithValue("@value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TenantInfo(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3) == "Active");
    }
}
