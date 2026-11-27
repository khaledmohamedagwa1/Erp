using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Application;

// ======================= DTOs =======================
public sealed record CashBankAccountDto(Guid Id, Guid LegalEntityId, string Kind, string Code, string Name, string Currency, Guid? GlAccountId,
    string? BankName, string? AccountNumber, string? Iban, bool IsActive, decimal Balance, decimal BaseBalance);

public sealed record AllocationInput(Guid OpenItemId, decimal Amount);

public sealed record AllocationDto(Guid Id, Guid OpenItemId, string SettlementKind, Guid SettlementId, string Reference, DateOnly Date, decimal Amount,
    decimal BaseRelieved, decimal SettlementBase, decimal RealizedFxBase);

public sealed record SettlementDto(Guid Id, string Direction, Guid LegalEntityId, Guid PartyId, string? Number, DateOnly Date, string Method,
    Guid CashBankAccountId, string Currency, decimal ExchangeRate, decimal Amount, Guid? WithholdingTaxGroupId, decimal WithholdingBase,
    decimal WithholdingAmount, decimal SettlementValue, string? Reference, string? Description, string Status, decimal UnallocatedAmount,
    Guid? JournalId, IReadOnlyList<AllocationInput> RequestedAllocations, IReadOnlyList<AllocationDto> Allocations);

public sealed record TransferDto(Guid Id, Guid LegalEntityId, string? Number, DateOnly Date, Guid FromAccountId, decimal FromAmount, decimal FromRate,
    Guid ToAccountId, decimal ToAmount, decimal ToRate, decimal Charges, string? Description, string Status, Guid? JournalId);

public sealed record ExpenseLineInput(Guid AccountId, string? Description, decimal NetAmount, Guid? TaxGroupId, Guid? CostCenterId);

public sealed record ExpenseLineDto(Guid Id, Guid AccountId, string? Description, decimal NetAmount, Guid? TaxGroupId, Guid? CostCenterId,
    decimal RecoverableTax, decimal NonRecoverableTax, decimal Gross);

public sealed record ExpenseDto(Guid Id, Guid LegalEntityId, string? Number, DateOnly Date, string Payee, Guid? CashBankAccountId, Guid? SupplierId,
    string Currency, decimal ExchangeRate, string? Reference, string? Description, string Status, decimal Total, Guid? JournalId, IReadOnlyList<ExpenseLineDto> Lines);

public sealed record MovementDto(Guid Id, DateOnly Date, decimal Amount, decimal BaseAmount, string SourceDocType, Guid SourceDocId, string Reference,
    string? Description, Guid? ReconciliationId);

public sealed record StatementLineInput(DateOnly Date, decimal Amount, string? Description, string? Reference);

public sealed record StatementLineDto(Guid Id, DateOnly Date, decimal Amount, string? Description, string? Reference, Guid? MatchedMovementId);

public sealed record ReconciliationDto(Guid Id, Guid CashBankAccountId, DateOnly StatementDate, decimal StatementBalance, decimal BookBalance,
    string Status, IReadOnlyList<StatementLineDto> Lines);

public sealed record RevaluationLineDto(string Kind, Guid ReferenceId, string Currency, decimal OpenAmount, decimal HistoricalBase, decimal RevaluedBase, decimal Difference);

public sealed record RevaluationDto(Guid Id, Guid LegalEntityId, DateOnly AsOf, DateOnly ReversalDate, decimal TotalGain, decimal TotalLoss,
    Guid? JournalId, Guid? ReversalJournalId, IReadOnlyList<RevaluationLineDto> Lines);

internal static class FinanceMapping
{
    public static SettlementDto ToDto(Settlement s, IEnumerable<Allocation> allocations) => new(s.Id, s.Direction.ToString(), s.LegalEntityId, s.PartyId,
        s.Number, s.Date, s.Method.ToString(), s.CashBankAccountId, s.Currency, s.ExchangeRate, s.Amount, s.WithholdingTaxGroupId, s.WithholdingBase,
        s.WithholdingAmount, s.SettlementValue, s.Reference, s.Description, s.Status.ToString(), s.UnallocatedAmount, s.JournalId,
        s.Allocations.Select(a => new AllocationInput(a.OpenItemId, a.Amount)).ToList(),
        allocations.Select(ToDto).ToList());

    public static AllocationDto ToDto(Allocation a) => new(a.Id, a.OpenItemId, a.SettlementKind.ToString(), a.SettlementId, a.SettlementReference, a.Date,
        a.Amount, a.BaseRelieved, a.SettlementBase, a.RealizedFxBase);

    public static TransferDto ToDto(InternalTransfer t) => new(t.Id, t.LegalEntityId, t.Number, t.Date, t.FromAccountId, t.FromAmount, t.FromRate,
        t.ToAccountId, t.ToAmount, t.ToRate, t.Charges, t.Description, t.Status.ToString(), t.JournalId);

    public static ExpenseDto ToDto(Expense e) => new(e.Id, e.LegalEntityId, e.Number, e.Date, e.Payee.ToString(), e.CashBankAccountId, e.SupplierId,
        e.Currency, e.ExchangeRate, e.Reference, e.Description, e.Status.ToString(), e.Total, e.JournalId,
        e.Lines.Select(l => new ExpenseLineDto(l.Uid, l.AccountId, l.Description, l.NetAmount, l.TaxGroupId, l.CostCenterId, l.RecoverableTax, l.NonRecoverableTax, l.Gross)).ToList());

    public static MovementDto ToDto(CashMovement m) => new(m.Id, m.Date, m.Amount, m.BaseAmount, m.SourceDocType, m.SourceDocId, m.Reference, m.Description, m.ReconciliationId);

    public static RevaluationDto ToDto(FxRevaluationRun r) => new(r.Id, r.LegalEntityId, r.AsOf, r.ReversalDate, r.TotalGain, r.TotalLoss, r.JournalId,
        r.ReversalJournalId, r.Lines.Select(l => new RevaluationLineDto(l.Kind, l.ReferenceId, l.Currency, l.OpenAmount, l.HistoricalBase, l.RevaluedBase, l.Difference)).ToList());

    public static PostingContext Party(string? postingGroup) => new(PartyPostingGroup: postingGroup);
}

/// <summary>Number allocation for finance documents: gapless per company, legal entity, document type and year (ADR-021).</summary>
internal static class FinanceNumbers
{
    public static Task<string> NextAsync(IDocumentNumberGenerator numbers, Guid companyId, Guid legalEntityId, string docType, string prefix, DateOnly date, CancellationToken ct) =>
        numbers.NextAsync(new NumberScope(companyId, docType, prefix, LegalEntityId: legalEntityId, FiscalYear: date.Year), ct);
}

// ======================= Cash & bank accounts =======================
public sealed record CreateCashBankAccountCommand(string Kind, Guid LegalEntityId, string Code, string Name, string Currency, Guid? GlAccountId,
    string? BankName, string? AccountNumber, string? Iban) : ICommand<CashBankAccountDto>;

public sealed class CreateCashBankAccountValidator : AbstractValidator<CreateCashBankAccountCommand>
{
    public CreateCashBankAccountValidator()
    {
        RuleFor(x => x.Kind).Must(k => Enum.TryParse<CashBankKind>(k, true, out _)).WithMessage("Kind must be Cash or Bank.");
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Iban).MaximumLength(34);
        RuleFor(x => x.AccountNumber).MaximumLength(50);
        RuleFor(x => x.BankName).MaximumLength(200);
    }
}

public sealed class CreateCashBankAccountHandler(IFinanceDbContext db, FinanceScope scope) : ICommandHandler<CreateCashBankAccountCommand, CashBankAccountDto>
{
    public async Task<Result<CashBankAccountDto>> Handle(CreateCashBankAccountCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.CashBankAccounts.AnyAsync(a => a.Code == code, cancellationToken))
        {
            return Error.Conflict("finance.account-code-taken", "A cash/bank account with this code already exists.");
        }

        var account = CashBankAccount.Create(le.Value.CompanyId, le.Value.Id, Enum.Parse<CashBankKind>(command.Kind, true), code, command.Name,
            command.Currency, command.GlAccountId, command.BankName, command.AccountNumber, command.Iban);
        db.CashBankAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return CashBankQueries.ToDto(account, 0, 0);
    }
}

public sealed record ListCashBankAccountsQuery(string? Kind) : IQuery<IReadOnlyList<CashBankAccountDto>>;

public sealed class CashBankQueries(IFinanceDbContext db) : IQueryHandler<ListCashBankAccountsQuery, IReadOnlyList<CashBankAccountDto>>
{
    public async Task<Result<IReadOnlyList<CashBankAccountDto>>> Handle(ListCashBankAccountsQuery query, CancellationToken cancellationToken)
    {
        var accounts = db.CashBankAccounts.AsNoTracking();
        if (Enum.TryParse<CashBankKind>(query.Kind, true, out var kind))
        {
            accounts = accounts.Where(a => a.Kind == kind);
        }

        var list = await accounts.OrderBy(a => a.Code).ToListAsync(cancellationToken);
        var balances = await db.CashMovements.AsNoTracking().GroupBy(m => m.CashBankAccountId)
            .Select(g => new { g.Key, Amount = g.Sum(m => m.Amount), Base = g.Sum(m => m.BaseAmount) })
            .ToDictionaryAsync(x => x.Key, cancellationToken);
        return list.Select(a => ToDto(a, balances.TryGetValue(a.Id, out var b) ? b.Amount : 0, b?.Base ?? 0)).ToList();
    }

    internal static CashBankAccountDto ToDto(CashBankAccount a, decimal balance, decimal baseBalance) => new(a.Id, a.LegalEntityId, a.Kind.ToString(), a.Code,
        a.Name, a.Currency, a.GlAccountId, a.BankName, a.AccountNumber, a.Iban, a.IsActive, balance, baseBalance);
}

public sealed record ListMovementsQuery(Guid CashBankAccountId, bool UnreconciledOnly, PageRequest Page) : IQuery<PagedResult<MovementDto>>;

public sealed class ListMovementsHandler(IFinanceDbContext db) : IQueryHandler<ListMovementsQuery, PagedResult<MovementDto>>
{
    public async Task<Result<PagedResult<MovementDto>>> Handle(ListMovementsQuery query, CancellationToken cancellationToken)
    {
        var source = db.CashMovements.AsNoTracking().Where(m => m.CashBankAccountId == query.CashBankAccountId);
        if (query.UnreconciledOnly)
        {
            source = source.Where(m => m.ReconciliationId == null);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(m => m.Date).ThenBy(m => m.Reference).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<MovementDto>(items.Select(FinanceMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ======================= Receipts & payments =======================
public sealed record CreateSettlementCommand(
    SettlementDirection Direction,
    Guid LegalEntityId,
    Guid PartyId,
    DateOnly Date,
    string Method,
    Guid CashBankAccountId,
    decimal Amount,
    decimal? ExchangeRate,
    Guid? WithholdingTaxGroupId,
    decimal? WithholdingBase,
    IReadOnlyList<AllocationInput>? Allocations,
    string? Reference,
    string? Description) : ICommand<SettlementDto>;

public sealed class CreateSettlementValidator : AbstractValidator<CreateSettlementCommand>
{
    public CreateSettlementValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.PartyId).NotEmpty();
        RuleFor(x => x.CashBankAccountId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Method).Must(m => Enum.TryParse<PaymentMethod>(m, true, out _)).WithMessage("Method must be Cash, BankTransfer, Cheque or Card.");
        RuleFor(x => x.WithholdingBase).GreaterThan(0).When(x => x.WithholdingTaxGroupId is not null);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleForEach(x => x.Allocations).ChildRules(a => a.RuleFor(x => x.Amount).GreaterThan(0));
    }
}

public sealed class CreateSettlementHandler(
    IFinanceDbContext db,
    FinanceScope scope,
    SubledgerService subledger,
    ITaxCalculator taxes) : ICommandHandler<CreateSettlementCommand, SettlementDto>
{
    public async Task<Result<SettlementDto>> Handle(CreateSettlementCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var partyType = command.Direction == SettlementDirection.Receipt ? PartyKind.Customer : PartyKind.Supplier;
        if (await subledger.PostingGroupAsync(partyType, command.PartyId, cancellationToken) is null)
        {
            return FinanceAppErrors.PartyNotFound;
        }

        var account = await db.CashBankAccounts.FirstOrDefaultAsync(a => a.Id == command.CashBankAccountId && a.IsActive && a.LegalEntityId == command.LegalEntityId, cancellationToken);
        if (account is null)
        {
            return FinanceAppErrors.AccountNotFound;
        }

        var rate = await scope.RateAsync(account.Currency, le.Value.FunctionalCurrency, command.Date, command.ExchangeRate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var settlement = Settlement.Create(le.Value.CompanyId, le.Value.Id, command.Direction, command.PartyId, command.Date,
            Enum.Parse<PaymentMethod>(command.Method, true), account.Id, account.Currency, rate.Value, command.Amount, command.Reference, command.Description);

        if (command.WithholdingTaxGroupId is { } groupId)
        {
            var tax = await taxes.CalculateAsync(groupId, command.WithholdingBase!.Value, false, account.Currency, command.Date, cancellationToken);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            if (tax.Value.TotalWithholding <= 0)
            {
                return FinanceAppErrors.WithholdingGroupInvalid;
            }

            settlement.SetWithholding(groupId, command.WithholdingBase.Value, tax.Value.TotalWithholding);
        }

        var requested = (command.Allocations ?? []).Select(a => new RequestedAllocation(a.OpenItemId, a.Amount)).ToList();
        var check = await SettlementRules.LoadAllocatableAsync(db, settlement, requested, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        settlement.SetAllocations(requested);
        db.Settlements.Add(settlement);
        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(settlement, []);
    }
}

internal static class SettlementRules
{
    /// <summary>Loads the open items being allocated and checks party, legal entity, currency and open amounts.</summary>
    public static async Task<Result<IReadOnlyList<(OpenItem Item, decimal Amount)>>> LoadAllocatableAsync(
        IFinanceDbContext db, Settlement settlement, IReadOnlyList<RequestedAllocation> requested, CancellationToken ct)
    {
        if (requested.Sum(a => a.Amount) > settlement.SettlementValue)
        {
            return FinanceErrors.OverAllocation;
        }

        return await LoadAsync(db, settlement.PartyType, settlement.PartyId, settlement.LegalEntityId, settlement.Currency, requested, ct);
    }

    public static async Task<Result<IReadOnlyList<(OpenItem Item, decimal Amount)>>> LoadAsync(
        IFinanceDbContext db, PartyKind partyType, Guid partyId, Guid legalEntityId, string currency, IReadOnlyList<RequestedAllocation> requested, CancellationToken ct)
    {
        var ids = requested.Select(a => a.OpenItemId).Distinct().ToList();
        var items = await db.OpenItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var result = new List<(OpenItem, decimal)>();
        foreach (var group in requested.GroupBy(a => a.OpenItemId))
        {
            if (!items.TryGetValue(group.Key, out var item) || item.PartyType != partyType || item.PartyId != partyId || item.LegalEntityId != legalEntityId || item.Sign < 0)
            {
                return FinanceAppErrors.OpenItemNotFound;
            }

            if (!string.Equals(item.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                return FinanceErrors.CurrencyMismatch;
            }

            var amount = group.Sum(a => a.Amount);
            if (item.Status is OpenItemStatus.Settled or OpenItemStatus.Cancelled || amount > item.OpenAmount)
            {
                return FinanceErrors.AllocationExceedsOpen;
            }

            result.Add((item, amount));
        }

        return result;
    }
}

public sealed record PostSettlementCommand(SettlementDirection Direction, Guid SettlementId) : ICommand<SettlementDto>;

/// <summary>
/// Posts a receipt or payment atomically: settles the open items, books realized exchange differences, keeps the remainder as an
/// advance, posts one balanced functional-currency journal and records the cash movement.
/// </summary>
public sealed class PostSettlementHandler(
    IFinanceDbContext db,
    FinanceScope scope,
    SubledgerService subledger,
    AllocationService allocations,
    IJournalPostingService journals,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostSettlementCommand, SettlementDto>
{
    public async Task<Result<SettlementDto>> Handle(PostSettlementCommand command, CancellationToken cancellationToken)
    {
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == command.SettlementId && s.Direction == command.Direction, cancellationToken);
        if (settlement is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        if (settlement.Status != DocumentStatus.Draft)
        {
            return FinanceErrors.NotDraft;
        }

        var le = await scope.LegalEntityAsync(settlement.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var account = await db.CashBankAccounts.FirstOrDefaultAsync(a => a.Id == settlement.CashBankAccountId, cancellationToken);
        var postingGroup = await subledger.PostingGroupAsync(settlement.PartyType, settlement.PartyId, cancellationToken);
        if (account is null || !account.IsActive)
        {
            return FinanceAppErrors.AccountNotFound;
        }

        if (postingGroup is null)
        {
            return FinanceAppErrors.PartyNotFound;
        }

        var items = await SettlementRules.LoadAllocatableAsync(db, settlement, settlement.Allocations.ToList(), cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        var isReceipt = settlement.Direction == SettlementDirection.Receipt;
        var number = await FinanceNumbers.NextAsync(numbers, settlement.CompanyId, settlement.LegalEntityId, isReceipt ? FinanceDocTypes.Receipt : FinanceDocTypes.Payment,
            isReceipt ? "RCT" : "PAY", settlement.Date, cancellationToken);
        var outcome = allocations.Apply(items.Value, isReceipt ? SettlementKind.Receipt : SettlementKind.Payment, settlement.Id, number, settlement.Date, settlement.ExchangeRate);

        var cashBase = FinanceMath.ToBase(settlement.Amount, settlement.ExchangeRate);
        var withheldBase = FinanceMath.ToBase(settlement.WithholdingAmount, settlement.ExchangeRate);
        var advanceBase = cashBase + withheldBase - outcome.SettlementBase;
        var gain = FinanceMath.RealizedFx(settlement.PartyType, outcome.RelievedBase, outcome.SettlementBase);
        var party = FinanceMapping.Party(postingGroup);
        var lines = new JournalLines();
        var cashRole = account.GlAccountId is null ? account.Role : null;
        if (isReceipt)
        {
            lines.Debit(cashRole, account.GlAccountId, cashBase, description: account.Code)
                .Debit(PostingRoles.WithholdingTaxReceivable, null, withheldBase)
                .Credit(PostingRoles.CustomerReceivable, null, outcome.RelievedBase, party, PartyKind.Customer, settlement.PartyId)
                .Credit(PostingRoles.CustomerAdvance, null, advanceBase, party, PartyKind.Customer, settlement.PartyId);
        }
        else
        {
            lines.Credit(cashRole, account.GlAccountId, cashBase, description: account.Code)
                .Credit(PostingRoles.WithholdingTaxPayable, null, withheldBase)
                .Debit(PostingRoles.SupplierPayable, null, outcome.RelievedBase, party, PartyKind.Supplier, settlement.PartyId)
                .Debit(PostingRoles.SupplierAdvance, null, advanceBase, party, PartyKind.Supplier, settlement.PartyId);
        }

        lines.Fx(gain, PostingRoles.RealizedFxGain, PostingRoles.RealizedFxLoss);
        var posted = await journals.PostAsync(new JournalRequest(settlement.LegalEntityId, settlement.Date, SourceModules.Finance,
            isReceipt ? FinanceDocTypes.Receipt : FinanceDocTypes.Payment, settlement.Id, "post",
            $"{(isReceipt ? "Receipt" : "Payment")} {number}", le.Value.FunctionalCurrency, 1m, lines.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var unallocated = settlement.SettlementValue - items.Value.Sum(i => i.Amount);
        settlement.MarkPosted(number, posted.Value.JournalId, unallocated);
        db.CashMovements.Add(CashMovement.Create(account, settlement.Date, isReceipt ? settlement.Amount : -settlement.Amount, isReceipt ? cashBase : -cashBase,
            isReceipt ? FinanceDocTypes.Receipt : FinanceDocTypes.Payment, settlement.Id, settlement.Reference ?? number, settlement.Description));
        await allocations.AnnounceAsync(items.Value.Select(i => i.Item), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(settlement, outcome.Allocations);
    }
}

public sealed record AllocateAdvanceCommand(SettlementDirection Direction, Guid SettlementId, DateOnly Date, IReadOnlyList<AllocationInput> Allocations) : ICommand<SettlementDto>;

public sealed class AllocateAdvanceValidator : AbstractValidator<AllocateAdvanceCommand>
{
    public AllocateAdvanceValidator()
    {
        RuleFor(x => x.Allocations).NotEmpty();
        RuleForEach(x => x.Allocations).ChildRules(a => a.RuleFor(x => x.Amount).GreaterThan(0));
    }
}

/// <summary>Allocates the unallocated remainder (advance) of a posted receipt/payment to open items; books the exchange difference.</summary>
public sealed class AllocateAdvanceHandler(
    IFinanceDbContext db,
    FinanceScope scope,
    AllocationService allocations,
    IJournalPostingService journals) : ICommandHandler<AllocateAdvanceCommand, SettlementDto>
{
    public async Task<Result<SettlementDto>> Handle(AllocateAdvanceCommand command, CancellationToken cancellationToken)
    {
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == command.SettlementId && s.Direction == command.Direction, cancellationToken);
        if (settlement is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        if (settlement.Status != DocumentStatus.Posted)
        {
            return Error.Conflict("finance.not-posted", "Only posted receipts and payments have an advance to allocate.");
        }

        var requested = command.Allocations.Select(a => new RequestedAllocation(a.OpenItemId, a.Amount)).ToList();
        if (requested.Sum(a => a.Amount) > settlement.UnallocatedAmount)
        {
            return FinanceErrors.OverAllocation;
        }

        var le = await scope.LegalEntityAsync(settlement.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var items = await SettlementRules.LoadAsync(db, settlement.PartyType, settlement.PartyId, settlement.LegalEntityId, settlement.Currency, requested, cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        var batchId = SequentialGuid.New();
        var outcome = allocations.Apply(items.Value, SettlementKind.Advance, settlement.Id, settlement.Number ?? string.Empty, command.Date, settlement.ExchangeRate);
        settlement.ConsumeAdvance(requested.Sum(a => a.Amount));
        var gain = FinanceMath.RealizedFx(settlement.PartyType, outcome.RelievedBase, outcome.SettlementBase);
        var postingGroup = items.Value[0].Item.PartyPostingGroup;
        var party = FinanceMapping.Party(postingGroup);
        var lines = new JournalLines();
        if (settlement.PartyType == PartyKind.Customer)
        {
            lines.Debit(PostingRoles.CustomerAdvance, null, outcome.SettlementBase, party, PartyKind.Customer, settlement.PartyId)
                .Credit(PostingRoles.CustomerReceivable, null, outcome.RelievedBase, party, PartyKind.Customer, settlement.PartyId);
        }
        else
        {
            lines.Credit(PostingRoles.SupplierAdvance, null, outcome.SettlementBase, party, PartyKind.Supplier, settlement.PartyId)
                .Debit(PostingRoles.SupplierPayable, null, outcome.RelievedBase, party, PartyKind.Supplier, settlement.PartyId);
        }

        lines.Fx(gain, PostingRoles.RealizedFxGain, PostingRoles.RealizedFxLoss);
        var posted = await journals.PostAsync(new JournalRequest(settlement.LegalEntityId, command.Date, SourceModules.Finance, FinanceDocTypes.AdvanceAllocation,
            batchId, "allocation", $"Allocation of advance {settlement.Number}", le.Value.FunctionalCurrency, 1m, lines.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        await allocations.AnnounceAsync(items.Value.Select(i => i.Item), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var all = await db.Allocations.AsNoTracking().Where(a => a.SettlementId == settlement.Id).OrderBy(a => a.Date).ToListAsync(cancellationToken);
        return FinanceMapping.ToDto(settlement, all);
    }
}

public sealed record GetSettlementQuery(SettlementDirection Direction, Guid SettlementId) : IQuery<SettlementDto>;

public sealed class GetSettlementHandler(IFinanceDbContext db) : IQueryHandler<GetSettlementQuery, SettlementDto>
{
    public async Task<Result<SettlementDto>> Handle(GetSettlementQuery query, CancellationToken cancellationToken)
    {
        var s = await db.Settlements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.SettlementId && x.Direction == query.Direction, cancellationToken);
        if (s is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        var allocations = await db.Allocations.AsNoTracking().Where(a => a.SettlementId == s.Id && a.SettlementKind != SettlementKind.CreditNote)
            .OrderBy(a => a.Date).ToListAsync(cancellationToken);
        return FinanceMapping.ToDto(s, allocations);
    }
}

public sealed record ListSettlementsQuery(SettlementDirection Direction, Guid? PartyId, string? Status, PageRequest Page) : IQuery<PagedResult<SettlementDto>>;

public sealed class ListSettlementsHandler(IFinanceDbContext db) : IQueryHandler<ListSettlementsQuery, PagedResult<SettlementDto>>
{
    public async Task<Result<PagedResult<SettlementDto>>> Handle(ListSettlementsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Settlements.AsNoTracking().Where(s => s.Direction == query.Direction);
        if (query.PartyId is { } partyId)
        {
            source = source.Where(s => s.PartyId == partyId);
        }

        if (Enum.TryParse<DocumentStatus>(query.Status, true, out var status))
        {
            source = source.Where(s => s.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(s => s.Date).ThenByDescending(s => s.Number).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<SettlementDto>(items.Select(s => FinanceMapping.ToDto(s, [])).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ======================= Open items & reports =======================
public sealed record ListOpenItemsQuery(string? PartyType, Guid? PartyId, Guid? LegalEntityId, bool OpenOnly, PageRequest Page) : IQuery<PagedResult<OpenItemView>>;

public sealed class ListOpenItemsHandler(IFinanceDbContext db) : IQueryHandler<ListOpenItemsQuery, PagedResult<OpenItemView>>
{
    public async Task<Result<PagedResult<OpenItemView>>> Handle(ListOpenItemsQuery query, CancellationToken cancellationToken)
    {
        var source = db.OpenItems.AsNoTracking().Where(i => i.Status != OpenItemStatus.Cancelled);
        if (Enum.TryParse<PartyKind>(query.PartyType, true, out var partyType))
        {
            source = source.Where(i => i.PartyType == partyType);
        }

        if (query.PartyId is { } partyId)
        {
            source = source.Where(i => i.PartyId == partyId);
        }

        if (query.LegalEntityId is { } le)
        {
            source = source.Where(i => i.LegalEntityId == le);
        }

        if (query.OpenOnly)
        {
            source = source.Where(i => i.OpenAmount > 0);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(i => i.DueDate).ThenBy(i => i.DocumentNumber).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<OpenItemView>(items.Select(OpenItemQueries.ToView).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record AgingQuery(PartyKind PartyType, Guid LegalEntityId, DateOnly AsOf) : IQuery<AgingReport>;

public sealed class AgingHandler(IOpenItemQueries queries) : IQueryHandler<AgingQuery, AgingReport>
{
    public async Task<Result<AgingReport>> Handle(AgingQuery query, CancellationToken cancellationToken) =>
        await queries.GetAgingAsync(query.PartyType, query.LegalEntityId, query.AsOf, cancellationToken);
}

public sealed record StatementQuery(PartyKind PartyType, Guid PartyId, Guid LegalEntityId, DateOnly From, DateOnly To) : IQuery<PartyStatement>;

public sealed class StatementHandler(IOpenItemQueries queries) : IQueryHandler<StatementQuery, PartyStatement>
{
    public async Task<Result<PartyStatement>> Handle(StatementQuery query, CancellationToken cancellationToken) =>
        await queries.GetStatementAsync(query.PartyType, query.PartyId, query.LegalEntityId, query.From, query.To, cancellationToken);
}

// ======================= Internal transfers =======================
public sealed record CreateTransferCommand(Guid LegalEntityId, DateOnly Date, Guid FromAccountId, decimal FromAmount, decimal? FromRate,
    Guid ToAccountId, decimal ToAmount, decimal? ToRate, decimal Charges, string? Description) : ICommand<TransferDto>;

public sealed class CreateTransferValidator : AbstractValidator<CreateTransferCommand>
{
    public CreateTransferValidator()
    {
        RuleFor(x => x.FromAmount).GreaterThan(0);
        RuleFor(x => x.ToAmount).GreaterThan(0);
        RuleFor(x => x.Charges).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public sealed class CreateTransferHandler(IFinanceDbContext db, FinanceScope scope) : ICommandHandler<CreateTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(CreateTransferCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var ids = new[] { command.FromAccountId, command.ToAccountId };
        var accounts = await db.CashBankAccounts.Where(a => ids.Contains(a.Id) && a.IsActive && a.LegalEntityId == command.LegalEntityId).ToDictionaryAsync(a => a.Id, cancellationToken);
        if (!accounts.TryGetValue(command.FromAccountId, out var from) || !accounts.TryGetValue(command.ToAccountId, out var to))
        {
            return FinanceAppErrors.AccountNotFound;
        }

        if (from.Currency == to.Currency && command.FromAmount != command.ToAmount)
        {
            return Error.Validation("finance.transfer-amount-mismatch", "Accounts in the same currency must transfer the same amount.");
        }

        var fromRate = await scope.RateAsync(from.Currency, le.Value.FunctionalCurrency, command.Date, command.FromRate, cancellationToken);
        var toRate = await scope.RateAsync(to.Currency, le.Value.FunctionalCurrency, command.Date, command.ToRate, cancellationToken);
        if (fromRate.IsFailure || toRate.IsFailure)
        {
            return FinanceAppErrors.RateMissing;
        }

        var transfer = InternalTransfer.Create(le.Value.CompanyId, le.Value.Id, command.Date, from.Id, command.FromAmount, fromRate.Value,
            to.Id, command.ToAmount, toRate.Value, command.Charges, command.Description);
        db.InternalTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(transfer);
    }
}

public sealed record PostTransferCommand(Guid TransferId) : ICommand<TransferDto>;

public sealed class PostTransferHandler(IFinanceDbContext db, FinanceScope scope, IJournalPostingService journals, IDocumentNumberGenerator numbers)
    : ICommandHandler<PostTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(PostTransferCommand command, CancellationToken cancellationToken)
    {
        var transfer = await db.InternalTransfers.FirstOrDefaultAsync(t => t.Id == command.TransferId, cancellationToken);
        if (transfer is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        if (transfer.Status != DocumentStatus.Draft)
        {
            return FinanceErrors.NotDraft;
        }

        var le = await scope.LegalEntityAsync(transfer.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var from = await db.CashBankAccounts.FirstAsync(a => a.Id == transfer.FromAccountId, cancellationToken);
        var to = await db.CashBankAccounts.FirstAsync(a => a.Id == transfer.ToAccountId, cancellationToken);
        var number = await FinanceNumbers.NextAsync(numbers, transfer.CompanyId, transfer.LegalEntityId, FinanceDocTypes.InternalTransfer, "ITR", transfer.Date, cancellationToken);

        var sentBase = FinanceMath.ToBase(transfer.FromAmount, transfer.FromRate);
        var chargesBase = FinanceMath.ToBase(transfer.Charges, transfer.FromRate);
        var receivedBase = FinanceMath.ToBase(transfer.ToAmount, transfer.ToRate);
        var lines = new JournalLines()
            .Debit(to.GlAccountId is null ? to.Role : null, to.GlAccountId, receivedBase, description: to.Code)
            .Debit(PostingRoles.BankCharges, null, chargesBase)
            .Credit(from.GlAccountId is null ? from.Role : null, from.GlAccountId, sentBase + chargesBase, description: from.Code)
            .Fx(receivedBase - sentBase, PostingRoles.RealizedFxGain, PostingRoles.RealizedFxLoss);
        var posted = await journals.PostAsync(new JournalRequest(transfer.LegalEntityId, transfer.Date, SourceModules.Finance, FinanceDocTypes.InternalTransfer,
            transfer.Id, "post", $"Internal transfer {number}", le.Value.FunctionalCurrency, 1m, lines.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        transfer.MarkPosted(number, posted.Value.JournalId);
        db.CashMovements.Add(CashMovement.Create(from, transfer.Date, -(transfer.FromAmount + transfer.Charges), -(sentBase + chargesBase), FinanceDocTypes.InternalTransfer, transfer.Id, number, transfer.Description));
        db.CashMovements.Add(CashMovement.Create(to, transfer.Date, transfer.ToAmount, receivedBase, FinanceDocTypes.InternalTransfer, transfer.Id, number, transfer.Description));
        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(transfer);
    }
}

public sealed record ListTransfersQuery(PageRequest Page) : IQuery<PagedResult<TransferDto>>;

public sealed class ListTransfersHandler(IFinanceDbContext db) : IQueryHandler<ListTransfersQuery, PagedResult<TransferDto>>
{
    public async Task<Result<PagedResult<TransferDto>>> Handle(ListTransfersQuery query, CancellationToken cancellationToken)
    {
        var source = db.InternalTransfers.AsNoTracking();
        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(t => t.Date).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<TransferDto>(items.Select(FinanceMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ======================= Expenses =======================
public sealed record CreateExpenseCommand(Guid LegalEntityId, DateOnly Date, string Payee, Guid? CashBankAccountId, Guid? SupplierId, string? Currency,
    decimal? ExchangeRate, string? Reference, string? Description, IReadOnlyList<ExpenseLineInput> Lines) : ICommand<ExpenseDto>;

public sealed class CreateExpenseValidator : AbstractValidator<CreateExpenseCommand>
{
    public CreateExpenseValidator()
    {
        RuleFor(x => x.Payee).Must(p => Enum.TryParse<ExpensePayee>(p, true, out _)).WithMessage("Payee must be CashBank or Supplier.");
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.AccountId).NotEmpty();
            l.RuleFor(x => x.NetAmount).GreaterThan(0);
            l.RuleFor(x => x.Description).MaximumLength(300);
        });
        RuleFor(x => x.Reference).MaximumLength(100);
    }
}

public sealed class CreateExpenseHandler(IFinanceDbContext db, FinanceScope scope, IPartyQueries parties, ITaxCalculator taxes)
    : ICommandHandler<CreateExpenseCommand, ExpenseDto>
{
    public async Task<Result<ExpenseDto>> Handle(CreateExpenseCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var payee = Enum.Parse<ExpensePayee>(command.Payee, true);
        string currency;
        if (payee == ExpensePayee.CashBank)
        {
            var account = await db.CashBankAccounts.FirstOrDefaultAsync(a => a.Id == command.CashBankAccountId && a.IsActive && a.LegalEntityId == command.LegalEntityId, cancellationToken);
            if (account is null)
            {
                return FinanceAppErrors.AccountNotFound;
            }

            currency = account.Currency;
        }
        else
        {
            var supplier = command.SupplierId is { } supplierId ? await parties.GetSupplierAsync(supplierId, cancellationToken) : null;
            if (supplier is not { IsActive: true })
            {
                return FinanceAppErrors.PartyNotFound;
            }

            currency = string.IsNullOrWhiteSpace(command.Currency) ? supplier.Currency : command.Currency.ToUpperInvariant();
        }

        var rate = await scope.RateAsync(currency, le.Value.FunctionalCurrency, command.Date, command.ExchangeRate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var lines = new List<ExpenseLine>();
        foreach (var input in command.Lines)
        {
            var line = new ExpenseLine(input.AccountId, input.Description, input.NetAmount, input.TaxGroupId, input.CostCenterId);
            if (input.TaxGroupId is { } groupId)
            {
                var tax = await taxes.CalculateAsync(groupId, input.NetAmount, false, currency, command.Date, cancellationToken);
                if (tax.IsFailure)
                {
                    return tax.Error;
                }

                if (tax.Value.Lines.Any(l => l.Kind != TaxKindCode.Input))
                {
                    return Error.Validation("finance.expense-tax-invalid", "Expense tax groups may only contain input tax components.");
                }

                var recoverable = tax.Value.Lines.Where(l => l.Recoverable).ToList();
                line.SetTax(recoverable.Sum(l => l.Amount), tax.Value.Lines.Where(l => !l.Recoverable).Sum(l => l.Amount), recoverable.FirstOrDefault()?.TaxCodeId);
            }

            lines.Add(line);
        }

        var expense = Expense.Create(le.Value.CompanyId, le.Value.Id, command.Date, payee, command.CashBankAccountId, command.SupplierId, currency,
            rate.Value, command.Reference, command.Description, lines);
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(expense);
    }
}

public sealed record PostExpenseCommand(Guid ExpenseId) : ICommand<ExpenseDto>;

public sealed class PostExpenseHandler(
    IFinanceDbContext db,
    FinanceScope scope,
    IPartyQueries parties,
    ISubledgerService subledger,
    IJournalPostingService journals,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostExpenseCommand, ExpenseDto>
{
    public async Task<Result<ExpenseDto>> Handle(PostExpenseCommand command, CancellationToken cancellationToken)
    {
        var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == command.ExpenseId, cancellationToken);
        if (expense is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        if (expense.Status != DocumentStatus.Draft)
        {
            return FinanceErrors.NotDraft;
        }

        var le = await scope.LegalEntityAsync(expense.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var number = await FinanceNumbers.NextAsync(numbers, expense.CompanyId, expense.LegalEntityId, FinanceDocTypes.Expense, "EXP", expense.Date, cancellationToken);
        var lines = new JournalLines();
        decimal totalBase = 0;
        foreach (var line in expense.Lines)
        {
            var costBase = FinanceMath.ToBase(line.NetAmount + line.NonRecoverableTax, expense.ExchangeRate);
            var taxBase = FinanceMath.ToBase(line.RecoverableTax, expense.ExchangeRate);
            lines.Debit(null, line.AccountId, costBase, costCenterId: line.CostCenterId, description: line.Description)
                .Debit(PostingRoles.InputTax, null, taxBase, new PostingContext(TaxCodeId: line.RecoverableTaxCodeId));
            totalBase += costBase + taxBase;
        }

        if (expense.Payee == ExpensePayee.CashBank)
        {
            var account = await db.CashBankAccounts.FirstAsync(a => a.Id == expense.CashBankAccountId, cancellationToken);
            lines.Credit(account.GlAccountId is null ? account.Role : null, account.GlAccountId, totalBase, description: account.Code);
            db.CashMovements.Add(CashMovement.Create(account, expense.Date, -expense.Total, -totalBase, FinanceDocTypes.Expense, expense.Id, expense.Reference ?? number, expense.Description));
        }
        else
        {
            var supplier = await parties.GetSupplierAsync(expense.SupplierId!.Value, cancellationToken);
            if (supplier is null)
            {
                return FinanceAppErrors.PartyNotFound;
            }

            lines.Credit(PostingRoles.SupplierPayable, null, totalBase, FinanceMapping.Party(supplier.PostingGroup), PartyKind.Supplier, supplier.Id);
            var items = await subledger.CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Supplier, supplier.Id, expense.LegalEntityId, SourceModules.Finance,
                FinanceDocTypes.Expense, expense.Id, expense.Reference ?? number, expense.Date, expense.Currency, expense.ExchangeRate, expense.Total, supplier.PaymentTermsId), cancellationToken);
            if (items.IsFailure)
            {
                return items.Error;
            }
        }

        var posted = await journals.PostAsync(new JournalRequest(expense.LegalEntityId, expense.Date, SourceModules.Finance, FinanceDocTypes.Expense,
            expense.Id, "post", $"Expense {number}", le.Value.FunctionalCurrency, 1m, lines.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        expense.MarkPosted(number, posted.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(expense);
    }
}

public sealed record GetExpenseQuery(Guid ExpenseId) : IQuery<ExpenseDto>;

public sealed class GetExpenseHandler(IFinanceDbContext db) : IQueryHandler<GetExpenseQuery, ExpenseDto>
{
    public async Task<Result<ExpenseDto>> Handle(GetExpenseQuery query, CancellationToken cancellationToken)
    {
        var e = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.ExpenseId, cancellationToken);
        return e is null ? FinanceAppErrors.DocumentNotFound : FinanceMapping.ToDto(e);
    }
}

public sealed record ListExpensesQuery(PageRequest Page) : IQuery<PagedResult<ExpenseDto>>;

public sealed class ListExpensesHandler(IFinanceDbContext db) : IQueryHandler<ListExpensesQuery, PagedResult<ExpenseDto>>
{
    public async Task<Result<PagedResult<ExpenseDto>>> Handle(ListExpensesQuery query, CancellationToken cancellationToken)
    {
        var source = db.Expenses.AsNoTracking();
        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(e => e.Date).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<ExpenseDto>(items.Select(FinanceMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ======================= Bank reconciliation =======================
public sealed record CreateReconciliationCommand(Guid CashBankAccountId, DateOnly StatementDate, decimal StatementBalance, IReadOnlyList<StatementLineInput> Lines)
    : ICommand<ReconciliationDto>;

public sealed class CreateReconciliationValidator : AbstractValidator<CreateReconciliationCommand>
{
    public CreateReconciliationValidator()
    {
        RuleFor(x => x.CashBankAccountId).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Amount).NotEqual(0));
    }
}

public sealed class CreateReconciliationHandler(IFinanceDbContext db) : ICommandHandler<CreateReconciliationCommand, ReconciliationDto>
{
    public async Task<Result<ReconciliationDto>> Handle(CreateReconciliationCommand command, CancellationToken cancellationToken)
    {
        var account = await db.CashBankAccounts.FirstOrDefaultAsync(a => a.Id == command.CashBankAccountId, cancellationToken);
        if (account is null)
        {
            return FinanceAppErrors.AccountNotFound;
        }

        var reconciliation = BankReconciliation.Create(account.CompanyId, account.Id, command.StatementDate, command.StatementBalance,
            command.Lines.Select(l => new BankStatementLine(l.Date, l.Amount, l.Description, l.Reference)));
        var candidates = await ReconciliationQueries.CandidatesAsync(db, account.Id, command.StatementDate, cancellationToken);
        reconciliation.AutoMatch(candidates);
        db.BankReconciliations.Add(reconciliation);
        await db.SaveChangesAsync(cancellationToken);
        return await ReconciliationQueries.ToDtoAsync(db, reconciliation, cancellationToken);
    }
}

public sealed record MatchStatementLineCommand(Guid ReconciliationId, Guid LineId, Guid? MovementId) : ICommand<ReconciliationDto>;

public sealed class MatchStatementLineHandler(IFinanceDbContext db) : ICommandHandler<MatchStatementLineCommand, ReconciliationDto>
{
    public async Task<Result<ReconciliationDto>> Handle(MatchStatementLineCommand command, CancellationToken cancellationToken)
    {
        var reconciliation = await db.BankReconciliations.FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, cancellationToken);
        if (reconciliation is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        if (command.MovementId is { } movementId &&
            !await db.CashMovements.AnyAsync(m => m.Id == movementId && m.CashBankAccountId == reconciliation.CashBankAccountId && m.ReconciliationId == null, cancellationToken))
        {
            return Error.NotFound("finance.movement-not-found", "The movement was not found or is already reconciled.");
        }

        reconciliation.MatchManually(command.LineId, command.MovementId);
        await db.SaveChangesAsync(cancellationToken);
        return await ReconciliationQueries.ToDtoAsync(db, reconciliation, cancellationToken);
    }
}

public sealed record CompleteReconciliationCommand(Guid ReconciliationId) : ICommand<ReconciliationDto>;

public sealed class CompleteReconciliationHandler(IFinanceDbContext db) : ICommandHandler<CompleteReconciliationCommand, ReconciliationDto>
{
    public async Task<Result<ReconciliationDto>> Handle(CompleteReconciliationCommand command, CancellationToken cancellationToken)
    {
        var reconciliation = await db.BankReconciliations.FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, cancellationToken);
        if (reconciliation is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        var book = await ReconciliationQueries.BookBalanceAsync(db, reconciliation.CashBankAccountId, reconciliation.StatementDate, cancellationToken);
        var completed = reconciliation.Complete(book);
        if (completed.IsFailure)
        {
            return completed.Error;
        }

        var matched = reconciliation.Lines.Select(l => l.MatchedMovementId!.Value).ToList();
        foreach (var movement in await db.CashMovements.Where(m => matched.Contains(m.Id)).ToListAsync(cancellationToken))
        {
            movement.MarkReconciled(reconciliation.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ReconciliationQueries.ToDtoAsync(db, reconciliation, cancellationToken);
    }
}

public sealed record GetReconciliationQuery(Guid ReconciliationId) : IQuery<ReconciliationDto>;

public sealed class GetReconciliationHandler(IFinanceDbContext db) : IQueryHandler<GetReconciliationQuery, ReconciliationDto>
{
    public async Task<Result<ReconciliationDto>> Handle(GetReconciliationQuery query, CancellationToken cancellationToken)
    {
        var r = await db.BankReconciliations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.ReconciliationId, cancellationToken);
        return r is null ? FinanceAppErrors.DocumentNotFound : await ReconciliationQueries.ToDtoAsync(db, r, cancellationToken);
    }
}

internal static class ReconciliationQueries
{
    public static async Task<IReadOnlyCollection<CashMovement>> CandidatesAsync(IFinanceDbContext db, Guid accountId, DateOnly statementDate, CancellationToken ct) =>
        await db.CashMovements.AsNoTracking().Where(m => m.CashBankAccountId == accountId && m.ReconciliationId == null && m.Date <= statementDate.AddDays(3)).ToListAsync(ct);

    public static Task<decimal> BookBalanceAsync(IFinanceDbContext db, Guid accountId, DateOnly asOf, CancellationToken ct) =>
        db.CashMovements.AsNoTracking().Where(m => m.CashBankAccountId == accountId && m.Date <= asOf).SumAsync(m => m.Amount, ct);

    public static async Task<ReconciliationDto> ToDtoAsync(IFinanceDbContext db, BankReconciliation r, CancellationToken ct) => new(
        r.Id, r.CashBankAccountId, r.StatementDate, r.StatementBalance, await BookBalanceAsync(db, r.CashBankAccountId, r.StatementDate, ct), r.Status.ToString(),
        r.Lines.Select(l => new StatementLineDto(l.Uid, l.Date, l.Amount, l.Description, l.Reference, l.MatchedMovementId)).ToList());
}

// ======================= FX revaluation =======================
public sealed record RunFxRevaluationCommand(Guid LegalEntityId, DateOnly AsOf) : ICommand<RevaluationDto>;

/// <summary>
/// Month-end unrealized revaluation of open foreign-currency receivables, payables and cash/bank balances at the closing rate
/// (falling back to spot). One journal on the as-of date, reversed automatically on the next day (OQ-ACC-5).
/// </summary>
public sealed class RunFxRevaluationHandler(IFinanceDbContext db, FinanceScope scope, IExchangeRateProvider rates, IJournalPostingService journals)
    : ICommandHandler<RunFxRevaluationCommand, RevaluationDto>
{
    public async Task<Result<RevaluationDto>> Handle(RunFxRevaluationCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (await db.FxRevaluationRuns.AnyAsync(r => r.LegalEntityId == command.LegalEntityId && r.AsOf == command.AsOf, cancellationToken))
        {
            return FinanceAppErrors.RevaluationExists;
        }

        var functional = le.Value.FunctionalCurrency;
        var asOf = command.AsOf;
        var revaluationLines = new List<RevaluationLine>();
        var rateCache = new Dictionary<string, decimal>();

        async Task<decimal?> RateFor(string currency)
        {
            if (rateCache.TryGetValue(currency, out var cached))
            {
                return cached;
            }

            var rate = await rates.GetRateAsync(currency, functional, asOf, ExchangeRateKind.Closing, cancellationToken)
                ?? await rates.GetRateAsync(currency, functional, asOf, ExchangeRateKind.Spot, cancellationToken);
            if (rate is { } r)
            {
                rateCache[currency] = r;
            }

            return rate;
        }

        // Open items as of the date (allocations after it are ignored).
        var items = await db.OpenItems.AsNoTracking()
            .Where(i => i.LegalEntityId == command.LegalEntityId && i.Currency != functional && i.Status != OpenItemStatus.Cancelled && i.DocumentDate <= asOf)
            .ToListAsync(cancellationToken);
        var ids = items.Select(i => i.Id).ToList();
        var settled = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.OpenItemId) && a.Date <= asOf)
            .GroupBy(a => a.OpenItemId).Select(g => new { g.Key, Amount = g.Sum(a => a.Amount), Base = g.Sum(a => a.BaseRelieved) })
            .ToDictionaryAsync(x => x.Key, cancellationToken);
        decimal receivablesChange = 0, payablesGain = 0;
        foreach (var item in items)
        {
            var open = item.OriginalAmount - (settled.TryGetValue(item.Id, out var s) ? s.Amount : 0);
            var historical = item.OriginalBase - (s?.Base ?? 0);
            if (open == 0)
            {
                continue;
            }

            var rate = await RateFor(item.Currency);
            if (rate is null)
            {
                return Error.Validation("finance.rate-missing", $"No closing or spot rate for {item.Currency} on {asOf}.");
            }

            var revalued = FinanceMath.ToBase(open, rate.Value);
            var gain = item.Sign * FinanceMath.Unrealized(item.PartyType, historical, revalued);
            revaluationLines.Add(new RevaluationLine(item.PartyType == PartyKind.Customer ? "Receivable" : "Payable", item.Id, item.Currency, item.Sign * open,
                item.Sign * historical, item.Sign * revalued, gain));
            if (item.PartyType == PartyKind.Customer)
            {
                receivablesChange += gain;
            }
            else
            {
                payablesGain += gain;
            }
        }

        // Foreign-currency cash and bank balances.
        var accounts = await db.CashBankAccounts.AsNoTracking().Where(a => a.LegalEntityId == command.LegalEntityId && a.Currency != functional).ToListAsync(cancellationToken);
        var lines = new JournalLines();
        foreach (var account in accounts)
        {
            var totals = await db.CashMovements.AsNoTracking().Where(m => m.CashBankAccountId == account.Id && m.Date <= asOf)
                .GroupBy(m => 1).Select(g => new { Amount = g.Sum(m => m.Amount), Base = g.Sum(m => m.BaseAmount) }).FirstOrDefaultAsync(cancellationToken);
            if (totals is null || totals.Amount == 0)
            {
                continue;
            }

            var rate = await RateFor(account.Currency);
            if (rate is null)
            {
                return Error.Validation("finance.rate-missing", $"No closing or spot rate for {account.Currency} on {asOf}.");
            }

            var revalued = FinanceMath.ToBase(totals.Amount, rate.Value);
            var gain = revalued - totals.Base;
            revaluationLines.Add(new RevaluationLine("CashBank", account.Id, account.Currency, totals.Amount, totals.Base, revalued, gain));
            lines.Signed(account.GlAccountId is null ? account.Role : null, account.GlAccountId, gain);
        }

        var run = FxRevaluationRun.Create(le.Value.CompanyId, le.Value.Id, asOf, revaluationLines);
        lines.Signed(PostingRoles.CustomerReceivable, null, receivablesChange)
            .Signed(PostingRoles.SupplierPayable, null, payablesGain)
            .Credit(PostingRoles.UnrealizedFxGain, null, run.TotalGain)
            .Debit(PostingRoles.UnrealizedFxLoss, null, run.TotalLoss);

        db.FxRevaluationRuns.Add(run);
        if (lines.Lines.Count >= 2)
        {
            var posted = await journals.PostAsync(new JournalRequest(le.Value.Id, asOf, SourceModules.Finance, FinanceDocTypes.FxRevaluation, run.Id, "revaluation",
                $"Unrealized FX revaluation {asOf:yyyy-MM-dd}", functional, 1m, lines.Lines), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            var reversal = await journals.ReverseAsync(le.Value.Id, FinanceDocTypes.FxRevaluation, run.Id, "revaluation", run.ReversalDate, cancellationToken);
            if (reversal.IsFailure)
            {
                return reversal.Error;
            }

            run.MarkPosted(posted.Value.JournalId, reversal.Value.JournalId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return FinanceMapping.ToDto(run);
    }
}

public sealed record ListRevaluationsQuery(Guid? LegalEntityId) : IQuery<IReadOnlyList<RevaluationDto>>;

public sealed class ListRevaluationsHandler(IFinanceDbContext db) : IQueryHandler<ListRevaluationsQuery, IReadOnlyList<RevaluationDto>>
{
    public async Task<Result<IReadOnlyList<RevaluationDto>>> Handle(ListRevaluationsQuery query, CancellationToken cancellationToken)
    {
        var source = db.FxRevaluationRuns.AsNoTracking();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(r => r.LegalEntityId == le);
        }

        var runs = await source.OrderByDescending(r => r.AsOf).ToListAsync(cancellationToken);
        return runs.Select(FinanceMapping.ToDto).ToList();
    }
}
