namespace Erp.SharedKernel.Domain;

/// <summary>
/// Marks an entity as owned by a tenant. The <c>TenantId</c> column is a shadow property set by
/// infrastructure from the trusted tenant context — never from client input.
/// </summary>
public interface ITenantScoped
{
}

/// <summary>Marks an entity as belonging to a company inside the tenant. Queries are filtered by the active company.</summary>
public interface ICompanyScoped : ITenantScoped
{
    Guid CompanyId { get; }
}

/// <summary>
/// The property holds a secret (password hash, MFA secret, recovery codes). The audit log records that it changed
/// but never its value.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute
{
}

/// <summary>Changes to entities carrying this attribute are written to the tamper-evident audit log.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AuditedAttribute : Attribute
{
}
