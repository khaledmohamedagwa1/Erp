using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Pos.Contracts;
using Erp.Modules.Pos.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Pos.Application;

// ============================ Shifts ============================

/// <summary>Expected drawer contents per payment method, summed from the shift's receipts (nothing is accumulated on the shift).</summary>
public sealed class ShiftTotals(IPosDbContext db)
{
    public sealed record Totals(
        IReadOnlyList<PosReceipt> Receipts,
        IReadOnlyDictionary<PaymentMethod, decimal> Sales,
        IReadOnlyDictionary<PaymentMethod, decimal> Refunds,
        IReadOnlyDictionary<PaymentMethod, decimal> Expected);

    public async Task<Totals> ComputeAsync(CashierShift shift, CancellationToken ct)
    {
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.ShiftId == shift.Id).ToListAsync(ct);
        var methods = Enum.GetValues<PaymentMethod>();
        var sales = methods.ToDictionary(m => m, m => receipts.Where(r => r.Kind == ReceiptKind.Sale).Sum(r => r.NetPaid(m)));
        var refunds = methods.ToDictionary(m => m, m => receipts.Where(r => r.Kind == ReceiptKind.Refund).Sum(r => r.NetPaid(m)));
        return new Totals(receipts, sales, refunds, Expected(shift, sales, refunds));
    }

    /// <summary>Cash: opening float + cash sales net of change − cash refunds + pay-ins − pay-outs. Other methods: sales − refunds.</summary>
    public static IReadOnlyDictionary<PaymentMethod, decimal> Expected(CashierShift shift, IReadOnlyDictionary<PaymentMethod, decimal> sales, IReadOnlyDictionary<PaymentMethod, decimal> refunds) =>
        Enum.GetValues<PaymentMethod>().ToDictionary(m => m, m => sales.GetValueOrDefault(m) - refunds.GetValueOrDefault(m)
            + (m == PaymentMethod.Cash ? shift.OpeningFloat + shift.PayIns - shift.PayOuts : 0));
}

public sealed record OpenShiftCommand(Guid TerminalId, decimal OpeningFloat, DateOnly? BusinessDate) : ICommand<ShiftDto>;

public sealed class OpenShiftHandler(IPosDbContext db, ICurrentUser user, IClock clock, IDocumentNumberGenerator numbers) : ICommandHandler<OpenShiftCommand, ShiftDto>
{
    public async Task<Result<ShiftDto>> Handle(OpenShiftCommand c, CancellationToken ct)
    {
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == c.TerminalId, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!terminal.IsActive)
        {
            return PosAppErrors.TerminalInactive;
        }

        if (await db.Shifts.AnyAsync(s => s.TerminalId == terminal.Id && s.Status != ShiftStatus.Closed, ct))
        {
            return PosAppErrors.ShiftAlreadyOpen;
        }

        CashierShift shift;
        try
        {
            shift = new CashierShift(terminal.CompanyId, terminal.Id, terminal.LegalEntityId, user.UserId ?? Guid.Empty, c.OpeningFloat,
                c.BusinessDate ?? DateOnly.FromDateTime(clock.UtcNow), terminal.Currency, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.Shifts.Add(shift);
        try
        {
            // Claims the "one non-closed shift per terminal" unique index before a number is drawn.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DuplicateKey.Is(ex))
        {
            return PosAppErrors.ShiftAlreadyOpen;
        }

        shift.AssignNumber(await numbers.NextAsync(new NumberScope(terminal.CompanyId, $"{PosDocumentTypes.Shift}:{terminal.Id:N}", "SH",
            LegalEntityId: terminal.LegalEntityId, BranchCode: terminal.Code, FiscalYear: shift.BusinessDate.Year), ct));
        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(shift);
    }
}

public sealed record ListShiftsQuery(Guid? TerminalId, string? Status, DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<ShiftDto>>;

public sealed class ListShiftsHandler(IPosDbContext db) : IQueryHandler<ListShiftsQuery, IReadOnlyList<ShiftDto>>
{
    public async Task<Result<IReadOnlyList<ShiftDto>>> Handle(ListShiftsQuery q, CancellationToken ct)
    {
        var query = db.Shifts.AsNoTracking();
        if (q.TerminalId is { } terminalId)
        {
            query = query.Where(s => s.TerminalId == terminalId);
        }

        if (Enum.TryParse<ShiftStatus>(q.Status, true, out var status))
        {
            query = query.Where(s => s.Status == status);
        }

        if (q.From is { } from)
        {
            query = query.Where(s => s.BusinessDate >= from);
        }

        if (q.To is { } to)
        {
            query = query.Where(s => s.BusinessDate <= to);
        }

        var shifts = await query.OrderByDescending(s => s.OpenedAtUtc).Take(500).ToListAsync(ct);
        return shifts.Select(PosMapping.ToDto).ToList();
    }
}

public sealed record GetShiftQuery(Guid Id) : IQuery<ShiftDto>;

public sealed class GetShiftHandler(IPosDbContext db) : IQueryHandler<GetShiftQuery, ShiftDto>
{
    public async Task<Result<ShiftDto>> Handle(GetShiftQuery q, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == q.Id, ct);
        return shift is null ? PosAppErrors.ShiftNotFound : PosMapping.ToDto(shift);
    }
}

/// <summary>
/// Pay-in / pay-out (float top-up, cash dropped to the safe). Only the expected drawer cash changes: the safe uses the same GL cash
/// account, so there is no journal. Expenses paid from the drawer go through Finance payments.
/// </summary>
public sealed record AddCashMovementCommand(Guid ShiftId, string Kind, decimal Amount, string Reason) : ICommand<ShiftDto>;

public sealed class AddCashMovementHandler(IPosDbContext db, ICurrentUser user, IClock clock) : ICommandHandler<AddCashMovementCommand, ShiftDto>
{
    public async Task<Result<ShiftDto>> Handle(AddCashMovementCommand c, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == c.ShiftId, ct);
        if (shift is null)
        {
            return PosAppErrors.ShiftNotFound;
        }

        if (!Enum.TryParse<CashMovementKind>(c.Kind, true, out var kind) || int.TryParse(c.Kind, out _))
        {
            return PosErrors.InvalidMovementKind;
        }

        try
        {
            shift.AddCashMovement(kind, c.Amount, c.Reason, user.UserId, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(shift);
    }
}

/// <summary>Stops selling on the shift (Open → Closing) so the cashier can count the drawer.</summary>
public sealed record StartCloseShiftCommand(Guid ShiftId) : ICommand<ShiftDto>;

public sealed class StartCloseShiftHandler(IPosDbContext db, IPosLocks locks, IClock clock) : ICommandHandler<StartCloseShiftCommand, ShiftDto>
{
    public async Task<Result<ShiftDto>> Handle(StartCloseShiftCommand c, CancellationToken ct)
    {
        await locks.LockShiftAsync(c.ShiftId, ct);
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == c.ShiftId, ct);
        if (shift is null)
        {
            return PosAppErrors.ShiftNotFound;
        }

        try
        {
            shift.StartClosing(clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(shift);
    }
}

public sealed record CountInput(string Method, decimal Amount);

/// <summary>
/// Closes the shift with the blind counts (from Open or Closing). The cash variance is posted at once (purpose "over-short"):
/// shortage Dr CashOverShort / Cr cash; overage Dr cash / Cr CashOverShort. Non-cash variances are reported, not posted.
/// </summary>
public sealed record CloseShiftCommand(Guid ShiftId, IReadOnlyList<CountInput> Counts) : ICommand<ShiftDto>;

public sealed class CloseShiftHandler(
    IPosDbContext db,
    IPosLocks locks,
    ShiftTotals totals,
    PosAccounts accounts,
    IJournalPostingService journals,
    ICurrentUser user,
    IClock clock) : ICommandHandler<CloseShiftCommand, ShiftDto>
{
    public async Task<Result<ShiftDto>> Handle(CloseShiftCommand c, CancellationToken ct)
    {
        // Wait for receipts being posted into this shift (they hold its lock); they see the shift closed afterwards.
        await locks.LockShiftAsync(c.ShiftId, ct);
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == c.ShiftId, ct);
        if (shift is null)
        {
            return PosAppErrors.ShiftNotFound;
        }

        if (shift.Status == ShiftStatus.Closed)
        {
            return PosErrors.InvalidTransition("shift", shift.Status.ToString(), "closed");
        }

        var counted = new Dictionary<PaymentMethod, decimal>();
        foreach (var count in c.Counts ?? [])
        {
            var method = PosPaymentMethods.Parse(count.Method);
            if (method.IsFailure)
            {
                return method.Error;
            }

            counted[method.Value] = counted.GetValueOrDefault(method.Value) + count.Amount;
        }

        var terminal = await db.Terminals.FirstAsync(t => t.Id == shift.TerminalId, ct);
        var computed = await totals.ComputeAsync(shift, ct);
        decimal variance;
        try
        {
            variance = shift.Close(computed.Expected, counted, user.UserId, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (variance != 0)
        {
            var cash = (await accounts.ResolveAsync(terminal, ct))[PaymentMethod.Cash];
            var amount = Math.Abs(variance);
            var shortage = variance < 0;
            var lines = new PosJournal()
                .Add(PostingRoles.CashOverShort, null, shortage ? amount : 0, shortage ? 0 : amount, description: shortage ? "Cash shortage" : "Cash overage")
                .Add(cash.Role, cash.AccountId, shortage ? 0 : amount, shortage ? amount : 0, description: $"Drawer {terminal.Code}")
                .Lines;
            var posted = await journals.PostAsync(new JournalRequest(shift.LegalEntityId, shift.BusinessDate, SourceModules.Pos, PosDocumentTypes.Shift, shift.Id,
                "over-short", $"POS cash over/short {shift.Number}", shift.Currency, 1m, lines, terminal.BranchId), ct);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            shift.RecordVarianceJournal(posted.Value.JournalId);
        }

        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(shift);
    }
}

/// <summary>X report (running totals, any time) or Z report (final, only after close).</summary>
public sealed record ShiftReportQuery(Guid ShiftId, bool Final) : IQuery<ShiftReportDto>;

public sealed class ShiftReportHandler(IPosDbContext db, ShiftTotals totals, IClock clock) : IQueryHandler<ShiftReportQuery, ShiftReportDto>
{
    public async Task<Result<ShiftReportDto>> Handle(ShiftReportQuery q, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == q.ShiftId, ct);
        if (shift is null)
        {
            return PosAppErrors.ShiftNotFound;
        }

        if (q.Final && shift.Status != ShiftStatus.Closed)
        {
            return PosAppErrors.ShiftNotClosed;
        }

        var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == shift.TerminalId, ct);
        var computed = await totals.ComputeAsync(shift, ct);
        var sales = computed.Receipts.Where(r => r.Kind == ReceiptKind.Sale).ToList();
        var refunds = computed.Receipts.Where(r => r.Kind == ReceiptKind.Refund).ToList();
        var closed = shift.Status == ShiftStatus.Closed;
        var methods = Enum.GetValues<PaymentMethod>().Select(m =>
        {
            var count = shift.Counts.FirstOrDefault(c => c.Method == m);
            var expected = closed && count is not null ? count.Expected : computed.Expected[m];
            return new MethodTotalDto(m.ToString(), computed.Sales[m], computed.Refunds[m], expected, count?.Counted, count?.Variance);
        }).ToList();
        return new ShiftReportDto(q.Final ? "Z" : "X", shift.Id, shift.Number, terminal.Id, terminal.Code, shift.Status.ToString(), shift.BusinessDate, shift.Currency,
            shift.OpenedAtUtc, shift.ClosedAtUtc, sales.Count, refunds.Count, sales.Sum(r => r.GrossTotal), refunds.Sum(r => r.GrossTotal),
            sales.Sum(r => r.NetTotal) - refunds.Sum(r => r.NetTotal), sales.Sum(r => r.TaxTotal) - refunds.Sum(r => r.TaxTotal),
            sales.Sum(r => r.DiscountTotal) - refunds.Sum(r => r.DiscountTotal), sales.Sum(r => r.RoundingAdjustment) - refunds.Sum(r => r.RoundingAdjustment),
            shift.OpeningFloat, shift.PayIns, shift.PayOuts, methods, shift.CashVariance, shift.VarianceJournalId, clock.UtcNow);
    }
}

public sealed record ListShiftReceiptsQuery(Guid ShiftId) : IQuery<IReadOnlyList<ReceiptDto>>;

public sealed class ListShiftReceiptsHandler(IPosDbContext db) : IQueryHandler<ListShiftReceiptsQuery, IReadOnlyList<ReceiptDto>>
{
    public async Task<Result<IReadOnlyList<ReceiptDto>>> Handle(ListShiftReceiptsQuery q, CancellationToken ct)
    {
        if (!await db.Shifts.AnyAsync(s => s.Id == q.ShiftId, ct))
        {
            return PosAppErrors.ShiftNotFound;
        }

        var receipts = await db.Receipts.AsNoTracking().Where(r => r.ShiftId == q.ShiftId).OrderBy(r => r.CreatedAtUtc).ToListAsync(ct);
        return receipts.Select(r => PosMapping.ToDto(r)).ToList();
    }
}
