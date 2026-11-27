using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Import.Application;
using Erp.Modules.Import.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Import.Infrastructure;

public sealed class ImportDbContext(DbContextOptions<ImportDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IImportDbContext
{
    public const string SchemaName = "import";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public override string Schema => SchemaName;

    public DbSet<ImportJob> Jobs => Set<ImportJob>();

    public DbSet<ImportJobFile> Files => Set<ImportJobFile>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Row errors are a JSON column (ADR-030), never an entity type.
        modelBuilder.Ignore<ImportRowError>();

        modelBuilder.Entity<ImportJob>(b =>
        {
            b.ToTable("ImportJob");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Format).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Errors).HasConversion(
                    v => JsonSerializer.Serialize(v, Json),
                    v => JsonSerializer.Deserialize<List<ImportRowError>>(v, Json) ?? new List<ImportRowError>(),
                    new ValueComparer<List<ImportRowError>>(
                        (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
                        v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                        v => JsonSerializer.Deserialize<List<ImportRowError>>(JsonSerializer.Serialize(v, Json), Json)!))
                .HasColumnType("nvarchar(max)")
                .IsRequired();
            b.HasIndex(TenantIdProperty, nameof(ImportJob.CompanyId), nameof(ImportJob.UploadedAtUtc));
        });

        modelBuilder.Entity<ImportJobFile>(b =>
        {
            b.ToTable("ImportJobFile");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Content).HasColumnType("varbinary(max)").IsRequired();
            b.HasOne<ImportJob>().WithOne().HasForeignKey<ImportJobFile>(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class ImportDbContextFactory : IDesignTimeDbContextFactory<ImportDbContext>
{
    public ImportDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ImportDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ImportDbContext.SchemaName))
        .Options);
}
