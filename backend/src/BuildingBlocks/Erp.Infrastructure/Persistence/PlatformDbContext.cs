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

/// <summary>Owns platform tables (numbering in <c>core</c>, audit in <c>audit</c>). Accessed through raw SQL services.</summary>
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
