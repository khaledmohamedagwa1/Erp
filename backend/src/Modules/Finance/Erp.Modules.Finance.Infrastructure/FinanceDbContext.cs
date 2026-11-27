using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Application;
using Erp.Modules.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Finance.Infrastructure;

public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IFinanceDbContext
{
    public const string SchemaName = "finance";

    public override string Schema => SchemaName;

    public DbSet<OpenItem> OpenItems => Set<OpenItem>();

    public DbSet<Allocation> Allocations => Set<Allocation>();

    public DbSet<CashBankAccount> CashBankAccounts => Set<CashBankAccount>();

    public DbSet<Settlement> Settlements => Set<Settlement>();

    public DbSet<InternalTransfer> InternalTransfers => Set<InternalTransfer>();

    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<CashMovement> CashMovements => Set<CashMovement>();

    public DbSet<BankReconciliation> BankReconciliations => Set<BankReconciliation>();

    public DbSet<FxRevaluationRun> FxRevaluationRuns => Set<FxRevaluationRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OpenItem>(b =>
        {
            b.ToTable("OpenItem");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PartyType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PartyPostingGroup).HasMaxLength(30).IsRequired();
            b.Property(x => x.SourceModule).HasMaxLength(30).IsRequired();
            b.Property(x => x.SourceDocType).HasMaxLength(50).IsRequired();
            b.Property(x => x.DocumentNumber).HasMaxLength(50).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(18, 8);
            b.Property(x => x.OriginalAmount).HasPrecision(19, 4);
            b.Property(x => x.OpenAmount).HasPrecision(19, 4);
            b.Property(x => x.OriginalBase).HasPrecision(19, 4);
            b.Property(x => x.OpenBase).HasPrecision(19, 4);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.IsCredit);
            b.HasIndex(TenantIdProperty, nameof(OpenItem.SourceDocType), nameof(OpenItem.SourceDocId), nameof(OpenItem.Installment)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(OpenItem.CompanyId), nameof(OpenItem.PartyType), nameof(OpenItem.PartyId), nameof(OpenItem.Status));
            b.HasIndex(TenantIdProperty, nameof(OpenItem.LegalEntityId), nameof(OpenItem.DueDate));
        });

        modelBuilder.Entity<Allocation>(b =>
        {
            b.ToTable("Allocation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PartyType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SettlementKind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SettlementReference).HasMaxLength(50).IsRequired();
            b.Property(x => x.Amount).HasPrecision(19, 4);
            b.Property(x => x.BaseRelieved).HasPrecision(19, 4);
            b.Property(x => x.SettlementBase).HasPrecision(19, 4);
            b.Property(x => x.RealizedFxBase).HasPrecision(19, 4);
            b.HasIndex(TenantIdProperty, nameof(Allocation.OpenItemId));
            b.HasIndex(TenantIdProperty, nameof(Allocation.SettlementId));
            b.HasOne<OpenItem>().WithMany().HasForeignKey(x => x.OpenItemId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<CashBankAccount>(b =>
        {
            b.ToTable("CashBankAccount");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.BankName).HasMaxLength(200);
            b.Property(x => x.AccountNumber).HasMaxLength(50);
            b.Property(x => x.Iban).HasMaxLength(34);
            b.Ignore(x => x.Role);
            b.HasIndex(TenantIdProperty, nameof(CashBankAccount.CompanyId), nameof(CashBankAccount.Code)).IsUnique();
        });

        modelBuilder.Entity<Settlement>(b =>
        {
            b.ToTable("Settlement");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Number).HasMaxLength(50);
            b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(18, 8);
            b.Property(x => x.Amount).HasPrecision(19, 4);
            b.Property(x => x.WithholdingBase).HasPrecision(19, 4);
            b.Property(x => x.WithholdingAmount).HasPrecision(19, 4);
            b.Property(x => x.UnallocatedAmount).HasPrecision(19, 4);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            b.Ignore(x => x.PartyType);
            b.Ignore(x => x.SettlementValue);
            b.OwnsMany(x => x.Allocations, a =>
            {
                a.ToJson("RequestedAllocations");
                a.Property(x => x.Amount).HasPrecision(19, 4);
            });
            b.Navigation(x => x.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasIndex(TenantIdProperty, nameof(Settlement.CompanyId), nameof(Settlement.Direction), nameof(Settlement.PartyId));
            b.HasOne<CashBankAccount>().WithMany().HasForeignKey(x => x.CashBankAccountId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<InternalTransfer>(b =>
        {
            b.ToTable("InternalTransfer");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Number).HasMaxLength(50);
            b.Property(x => x.FromAmount).HasPrecision(19, 4);
            b.Property(x => x.ToAmount).HasPrecision(19, 4);
            b.Property(x => x.Charges).HasPrecision(19, 4);
            b.Property(x => x.FromRate).HasPrecision(18, 8);
            b.Property(x => x.ToRate).HasPrecision(18, 8);
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            b.HasOne<CashBankAccount>().WithMany().HasForeignKey(x => x.FromAccountId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<CashBankAccount>().WithMany().HasForeignKey(x => x.ToAccountId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Expense>(b =>
        {
            b.ToTable("Expense");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Number).HasMaxLength(50);
            b.Property(x => x.Payee).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(18, 8);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            b.Ignore(x => x.Total);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToJson("Lines");
                l.Property(x => x.Uid);
                l.Property(x => x.NetAmount).HasPrecision(19, 4);
                l.Property(x => x.RecoverableTax).HasPrecision(19, 4);
                l.Property(x => x.NonRecoverableTax).HasPrecision(19, 4);
                l.Ignore(x => x.Gross);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CashMovement>(b =>
        {
            b.ToTable("CashMovement");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Amount).HasPrecision(19, 4);
            b.Property(x => x.BaseAmount).HasPrecision(19, 4);
            b.Property(x => x.SourceDocType).HasMaxLength(50).IsRequired();
            b.Property(x => x.Reference).HasMaxLength(100).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500);
            b.HasIndex(TenantIdProperty, nameof(CashMovement.CashBankAccountId), nameof(CashMovement.Date));
            b.HasOne<CashBankAccount>().WithMany().HasForeignKey(x => x.CashBankAccountId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<BankReconciliation>(b =>
        {
            b.ToTable("BankReconciliation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.StatementBalance).HasPrecision(19, 4);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToJson("Lines");
                l.Property(x => x.Uid);
                l.Property(x => x.Amount).HasPrecision(19, 4);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasOne<CashBankAccount>().WithMany().HasForeignKey(x => x.CashBankAccountId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<FxRevaluationRun>(b =>
        {
            b.ToTable("FxRevaluationRun");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TotalGain).HasPrecision(19, 4);
            b.Property(x => x.TotalLoss).HasPrecision(19, 4);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToJson("Lines");
                l.Property(x => x.OpenAmount).HasPrecision(19, 4);
                l.Property(x => x.HistoricalBase).HasPrecision(19, 4);
                l.Property(x => x.RevaluedBase).HasPrecision(19, 4);
                l.Property(x => x.Difference).HasPrecision(19, 4);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasIndex(TenantIdProperty, nameof(FxRevaluationRun.LegalEntityId), nameof(FxRevaluationRun.AsOf)).IsUnique();
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class FinanceDbContextFactory : IDesignTimeDbContextFactory<FinanceDbContext>
{
    public FinanceDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<FinanceDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", FinanceDbContext.SchemaName))
        .Options);
}
