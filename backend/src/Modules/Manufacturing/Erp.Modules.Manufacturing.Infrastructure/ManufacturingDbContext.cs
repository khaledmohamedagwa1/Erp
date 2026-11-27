using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Manufacturing.Application;
using Erp.Modules.Manufacturing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Manufacturing.Infrastructure;

public sealed class ManufacturingDbContext(DbContextOptions<ManufacturingDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IManufacturingDbContext
{
    public const string SchemaName = "manufacturing";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public override string Schema => SchemaName;

    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();

    public DbSet<Routing> Routings => Set<Routing>();

    public DbSet<BillOfMaterials> Boms => Set<BillOfMaterials>();

    public DbSet<BomVersion> BomVersions => Set<BomVersion>();

    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();

    public DbSet<ItemPlanningParameters> PlanningParameters => Set<ItemPlanningParameters>();

    public DbSet<MrpRun> MrpRuns => Set<MrpRun>();

    public DbSet<ManufacturingSettings> Settings => Set<ManufacturingSettings>();

    public DbSet<CostVersion> CostVersions => Set<CostVersion>();

    public DbSet<CostPool> CostPools => Set<CostPool>();

    public DbSet<AbsorptionRun> AbsorptionRuns => Set<AbsorptionRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Child collections are stored as JSON columns, never as entity types.
        modelBuilder.Ignore<RoutingOperation>();
        modelBuilder.Ignore<BomLine>();
        modelBuilder.Ignore<ProductionComponent>();
        modelBuilder.Ignore<ProductionOperation>();
        modelBuilder.Ignore<ProductionTransaction>();
        modelBuilder.Ignore<MrpSuggestion>();
        modelBuilder.Ignore<MrpShortage>();
        modelBuilder.Ignore<PeggingEntry>();
        modelBuilder.Ignore<ProductionSubstitution>();
        modelBuilder.Ignore<StandardCostItem>();
        modelBuilder.Ignore<AbsorptionLine>();
        modelBuilder.Ignore<AbsorptionAccountAmount>();

        modelBuilder.Entity<WorkCenter>(b =>
        {
            b.ToTable("WorkCenter");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.CapacityHoursPerDay).HasPrecision(18, 6);
            b.Property(x => x.LaborRatePerHour).HasPrecision(19, 6);
            b.Property(x => x.MachineRatePerHour).HasPrecision(19, 6);
            b.Property(x => x.OverheadRatePerHour).HasPrecision(19, 6);
            b.Property(x => x.OverheadBasis).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.WorkingDaysMask);
            b.HasIndex(TenantIdProperty, nameof(WorkCenter.CompanyId), nameof(WorkCenter.Code)).IsUnique();
        });

        modelBuilder.Entity<Routing>(b =>
        {
            b.ToTable("Routing");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            JsonList(b.Property(x => x.Operations));
            b.HasIndex(TenantIdProperty, nameof(Routing.CompanyId), nameof(Routing.Code)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(Routing.ProductId));
        });

        modelBuilder.Entity<BillOfMaterials>(b =>
        {
            b.ToTable("BillOfMaterials");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(BillOfMaterials.CompanyId), nameof(BillOfMaterials.Code)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(BillOfMaterials.ProductId));
        });

        modelBuilder.Entity<BomVersion>(b =>
        {
            b.ToTable("BomVersion");
            Key(b);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.OutputQuantity).HasPrecision(18, 6);
            b.Property(x => x.ExpectedYieldPercent).HasPrecision(9, 6);
            b.Ignore(x => x.IsUsable);
            JsonList(b.Property(x => x.Lines));
            b.HasIndex(TenantIdProperty, nameof(BomVersion.BomId), nameof(BomVersion.VersionNo)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(BomVersion.ProductId));
            b.HasOne<BillOfMaterials>().WithMany().HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ProductionOrder>(b =>
        {
            b.ToTable("ProductionOrder");
            Key(b);
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PlannedQuantity).HasPrecision(18, 6);
            b.Property(x => x.CompletedQuantity).HasPrecision(18, 6);
            b.Property(x => x.ScrappedQuantity).HasPrecision(18, 6);
            foreach (var money in new[] { nameof(ProductionOrder.WipBalance), nameof(ProductionOrder.MaterialCost), nameof(ProductionOrder.LaborCost),
                         nameof(ProductionOrder.MachineCost), nameof(ProductionOrder.OverheadCost), nameof(ProductionOrder.ReceivedCost), nameof(ProductionOrder.VarianceAmount),
                         nameof(ProductionOrder.MaterialPriceVariance), nameof(ProductionOrder.MaterialUsageVariance), nameof(ProductionOrder.ConversionVariance),
                         nameof(ProductionOrder.OtherVariance) })
            {
                b.Property<decimal>(money).HasPrecision(19, 4);
            }

            b.Property(x => x.SourceReference).HasMaxLength(100);
            b.Property(x => x.OrderType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CostingMethod).HasConversion<string>().HasMaxLength(20);
            foreach (var cost in new[] { nameof(ProductionOrder.StandardMaterialUnitCost), nameof(ProductionOrder.StandardLaborUnitCost),
                         nameof(ProductionOrder.StandardMachineUnitCost), nameof(ProductionOrder.StandardOverheadUnitCost) })
            {
                b.Property<decimal>(cost).HasPrecision(19, 6);
            }

            b.Ignore(x => x.StandardConversionUnitCost);
            b.Ignore(x => x.StandardUnitCost);
            JsonList(b.Property(x => x.Substitutions));
            b.Ignore(x => x.OpenQuantity);
            b.Ignore(x => x.IsOpenForWork);
            JsonList(b.Property(x => x.Components));
            JsonList(b.Property(x => x.Operations));
            JsonList(b.Property(x => x.Transactions));
            b.HasIndex(TenantIdProperty, nameof(ProductionOrder.CompanyId), nameof(ProductionOrder.Number)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(ProductionOrder.ProductId), nameof(ProductionOrder.Status));
        });

        modelBuilder.Entity<ItemPlanningParameters>(b =>
        {
            b.ToTable("ItemPlanningParameters");
            Key(b);
            b.Property(x => x.SafetyStock).HasPrecision(18, 6);
            b.Property(x => x.LotSize).HasPrecision(18, 6);
            b.Property(x => x.LotSizeRule).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Procurement).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.CostingMethod).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(ItemPlanningParameters.CompanyId), nameof(ItemPlanningParameters.ProductId)).IsUnique();
        });

        modelBuilder.Entity<MrpRun>(b =>
        {
            b.ToTable("MrpRun");
            Key(b);
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            JsonList(b.Property(x => x.Suggestions));
            JsonList(b.Property(x => x.Shortages));
            b.HasIndex(TenantIdProperty, nameof(MrpRun.CompanyId), nameof(MrpRun.Number)).IsUnique();
        });

        modelBuilder.Entity<ManufacturingSettings>(b =>
        {
            b.ToTable("ManufacturingSettings");
            Key(b);
            b.Property(x => x.DefaultCostingMethod).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(ManufacturingSettings.CompanyId)).IsUnique();
        });

        modelBuilder.Entity<CostVersion>(b =>
        {
            b.ToTable("CostVersion");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            JsonList(b.Property(x => x.Items));
            b.HasIndex(TenantIdProperty, nameof(CostVersion.CompanyId), nameof(CostVersion.Code)).IsUnique();
        });

        modelBuilder.Entity<CostPool>(b =>
        {
            b.ToTable("CostPool");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            JsonList(b.Property(x => x.WorkCenterIds));
            JsonList(b.Property(x => x.ExpenseAccountIds));
            b.HasIndex(TenantIdProperty, nameof(CostPool.CompanyId), nameof(CostPool.Code)).IsUnique();
        });

        modelBuilder.Entity<AbsorptionRun>(b =>
        {
            b.ToTable("AbsorptionRun");
            Key(b);
            b.Ignore(x => x.TotalVariance);
            JsonList(b.Property(x => x.Lines));
            b.HasIndex(TenantIdProperty, nameof(AbsorptionRun.LegalEntityId), nameof(AbsorptionRun.PeriodFrom), nameof(AbsorptionRun.PeriodTo)).IsUnique();
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
public sealed class ManufacturingDbContextFactory : IDesignTimeDbContextFactory<ManufacturingDbContext>
{
    public ManufacturingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ManufacturingDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ManufacturingDbContext.SchemaName))
        .Options);
}
