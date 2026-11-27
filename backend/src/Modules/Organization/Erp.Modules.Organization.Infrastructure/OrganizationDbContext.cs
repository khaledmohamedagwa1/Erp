using Erp.Infrastructure.Persistence;
using Erp.Modules.Organization.Application;
using Erp.Modules.Organization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Organization.Infrastructure;

public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IOrganizationDbContext
{
    public const string SchemaName = "organization";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenant");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.IsolationMode).HasMaxLength(20).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Company>(b =>
        {
            b.ToTable("Company");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.BaseCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.BusinessPreset).HasMaxLength(30).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(Company.Code)).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(TenantIdProperty).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<LegalEntity>(b =>
        {
            b.ToTable("LegalEntity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.FunctionalCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.TaxRegistrationNo).HasMaxLength(50);
            b.HasIndex(TenantIdProperty, nameof(LegalEntity.CompanyId));
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Branch>(b =>
        {
            b.ToTable("Branch");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(Branch.CompanyId), nameof(Branch.Code)).IsUnique();
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => x.LegalEntityId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Warehouse>(b =>
        {
            b.ToTable("Warehouse");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(TenantIdProperty, nameof(Warehouse.CompanyId), nameof(Warehouse.Code)).IsUnique();
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => x.LegalEntityId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class OrganizationDbContextFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OrganizationDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", OrganizationDbContext.SchemaName))
        .Options);
}
