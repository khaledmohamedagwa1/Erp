using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Application;
using Erp.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Inventory.Infrastructure;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IInventoryDbContext
{
    public const string SchemaName = "inventory";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string Schema => SchemaName;

    public DbSet<InventoryMovement> Movements => Set<InventoryMovement>();

    public DbSet<InventoryBalance> Balances => Set<InventoryBalance>();

    public DbSet<ItemValuation> Valuations => Set<ItemValuation>();

    public DbSet<CostLayer> CostLayers => Set<CostLayer>();

    public DbSet<LayerConsumption> LayerConsumptions => Set<LayerConsumption>();

    public DbSet<Batch> Batches => Set<Batch>();

    public DbSet<SerialNumber> Serials => Set<SerialNumber>();

    public DbSet<StockReservation> Reservations => Set<StockReservation>();

    public DbSet<OpeningStockDocument> OpeningStocks => Set<OpeningStockDocument>();

    public DbSet<StockAdjustment> Adjustments => Set<StockAdjustment>();

    public DbSet<StockTransfer> Transfers => Set<StockTransfer>();

    public DbSet<StockCount> Counts => Set<StockCount>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Lines are JSON values, not entities.
        modelBuilder.Ignore<StockDocumentLine>();
        modelBuilder.Ignore<StockCountLine>();

        modelBuilder.Entity<InventoryMovement>(b =>
        {
            b.ToTable("InventoryMovement");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MovementType).HasMaxLength(30).IsRequired();
            b.Property(x => x.Quantity).HasPrecision(18, 6);
            b.Property(x => x.BaseQuantity).HasPrecision(18, 6);
            b.Property(x => x.UnitCost).HasPrecision(19, 6);
            b.Property(x => x.TotalCost).HasPrecision(19, 4);
            b.Property(x => x.SourceModule).HasMaxLength(30).IsRequired();
            b.Property(x => x.SourceDocType).HasMaxLength(50).IsRequired();
            b.Property(x => x.ReferenceNo).HasMaxLength(60);
            b.Property(x => x.JournalPurpose).HasMaxLength(50);
            b.Property(x => x.CreatedAtUtc).HasPrecision(3);
            // One movement per source line, type, direction, batch and serial; reversals have the opposite direction.
            b.HasIndex(TenantIdProperty, nameof(InventoryMovement.SourceDocType), nameof(InventoryMovement.SourceDocId), nameof(InventoryMovement.SourceLineId),
                    nameof(InventoryMovement.MovementType), nameof(InventoryMovement.Direction), nameof(InventoryMovement.BatchId), nameof(InventoryMovement.SerialId))
                .IsUnique().HasFilter(null).HasDatabaseName("UX_InventoryMovement_Source");
            b.HasIndex(TenantIdProperty, nameof(InventoryMovement.ProductId), nameof(InventoryMovement.WarehouseId), nameof(InventoryMovement.TransactionDate));
            b.HasIndex(TenantIdProperty, nameof(InventoryMovement.LegalEntityId), nameof(InventoryMovement.ValuationAreaId));
        });

        modelBuilder.Entity<InventoryBalance>(b =>
        {
            b.ToTable("InventoryBalance");
            b.HasKey(x => x.Id);
            b.Property(x => x.OnHand).HasPrecision(18, 6);
            b.Property(x => x.Reserved).HasPrecision(18, 6);
            b.Property(x => x.UpdatedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(InventoryBalance.ProductId), nameof(InventoryBalance.VariantKey), nameof(InventoryBalance.WarehouseId), nameof(InventoryBalance.BatchKey))
                .IsUnique().HasDatabaseName("UX_InventoryBalance_Key");
            b.HasIndex(TenantIdProperty, nameof(InventoryBalance.WarehouseId));
        });

        modelBuilder.Entity<ItemValuation>(b =>
        {
            b.ToTable("ItemValuation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CostingMethod).HasMaxLength(20).IsRequired();
            b.Property(x => x.Quantity).HasPrecision(18, 6);
            b.Property(x => x.Value).HasPrecision(19, 4);
            b.Property(x => x.AverageCost).HasPrecision(19, 6);
            b.Property(x => x.LastCost).HasPrecision(19, 6);
            b.HasIndex(TenantIdProperty, nameof(ItemValuation.ValuationAreaId), nameof(ItemValuation.ProductId), nameof(ItemValuation.VariantKey))
                .IsUnique().HasDatabaseName("UX_ItemValuation_Key");
        });

        modelBuilder.Entity<CostLayer>(b =>
        {
            b.ToTable("CostLayer");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.OriginalQuantity).HasPrecision(18, 6);
            b.Property(x => x.RemainingQuantity).HasPrecision(18, 6);
            b.Property(x => x.UnitCost).HasPrecision(19, 6);
            b.Property(x => x.ReceivedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(CostLayer.ValuationAreaId), nameof(CostLayer.ProductId), nameof(CostLayer.VariantKey), nameof(CostLayer.Sequence));
            b.HasIndex(nameof(CostLayer.ReceiptMovementId)).IsUnique();
        });

        modelBuilder.Entity<LayerConsumption>(b =>
        {
            b.ToTable("LayerConsumption");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Quantity).HasPrecision(18, 6);
            b.Property(x => x.UnitCost).HasPrecision(19, 6);
            b.HasIndex(nameof(LayerConsumption.MovementId));
            b.HasIndex(nameof(LayerConsumption.LayerId));
        });

        modelBuilder.Entity<Batch>(b =>
        {
            b.ToTable("Batch");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Number).HasMaxLength(50).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(Batch.ProductId), nameof(Batch.Number)).IsUnique();
        });

        modelBuilder.Entity<SerialNumber>(b =>
        {
            b.ToTable("SerialNumber");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Serial).HasMaxLength(100).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(SerialNumber.ProductId), nameof(SerialNumber.Serial)).IsUnique();
        });

        modelBuilder.Entity<StockReservation>(b =>
        {
            b.ToTable("StockReservation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DemandType).HasMaxLength(50).IsRequired();
            b.Property(x => x.Quantity).HasPrecision(18, 6);
            b.Property(x => x.Consumed).HasPrecision(18, 6);
            b.Property(x => x.Released).HasPrecision(18, 6);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.Open);
            b.HasIndex(TenantIdProperty, nameof(StockReservation.DemandType), nameof(StockReservation.DemandId), nameof(StockReservation.DemandLineId));
        });

        modelBuilder.Entity<OpeningStockDocument>(b =>
        {
            Document(b, "OpeningStock");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TotalCost).HasPrecision(19, 4);
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<StockAdjustment>(b =>
        {
            Document(b, "StockAdjustment");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reason).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.NetCost).HasPrecision(19, 4);
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<StockTransfer>(b =>
        {
            Document(b, "StockTransfer");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Notes).HasMaxLength(500);
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<StockCount>(b =>
        {
            Document(b, "StockCount");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.SnapshotAtUtc).HasPrecision(3);
            Lines(b.Property(x => x.Lines));
        });
    }

    private static void Document<T>(EntityTypeBuilder<T> b, string table)
        where T : class
    {
        b.ToTable(table);
        b.HasKey("Id");
        b.Property<Guid>("Id").ValueGeneratedNever();
        b.Property<string>("Number").HasMaxLength(60).IsRequired();
        b.HasIndex(TenantIdProperty, "CompanyId", "Number").IsUnique();
    }

    /// <summary>Document lines are stored as a JSON column; the comparer snapshots a deep copy so in-place edits are detected.</summary>
    private static void Lines<TLine>(PropertyBuilder<List<TLine>> property)
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<TLine>>(v, Json) ?? new List<TLine>(),
                new ValueComparer<List<TLine>>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<List<TLine>>(JsonSerializer.Serialize(v, Json), Json)!))
            .HasColumnType("nvarchar(max)")
            .IsRequired();
    }
}

public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<InventoryDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", InventoryDbContext.SchemaName))
        .Options);
}
