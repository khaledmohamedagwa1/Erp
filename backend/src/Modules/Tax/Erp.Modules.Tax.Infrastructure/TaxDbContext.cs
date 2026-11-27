using Erp.Infrastructure.Persistence;
using Erp.Modules.Tax.Application;
using Erp.Modules.Tax.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Tax.Infrastructure;

public sealed class TaxDbContext(DbContextOptions<TaxDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), ITaxDbContext
{
    public const string SchemaName = "tax";

    public override string Schema => SchemaName;

    public DbSet<Currency> Currencies => Set<Currency>();

    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    public DbSet<TaxCode> TaxCodes => Set<TaxCode>();

    public DbSet<TaxGroup> TaxGroups => Set<TaxGroup>();

    public DbSet<TaxCategoryMapping> TaxCategoryMappings => Set<TaxCategoryMapping>();

    public DbSet<EInvoicingSettings> EInvoicingSettings => Set<EInvoicingSettings>();

    public DbSet<EInvoiceSubmission> EInvoiceSubmissions => Set<EInvoiceSubmission>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EInvoicingSettings>(b =>
        {
            b.ToTable("EInvoicingSettings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            b.Property(x => x.Environment).HasMaxLength(50).IsRequired();
            b.Property(x => x.LastHash).HasMaxLength(100);
            b.HasIndex(TenantIdProperty, nameof(Domain.EInvoicingSettings.CompanyId)).IsUnique();
        });

        modelBuilder.Entity<EInvoiceSubmission>(b =>
        {
            b.ToTable("EInvoiceSubmission");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SourceModule).HasMaxLength(30).IsRequired();
            b.Property(x => x.SourceDocType).HasMaxLength(50).IsRequired();
            b.Property(x => x.DocumentNumber).HasMaxLength(50).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Hash).HasMaxLength(100);
            b.Property(x => x.PreviousHash).HasMaxLength(100);
            b.Property(x => x.Provider).HasMaxLength(50);
            b.Property(x => x.ProviderReference).HasMaxLength(200);
            b.Property(x => x.QrCode).HasMaxLength(2000);
            b.Property(x => x.Errors).HasMaxLength(4000);
            b.HasIndex(TenantIdProperty, nameof(EInvoiceSubmission.SourceDocType), nameof(EInvoiceSubmission.SourceDocId)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(EInvoiceSubmission.CompanyId), nameof(EInvoiceSubmission.Status));
        });

        modelBuilder.Entity<Currency>(b =>
        {
            b.ToTable("Currency");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.CashRoundingIncrement).HasPrecision(19, 4);
            b.HasIndex(TenantIdProperty, nameof(Currency.Code)).IsUnique();
        });

        modelBuilder.Entity<ExchangeRate>(b =>
        {
            b.ToTable("ExchangeRate");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.FromCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.ToCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Rate).HasPrecision(18, 8);
            b.HasIndex(TenantIdProperty, nameof(ExchangeRate.FromCurrency), nameof(ExchangeRate.ToCurrency), nameof(ExchangeRate.Type), nameof(ExchangeRate.EffectiveDate)).IsUnique();
        });

        modelBuilder.Entity<TaxCode>(b =>
        {
            b.ToTable("TaxCode");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ReportingBox).HasMaxLength(50);
            b.Property(x => x.Jurisdiction).HasMaxLength(50);
            b.HasIndex(TenantIdProperty, nameof(TaxCode.CompanyId), nameof(TaxCode.Code)).IsUnique();
            b.HasMany(x => x.Rates).WithOne().HasForeignKey(x => x.TaxCodeId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Rates).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<TaxRate>(b =>
        {
            b.ToTable("TaxRate");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Rate).HasPrecision(9, 6);
        });

        modelBuilder.Entity<TaxGroup>(b =>
        {
            b.ToTable("TaxGroup");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(TaxGroup.CompanyId), nameof(TaxGroup.Code)).IsUnique();
            b.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.TaxGroupId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Components).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_components");
        });

        modelBuilder.Entity<TaxGroupComponent>(b =>
        {
            b.ToTable("TaxGroupComponent");
            b.HasKey(x => new { x.TaxGroupId, x.Sequence });
            b.HasOne<TaxCode>().WithMany().HasForeignKey(x => x.TaxCodeId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<TaxCategoryMapping>(b =>
        {
            b.ToTable("TaxCategoryMapping");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CategoryCode).HasMaxLength(30).IsRequired();
            b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10);
            b.HasIndex(TenantIdProperty, nameof(TaxCategoryMapping.CompanyId), nameof(TaxCategoryMapping.CategoryCode), nameof(TaxCategoryMapping.Direction)).IsUnique();
            b.HasOne<TaxGroup>().WithMany().HasForeignKey(x => x.TaxGroupId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class TaxDbContextFactory : IDesignTimeDbContextFactory<TaxDbContext>
{
    public TaxDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TaxDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", TaxDbContext.SchemaName))
        .Options);
}
