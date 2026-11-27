using System.Data;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Pos.Application;
using Erp.Modules.Pos.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Pos.Infrastructure;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IPosDbContext
{
    public const string SchemaName = "pos";

    public override string Schema => SchemaName;

    public DbSet<PosTerminal> Terminals => Set<PosTerminal>();

    public DbSet<CashierShift> Shifts => Set<CashierShift>();

    public DbSet<PosReceipt> Receipts => Set<PosReceipt>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PosTerminal>(b =>
        {
            b.ToTable("PosTerminal");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(12).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.MaxDiscountPercent).HasPrecision(9, 6);
            b.Property(x => x.StalePricePolicy).HasMaxLength(30).IsRequired();
            b.Property(x => x.DeviceKeyHash).HasMaxLength(64);
            b.Ignore(x => x.ReceiptPrefix);
            b.HasIndex(TenantIdProperty, nameof(PosTerminal.CompanyId), nameof(PosTerminal.Code)).IsUnique();
        });

        modelBuilder.Entity<CashierShift>(b =>
        {
            b.ToTable("CashierShift");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Number).HasMaxLength(50).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.OpeningFloat).HasPrecision(19, 4);
            b.Property(x => x.CashVariance).HasPrecision(19, 4);
            b.Ignore(x => x.PayIns);
            b.Ignore(x => x.PayOuts);

            // One shift that is not closed per terminal (Open or Closing).
            b.HasIndex(TenantIdProperty, nameof(CashierShift.TerminalId)).IsUnique().HasFilter("[Status] <> 'Closed'").HasDatabaseName("UX_CashierShift_OpenPerTerminal");
            b.HasIndex(TenantIdProperty, nameof(CashierShift.CompanyId), nameof(CashierShift.TerminalId), nameof(CashierShift.BusinessDate));
            b.OwnsMany(x => x.CashMovements, m =>
            {
                m.ToJson("CashMovements");
                m.Property(x => x.Uid);
                m.Property(x => x.Kind).HasConversion<string>();
                m.Property(x => x.Amount).HasPrecision(19, 4);
                m.Property(x => x.Reason).HasMaxLength(200);
            });
            b.OwnsMany(x => x.Counts, cnt =>
            {
                cnt.ToJson("Counts");
                cnt.Property(x => x.Method).HasConversion<string>();
                cnt.Property(x => x.Expected).HasPrecision(19, 4);
                cnt.Property(x => x.Counted).HasPrecision(19, 4);
                cnt.Property(x => x.Variance).HasPrecision(19, 4);
            });
        });

        modelBuilder.Entity<PosReceipt>(b =>
        {
            b.ToTable("PosReceipt");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Number).HasMaxLength(50).IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.PriceVersion).HasMaxLength(64);
            foreach (var amount in new[] { nameof(PosReceipt.NetTotal), nameof(PosReceipt.TaxTotal), nameof(PosReceipt.GrossTotal), nameof(PosReceipt.DiscountTotal),
                         nameof(PosReceipt.RoundingAdjustment), nameof(PosReceipt.AmountDue), nameof(PosReceipt.Tendered), nameof(PosReceipt.Change), nameof(PosReceipt.CashNet) })
            {
                b.Property(amount).HasPrecision(19, 4);
            }

            // Idempotency: a client receipt id is accepted once per terminal.
            b.HasIndex(TenantIdProperty, nameof(PosReceipt.TerminalId), nameof(PosReceipt.ClientReceiptId)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(PosReceipt.CompanyId), nameof(PosReceipt.Number)).IsUnique();
            b.HasIndex(TenantIdProperty, nameof(PosReceipt.ShiftId));
            b.HasIndex(TenantIdProperty, nameof(PosReceipt.OriginalReceiptId));
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToJson("Lines");
                l.Property(x => x.Uid);
                l.Property(x => x.Sku).HasMaxLength(64);
                l.Property(x => x.Description).HasMaxLength(500);
                l.Property(x => x.PostingGroup).HasMaxLength(30);
                l.Property(x => x.BatchNumber).HasMaxLength(64);
                l.Property(x => x.Quantity).HasPrecision(18, 6);
                l.Property(x => x.BaseQuantity).HasPrecision(18, 6);
                l.Property(x => x.RefundedQuantity).HasPrecision(18, 6);
                l.Property(x => x.UnitPrice).HasPrecision(19, 6);
                l.Property(x => x.DiscountPercent).HasPrecision(9, 6);
                l.Property(x => x.LineAmount).HasPrecision(19, 4);
                l.Property(x => x.DiscountAmount).HasPrecision(19, 4);
                l.Property(x => x.NetAmount).HasPrecision(19, 4);
                l.Property(x => x.TaxAmount).HasPrecision(19, 4);
                l.Property(x => x.GrossAmount).HasPrecision(19, 4);
                l.Property(x => x.CostAmount).HasPrecision(19, 4);
                l.Property(x => x.RefundedNetAmount).HasPrecision(19, 4);
                l.Ignore(x => x.RefundableQuantity);
                l.Ignore(x => x.SerialList);
                l.OwnsMany(x => x.Taxes, t =>
                {
                    t.Property(x => x.Code).HasMaxLength(30);
                    t.Property(x => x.Rate).HasPrecision(9, 6);
                    t.Property(x => x.Base).HasPrecision(19, 4);
                    t.Property(x => x.Amount).HasPrecision(19, 4);
                    t.Property(x => x.RefundedAmount).HasPrecision(19, 4);
                });
            });
            b.OwnsMany(x => x.Payments, p =>
            {
                p.ToJson("Payments");
                p.Property(x => x.Method).HasConversion<string>();
                p.Property(x => x.Amount).HasPrecision(19, 4);
                p.Property(x => x.Reference).HasMaxLength(100);
            });
        });
    }
}

/// <summary>Row lock on an existing shift row (UPDLOCK, ROWLOCK — never a range lock on rows that may not exist).</summary>
public sealed class PosLocks(ErpDbSession session, ITenantContext tenant) : IPosLocks
{
    public async Task LockShiftAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        var transaction = session.Transaction ?? throw new InvalidOperationException("POS locks require a transaction.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @lock int;
            SELECT @lock = 1 FROM pos.CashierShift WITH (UPDLOCK, ROWLOCK) WHERE TenantId = @t AND Id = @id;
            """;
        Add(command, "@t", tenant.TenantId ?? throw new InvalidOperationException("Tenant context required."));
        Add(command, "@id", shiftId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Guid;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Used only by <c>dotnet ef</c> to build the model for migrations.</summary>
public sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PosDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PosDbContext.SchemaName))
        .Options);
}
