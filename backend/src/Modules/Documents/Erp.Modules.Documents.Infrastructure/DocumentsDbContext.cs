using Erp.Infrastructure.Persistence;
using Erp.Modules.Documents.Application;
using Erp.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Documents.Infrastructure;

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IDocumentsDbContext
{
    public const string SchemaName = "documents";

    public override string Schema => SchemaName;

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Attachment>(b =>
        {
            b.ToTable("Attachment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.OwnerType).HasMaxLength(100).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(Attachment.MaxFileNameLength).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
            b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
            b.Property(x => x.StorageKey).HasMaxLength(200).IsUnicode(false).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500);
            b.HasIndex(TenantIdProperty, nameof(Attachment.CompanyId), nameof(Attachment.OwnerType), nameof(Attachment.OwnerId));
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("Notification");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Category).HasMaxLength(100).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Body).HasMaxLength(2000);
            b.Property(x => x.Link).HasMaxLength(500);
            b.Ignore(x => x.IsRead);
            b.HasIndex(TenantIdProperty, nameof(Notification.UserId), nameof(Notification.ReadAtUtc), nameof(Notification.CreatedAtUtc));
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class DocumentsDbContextFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DocumentsDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", DocumentsDbContext.SchemaName))
        .Options);
}
