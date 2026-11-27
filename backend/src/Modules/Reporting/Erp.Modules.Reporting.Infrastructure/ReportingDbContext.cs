using Erp.Infrastructure.Persistence;
using Erp.Modules.Reporting.Application;
using Erp.Modules.Reporting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Reporting.Infrastructure;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IReportingDbContext
{
    public const string SchemaName = "reporting";

    public override string Schema => SchemaName;

    public DbSet<SavedReportView> SavedViews => Set<SavedReportView>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SavedReportView>(b =>
        {
            b.ToTable("SavedReportView");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.ReportKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.FiltersJson).HasMaxLength(SavedReportView.MaxFiltersLength).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(SavedReportView.CompanyId), nameof(SavedReportView.UserId), nameof(SavedReportView.ReportKey), nameof(SavedReportView.Name))
                .IsUnique();
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class ReportingDbContextFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ReportingDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ReportingDbContext.SchemaName))
        .Options);
}
