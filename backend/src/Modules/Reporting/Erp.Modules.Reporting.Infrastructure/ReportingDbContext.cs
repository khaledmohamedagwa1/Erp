using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Reporting.Application;
using Erp.Modules.Reporting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Reporting.Infrastructure;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IReportingDbContext
{
    public const string SchemaName = "reporting";

    public override string Schema => SchemaName;

    public DbSet<SavedReportView> SavedViews => Set<SavedReportView>();

    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    public DbSet<ReportSchedule> Schedules => Set<ReportSchedule>();

    public DbSet<ReportScheduleRun> ScheduleRuns => Set<ReportScheduleRun>();

    public DbSet<DailySalesSummary> DailySalesSummaries => Set<DailySalesSummary>();

    public DbSet<StockValueSnapshot> StockValueSnapshots => Set<StockValueSnapshot>();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

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

        modelBuilder.Ignore<DashboardWidget>();
        modelBuilder.Entity<Dashboard>(b =>
        {
            b.ToTable("Dashboard");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500);
            JsonValue(b.Property(x => x.SharedWithRoleIds));
            JsonValue(b.Property(x => x.Widgets));
            b.HasIndex(TenantIdProperty, nameof(Dashboard.CompanyId), nameof(Dashboard.OwnerUserId));
        });

        modelBuilder.Entity<ReportSchedule>(b =>
        {
            b.ToTable("ReportSchedule");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.ReportKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.Format).HasMaxLength(10).IsRequired();
            b.Property(x => x.Language).HasMaxLength(5).IsRequired();
            b.Property(x => x.Cron).HasMaxLength(100).IsRequired();
            b.Property(x => x.LastStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.NextRunAtUtc).HasPrecision(3);
            b.Property(x => x.LastRunAtUtc).HasPrecision(3);
            JsonValue(b.Property(x => x.Parameters));
            JsonValue(b.Property(x => x.Recipients));
            b.HasIndex(TenantIdProperty, nameof(ReportSchedule.IsActive), nameof(ReportSchedule.NextRunAtUtc));
            b.HasIndex(TenantIdProperty, nameof(ReportSchedule.CompanyId), nameof(ReportSchedule.OwnerUserId));
        });

        modelBuilder.Entity<ReportScheduleRun>(b =>
        {
            b.ToTable("ReportScheduleRun");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Trigger).HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Error).HasMaxLength(1000);
            b.Property(x => x.FileName).HasMaxLength(200);
            b.Property(x => x.ContentType).HasMaxLength(200);
            b.Property(x => x.StartedAtUtc).HasPrecision(3);
            b.Property(x => x.FinishedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(ReportScheduleRun.ScheduleId), nameof(ReportScheduleRun.StartedAtUtc));
            b.HasOne<ReportSchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<DailySalesSummary>(b =>
        {
            b.ToTable("DailySalesSummary");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            foreach (var money in new[]
                     {
                         nameof(DailySalesSummary.GrossSales), nameof(DailySalesSummary.Returns), nameof(DailySalesSummary.NetRevenue), nameof(DailySalesSummary.Tax),
                         nameof(DailySalesSummary.Cogs),
                     })
            {
                b.Property<decimal>(money).HasPrecision(19, 4);
            }

            b.Property(x => x.UpdatedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(DailySalesSummary.CompanyId), nameof(DailySalesSummary.Date), nameof(DailySalesSummary.BranchKey)).IsUnique();
        });

        modelBuilder.Entity<StockValueSnapshot>(b =>
        {
            b.ToTable("StockValueSnapshot");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Quantity).HasPrecision(19, 4);
            b.Property(x => x.Value).HasPrecision(19, 4);
            b.Property(x => x.UpdatedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(StockValueSnapshot.CompanyId), nameof(StockValueSnapshot.Date), nameof(StockValueSnapshot.WarehouseId)).IsUnique();
        });
    }

    /// <summary>Small collections owned by the row are JSON columns; the comparer snapshots a deep copy so in-place edits are detected.</summary>
    private static void JsonValue<T>(PropertyBuilder<T> property)
        where T : class, new()
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<T>(v, Json) ?? new T(),
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!))
            .HasColumnType("nvarchar(max)")
            .IsRequired();
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class ReportingDbContextFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ReportingDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ReportingDbContext.SchemaName))
        .Options);
}
