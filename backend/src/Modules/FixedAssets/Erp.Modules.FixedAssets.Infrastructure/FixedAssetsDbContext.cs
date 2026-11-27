using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Infrastructure.Persistence;
using Erp.Modules.FixedAssets.Application;
using Erp.Modules.FixedAssets.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.FixedAssets.Infrastructure;

public sealed class FixedAssetsDbContext(DbContextOptions<FixedAssetsDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IFixedAssetsDbContext
{
    public const string SchemaName = "assets";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public override string Schema => SchemaName;

    public DbSet<AssetCategory> Categories => Set<AssetCategory>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<DepreciationRun> DepreciationRuns => Set<DepreciationRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Schedule, history and run lines are JSON columns, never entity types.
        modelBuilder.Ignore<ScheduleLine>();
        modelBuilder.Ignore<AssetEvent>();
        modelBuilder.Ignore<DepreciationRunLine>();

        modelBuilder.Entity<AssetCategory>(b =>
        {
            b.ToTable("AssetCategory");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.DefaultMethod).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.SalvagePercent).HasPrecision(9, 6);
            b.Property(x => x.DecliningRatePercent).HasPrecision(9, 6);
            b.Property(x => x.PostingGroup).HasMaxLength(30);
            b.HasIndex(TenantIdProperty, nameof(AssetCategory.CompanyId), nameof(AssetCategory.Code)).IsUnique();
        });

        modelBuilder.Entity<Asset>(b =>
        {
            b.ToTable("Asset");
            Key(b);
            b.Property(x => x.Number).HasMaxLength(60);
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Location).HasMaxLength(200);
            b.Property(x => x.Method).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.AcquisitionMode).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PostingGroup).HasMaxLength(30);
            b.Property(x => x.SourceDocType).HasMaxLength(60);
            foreach (var money in new[] { nameof(Asset.AcquisitionCost), nameof(Asset.Cost), nameof(Asset.SalvageValue), nameof(Asset.AccumulatedDepreciation) })
            {
                b.Property<decimal>(money).HasPrecision(19, 4);
            }

            b.Property(x => x.DisposalProceeds).HasPrecision(19, 4);
            b.Property(x => x.DisposalGainLoss).HasPrecision(19, 4);
            b.Property(x => x.DecliningRatePercent).HasPrecision(9, 6);
            b.Property(x => x.TotalUnits).HasPrecision(18, 6);
            b.Ignore(x => x.NetBookValue);
            b.Ignore(x => x.FirstDepreciationMonth);
            JsonList(b.Property(x => x.Schedule));
            JsonList(b.Property(x => x.History));
            b.HasIndex(TenantIdProperty, nameof(Asset.CompanyId), nameof(Asset.Number)).IsUnique().HasFilter("[Number] IS NOT NULL");
            b.HasIndex(TenantIdProperty, nameof(Asset.LegalEntityId), nameof(Asset.Status));
            b.HasOne<AssetCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<DepreciationRun>(b =>
        {
            b.ToTable("DepreciationRun");
            Key(b);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Total).HasPrecision(19, 4);
            JsonList(b.Property(x => x.Lines));

            // One posted run per legal entity and period; a reversed run frees the period for a new run.
            b.HasIndex(TenantIdProperty, nameof(DepreciationRun.LegalEntityId), nameof(DepreciationRun.Period)).IsUnique().HasFilter("[Status] = 'Posted'");
        });
    }

    private static void Key<T>(EntityTypeBuilder<T> b)
        where T : class
    {
        b.HasKey("Id");
        b.Property<Guid>("Id").ValueGeneratedNever();
    }

    /// <summary>Child collections are JSON columns; the comparer snapshots a deep copy so in-place edits are detected.</summary>
    private static void JsonList<TItem>(PropertyBuilder<List<TItem>> property)
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<TItem>>(v, Json) ?? new List<TItem>(),
                new ValueComparer<List<TItem>>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<List<TItem>>(JsonSerializer.Serialize(v, Json), Json)!))
            .HasColumnType("nvarchar(max)")
            .IsRequired();
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class FixedAssetsDbContextFactory : IDesignTimeDbContextFactory<FixedAssetsDbContext>
{
    public FixedAssetsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<FixedAssetsDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", FixedAssetsDbContext.SchemaName))
        .Options);
}
