using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Procurement.Application;
using Erp.Modules.Procurement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Procurement.Infrastructure;

public sealed class ProcurementDbContext(DbContextOptions<ProcurementDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IProcurementDbContext
{
    public const string SchemaName = "procurement";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string Schema => SchemaName;

    public DbSet<PurchaseRequest> Requests => Set<PurchaseRequest>();

    public DbSet<PurchaseQuotation> Quotations => Set<PurchaseQuotation>();

    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();

    public DbSet<GoodsReceipt> Receipts => Set<GoodsReceipt>();

    public DbSet<PurchaseInvoice> Invoices => Set<PurchaseInvoice>();

    public DbSet<PurchaseReturn> Returns => Set<PurchaseReturn>();

    public DbSet<LandedCost> LandedCosts => Set<LandedCost>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Lines are JSON values, not entities.
        modelBuilder.Ignore<RequestLine>();
        modelBuilder.Ignore<QuotationLine>();
        modelBuilder.Ignore<PurchaseOrderLine>();
        modelBuilder.Ignore<GoodsReceiptLine>();
        modelBuilder.Ignore<PurchaseInvoiceLine>();
        modelBuilder.Ignore<PurchaseReturnLine>();
        modelBuilder.Ignore<LandedCostCharge>();
        modelBuilder.Ignore<LandedCostAllocation>();

        modelBuilder.Entity<PurchaseRequest>(b =>
        {
            Document(b, "PurchaseRequest", numbered: true);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.RejectionReason).HasMaxLength(500);
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<PurchaseQuotation>(b =>
        {
            Document(b, "PurchaseQuotation", numbered: true);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.SupplierReference).HasMaxLength(50);
            b.HasIndex(TenantIdProperty, nameof(PurchaseQuotation.RequestId));
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            Document(b, "PurchaseOrder", numbered: true);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(19, 8);
            b.Property(x => x.NetTotal).HasPrecision(19, 4);
            b.Property(x => x.TaxTotal).HasPrecision(19, 4);
            b.Property(x => x.GrandTotal).HasPrecision(19, 4);
            b.Property(x => x.Notes).HasMaxLength(500);
            b.HasIndex(TenantIdProperty, "CompanyId", nameof(PurchaseOrder.SupplierId), nameof(PurchaseOrder.Status));
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<GoodsReceipt>(b =>
        {
            Document(b, "GoodsReceipt", numbered: true);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(19, 8);
            b.Property(x => x.TotalBaseValue).HasPrecision(19, 4);
            b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.InspectionNotes).HasMaxLength(500);
            b.HasIndex(TenantIdProperty, nameof(GoodsReceipt.PurchaseOrderId));
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<PurchaseInvoice>(b =>
        {
            Document(b, "PurchaseInvoice", numbered: false);
            b.Property(x => x.Number).HasMaxLength(60);
            b.HasIndex(TenantIdProperty, "CompanyId", nameof(PurchaseInvoice.Number)).IsUnique().HasFilter("[Number] IS NOT NULL");
            b.Property(x => x.SupplierInvoiceNumber).HasMaxLength(50).IsRequired();
            // Not unique: a cancelled invoice may be re-entered; the handlers refuse active duplicates.
            b.HasIndex(TenantIdProperty, nameof(PurchaseInvoice.SupplierId), nameof(PurchaseInvoice.SupplierInvoiceNumber));
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(19, 8);
            b.Property(x => x.NetTotal).HasPrecision(19, 4);
            b.Property(x => x.TaxTotal).HasPrecision(19, 4);
            b.Property(x => x.GrandTotal).HasPrecision(19, 4);
            b.Property(x => x.BaseTotal).HasPrecision(19, 4);
            b.Property(x => x.OpenAmount).HasPrecision(19, 4);
            b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.CancelReason).HasMaxLength(500);
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<PurchaseReturn>(b =>
        {
            Document(b, "PurchaseReturn", numbered: false);
            b.Property(x => x.Number).HasMaxLength(60);
            b.HasIndex(TenantIdProperty, "CompanyId", nameof(PurchaseReturn.Number)).IsUnique().HasFilter("[Number] IS NOT NULL");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.TotalCost).HasPrecision(19, 4);
            b.Property(x => x.DebitNoteTotal).HasPrecision(19, 4);
            b.Property(x => x.DebitNoteCurrency).HasMaxLength(3);
            b.HasIndex(TenantIdProperty, nameof(PurchaseReturn.GoodsReceiptId));
            Lines(b.Property(x => x.Lines));
        });

        modelBuilder.Entity<LandedCost>(b =>
        {
            Document(b, "LandedCost", numbered: false);
            b.Property(x => x.Number).HasMaxLength(60);
            b.HasIndex(TenantIdProperty, "CompanyId", nameof(LandedCost.Number)).IsUnique().HasFilter("[Number] IS NOT NULL");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Basis).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SupplierReference).HasMaxLength(50);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(19, 8);
            b.Property(x => x.GrossTotal).HasPrecision(19, 4);
            b.Ignore(x => x.ChargesTotal);
            Lines(b.Property(x => x.Charges));
            Lines(b.Property(x => x.GoodsReceiptIds));
            Lines(b.Property(x => x.Allocations));
        });
    }

    private static void Document<T>(EntityTypeBuilder<T> b, string table, bool numbered)
        where T : class
    {
        b.ToTable(table);
        b.HasKey("Id");
        b.Property<Guid>("Id").ValueGeneratedNever();
        if (numbered)
        {
            b.Property<string>("Number").HasMaxLength(60).IsRequired();
            b.HasIndex(TenantIdProperty, "CompanyId", "Number").IsUnique();
        }
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

public sealed class ProcurementDbContextFactory : IDesignTimeDbContextFactory<ProcurementDbContext>
{
    public ProcurementDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ProcurementDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ProcurementDbContext.SchemaName))
        .Options);
}
