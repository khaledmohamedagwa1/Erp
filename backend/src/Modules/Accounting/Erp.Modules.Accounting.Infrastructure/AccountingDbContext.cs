using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Accounting.Application;
using Erp.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Accounting.Infrastructure;

public sealed class AccountingDbContext(DbContextOptions<AccountingDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IAccountingDbContext
{
    public const string SchemaName = "accounting";

    public override string Schema => SchemaName;

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<CostCenter> CostCenters => Set<CostCenter>();

    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();

    public DbSet<JournalEntry> Journals => Set<JournalEntry>();

    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    public DbSet<AccountDetermination> PostingRules => Set<AccountDetermination>();

    public DbSet<AccountBalance> AccountBalances => Set<AccountBalance>();

    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();

    /// <summary>Database triggers that make posted journals immutable (see migration PostedJournalImmutability).</summary>
    public const string JournalEntryTrigger = "TR_JournalEntry_Immutable";
    public const string JournalLineTrigger = "TR_JournalLine_Immutable";

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(b =>
        {
            b.ToTable("Account");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(20).IsRequired();
            b.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ControlSubledger).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CashFlowCategory).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ReportGroup).HasConversion<string>().HasMaxLength(30);
            b.Ignore(x => x.IsControl);
            b.Ignore(x => x.IsBalanceSheet);
            b.Ignore(x => x.NormalBalance);
            b.HasIndex(TenantIdProperty, nameof(Account.CompanyId), nameof(Account.Code)).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<CostCenter>(b =>
        {
            b.ToTable("CostCenter");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(20).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(CostCenter.CompanyId), nameof(CostCenter.Code)).IsUnique();
            b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<FiscalYear>(b =>
        {
            b.ToTable("FiscalYear");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(20).IsRequired();
            b.Ignore(x => x.NumberingYear);
            b.Ignore(x => x.LastPeriod);
            b.Property(x => x.ClosedAtUtc).HasPrecision(3);
            b.HasIndex(TenantIdProperty, nameof(FiscalYear.LegalEntityId), nameof(FiscalYear.StartDate)).IsUnique();
            b.HasMany(x => x.Periods).WithOne().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Periods).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<FiscalPeriod>(b =>
        {
            b.ToTable("FiscalPeriod");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.FiscalYearId, x.PeriodNo }).IsUnique();
        });

        modelBuilder.Entity<JournalEntry>(b =>
        {
            b.ToTable("JournalEntry");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Number).HasMaxLength(40);
            b.Property(x => x.SourceModule).HasMaxLength(50).IsRequired();
            b.Property(x => x.SourceDocType).HasMaxLength(50).IsRequired();
            b.Property(x => x.Purpose).HasMaxLength(60).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.ExchangeRate).HasPrecision(18, 8);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PostedAtUtc).HasPrecision(3);
            b.Ignore(x => x.TotalDebit);
            b.Ignore(x => x.TotalCredit);
            b.Ignore(x => x.TotalBaseDebit);
            b.Ignore(x => x.TotalBaseCredit);
            b.Ignore(x => x.BaseImbalance);

            // Tell EF the table has triggers so it does not use OUTPUT clauses (incompatible with triggers).
            b.ToTable(t => t.HasTrigger(JournalEntryTrigger));
            // One journal per source document and purpose: makes module postings idempotent.
            b.HasIndex(TenantIdProperty, nameof(JournalEntry.LegalEntityId), nameof(JournalEntry.SourceDocType), nameof(JournalEntry.SourceDocId), nameof(JournalEntry.Purpose)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(JournalEntry.LegalEntityId), nameof(JournalEntry.Number)).IsUnique().HasFilter("[Number] IS NOT NULL");
            b.HasIndex(TenantIdProperty, nameof(JournalEntry.LegalEntityId), nameof(JournalEntry.JournalDate));
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<JournalLine>(b =>
        {
            b.ToTable("JournalLine");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Debit).HasPrecision(19, 4);
            b.Property(x => x.Credit).HasPrecision(19, 4);
            b.Property(x => x.BaseDebit).HasPrecision(19, 4);
            b.Property(x => x.BaseCredit).HasPrecision(19, 4);
            b.Property(x => x.PartyType).HasMaxLength(30);
            b.Property(x => x.Description).HasMaxLength(300);
            b.Property(x => x.PostingRole).HasMaxLength(60);
            b.Property(x => x.TaxableBase).HasPrecision(19, 4);
            b.HasIndex(x => x.AccountId);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.NoAction);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_JournalLine_NonNegative", "[Debit] >= 0 AND [Credit] >= 0 AND [BaseDebit] >= 0 AND [BaseCredit] >= 0");
                t.HasTrigger(JournalLineTrigger);
            });
        });

        modelBuilder.Entity<OpeningBalance>(b =>
        {
            b.ToTable("OpeningBalance");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.TotalDebit);
            b.Ignore(x => x.TotalCredit);
            b.Ignore(x => x.EquityDifference);
            // At most one posted opening per legal entity, enforced by the database too (concurrent posts).
            b.HasIndex(TenantIdProperty, nameof(OpeningBalance.LegalEntityId)).IsUnique().HasFilter("[Status] = 'Posted'");
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OpeningBalanceId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpeningBalanceLine>(b =>
        {
            b.ToTable("OpeningBalanceLine");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Debit).HasPrecision(19, 4);
            b.Property(x => x.Credit).HasPrecision(19, 4);
            b.Property(x => x.Description).HasMaxLength(300);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AccountDetermination>(b =>
        {
            b.ToTable("AccountDetermination");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Role).HasMaxLength(60).IsRequired();
            b.Property(x => x.ProductPostingGroup).HasMaxLength(30);
            b.Property(x => x.PartyPostingGroup).HasMaxLength(30);
            b.Ignore(x => x.Specificity);
            b.HasIndex(TenantIdProperty, nameof(AccountDetermination.CompanyId), nameof(AccountDetermination.Role));
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AccountBalance>(b =>
        {
            b.ToTable("AccountBalance");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.BaseDebit).HasPrecision(19, 4);
            b.Property(x => x.BaseCredit).HasPrecision(19, 4);
            b.Property(x => x.TxDebit).HasPrecision(19, 4);
            b.Property(x => x.TxCredit).HasPrecision(19, 4);
            b.HasIndex(TenantIdProperty, nameof(AccountBalance.LegalEntityId), nameof(AccountBalance.AccountId), nameof(AccountBalance.FiscalPeriodId),
                nameof(AccountBalance.CostCenterId), nameof(AccountBalance.Currency)).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}

/// <summary>Upserts running balances with an update lock held to the end of the posting transaction (no lost updates).</summary>
public sealed class AccountBalanceWriter(AccountingDbContext db, ITenantContext tenant) : IAccountBalanceWriter
{
    public async Task ApplyAsync(JournalEntry journal, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant context required.");
        var periodId = journal.FiscalPeriodId ?? throw new InvalidOperationException("Only posted journals update balances.");
        var groups = journal.Lines
            .GroupBy(l => new { l.AccountId, CostCenterId = l.CostCenterId ?? Guid.Empty })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.CostCenterId,
                BaseDebit = g.Sum(l => l.BaseDebit),
                BaseCredit = g.Sum(l => l.BaseCredit),
                TxDebit = g.Sum(l => l.Debit),
                TxCredit = g.Sum(l => l.Credit),
            })
            .OrderBy(g => g.AccountId)
            .ThenBy(g => g.CostCenterId); // deterministic lock order avoids deadlocks between concurrent postings

        foreach (var g in groups)
        {
            // Row locks only (no HOLDLOCK range locks): an existing row is updated under an exclusive lock held to commit;
            // a missing row is inserted, and if a concurrent posting inserted it first the unique index makes us wait,
            // then fail with a duplicate key, and we update instead. This avoids key-range deadlocks between postings.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE accounting.AccountBalance WITH (UPDLOCK, ROWLOCK)
                   SET BaseDebit = BaseDebit + {g.BaseDebit}, BaseCredit = BaseCredit + {g.BaseCredit},
                       TxDebit = TxDebit + {g.TxDebit}, TxCredit = TxCredit + {g.TxCredit}
                 WHERE TenantId = {tenantId} AND LegalEntityId = {journal.LegalEntityId} AND AccountId = {g.AccountId}
                   AND FiscalPeriodId = {periodId} AND CostCenterId = {g.CostCenterId} AND Currency = {journal.Currency};
                IF @@ROWCOUNT = 0
                BEGIN
                    BEGIN TRY
                        INSERT INTO accounting.AccountBalance (Id, TenantId, CompanyId, LegalEntityId, AccountId, FiscalPeriodId, CostCenterId, Currency, BaseDebit, BaseCredit, TxDebit, TxCredit)
                        VALUES (NEWID(), {tenantId}, {journal.CompanyId}, {journal.LegalEntityId}, {g.AccountId}, {periodId}, {g.CostCenterId}, {journal.Currency},
                                {g.BaseDebit}, {g.BaseCredit}, {g.TxDebit}, {g.TxCredit});
                    END TRY
                    BEGIN CATCH
                        IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
                        UPDATE accounting.AccountBalance WITH (UPDLOCK, ROWLOCK)
                           SET BaseDebit = BaseDebit + {g.BaseDebit}, BaseCredit = BaseCredit + {g.BaseCredit},
                               TxDebit = TxDebit + {g.TxDebit}, TxCredit = TxCredit + {g.TxCredit}
                         WHERE TenantId = {tenantId} AND LegalEntityId = {journal.LegalEntityId} AND AccountId = {g.AccountId}
                           AND FiscalPeriodId = {periodId} AND CostCenterId = {g.CostCenterId} AND Currency = {journal.Currency};
                    END CATCH
                END
                """, cancellationToken);
        }
    }
}

public sealed class AccountingDbContextFactory : IDesignTimeDbContextFactory<AccountingDbContext>
{
    public AccountingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AccountingDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AccountingDbContext.SchemaName))
        .Options);
}
