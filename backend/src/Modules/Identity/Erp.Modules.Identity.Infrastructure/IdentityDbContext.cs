using Erp.Infrastructure.Persistence;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Identity.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IIdentityDbContext
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<ExternalIdentityProvider> IdentityProviders => Set<ExternalIdentityProvider>();

    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();

    public DbSet<PlatformAuditEntry> PlatformAudit => Set<PlatformAuditEntry>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("User");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            b.Property(x => x.LockoutEndUtc).HasPrecision(3);
            b.Property(x => x.LastLoginAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(User.NormalizedEmail)).IsUnique();
            b.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.CompanyAccess).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(x => x.CompanyAccess).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<UserRole>(b =>
        {
            b.ToTable("UserRole");
            b.HasKey(x => new { x.UserId, x.RoleId });
            b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<UserCompanyAccess>(b =>
        {
            b.ToTable("UserCompanyAccess");
            b.HasKey(x => new { x.UserId, x.CompanyId });
            b.PrimitiveCollection(x => x.BranchIds);
            b.PrimitiveCollection(x => x.WarehouseIds);
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("Role");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.PrimitiveCollection(x => x.Permissions);
            b.HasIndex(TenantIdProperty, nameof(Role.Name)).IsUnique();
        });

        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable("UserSession");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.StartedAtUtc).HasPrecision(3);
            b.Property(x => x.AbsoluteExpiresAtUtc).HasPrecision(3);
            b.Property(x => x.RevokedAtUtc).HasPrecision(3);
            b.Property(x => x.RevokedReason).HasMaxLength(50);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.Property(x => x.LastMfaAtUtc).HasPrecision(3);
            b.Property(x => x.AuthenticationMethod).HasMaxLength(60).HasDefaultValue("pwd");
            b.HasIndex(x => x.UserId);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            b.HasMany(x => x.Tokens).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Tokens).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ExternalIdentityProvider>(b =>
        {
            b.ToTable("ExternalIdentityProvider");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Key).HasMaxLength(40).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            b.Property(x => x.Authority).HasMaxLength(300).IsRequired();
            b.Property(x => x.ClientId).HasMaxLength(200).IsRequired();
            b.Property(x => x.ClientSecretName).HasMaxLength(100);
            b.Property(x => x.Scopes).HasMaxLength(300).IsRequired();
            b.Property(x => x.EmailClaim).HasMaxLength(100).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(ExternalIdentityProvider.Key)).IsUnique();
        });

        modelBuilder.Entity<PlatformOperator>(b =>
        {
            b.ToTable("PlatformOperator");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            b.Property(x => x.MfaSecretProtected).HasMaxLength(1000).IsRequired();
            b.Property(x => x.LockoutEndUtc).HasPrecision(3);
            b.Property(x => x.LastLoginAtUtc).HasPrecision(3);
            b.HasIndex(x => x.NormalizedEmail).IsUnique();

            // Only one operator can ever be created with the static bootstrap key, even under concurrent requests.
            b.HasIndex(x => x.IsBootstrap).IsUnique().HasFilter("[IsBootstrap] = 1");
        });

        modelBuilder.Entity<PlatformAuditEntry>(b =>
        {
            b.ToTable("PlatformAuditEntry");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityColumn();
            b.Property(x => x.AtUtc).HasPrecision(3);
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.Details).HasMaxLength(2000);
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.HasIndex(x => x.AtUtc);
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.ToTable("RefreshToken");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.CreatedAtUtc).HasPrecision(3);
            b.Property(x => x.ExpiresAtUtc).HasPrecision(3);
            b.Property(x => x.UsedAtUtc).HasPrecision(3);
            b.HasIndex(x => x.TokenHash).IsUnique();
        });
    }
}

public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<IdentityDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.SchemaName))
        .Options);
}
