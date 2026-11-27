using Erp.Application.Abstractions;

namespace Erp.Infrastructure.Tenancy;

/// <summary>Scoped holder of the trusted tenant/company. A tenant, once set, can never be switched within a scope.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public Guid? CompanyId { get; private set; }

    public bool HasTenant => TenantId.HasValue;

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (TenantId.HasValue && TenantId.Value != tenantId)
        {
            throw new InvalidOperationException("The tenant of a scope cannot be changed once set.");
        }

        TenantId = tenantId;
    }

    public void SetCompany(Guid companyId)
    {
        if (!TenantId.HasValue)
        {
            throw new InvalidOperationException("A tenant must be set before a company.");
        }

        CompanyId = companyId;
    }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply migrations when the API starts. Development only — production uses Erp.Migrator (ADR-015).</summary>
    public bool AutoMigrate { get; set; }
}

public interface IConnectionStringProvider
{
    string GetConnectionString();
}

/// <summary>Shared-database model: every tenant uses the same connection string (ADR-003). Per-tenant routing comes later.</summary>
public sealed class ConfigurationConnectionStringProvider(string connectionString) : IConnectionStringProvider
{
    public string GetConnectionString() => connectionString;
}
