using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Application;
using Erp.Modules.Sales.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Infrastructure;

public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), ISalesDbContext
{
    public const string SchemaName = "sales";

    public override string Schema => SchemaName;

    public DbSet<SalesQuotation> Quotations => Set<SalesQuotation>();

    public DbSet<SalesOrder> Orders => Set<SalesOrder>();

    public DbSet<DeliveryNote> Deliveries => Set<DeliveryNote>();

    public DbSet<SalesInvoice> Invoices => Set<SalesInvoice>();

    public DbSet<SalesReturn> Returns => Set<SalesReturn>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SalesQuotation>(b =>
        {
            Document(b, "SalesQuotation");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(SalesQuotation.CompanyId), nameof(SalesQuotation.Status), nameof(SalesQuotation.DocumentDate));
        });

        modelBuilder.Entity<SalesOrder>(b =>
        {
            Document(b, "SalesOrder");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(SalesOrder.CompanyId), nameof(SalesOrder.Status), nameof(SalesOrder.DocumentDate));
        });

        modelBuilder.Entity<DeliveryNote>(b =>
        {
            Document(b, "DeliveryNote");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(DeliveryNote.SalesOrderId));
            b.HasIndex(TenantIdProperty, nameof(DeliveryNote.CompanyId), nameof(DeliveryNote.Status), nameof(DeliveryNote.DocumentDate));
        });

        modelBuilder.Entity<SalesInvoice>(b =>
        {
            Document(b, "SalesInvoice");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.OpenAmount).HasPrecision(19, 4);
            b.HasIndex(TenantIdProperty, nameof(SalesInvoice.SalesOrderId));
            b.HasIndex(TenantIdProperty, nameof(SalesInvoice.DeliveryNoteId));
            b.HasIndex(TenantIdProperty, nameof(SalesInvoice.CompanyId), nameof(SalesInvoice.Status), nameof(SalesInvoice.DocumentDate));
        });

        modelBuilder.Entity<SalesReturn>(b =>
        {
            Document(b, "SalesReturn");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reason).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(SalesReturn.SalesInvoiceId));
            b.HasIndex(TenantIdProperty, nameof(SalesReturn.CompanyId), nameof(SalesReturn.Status), nameof(SalesReturn.DocumentDate));
        });
    }

    private void Document<T>(EntityTypeBuilder<T> b, string table)
        where T : SalesDocument
    {
        b.ToTable(table);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CustomerPostingGroup).HasMaxLength(30).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.ExchangeRate).HasPrecision(18, 8);
        b.Property(x => x.Number).HasMaxLength(50).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.NetTotal).HasPrecision(19, 4);
        b.Property(x => x.TaxTotal).HasPrecision(19, 4);
        b.Property(x => x.GrossTotal).HasPrecision(19, 4);
        b.HasIndex(TenantIdProperty, nameof(SalesDocument.CompanyId), nameof(SalesDocument.Number)).IsUnique();
        b.HasIndex(TenantIdProperty, nameof(SalesDocument.CompanyId), nameof(SalesDocument.CustomerId));
        b.OwnsMany(x => x.Lines, l =>
        {
            l.ToJson("Lines");
            l.Property(x => x.Uid);
            l.Property(x => x.Sku).HasMaxLength(64);
            l.Property(x => x.Description).HasMaxLength(500);
            l.Property(x => x.PostingGroup).HasMaxLength(30);
            l.Property(x => x.BatchNumber).HasMaxLength(64);
            l.Property(x => x.Quantity).HasPrecision(18, 6);
            l.Property(x => x.BaseQuantity).HasPrecision(18, 6);
            l.Property(x => x.DeliveredQuantity).HasPrecision(18, 6);
            l.Property(x => x.InvoicedQuantity).HasPrecision(18, 6);
            l.Property(x => x.ReturnedQuantity).HasPrecision(18, 6);
            l.Property(x => x.UnitPrice).HasPrecision(19, 6);
            l.Property(x => x.DiscountPercent).HasPrecision(9, 6);
            l.Property(x => x.LineAmount).HasPrecision(19, 4);
            l.Property(x => x.DiscountAmount).HasPrecision(19, 4);
            l.Property(x => x.NetAmount).HasPrecision(19, 4);
            l.Property(x => x.TaxAmount).HasPrecision(19, 4);
            l.Property(x => x.GrossAmount).HasPrecision(19, 4);
            l.Property(x => x.CostAmount).HasPrecision(19, 4);
            l.OwnsMany(x => x.Taxes, t =>
            {
                t.Property(x => x.Code).HasMaxLength(30);
                t.Property(x => x.Rate).HasPrecision(9, 6);
                t.Property(x => x.Base).HasPrecision(19, 4);
                t.Property(x => x.Amount).HasPrecision(19, 4);
            });
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class SalesDbContextFactory : IDesignTimeDbContextFactory<SalesDbContext>
{
    public SalesDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SalesDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", SalesDbContext.SchemaName))
        .Options);
}
