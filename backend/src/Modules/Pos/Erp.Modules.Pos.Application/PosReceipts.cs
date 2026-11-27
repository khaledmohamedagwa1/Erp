using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Pos.Contracts;
using Erp.Modules.Pos.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Pos.Application;

// ============================ Receipts ============================

/// <summary>Shared steps of posting a sale or a refund receipt.</summary>
public sealed class ReceiptPosting(
    IPosDbContext db,
    IPosLocks locks,
    PosAccounts accounts,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    IDocumentNumberGenerator numbers,
    ICurrencyQueries currencies,
    IExchangeRateProvider rates)
{
    /// <summary>
    /// Parses payments; foreign cash is converted to the terminal currency at the business date's spot rate (rounded to the currency's
    /// minor units) and keeps its original currency, amount and rate on the payment.
    /// </summary>
    public async Task<Result<IReadOnlyList<(PaymentInput Input, PosPayment Payment)>>> ConvertPaymentsAsync(
        PosTerminal terminal, IReadOnlyList<PosPaymentInput> inputs, DateOnly date, int decimals, CancellationToken ct)
    {
        var result = new List<(PaymentInput, PosPayment)>();
        foreach (var payment in inputs)
        {
            var method = PosPaymentMethods.Parse(payment.Method);
            if (method.IsFailure)
            {
                return method.Error;
            }

            if (string.IsNullOrWhiteSpace(payment.Currency) || string.Equals(payment.Currency, terminal.Currency, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((new PaymentInput(method.Value, payment.Amount), new PosPayment(method.Value, payment.Amount, payment.Reference)));
                continue;
            }

            if (method.Value != PaymentMethod.Cash)
            {
                return PosAppErrors.ForeignCurrencyCashOnly;
            }

            var currency = payment.Currency.Trim().ToUpperInvariant();
            var rate = await rates.GetRateAsync(currency, terminal.Currency, date, ExchangeRateKind.Spot, ct);
            if (rate is not > 0)
            {
                return PosAppErrors.RateMissing;
            }

            var converted = PosMath.Round(payment.Amount * rate.Value, decimals);
            result.Add((new PaymentInput(method.Value, converted), new PosPayment(method.Value, converted, payment.Reference, currency, payment.Amount, rate.Value)));
        }

        return result;
    }

    public async Task<PosReceipt?> FindAsync(Guid terminalId, Guid clientReceiptId, CancellationToken ct) =>
        await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.TerminalId == terminalId && r.ClientReceiptId == clientReceiptId, ct);

    /// <summary>The terminal's open shift, update-locked until the transaction ends (so it cannot close under a receipt).</summary>
    public async Task<Result<CashierShift>> LockOpenShiftAsync(PosTerminal terminal, CancellationToken ct)
    {
        var shiftId = await db.Shifts.AsNoTracking().Where(s => s.TerminalId == terminal.Id && s.Status != ShiftStatus.Closed).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (shiftId is null)
        {
            return PosAppErrors.NoOpenShift;
        }

        await locks.LockShiftAsync(shiftId.Value, ct);
        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shiftId.Value, ct);
        return shift is { Status: ShiftStatus.Open } ? shift : PosAppErrors.NoOpenShift;
    }

    public async Task<(int Decimals, decimal CashIncrement)> CurrencyAsync(string currency, CancellationToken ct)
    {
        var info = await currencies.GetAsync(currency, ct);
        return (info?.MinorUnits ?? 2, info?.CashRoundingIncrement ?? 0);
    }

    /// <summary>
    /// Inserts the receipt first: the (terminal, client receipt id) unique index makes a concurrent duplicate wait and then fail,
    /// in which case the receipt that won is returned instead (null = this one was inserted).
    /// </summary>
    public async Task<PosReceipt?> ClaimAsync(PosReceipt receipt, CancellationToken ct)
    {
        db.Receipts.Add(receipt);
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (DuplicateKey.Is(ex))
        {
            db.Entry(receipt).State = EntityState.Detached;
            return await FindAsync(receipt.TerminalId, receipt.ClientReceiptId, ct);
        }
    }

    /// <summary>Number per terminal, stock movements (with the COGS journal), the receipt journal. Returns a failure to roll back.</summary>
    public async Task<Result> PostAsync(PosTerminal terminal, PosReceipt receipt, IReadOnlyDictionary<Guid, Guid?> costSources, CancellationToken ct, string? offlineNumber = null)
    {
        var sale = receipt.Kind == ReceiptKind.Sale;
        var docType = sale ? PosDocumentTypes.Receipt : PosDocumentTypes.Refund;
        var number = offlineNumber ?? await numbers.NextAsync(new NumberScope(receipt.CompanyId, $"{docType}:{terminal.Id:N}", sale ? terminal.ReceiptPrefix : "PRF",
            LegalEntityId: receipt.LegalEntityId, BranchCode: terminal.Code, FiscalYear: receipt.BusinessDate.Year), ct);

        var stockLines = receipt.Lines.Where(l => l.IsStockable).ToList();
        if (stockLines.Count > 0)
        {
            var posted = await inventory.PostAsync(new InventoryPostingRequest(receipt.LegalEntityId, receipt.BusinessDate, SourceModules.Pos, docType, receipt.Id, number,
                stockLines.Select(l => new InventoryPostingLine(l.Uid, l.ProductId, l.VariantId, receipt.WarehouseId, l.UomId, sale ? -l.Quantity : l.Quantity,
                    sale ? InventoryMovementTypes.SalesIssue : InventoryMovementTypes.SalesReturn,
                    Batch: l.BatchNumber is null ? null : new BatchInput(l.BatchNumber),
                    Serials: l.SerialList.Count > 0 ? l.SerialList : null,
                    CostSourceMovementId: costSources.GetValueOrDefault(l.Uid),
                    CounterRole: PostingRoles.CostOfGoodsSold)).ToList(),
                PostJournal: true, JournalDescription: $"Cost of sales {number}"), ct);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            foreach (var result in posted.Value.Lines)
            {
                var first = result.Movements.FirstOrDefault();
                receipt.Line(result.LineId).RecordCost(Math.Abs(result.TotalCost), first?.MovementId, first?.BatchNumber);
            }
        }

        receipt.Complete(number, null);
        var journal = PosJournal.ForReceipt(receipt, await accounts.ResolveAsync(terminal, ct));
        var postedJournal = await journals.PostAsync(new JournalRequest(receipt.LegalEntityId, receipt.BusinessDate, SourceModules.Pos, docType, receipt.Id,
            "receipt", $"{(sale ? "POS receipt" : "POS refund")} {number}", receipt.Currency, 1m, journal.Lines, receipt.BranchId), ct);
        if (postedJournal.IsFailure)
        {
            return postedJournal.Error;
        }

        receipt.Complete(number, postedJournal.Value.JournalId);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Online submission of a sale. Idempotent on (terminal, ClientReceiptId): a resubmission returns the original receipt with
/// <c>Replayed = true</c> and posts nothing. Prices are recomputed from the terminal's price list; when the client's price version
/// is stale and the totals differ (policy Reject), nothing is posted and the repriced cart is returned (HTTP 409).
/// </summary>
public sealed record SubmitReceiptCommand(
    Guid TerminalId,
    Guid ClientReceiptId,
    string? PriceVersion,
    decimal? ExpectedGross,
    Guid? CustomerId,
    IReadOnlyList<PosLineInput> Lines,
    IReadOnlyList<PosPaymentInput> Payments,
    string? DeviceKey = null,
    Guid? SalespersonId = null) : ICommand<ReceiptOutcome>;

public sealed class SubmitReceiptHandler(
    IPosDbContext db,
    ReceiptPosting posting,
    PosPricing pricing,
    IPriceListSnapshotQueries priceLists,
    IPartyQueries parties,
    ICurrentUser user,
    IClock clock,
    PosSalespersonResolver salespersons) : ICommandHandler<SubmitReceiptCommand, ReceiptOutcome>
{
    public async Task<Result<ReceiptOutcome>> Handle(SubmitReceiptCommand c, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == c.TerminalId, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!PosDevice.Accepts(terminal, c.DeviceKey))
        {
            return PosAppErrors.DeviceKeyInvalid;
        }

        if (await posting.FindAsync(terminal.Id, c.ClientReceiptId, ct) is { } existing)
        {
            return new ReceiptOutcome(PosMapping.ToDto(existing, replayed: true), null);
        }

        if (!terminal.IsActive)
        {
            return PosAppErrors.TerminalInactive;
        }

        var shift = await posting.LockOpenShiftAsync(terminal, ct);
        if (shift.IsFailure)
        {
            return shift.Error;
        }

        // A duplicate of the same terminal waited on the shift lock: it finds the committed original now.
        if (await posting.FindAsync(terminal.Id, c.ClientReceiptId, ct) is { } committed)
        {
            return new ReceiptOutcome(PosMapping.ToDto(committed, replayed: true), null);
        }

        Guid customerId;
        if (c.CustomerId is { } requested)
        {
            if (await parties.GetCustomerAsync(requested, ct) is not { IsActive: true })
            {
                return PosAppErrors.CustomerNotFound;
            }

            customerId = requested;
        }
        else
        {
            customerId = terminal.DefaultCustomerId;
        }

        var date = shift.Value.BusinessDate;
        var (decimals, increment) = await posting.CurrencyAsync(terminal.Currency, ct);
        var cart = await pricing.PriceAsync(terminal, date, c.Lines ?? [], decimals, ct);
        if (cart.IsFailure)
        {
            return cart.Error;
        }

        var version = await priceLists.GetVersionAsync(terminal.PriceListId, ct) ?? string.Empty;
        if (c.PriceVersion is not null && c.PriceVersion != version && terminal.StalePricePolicy != StalePricePolicies.AcceptServerPrices
            && c.ExpectedGross != cart.Value.Gross)
        {
            var lines = cart.Value.Lines;
            return new ReceiptOutcome(null, new RepricedCartDto(version, cart.Value.TaxInclusive, lines.Sum(l => l.NetAmount), lines.Sum(l => l.TaxAmount),
                cart.Value.Gross, lines.Select(PosMapping.ToDto).ToList()));
        }

        var payments = await posting.ConvertPaymentsAsync(terminal, c.Payments ?? [], date, decimals, ct);
        if (payments.IsFailure)
        {
            return payments.Error;
        }

        var salesperson = await salespersons.ResolveAsync(c.SalespersonId, customerId, user.UserId, ct);
        if (salesperson.IsFailure)
        {
            return salesperson.Error;
        }

        var receipt = new PosReceipt(terminal.CompanyId, terminal.LegalEntityId, terminal.BranchId, terminal.Id, shift.Value.Id, terminal.WarehouseId, c.ClientReceiptId,
            ReceiptKind.Sale, customerId, terminal.Currency, terminal.PriceListId, version, cart.Value.TaxInclusive, date, user.UserId, clock.UtcNow, null);
        receipt.AssignSalesperson(salesperson.Value);
        try
        {
            receipt.SetLines(cart.Value.Lines);
            var settlement = PosMath.SettleSale(receipt.GrossTotal, payments.Value.Select(p => p.Input).ToList(), increment, decimals);
            receipt.Settle(settlement, payments.Value.Select(p => p.Payment));
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (await posting.ClaimAsync(receipt, ct) is { } winner)
        {
            return new ReceiptOutcome(PosMapping.ToDto(winner, replayed: true), null);
        }

        var posted = await posting.PostAsync(terminal, receipt, new Dictionary<Guid, Guid?>(), ct);
        return posted.IsFailure ? posted.Error : new ReceiptOutcome(PosMapping.ToDto(receipt), null);
    }
}

/// <summary>A receipt the device created while offline (numbered from the terminal's offline range, dated when it was made).</summary>
public sealed record OfflineReceiptInput(
    Guid ClientReceiptId,
    string OfflineNumber,
    DateTime CreatedAtUtc,
    decimal? ExpectedGross,
    Guid? CustomerId,
    IReadOnlyList<PosLineInput> Lines,
    IReadOnlyList<PosPaymentInput> Payments);

public sealed record OfflineConflictDto(string Code, string Message);

/// <summary>Accepted (posted now), Replayed (posted before), or Rejected (nothing posted; resolve the conflicts and resend).</summary>
public sealed record OfflineReceiptResultDto(Guid ClientReceiptId, string OfflineNumber, string Status, ReceiptDto? Receipt, IReadOnlyList<OfflineConflictDto> Conflicts);

public sealed record OfflineRangeDto(Guid TerminalId, string Prefix, long From, long To);

/// <summary>Reserves a block of receipt numbers for the device to use while offline.</summary>
public sealed record AllocateOfflineRangeCommand(Guid TerminalId, int Size, string? DeviceKey = null) : ICommand<OfflineRangeDto>;

public sealed class AllocateOfflineRangeHandler(IPosDbContext db) : ICommandHandler<AllocateOfflineRangeCommand, OfflineRangeDto>
{
    public async Task<Result<OfflineRangeDto>> Handle(AllocateOfflineRangeCommand c, CancellationToken ct)
    {
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == c.TerminalId, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!PosDevice.Accepts(terminal, c.DeviceKey))
        {
            return PosAppErrors.DeviceKeyInvalid;
        }

        try
        {
            var (from, to) = terminal.AllocateOfflineRange(c.Size);
            await db.SaveChangesAsync(ct);
            return new OfflineRangeDto(terminal.Id, terminal.OfflinePrefix, from, to);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

/// <summary>
/// One offline receipt of a sync batch (each runs in its own transaction). Idempotent on (terminal, ClientReceiptId). Prices are
/// recomputed by the server for the receipt's date; a different total is reported as a <c>price-changed</c> conflict but the sale
/// stands as long as the payments cover the server total. Stock follows the inventory policy: without negative stock allowed, a
/// shortage rejects the receipt (<c>inventory.insufficient-stock</c>) and nothing is posted.
/// </summary>
public sealed record SubmitOfflineReceiptCommand(Guid TerminalId, OfflineReceiptInput Receipt, string? DeviceKey = null) : ICommand<OfflineReceiptResultDto>;

public sealed class SubmitOfflineReceiptHandler(
    IPosDbContext db,
    ReceiptPosting posting,
    PosPricing pricing,
    IPriceListSnapshotQueries priceLists,
    IPartyQueries parties,
    ICurrentUser user,
    IClock clock) : ICommandHandler<SubmitOfflineReceiptCommand, OfflineReceiptResultDto>
{
    public async Task<Result<OfflineReceiptResultDto>> Handle(SubmitOfflineReceiptCommand c, CancellationToken ct)
    {
        var input = c.Receipt;
        var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == c.TerminalId, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!PosDevice.Accepts(terminal, c.DeviceKey))
        {
            return PosAppErrors.DeviceKeyInvalid;
        }

        if (await posting.FindAsync(terminal.Id, input.ClientReceiptId, ct) is { } existing)
        {
            return new OfflineReceiptResultDto(input.ClientReceiptId, input.OfflineNumber, "Replayed", PosMapping.ToDto(existing, replayed: true), []);
        }

        if (!terminal.IsAllocatedOfflineNumber(input.OfflineNumber))
        {
            return Rejected(input, PosAppErrors.OfflineNumberInvalid);
        }

        var date = DateOnly.FromDateTime(input.CreatedAtUtc);
        if (date > DateOnly.FromDateTime(clock.UtcNow))
        {
            return Rejected(input, PosAppErrors.OfflineDateInvalid);
        }

        var shift = await posting.LockOpenShiftAsync(terminal, ct);
        if (shift.IsFailure)
        {
            return Rejected(input, shift.Error);
        }

        if (await posting.FindAsync(terminal.Id, input.ClientReceiptId, ct) is { } committed)
        {
            return new OfflineReceiptResultDto(input.ClientReceiptId, input.OfflineNumber, "Replayed", PosMapping.ToDto(committed, replayed: true), []);
        }

        var customerId = terminal.DefaultCustomerId;
        if (input.CustomerId is { } requested)
        {
            if (await parties.GetCustomerAsync(requested, ct) is not { IsActive: true })
            {
                return Rejected(input, PosAppErrors.CustomerNotFound);
            }

            customerId = requested;
        }

        var (decimals, increment) = await posting.CurrencyAsync(terminal.Currency, ct);
        var cart = await pricing.PriceAsync(terminal, date, input.Lines ?? [], decimals, ct);
        if (cart.IsFailure)
        {
            return Rejected(input, cart.Error);
        }

        var conflicts = new List<OfflineConflictDto>();
        if (input.ExpectedGross is { } expected && expected != cart.Value.Gross)
        {
            conflicts.Add(new OfflineConflictDto("pos.price-changed", $"The device charged {expected}; current prices total {cart.Value.Gross}."));
        }

        var payments = await posting.ConvertPaymentsAsync(terminal, input.Payments ?? [], date, decimals, ct);
        if (payments.IsFailure)
        {
            return Rejected(input, payments.Error, conflicts);
        }

        var version = await priceLists.GetVersionAsync(terminal.PriceListId, ct) ?? string.Empty;
        var receipt = new PosReceipt(terminal.CompanyId, terminal.LegalEntityId, terminal.BranchId, terminal.Id, shift.Value.Id, terminal.WarehouseId, input.ClientReceiptId,
            ReceiptKind.Sale, customerId, terminal.Currency, terminal.PriceListId, version, cart.Value.TaxInclusive, date, user.UserId, clock.UtcNow, null);
        receipt.MarkOffline(input.CreatedAtUtc);
        try
        {
            receipt.SetLines(cart.Value.Lines);
            var settlement = PosMath.SettleSale(receipt.GrossTotal, payments.Value.Select(p => p.Input).ToList(), increment, decimals);
            receipt.Settle(settlement, payments.Value.Select(p => p.Payment));
        }
        catch (DomainException ex)
        {
            return Rejected(input, ex.Error, conflicts);
        }

        if (await posting.ClaimAsync(receipt, ct) is { } winner)
        {
            return new OfflineReceiptResultDto(input.ClientReceiptId, input.OfflineNumber, "Replayed", PosMapping.ToDto(winner, replayed: true), []);
        }

        var posted = await posting.PostAsync(terminal, receipt, new Dictionary<Guid, Guid?>(), ct, input.OfflineNumber);
        if (posted.IsFailure)
        {
            // The pipeline rolls back on a failed result; the receipt row claimed above goes with it.
            return posted.Error;
        }

        return new OfflineReceiptResultDto(input.ClientReceiptId, input.OfflineNumber, "Accepted", PosMapping.ToDto(receipt), conflicts);
    }

    private static Result<OfflineReceiptResultDto> Rejected(OfflineReceiptInput input, Error error, IReadOnlyList<OfflineConflictDto>? earlier = null) =>
        new OfflineReceiptResultDto(input.ClientReceiptId, input.OfflineNumber, "Rejected", null,
            [.. earlier ?? [], new OfflineConflictDto(error.Code, error.Message)]);
}

public sealed record RefundLineInput(Guid LineId, decimal Quantity);

/// <summary>
/// Refunds lines of a sale receipt (quantity ≤ what is not yet refunded) as a refund receipt in the open shift of the given
/// terminal (default: the sale's terminal). Amounts are prorated from the sale; stock comes back at the original cost.
/// Idempotent on (terminal, ClientReceiptId) like a sale.
/// </summary>
public sealed record RefundReceiptCommand(
    Guid ReceiptId,
    Guid ClientReceiptId,
    IReadOnlyList<RefundLineInput> Lines,
    IReadOnlyList<PosPaymentInput>? Payments,
    Guid? TerminalId = null,
    string? DeviceKey = null) : ICommand<ReceiptDto>;

public sealed class RefundReceiptHandler(IPosDbContext db, ReceiptPosting posting, ICurrentUser user, IClock clock) : ICommandHandler<RefundReceiptCommand, ReceiptDto>
{
    public async Task<Result<ReceiptDto>> Handle(RefundReceiptCommand c, CancellationToken ct)
    {
        var original = await db.Receipts.FirstOrDefaultAsync(r => r.Id == c.ReceiptId, ct);
        if (original is null)
        {
            return PosAppErrors.ReceiptNotFound;
        }

        var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == (c.TerminalId ?? original.TerminalId), ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!PosDevice.Accepts(terminal, c.DeviceKey))
        {
            return PosAppErrors.DeviceKeyInvalid;
        }

        if (await posting.FindAsync(terminal.Id, c.ClientReceiptId, ct) is { } existing)
        {
            return PosMapping.ToDto(existing, replayed: true);
        }

        if (original.Kind != ReceiptKind.Sale)
        {
            return PosAppErrors.NotASale;
        }

        if (terminal.LegalEntityId != original.LegalEntityId || terminal.Currency != original.Currency)
        {
            return PosAppErrors.RefundTerminalMismatch;
        }

        if (!terminal.IsActive)
        {
            return PosAppErrors.TerminalInactive;
        }

        var shift = await posting.LockOpenShiftAsync(terminal, ct);
        if (shift.IsFailure)
        {
            return shift.Error;
        }

        if (await posting.FindAsync(terminal.Id, c.ClientReceiptId, ct) is { } committed)
        {
            return PosMapping.ToDto(committed, replayed: true);
        }

        var (decimals, increment) = await posting.CurrencyAsync(terminal.Currency, ct);
        var refund = new PosReceipt(terminal.CompanyId, terminal.LegalEntityId, terminal.BranchId, terminal.Id, shift.Value.Id, terminal.WarehouseId, c.ClientReceiptId,
            ReceiptKind.Refund, original.CustomerId, original.Currency, original.PriceListId, null, original.TaxInclusive, shift.Value.BusinessDate, user.UserId,
            clock.UtcNow, original.Id);
        refund.AssignSalesperson(original.SalespersonId);
        var costSources = new Dictionary<Guid, Guid?>();
        try
        {
            if (c.Lines is not { Count: > 0 })
            {
                throw new DomainException(PosErrors.NoLines);
            }

            var lines = new List<PosReceiptLine>();
            foreach (var input in c.Lines)
            {
                var source = original.Line(input.LineId);
                var line = source.Refund(input.Quantity, decimals);
                costSources[line.Uid] = source.CostMovementId;
                lines.Add(line);
            }

            refund.SetLines(lines);
            var payments = new List<(PaymentInput Input, string? Reference)>();
            foreach (var payment in c.Payments ?? [])
            {
                var method = PosPaymentMethods.Parse(payment.Method);
                if (method.IsFailure)
                {
                    return method.Error;
                }

                payments.Add((new PaymentInput(method.Value, payment.Amount), payment.Reference));
            }

            var (settlement, settled) = PosMath.SettleRefund(refund.GrossTotal, payments.Count == 0 ? null : payments.Select(p => p.Input).ToList(), increment, decimals);
            refund.Settle(settlement, settled.Select((p, i) => new PosPayment(p.Method, p.Amount, i < payments.Count ? payments[i].Reference : null)));
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        original.UpdateRefundStatus();
        if (await posting.ClaimAsync(refund, ct) is { } winner)
        {
            return PosMapping.ToDto(winner, replayed: true);
        }

        var posted = await posting.PostAsync(terminal, refund, costSources, ct);
        return posted.IsFailure ? posted.Error : PosMapping.ToDto(refund);
    }
}

public sealed record GetReceiptQuery(Guid Id) : IQuery<ReceiptDto>;

public sealed class GetReceiptHandler(IPosDbContext db) : IQueryHandler<GetReceiptQuery, ReceiptDto>
{
    public async Task<Result<ReceiptDto>> Handle(GetReceiptQuery q, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == q.Id, ct);
        return receipt is null ? PosAppErrors.ReceiptNotFound : PosMapping.ToDto(receipt);
    }
}
