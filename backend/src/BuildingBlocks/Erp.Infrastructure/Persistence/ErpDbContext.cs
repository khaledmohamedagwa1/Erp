using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Auditing;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Infrastructure.Persistence;

/// <summary>Scoped services a module DbContext needs at runtime. Null at design time (migrations tooling).</summary>
public sealed record ErpDbContextDependencies(
    ITenantContext TenantContext,
    ICurrentUser CurrentUser,
    IClock Clock,
    ErpDbSession Session,
    IAuditWriter AuditWriter);

/// <summary>
/// Base for every module DbContext. Applies, by convention:
/// tenant + company query filters, tenant write guard, rowversion and audit stamps (shadow properties),
/// the module outbox, and audit-log capture for <see cref="AuditedAttribute"/> entities.
/// </summary>
public abstract class ErpDbContext : DbContext
{
    public const string TenantIdProperty = "TenantId";
    public const string RowVersionProperty = "RowVersion";

    private readonly ErpDbContextDependencies? _deps;

    protected ErpDbContext(DbContextOptions options, ErpDbContextDependencies? dependencies)
        : base(options)
    {
        _deps = dependencies;
        _deps?.Session.Register(this);
    }

    /// <summary>Database schema owned by the module (Part 21).</summary>
    public abstract string Schema { get; }

    /// <summary>Whether this context owns an outbox table.</summary>
    protected virtual bool UsesOutbox => true;

    // Referenced by query filters; EF parameterizes these per context instance.
    public Guid CurrentTenantId => _deps?.TenantContext.TenantId ?? Guid.Empty;

    public Guid CurrentCompanyId => _deps?.TenantContext.CompanyId ?? Guid.Empty;

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        // Declare the shadow TenantId first so module mappings can index it or use it as a foreign key.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)).ToList())
        {
            modelBuilder.Entity(entityType.ClrType).Property<Guid>(TenantIdProperty).IsRequired();
        }

        ConfigureModel(modelBuilder);

        if (UsesOutbox)
        {
            modelBuilder.Entity<OutboxMessage>(b =>
            {
                b.ToTable("OutboxMessage", Schema);
                b.HasKey(x => x.Id);
                b.Property(x => x.Type).HasMaxLength(500).IsRequired();
                b.Property(x => x.CorrelationId).HasMaxLength(100);
                b.Property(x => x.Error).HasMaxLength(4000);
                b.HasIndex(x => new { x.ProcessedAtUtc, x.OccurredAtUtc });
            });
        }

        ApplyConventions(modelBuilder);
    }

    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    private void ApplyConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList())
        {
            var clr = entityType.ClrType;
            var builder = modelBuilder.Entity(clr);

            if (typeof(AggregateRoot).IsAssignableFrom(clr))
            {
                builder.Property<byte[]>(RowVersionProperty).IsRowVersion();
                builder.Property<DateTime>("CreatedAtUtc").HasPrecision(3);
                builder.Property<Guid?>("CreatedBy");
                builder.Property<DateTime?>("ModifiedAtUtc").HasPrecision(3);
                builder.Property<Guid?>("ModifiedBy");
                builder.Ignore(nameof(AggregateRoot.DomainEvents));
            }

            if (typeof(ITenantScoped).IsAssignableFrom(clr))
            {
                builder.Property<Guid>(TenantIdProperty).IsRequired();
                builder.HasIndex(TenantIdProperty);
                var method = typeof(ErpDbContext).GetMethod(nameof(BuildFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(clr);
                builder.HasQueryFilter((LambdaExpression)method.Invoke(this, null)!);
            }

            foreach (var property in entityType.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            {
                if (property.GetPrecision() is null)
                {
                    throw new InvalidOperationException(
                        $"{clr.Name}.{property.Name} is a decimal without explicit precision. Every money/quantity column must declare it (Part 21).");
                }
            }
        }
    }

    private LambdaExpression BuildFilter<TEntity>()
        where TEntity : class
    {
        if (typeof(ICompanyScoped).IsAssignableFrom(typeof(TEntity)))
        {
            Expression<Func<TEntity, bool>> companyFilter = e =>
                EF.Property<Guid>(e, TenantIdProperty) == CurrentTenantId &&
                EF.Property<Guid>(e, nameof(ICompanyScoped.CompanyId)) == CurrentCompanyId;
            return companyFilter;
        }

        Expression<Func<TEntity, bool>> tenantFilter = e => EF.Property<Guid>(e, TenantIdProperty) == CurrentTenantId;
        return tenantFilter;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync. Synchronous saves are not supported.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_deps is null)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        ApplyOwnershipAndStamps();
        var audit = CaptureAudit();
        WriteOutbox();

        var session = _deps.Session;
        var ownsTransaction = !session.HasActiveTransaction;
        if (ownsTransaction)
        {
            await session.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var affected = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (audit.Count > 0)
            {
                await _deps.AuditWriter.WriteAsync(audit, cancellationToken);
            }

            if (ownsTransaction)
            {
                await session.CommitAsync(cancellationToken);
            }

            return affected;
        }
        catch
        {
            if (ownsTransaction)
            {
                await session.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private void ApplyOwnershipAndStamps()
    {
        var now = _deps!.Clock.UtcNow;
        var userId = _deps.CurrentUser.UserId;
        var tenantId = _deps.TenantContext.TenantId;

        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is ITenantScoped)
            {
                var tenantProperty = entry.Property(TenantIdProperty);
                if (entry.State == EntityState.Added)
                {
                    tenantProperty.CurrentValue = tenantId
                        ?? throw new InvalidOperationException($"Cannot add {entry.Metadata.ClrType.Name} without a tenant context.");
                }
                else if (!Equals(tenantProperty.OriginalValue, tenantId))
                {
                    throw new InvalidOperationException($"Cross-tenant modification of {entry.Metadata.ClrType.Name} was blocked.");
                }
            }

            if (entry.Entity is AggregateRoot)
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Property("CreatedAtUtc").CurrentValue = now;
                    entry.Property("CreatedBy").CurrentValue = userId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Property("ModifiedAtUtc").CurrentValue = now;
                    entry.Property("ModifiedBy").CurrentValue = userId;
                }
            }
        }

        // An aggregate whose owned children changed must still bump its rowversion: mark it modified.
        foreach (var entry in ChangeTracker.Entries<AggregateRoot>().Where(e => e.State == EntityState.Unchanged))
        {
            if (entry.References.Any(r => r.TargetEntry is { State: not EntityState.Unchanged }) ||
                entry.Collections.Any(c => c.CurrentValue is not null && c.CurrentValue.Cast<object>().Any(o => Entry(o).State != EntityState.Unchanged)))
            {
                entry.State = EntityState.Modified;
                entry.Property("ModifiedAtUtc").CurrentValue = now;
                entry.Property("ModifiedBy").CurrentValue = userId;
            }
        }
    }

    private List<AuditEntry> CaptureAudit()
    {
        var entries = new List<AuditEntry>();
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Metadata.ClrType.GetCustomAttribute<AuditedAttribute>() is null)
            {
                continue;
            }

            var changes = new Dictionary<string, object?[]>();
            foreach (var property in entry.Properties.Where(p => p.Metadata.Name is not RowVersionProperty))
            {
                var before = entry.State == EntityState.Added ? null : property.OriginalValue;
                var after = entry.State == EntityState.Deleted ? null : property.CurrentValue;
                if (entry.State == EntityState.Modified && !property.IsModified)
                {
                    continue;
                }

                if (property.Metadata.PropertyInfo?.GetCustomAttribute<SensitiveAttribute>() is not null)
                {
                    before = before is null ? null : "***";
                    after = after is null ? null : "***";
                }

                changes[property.Metadata.Name] = [before, after];
            }

            entries.Add(new AuditEntry(
                Schema + "." + entry.Metadata.ClrType.Name,
                KeyOf(entry),
                entry.State.ToString(),
                JsonSerializer.Serialize(changes)));
        }

        return entries;
    }

    private static string KeyOf(EntityEntry entry) =>
        string.Join(",", entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString()));

    private void WriteOutbox()
    {
        if (!UsesOutbox)
        {
            return;
        }

        var aggregates = ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).Where(a => a.DomainEvents.Count > 0).ToList();
        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = SequentialGuid.New(),
                    TenantId = _deps!.TenantContext.TenantId,
                    OccurredAtUtc = _deps.Clock.UtcNow,
                    Type = domainEvent.GetType().FullName!,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                    CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
