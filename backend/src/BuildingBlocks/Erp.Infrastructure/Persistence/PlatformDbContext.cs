using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence;

public sealed class NumberSequenceRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string ScopeKey { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public int Padding { get; set; }

    public long NextValue { get; set; }
}

public sealed class AuditLogRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public long Sequence { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public Guid? UserId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string Changes { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string PreviousHash { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;
}

public sealed class AuditChainHeadRecord
{
    public Guid TenantId { get; set; }

    public long LastSequence { get; set; }

    public string LastHash { get; set; } = string.Empty;
}

/// <summary>A processed idempotent request. Written in the same transaction as the request's effect.</summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public string? ResponseBody { get; set; }

    public string? ContentType { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Marks an outbox message as handled by one consumer (at-most-once processing per consumer).</summary>
public sealed class InboxMessageRecord
{
    public Guid MessageId { get; set; }

    public string Consumer { get; set; } = string.Empty;

    public DateTime ProcessedAtUtc { get; set; }
}

/// <summary>ASP.NET Core Data Protection key (XML). Shared by all API/worker instances so protected data survives restarts.</summary>
public sealed class DataProtectionKeyRecord
{
    public int Id { get; set; }

    public string FriendlyName { get; set; } = string.Empty;

    public string Xml { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Owns platform tables (numbering, idempotency and inbox in <c>core</c>, audit in <c>audit</c>). Accessed through raw SQL services.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies)
{
    public const string SchemaName = "core";

    public override string Schema => SchemaName;

    protected override bool UsesOutbox => false;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NumberSequenceRecord>(b =>
        {
            b.ToTable("NumberSequence", "core");
            b.HasKey(x => x.Id);
            b.Property(x => x.ScopeKey).HasMaxLength(400).IsRequired();
            b.Property(x => x.Prefix).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ScopeKey }).IsUnique();
        });

        modelBuilder.Entity<AuditLogRecord>(b =>
        {
            b.ToTable("AuditLog", "audit");
            b.HasKey(x => x.Id);
            b.Property(x => x.OccurredAtUtc).HasPrecision(3);
            b.Property(x => x.Action).HasMaxLength(20).IsRequired();
            b.Property(x => x.EntityType).HasMaxLength(200).IsRequired();
            b.Property(x => x.EntityId).HasMaxLength(200).IsRequired();
            b.Property(x => x.CorrelationId).HasMaxLength(100);
            b.Property(x => x.PreviousHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("IdempotencyRecord", "core");
            b.HasKey(x => x.Id);
            b.Property(x => x.Key).HasMaxLength(100).IsRequired();
            b.Property(x => x.Endpoint).HasMaxLength(300).IsRequired();
            b.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(200);
            b.Property(x => x.CreatedAtUtc).HasPrecision(3);
            b.Property(x => x.ExpiresAtUtc).HasPrecision(3);
            b.HasIndex(x => new { x.TenantId, x.Key, x.Endpoint }).IsUnique();
            b.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<DataProtectionKeyRecord>(b =>
        {
            b.ToTable("DataProtectionKey", "core");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityColumn();
            b.Property(x => x.FriendlyName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Xml).IsRequired();
            b.Property(x => x.CreatedAtUtc).HasPrecision(3);
        });

        modelBuilder.Entity<InboxMessageRecord>(b =>
        {
            b.ToTable("InboxMessage", "core");
            b.HasKey(x => new { x.MessageId, x.Consumer });
            b.Property(x => x.Consumer).HasMaxLength(200);
            b.Property(x => x.ProcessedAtUtc).HasPrecision(3);
        });

        modelBuilder.Entity<AuditChainHeadRecord>(b =>
        {
            b.ToTable("AuditChainHead", "audit");
            b.HasKey(x => x.TenantId);
            b.Property(x => x.LastHash).HasMaxLength(64).IsFixedLength().IsRequired();
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class PlatformDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.SchemaName))
        .Options);
}
