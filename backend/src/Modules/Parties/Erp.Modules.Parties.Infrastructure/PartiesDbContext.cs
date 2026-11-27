using Erp.Infrastructure.Persistence;
using Erp.Modules.Parties.Application;
using Erp.Modules.Parties.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Parties.Infrastructure;

public sealed class PartiesDbContext(DbContextOptions<PartiesDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IPartiesDbContext
{
    public const string SchemaName = "crm";

    public override string Schema => SchemaName;

    public DbSet<PaymentTerms> PaymentTerms => Set<PaymentTerms>();

    public DbSet<CustomerGroup> CustomerGroups => Set<CustomerGroup>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentTerms>(b =>
        {
            b.ToTable("PaymentTerms");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(Domain.PaymentTerms.CompanyId), nameof(Domain.PaymentTerms.Code)).IsUnique();

            // Installments are part of the terms (never queried alone): stored as a JSON column on the same row,
            // which keeps them under the row's tenant security and rowversion.
            b.OwnsMany(x => x.Installments, i =>
            {
                i.ToJson("Installments");
                i.Property(x => x.Percent).HasPrecision(9, 6);
                i.Property(x => x.Basis).HasConversion<string>();
            });
            b.Navigation(x => x.Installments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomerGroup>(b =>
        {
            b.ToTable("CustomerGroup");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.PostingGroup).HasMaxLength(30).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(CustomerGroup.CompanyId), nameof(CustomerGroup.Code)).IsUnique();
            b.HasOne<PaymentTerms>().WithMany().HasForeignKey(x => x.DefaultPaymentTermsId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("Customer");
            ConfigureParty(b);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreditLimit).HasPrecision(19, 4);
            b.Property(x => x.CreditAction).HasConversion<string>().HasMaxLength(20);
            b.HasOne<CustomerGroup>().WithMany().HasForeignKey(x => x.CustomerGroupId).OnDelete(DeleteBehavior.NoAction);

            // At most one walk-in customer per company, guaranteed by the database.
            b.HasIndex(TenantIdProperty, nameof(Customer.CompanyId)).IsUnique().HasFilter("[IsWalkIn] = 1").HasDatabaseName("UX_Customer_WalkIn");
        });

        modelBuilder.Entity<Supplier>(b =>
        {
            b.ToTable("Supplier");
            ConfigureParty(b);
        });
    }

    private static void ConfigureParty<TParty>(EntityTypeBuilder<TParty> b)
        where TParty : Party
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
        b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
        b.Property(x => x.TaxRegistrationNo).HasMaxLength(50);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.PostingGroup).HasMaxLength(30).IsRequired();
        b.HasIndex(TenantIdProperty, nameof(Party.CompanyId), nameof(Party.Code)).IsUnique();
        b.HasIndex(TenantIdProperty, nameof(Party.CompanyId), nameof(Party.NameEn));
        b.HasOne<PaymentTerms>().WithMany().HasForeignKey(x => x.PaymentTermsId).OnDelete(DeleteBehavior.NoAction);

        b.OwnsMany(x => x.Contacts, c =>
        {
            c.ToJson("Contacts");
            c.Property(x => x.Uid);
        });
        b.OwnsMany(x => x.Addresses, a =>
        {
            a.ToJson("Addresses");
            a.Property(x => x.Uid);
            a.Property(x => x.Type).HasConversion<string>();
        });
        b.Navigation(x => x.Contacts).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Addresses).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class PartiesDbContextFactory : IDesignTimeDbContextFactory<PartiesDbContext>
{
    public PartiesDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PartiesDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PartiesDbContext.SchemaName))
        .Options);
}
