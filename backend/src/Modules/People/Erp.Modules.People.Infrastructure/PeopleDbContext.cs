using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Infrastructure.Persistence;
using Erp.Modules.People.Application;
using Erp.Modules.People.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.People.Infrastructure;

public sealed class PeopleDbContext(DbContextOptions<PeopleDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IPeopleDbContext
{
    public const string SchemaName = "people";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public override string Schema => SchemaName;

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<CommissionPlan> CommissionPlans => Set<CommissionPlan>();

    public DbSet<CommissionRun> CommissionRuns => Set<CommissionRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Tiers, category rates, run lines and details are JSON columns (ADR-030), never entity types.
        modelBuilder.Ignore<CommissionTier>();
        modelBuilder.Ignore<CategoryRate>();
        modelBuilder.Ignore<CommissionRunLine>();
        modelBuilder.Ignore<CommissionDetail>();

        modelBuilder.Entity<Employee>(b =>
        {
            b.ToTable("Employee");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Department).HasMaxLength(100);
            b.Property(x => x.JobTitle).HasMaxLength(100);
            b.HasIndex(TenantIdProperty, nameof(Employee.CompanyId), nameof(Employee.Code)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(Employee.CompanyId), nameof(Employee.UserId)).IsUnique().HasFilter("[UserId] IS NOT NULL");
            b.HasOne<CommissionPlan>().WithMany().HasForeignKey(x => x.CommissionPlanId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<CommissionPlan>(b =>
        {
            b.ToTable("CommissionPlan");
            Key(b);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Basis).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.RatePercent).HasPrecision(9, 6);
            JsonList(b.Property(x => x.Tiers));
            JsonList(b.Property(x => x.CategoryRates));
            b.HasIndex(TenantIdProperty, nameof(CommissionPlan.CompanyId), nameof(CommissionPlan.Code)).IsUnique();
        });

        modelBuilder.Entity<CommissionRun>(b =>
        {
            b.ToTable("CommissionRun");
            Key(b);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Number).HasMaxLength(60);
            b.Property(x => x.Total).HasPrecision(19, 4);
            b.Property(x => x.CalculatedAtUtc).HasPrecision(3);
            b.Property(x => x.ApprovedAtUtc).HasPrecision(3);
            b.Property(x => x.PostedAtUtc).HasPrecision(3);
            JsonList(b.Property(x => x.Lines));
            JsonList(b.Property(x => x.Details));
            JsonList(b.Property(x => x.SkippedSalespersons));

            // One active run per legal entity and month; cancelled and reversed runs free the month.
            b.HasIndex(TenantIdProperty, nameof(CommissionRun.LegalEntityId), nameof(CommissionRun.PeriodFrom)).IsUnique()
                .HasFilter("[Status] IN ('Draft', 'Approved', 'Posted')");
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
public sealed class PeopleDbContextFactory : IDesignTimeDbContextFactory<PeopleDbContext>
{
    public PeopleDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PeopleDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PeopleDbContext.SchemaName))
        .Options);
}
