using Erp.Infrastructure.Persistence;
using Erp.Modules.Configuration.Application;
using Erp.Modules.Configuration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Configuration.Infrastructure;

public sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IConfigurationDbContext
{
    public const string SchemaName = "configuration";

    public override string Schema => SchemaName;

    public DbSet<CompanyModule> CompanyModules => Set<CompanyModule>();

    public DbSet<CompanyPolicy> CompanyPolicies => Set<CompanyPolicy>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CompanyModule>(b =>
        {
            b.ToTable("CompanyModule");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ModuleCode).HasMaxLength(50).IsRequired();
            b.Property(x => x.ChangedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(CompanyModule.CompanyId), nameof(CompanyModule.ModuleCode)).IsUnique();
        });

        modelBuilder.Entity<CompanyPolicy>(b =>
        {
            b.ToTable("CompanyPolicy");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Section).HasMaxLength(50).IsRequired();
            b.Property(x => x.Json).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(CompanyPolicy.CompanyId), nameof(CompanyPolicy.Section)).IsUnique();
        });
    }
}

public sealed class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConfigurationDbContext>
{
    public ConfigurationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ConfigurationDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ConfigurationDbContext.SchemaName))
        .Options);
}
