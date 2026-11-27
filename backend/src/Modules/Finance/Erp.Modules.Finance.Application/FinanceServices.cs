using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Finance.Application;

public interface IFinanceDbContext
{
    DbSet<OpenItem> OpenItems { get; }

    DbSet<Allocation> Allocations { get; }

    DbSet<CashBankAccount> CashBankAccounts { get; }

    DbSet<Settlement> Settlements { get; }

    DbSet<InternalTransfer> InternalTransfers { get; }

    DbSet<Expense> Expenses { get; }

    DbSet<CashMovement> CashMovements { get; }

    DbSet<BankReconciliation> BankReconciliations { get; }

    DbSet<FxRevaluationRun> FxRevaluationRuns { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class FinanceDocTypes
{
    public const string Receipt = "Receipt";
    public const string Payment = "Payment";
    public const string AdvanceAllocation = "AdvanceAllocation";
    public const string CreditNoteApplication = "CreditNoteApplication";
    public const string InternalTransfer = "InternalTransfer";
    public const string Expense = "Expense";
    public const string FxRevaluation = "FxRevaluation";
}

public static class FinanceAppErrors
{
    public static readonly Error TransactionRequired = Error.Failure("finance.transaction-required", "The sub-ledger must be called inside the caller's transaction.");
    public static readonly Error LegalEntityNotFound = Error.NotFound("finance.legal-entity-not-found", "The legal entity was not found in this company.");
    public static readonly Error PartyNotFound = Error.NotFound("finance.party-not-found", "The customer or supplier was not found or is inactive.");
    public static readonly Error AccountNotFound = Error.NotFound("finance.cash-bank-account-not-found", "The cash/bank account was not found or is inactive.");
    public static readonly Error InvalidExpenseAccount = Error.Validation("finance.invalid-expense-account",
        "Expense lines must use an active posting account of type Expense or Asset that is not a control account.");
    public static readonly Error DocumentNotFound = Error.NotFound("finance.document-not-found", "The document was not found.");
    public static readonly Error OpenItemNotFound = Error.NotFound("finance.open-item-not-found", "An open item was not found for this party.");
    public static readonly Error RateMissing = Error.Validation("finance.rate-missing", "No exchange rate is recorded for the currency on this date.");
    public static readonly Error WithholdingGroupInvalid = Error.Validation("finance.withholding-group-invalid", "The tax group has no withholding component.");
    public static readonly Error SourceNotFound = Error.NotFound("finance.source-not-found", "No open items exist for the source document.");
    public static readonly Error RevaluationExists = Error.Conflict("finance.revaluation-exists", "A revaluation already exists for this legal entity and date.");
}

/// <summary>Resolves the legal entity (must belong to the active company, adopting it when none is set) and its functional currency.</summary>
public sealed class FinanceScope(ITenantContext tenant, IOrganizationQueries organization, IExchangeRateProvider rates)
{
    public async Task<Result<LegalEntitySummary>> LegalEntityAsync(Guid legalEntityId, CancellationToken ct)
    {
        var le = await organization.GetLegalEntityAsync(legalEntityId, ct);
        if (le is null)
        {
            return FinanceAppErrors.LegalEntityNotFound;
        }

        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(le.CompanyId);
        }
        else if (tenant.CompanyId != le.CompanyId)
        {
            return FinanceAppErrors.LegalEntityNotFound;
        }

        return le;
    }

    /// <summary>Base units per one unit of <paramref name="currency"/>. Functional currency → 1; explicit rate wins; else the recorded spot rate.</summary>
    public async Task<Result<decimal>> RateAsync(string currency, string functionalCurrency, DateOnly date, decimal? explicitRate, CancellationToken ct)
    {
        if (string.Equals(currency, functionalCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        if (explicitRate is > 0)
        {
            return explicitRate.Value;
        }

        var rate = await rates.GetRateAsync(currency, functionalCurrency, date, ExchangeRateKind.Spot, ct);
        return rate is > 0 ? rate.Value : FinanceAppErrors.RateMissing;
    }
}

/// <summary>Builds functional-currency journal lines (rate 1), skipping zero amounts.</summary>
public sealed class JournalLines
{
    private readonly List<JournalLineRequest> _lines = [];

    public IReadOnlyList<JournalLineRequest> Lines => _lines;

    public JournalLines Debit(string? role, Guid? accountId, decimal amount, PostingContext? context = null, PartyKind? party = null, Guid? partyId = null, Guid? costCenterId = null, string? description = null) =>
        Add(role, accountId, amount, 0, context, party, partyId, costCenterId, description);

    public JournalLines Credit(string? role, Guid? accountId, decimal amount, PostingContext? context = null, PartyKind? party = null, Guid? partyId = null, Guid? costCenterId = null, string? description = null) =>
        Add(role, accountId, 0, amount, context, party, partyId, costCenterId, description);

    /// <summary>Signed: positive debits, negative credits.</summary>
    public JournalLines Signed(string? role, Guid? accountId, decimal amount, PostingContext? context = null, PartyKind? party = null, Guid? partyId = null) =>
        amount >= 0 ? Debit(role, accountId, amount, context, party, partyId) : Credit(role, accountId, -amount, context, party, partyId);

    /// <summary>Realized/unrealized difference: positive gain is credited to the gain role, negative debited to the loss role.</summary>
    public JournalLines Fx(decimal gain, string gainRole, string lossRole) =>
        gain >= 0 ? Credit(gainRole, null, gain) : Debit(lossRole, null, -gain);

    private JournalLines Add(string? role, Guid? accountId, decimal debit, decimal credit, PostingContext? context, PartyKind? party, Guid? partyId, Guid? costCenterId, string? description)
    {
        if (debit == 0 && credit == 0)
        {
            return this;
        }

        _lines.Add(new JournalLineRequest(accountId is null ? role : null, accountId, debit, credit, context, costCenterId,
            party?.ToString(), partyId, description));
        return this;
    }
}

public sealed record AllocationOutcome(decimal RelievedBase, decimal SettlementBase, IReadOnlyList<Allocation> Allocations);

/// <summary>
/// Applies allocations to open items and announces the document-level settlement status (outbox event <see cref="InvoiceSettled"/>).
/// Callers have already loaded and validated the items (same party, legal entity and currency).
/// </summary>
public sealed class AllocationService(IFinanceDbContext db)
{
    public AllocationOutcome Apply(IReadOnlyList<(OpenItem Item, decimal Amount)> allocations, SettlementKind kind, Guid settlementId, string reference, DateOnly date, decimal settlementRate)
    {
        var created = new List<Allocation>();
        decimal relieved = 0, settlementBase = 0;
        foreach (var (item, amount) in allocations)
        {
            var itemRelieved = item.Settle(amount);
            var itemSettlementBase = FinanceMath.ToBase(amount, settlementRate);
            var allocation = Allocation.Create(item, kind, settlementId, reference, date, amount, itemRelieved, itemSettlementBase);
            db.Allocations.Add(allocation);
            created.Add(allocation);
            relieved += itemRelieved;
            settlementBase += itemSettlementBase;
        }

        return new AllocationOutcome(relieved, settlementBase, created);
    }

    /// <summary>Raises InvoiceSettled for every source document touched, using all of its installments.</summary>
    public async Task AnnounceAsync(IEnumerable<OpenItem> touched, CancellationToken ct)
    {
        foreach (var group in touched.GroupBy(i => (i.SourceDocType, i.SourceDocId)))
        {
            var docType = group.Key.SourceDocType;
            var docId = group.Key.SourceDocId;
            var loaded = group.ToDictionary(i => i.Id);
            var others = await db.OpenItems.Where(i => i.SourceDocType == docType && i.SourceDocId == docId && !loaded.Keys.Contains(i.Id)).ToListAsync(ct);
            var all = loaded.Values.Concat(others).Where(i => i.Status != OpenItemStatus.Cancelled).ToList();
            var open = all.Sum(i => i.OpenAmount);
            var original = all.Sum(i => i.OriginalAmount);
            var status = open == 0 ? SettlementStatuses.Paid : open == original ? SettlementStatuses.Open : SettlementStatuses.PartiallyPaid;
            group.First().Announce(status, open);
        }
    }
}

/// <summary>The receivables/payables sub-ledger used by Sales, Procurement, POS and Finance's own expenses.</summary>
public sealed class SubledgerService(
    IFinanceDbContext db,
    IUnitOfWork unitOfWork,
    FinanceScope scope,
    IPartyQueries parties,
    ICurrencyQueries currencies,
    AllocationService allocations,
    IJournalPostingService journals) : ISubledgerService
{
    public async Task<Result<IReadOnlyList<OpenItemSummary>>> CreateOpenItemsAsync(OpenItemRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return FinanceAppErrors.TransactionRequired;
        }

        var existing = await db.OpenItems.Where(i => i.SourceDocType == request.SourceDocType && i.SourceDocId == request.SourceDocId)
            .OrderBy(i => i.Installment).ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return existing.Select(ToSummary).ToList();
        }

        if (request.TotalAmount == 0 || request.ExchangeRate <= 0)
        {
            return FinanceErrors.InvalidAmount;
        }

        var le = await scope.LegalEntityAsync(request.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var postingGroup = await PostingGroupAsync(request.PartyType, request.PartyId, cancellationToken);
        if (postingGroup is null)
        {
            return FinanceAppErrors.PartyNotFound;
        }

        var currency = request.Currency.ToUpperInvariant();
        var rate = string.Equals(currency, le.Value.FunctionalCurrency, StringComparison.OrdinalIgnoreCase) ? 1m : request.ExchangeRate;
        var sign = request.TotalAmount > 0 ? 1 : -1;
        var total = Math.Abs(request.TotalAmount);
        var decimals = (await currencies.GetAsync(currency, cancellationToken))?.MinorUnits ?? 2;

        IReadOnlyList<DueInstallment> schedule = sign > 0
            ? await parties.GetDueScheduleAsync(request.PaymentTermsId, request.DocumentDate, total, decimals, cancellationToken)
            : [new DueInstallment(request.DocumentDate, total)];
        var baseShares = FinanceMath.SplitBase(schedule.Select(s => s.Amount).ToList(), FinanceMath.ToBase(total, rate));

        var items = new List<OpenItem>();
        for (var i = 0; i < schedule.Count; i++)
        {
            if (schedule[i].Amount <= 0)
            {
                continue;
            }

            var item = OpenItem.Create(le.Value.CompanyId, le.Value.Id, request.PartyType, request.PartyId, postingGroup,
                request.SourceModule, request.SourceDocType, request.SourceDocId, request.DocumentNumber, request.DocumentDate,
                i + 1, schedule[i].DueDate, currency, rate, sign, schedule[i].Amount, baseShares[i]);
            db.OpenItems.Add(item);
            items.Add(item);
        }

        await db.SaveChangesAsync(cancellationToken);
        return items.Select(ToSummary).ToList();
    }

    public async Task<Result> CancelOpenItemsAsync(string sourceDocType, Guid sourceDocId, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return FinanceAppErrors.TransactionRequired;
        }

        var items = await db.OpenItems.Where(i => i.SourceDocType == sourceDocType && i.SourceDocId == sourceDocId).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return FinanceAppErrors.SourceNotFound;
        }

        if (items.Any(i => i.Status != OpenItemStatus.Cancelled && i.OpenAmount != i.OriginalAmount))
        {
            return FinanceErrors.OpenItemAllocated;
        }

        foreach (var item in items.Where(i => i.Status != OpenItemStatus.Cancelled))
        {
            item.Cancel();
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<decimal>> ApplyCreditNoteAsync(string creditNoteDocType, Guid creditNoteDocId, string? invoiceDocType, Guid? invoiceDocId, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return FinanceAppErrors.TransactionRequired;
        }

        var credits = await db.OpenItems
            .Where(i => i.SourceDocType == creditNoteDocType && i.SourceDocId == creditNoteDocId && i.Sign < 0 && i.OpenAmount > 0)
            .OrderBy(i => i.Installment).ToListAsync(cancellationToken);
        if (credits.Count == 0)
        {
            return FinanceAppErrors.SourceNotFound;
        }

        var first = credits[0];
        var query = db.OpenItems.Where(i => i.PartyType == first.PartyType && i.PartyId == first.PartyId && i.LegalEntityId == first.LegalEntityId
            && i.Currency == first.Currency && i.Sign > 0 && i.OpenAmount > 0 && i.Status != OpenItemStatus.Cancelled);
        if (invoiceDocType is not null && invoiceDocId is not null)
        {
            query = query.Where(i => i.SourceDocType == invoiceDocType && i.SourceDocId == invoiceDocId);
        }

        var invoices = await query.OrderBy(i => i.DueDate).ThenBy(i => i.Installment).ToListAsync(cancellationToken);
        if (invoices.Count == 0)
        {
            return 0m;
        }

        var le = await scope.LegalEntityAsync(first.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        decimal applied = 0, gain = 0;
        var touched = new List<OpenItem>();
        var applicationId = SequentialGuid.New();
        foreach (var credit in credits)
        {
            foreach (var invoice in invoices.Where(i => i.OpenAmount > 0))
            {
                if (credit.OpenAmount == 0)
                {
                    break;
                }

                var amount = Math.Min(credit.OpenAmount, invoice.OpenAmount);
                var creditRelieved = credit.Settle(amount);
                var invoiceRelieved = invoice.Settle(amount);
                db.Allocations.Add(Allocation.Create(credit, SettlementKind.CreditNote, invoice.Id, invoice.DocumentNumber, date, amount, creditRelieved, creditRelieved));
                var allocation = Allocation.Create(invoice, SettlementKind.CreditNote, credit.Id, credit.DocumentNumber, date, amount, invoiceRelieved, creditRelieved);
                db.Allocations.Add(allocation);
                gain += allocation.RealizedFxBase;
                applied += amount;
                touched.Add(credit);
                touched.Add(invoice);
            }
        }

        await allocations.AnnounceAsync(touched.Distinct(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (gain != 0)
        {
            // Both sides hit the same control account; only the exchange difference remains to be booked.
            var controlRole = first.PartyType == PartyKind.Customer ? PostingRoles.CustomerReceivable : PostingRoles.SupplierPayable;
            var lines = new JournalLines();
            var controlChange = first.PartyType == PartyKind.Customer ? gain : -gain;
            lines.Signed(controlRole, null, controlChange, new PostingContext(PartyPostingGroup: first.PartyPostingGroup), first.PartyType, first.PartyId)
                .Fx(gain, PostingRoles.RealizedFxGain, PostingRoles.RealizedFxLoss);
            var posted = await journals.PostAsync(new JournalRequest(first.LegalEntityId, date, SourceModules.Finance, FinanceDocTypes.CreditNoteApplication,
                applicationId, "fx", $"Exchange difference on credit note {first.DocumentNumber}", le.Value.FunctionalCurrency, 1m, lines.Lines), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }
        }

        return applied;
    }

    internal async Task<string?> PostingGroupAsync(PartyKind partyType, Guid partyId, CancellationToken ct)
    {
        if (partyType == PartyKind.Customer)
        {
            var customer = await parties.GetCustomerAsync(partyId, ct);
            return customer is { IsActive: true } ? customer.PostingGroup : null;
        }

        var supplier = await parties.GetSupplierAsync(partyId, ct);
        return supplier is { IsActive: true } ? supplier.PostingGroup : null;
    }

    internal static OpenItemSummary ToSummary(OpenItem i) =>
        new(i.Id, i.Installment, i.DueDate, i.Currency, i.OriginalAmount, i.OpenAmount, i.OriginalBase, i.OpenBase, i.Status.ToString());
}

public sealed class CreditExposureQueries(IFinanceDbContext db) : ICreditExposureQueries
{
    public async Task<CustomerExposure> GetCustomerExposureAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var open = await db.OpenItems.AsNoTracking()
            .Where(i => i.PartyType == PartyKind.Customer && i.PartyId == customerId && i.Status != OpenItemStatus.Cancelled)
            .SumAsync(i => i.Sign * i.OpenBase, cancellationToken);
        var advances = await db.Settlements.AsNoTracking()
            .Where(s => s.Direction == SettlementDirection.Receipt && s.PartyId == customerId && s.Status == DocumentStatus.Posted && s.UnallocatedAmount > 0)
            .Select(s => new { s.UnallocatedAmount, s.ExchangeRate })
            .ToListAsync(cancellationToken);
        var advanceBase = advances.Sum(a => FinanceMath.ToBase(a.UnallocatedAmount, a.ExchangeRate));
        return new CustomerExposure(customerId, open, advanceBase, open - advanceBase);
    }
}

public sealed class OpenItemQueries(IFinanceDbContext db) : IOpenItemQueries
{
    public async Task<IReadOnlyList<OpenItemView>> GetOpenItemsAsync(PartyKind partyType, Guid partyId, bool includeSettled = false, CancellationToken cancellationToken = default)
    {
        var query = db.OpenItems.AsNoTracking().Where(i => i.PartyType == partyType && i.PartyId == partyId && i.Status != OpenItemStatus.Cancelled);
        if (!includeSettled)
        {
            query = query.Where(i => i.OpenAmount > 0);
        }

        var items = await query.OrderBy(i => i.DueDate).ThenBy(i => i.DocumentNumber).ToListAsync(cancellationToken);
        return items.Select(ToView).ToList();
    }

    public async Task<AgingReport> GetAgingAsync(PartyKind partyType, Guid legalEntityId, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var items = await db.OpenItems.AsNoTracking()
            .Where(i => i.PartyType == partyType && i.LegalEntityId == legalEntityId && i.Status != OpenItemStatus.Cancelled && i.DocumentDate <= asOf)
            .ToListAsync(cancellationToken);
        var ids = items.Select(i => i.Id).ToList();
        var relieved = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.OpenItemId) && a.Date <= asOf)
            .GroupBy(a => a.OpenItemId).Select(g => new { g.Key, Base = g.Sum(a => a.BaseRelieved) })
            .ToDictionaryAsync(x => x.Key, x => x.Base, cancellationToken);

        var rows = new Dictionary<Guid, decimal[]>();
        foreach (var item in items)
        {
            var openBase = item.OriginalBase - relieved.GetValueOrDefault(item.Id);
            if (openBase == 0)
            {
                continue;
            }

            var buckets = rows.TryGetValue(item.PartyId, out var b) ? b : rows[item.PartyId] = new decimal[5];
            buckets[FinanceMath.AgingBucket(item.DueDate, asOf)] += item.Sign * openBase;
        }

        var list = rows.Select(r => Row(r.Key, r.Value)).OrderByDescending(r => r.Total).ToList();
        var totals = new decimal[5];
        foreach (var row in rows.Values)
        {
            for (var i = 0; i < 5; i++)
            {
                totals[i] += row[i];
            }
        }

        return new AgingReport(partyType, legalEntityId, asOf, list, Row(Guid.Empty, totals));
    }

    public async Task<PartyStatement> GetStatementAsync(PartyKind partyType, Guid partyId, Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        // Entries in "increases what the party owes us / we owe the party" terms: positive = invoice side.
        var entries = new List<(DateOnly Date, int Order, string Type, string Reference, string Description, decimal Amount)>();

        var items = await db.OpenItems.AsNoTracking()
            .Where(i => i.PartyType == partyType && i.PartyId == partyId && i.LegalEntityId == legalEntityId && i.Status != OpenItemStatus.Cancelled && i.DocumentDate <= to)
            .ToListAsync(cancellationToken);
        foreach (var doc in items.GroupBy(i => (i.SourceDocType, i.SourceDocId)))
        {
            var first = doc.First();
            entries.Add((first.DocumentDate, 0, first.SourceDocType, first.DocumentNumber, first.Sign > 0 ? "Invoice" : "Credit/debit note", doc.Sum(i => i.Sign * i.OriginalBase)));
        }

        var direction = partyType == PartyKind.Customer ? SettlementDirection.Receipt : SettlementDirection.Payment;
        var settlements = await db.Settlements.AsNoTracking()
            .Where(s => s.Direction == direction && s.PartyId == partyId && s.LegalEntityId == legalEntityId && s.Status == DocumentStatus.Posted && s.Date <= to)
            .ToListAsync(cancellationToken);
        foreach (var s in settlements)
        {
            var value = FinanceMath.ToBase(s.Amount, s.ExchangeRate) + FinanceMath.ToBase(s.WithholdingAmount, s.ExchangeRate);
            entries.Add((s.Date, 1, direction.ToString(), s.Number ?? string.Empty, s.WithholdingAmount > 0 ? "Including tax withheld" : string.Empty, -value));
        }

        var itemIds = items.Select(i => i.Id).ToList();
        var fx = await db.Allocations.AsNoTracking()
            .Where(a => itemIds.Contains(a.OpenItemId) && a.RealizedFxBase != 0 && a.Date <= to)
            .ToListAsync(cancellationToken);
        foreach (var a in fx)
        {
            // A gain raises what a customer owes in base terms and lowers what we owe a supplier.
            var amount = partyType == PartyKind.Customer ? a.RealizedFxBase : -a.RealizedFxBase;
            entries.Add((a.Date, 2, "ExchangeDifference", a.SettlementReference, a.RealizedFxBase > 0 ? "Exchange gain" : "Exchange loss", amount));
        }

        var opening = entries.Where(e => e.Date < from).Sum(e => e.Amount);
        var balance = opening;
        var lines = new List<Contracts.StatementLine>();
        foreach (var e in entries.Where(e => e.Date >= from).OrderBy(e => e.Date).ThenBy(e => e.Order))
        {
            balance += e.Amount;
            // Customers: invoices are debits. Suppliers: invoices are credits (the statement as seen in our books).
            var (debit, credit) = partyType == PartyKind.Customer
                ? (Math.Max(e.Amount, 0), Math.Max(-e.Amount, 0))
                : (Math.Max(-e.Amount, 0), Math.Max(e.Amount, 0));
            lines.Add(new Contracts.StatementLine(e.Date, e.Type, e.Reference, e.Description, debit, credit, balance));
        }

        return new PartyStatement(partyType, partyId, legalEntityId, from, to, opening, lines, balance);
    }

    internal static OpenItemView ToView(OpenItem i) => new(i.Id, i.PartyType, i.PartyId, i.LegalEntityId, i.SourceModule, i.SourceDocType, i.SourceDocId,
        i.DocumentNumber, i.DocumentDate, i.Installment, i.DueDate, i.Currency, i.ExchangeRate, i.Sign, i.OriginalAmount, i.OpenAmount, i.OriginalBase, i.OpenBase, i.Status.ToString());

    private static AgingRow Row(Guid partyId, decimal[] b) => new(partyId, b[0], b[1], b[2], b[3], b[4], b.Sum());
}

public sealed class CashBankAccountQueries(IFinanceDbContext db) : ICashBankAccountQueries
{
    public async Task<CashBankAccountInfo?> GetAsync(Guid cashBankAccountId, CancellationToken cancellationToken = default) =>
        await db.CashBankAccounts.AsNoTracking().Where(a => a.Id == cashBankAccountId)
            .Select(a => new CashBankAccountInfo(a.Id, a.LegalEntityId, a.Kind.ToString(), a.Code, a.Name, a.Currency, a.GlAccountId, a.IsActive))
            .FirstOrDefaultAsync(cancellationToken);
}
