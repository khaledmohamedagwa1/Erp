using Erp.Infrastructure.Persistence;
using Erp.Modules.Catalog.Application;
using Erp.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Catalog.Infrastructure;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), ICatalogDbContext
{
    public const string SchemaName = "catalog";

    public override string Schema => SchemaName;

    public DbSet<UnitOfMeasure> Uoms => Set<UnitOfMeasure>();

    public DbSet<ProductCategory> Categories => Set<ProductCategory>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<PriceList> PriceLists => Set<PriceList>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnitOfMeasure>(b =>
        {
            b.ToTable("UnitOfMeasure");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(40).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Class).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(UnitOfMeasure.CompanyId), nameof(UnitOfMeasure.Code)).IsUnique();
        });

        modelBuilder.Entity<ProductCategory>(b =>
        {
            b.ToTable("ProductCategory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(40).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.DefaultCostingMethod).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PostingGroup).HasMaxLength(40).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(ProductCategory.CompanyId), nameof(ProductCategory.Code)).IsUnique();
            b.HasOne<ProductCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Brand>(b =>
        {
            b.ToTable("Brand");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(40).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(Brand.CompanyId), nameof(Brand.Code)).IsUnique();
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("Product");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Sku).HasMaxLength(40).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Tracking).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TaxCategory).HasMaxLength(40).IsRequired();
            b.Property(x => x.CostingMethodOverride).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PostingGroupOverride).HasMaxLength(40);
            b.Ignore(x => x.IsStockable);
            b.HasIndex(TenantIdProperty, nameof(Product.CompanyId), nameof(Product.Sku)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(Product.CompanyId), nameof(Product.NameEn));
            b.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.BaseUomId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<ProductCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.NoAction);
            b.HasMany(x => x.Units).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Barcodes).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Variants).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Units).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(x => x.Barcodes).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(x => x.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ProductUnit>(b =>
        {
            b.ToTable("ProductUnit");
            b.HasKey(x => new { x.ProductId, x.UomId });
            b.Property(x => x.FactorToBase).HasPrecision(18, 6);
            b.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.UomId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ProductBarcode>(b =>
        {
            b.ToTable("ProductBarcode");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Barcode).HasMaxLength(64).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(ProductBarcode.CompanyId), nameof(ProductBarcode.Barcode)).IsUnique();
        });

        modelBuilder.Entity<ProductVariant>(b =>
        {
            b.ToTable("ProductVariant");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Sku).HasMaxLength(40).IsRequired();
            b.Property(x => x.Attributes).HasMaxLength(2000).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(ProductVariant.CompanyId), nameof(ProductVariant.Sku)).IsUnique();
        });

        modelBuilder.Entity<PriceList>(b =>
        {
            b.ToTable("PriceList");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(40).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.HasIndex(TenantIdProperty, nameof(PriceList.CompanyId), nameof(PriceList.Code)).IsUnique();
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PriceListItem>(b =>
        {
            b.ToTable("PriceListItem");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MinQuantity).HasPrecision(18, 6);
            b.Property(x => x.Price).HasPrecision(19, 4);
            b.HasIndex(x => new { x.PriceListId, x.ProductId, x.UomId });
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.UomId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CatalogDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", CatalogDbContext.SchemaName))
        .Options);
}
