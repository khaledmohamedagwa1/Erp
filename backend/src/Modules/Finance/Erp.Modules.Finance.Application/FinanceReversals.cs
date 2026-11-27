using System.Globalization;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Application;

// ======================= GL movements for bank reconciliation =======================

/// <summary>
/// Imports general-ledger lines posted to a cash/bank account's own GL account by other modules or manual journals as cash movements
/// (<see cref="FinanceDocTypes.GlJournalLine"/>), so bank reconciliation and balances see every movement on the account, not only
/// Finance documents. Accounts that post through the shared CashAccount/BankAccount role are not synced: a shared GL account cannot be
/// attributed to one cash/bank account. Idempotent (one movement per journal line, enforced by a unique index).
/// </summary>
public sealed class GlMovementSync(IFinanceDbContext db, IAccountLedgerQueries ledger, FinanceScope scope)
{
    public async Task<int> SyncAsync(CashBankAccount account, DateOnly upTo, CancellationToken ct)
    {
        if (account.GlAccountId is not { } glAccountId)
        {
            return 0;
        }

        var le = await scope.LegalEntityAsync(account.LegalEntityId, ct);
        if (le.IsFailure)
        {
            return 0;
        }

        var functional = string.Equals(account.Currency, le.Value.FunctionalCurrency, StringComparison.OrdinalIgnoreCase);
        var lines = await ledger.GetAccountLinesAsync(account.LegalEntityId, glAccountId, upTo, SourceModules.Finance, ct);
        if (lines.Count == 0)
        {
            return 0;
        }

        var known = (await db.CashMovements.AsNoTracking()
                .Where(m => m.CashBankAccountId == account.Id && m.SourceDocType == FinanceDocTypes.GlJournalLine)
                .Select(m => m.SourceDocId).ToListAsync(ct))
            .ToHashSet();
        var added = 0;
        foreach (var line in lines.Where(l => !known.Contains(l.LineId)))
        {
            var baseAmount = line.BaseDebit - line.BaseCredit;
            decimal amount;
            if (string.Equals(line.Currency, account.Currency, StringComparison.OrdinalIgnoreCase) && (line.Debit != 0 || line.Credit != 0))
            {
                amount = line.Debit - line.Credit;
            }
            else if (functional)
            {
                amount = baseAmount;
            }
            else
            {
                // A foreign-currency account touched by a journal in another currency: the account-currency amount is unknown.
                continue;
            }

            if (amount == 0 && baseAmount == 0)
            {
                continue;
            }

            db.CashMovements.Add(CashMovement.Create(account, line.Date, amount, baseAmount, FinanceDocTypes.GlJournalLine, line.LineId,
                string.IsNullOrEmpty(line.Number) ? line.SourceDocType : line.Number, line.Description));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }
}

public sealed record SyncGlMovementsCommand(Guid CashBankAccountId, DateOnly? UpTo) : ICommand<int>;

public sealed class SyncGlMovementsHandler(IFinanceDbContext db, GlMovementSync sync, IClock clock) : ICommandHandler<SyncGlMovementsCommand, int>
{
    public async Task<Result<int>> Handle(SyncGlMovementsCommand command, CancellationToken cancellationToken)
    {
        var account = await db.CashBankAccounts.FirstOrDefaultAsync(a => a.Id == command.CashBankAccountId, cancellationToken);
        if (account is null)
        {
            return FinanceAppErrors.AccountNotFound;
        }

        return await sync.SyncAsync(account, command.UpTo ?? DateOnly.FromDateTime(clock.UtcNow), cancellationToken);
    }
}

// ======================= Reversal of receipts and payments =======================
public sealed record ReverseSettlementCommand(SettlementDirection Direction, Guid SettlementId, DateOnly Date, string Reason) : ICommand<SettlementDto>;

public sealed class ReverseSettlementValidator : AbstractValidator<ReverseSettlementCommand>
{
    public ReverseSettlementValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
}

/// <summary>
/// Reverses a posted receipt or payment: reverses its journal, undoes every allocation (including later advance allocations, with a
/// compensating journal) with negated allocation rows, reopens the open items, adds the opposite cash movement and marks it Reversed.
/// The period of the reversal date must be open; a movement in a completed bank reconciliation or a document on an active withholding
/// certificate blocks the reversal until the reconciliation is reopened / the certificate cancelled.
/// </summary>
public sealed class ReverseSettlementHandler(
    IFinanceDbContext db,
    FinanceScope scope,
    AllocationService allocations,
    IJournalPostingService journals) : ICommandHandler<ReverseSettlementCommand, SettlementDto>
{
    public async Task<Result<SettlementDto>> Handle(ReverseSettlementCommand command, CancellationToken cancellationToken)
    {
        var settlement = await db.Settlements.FirstOrDefaultAsync(s => s.Id == command.SettlementId && s.Direction == command.Direction, cancellationToken);
        if (settlement is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        if (settlement.Status != DocumentStatus.Posted)
        {
            return FinanceErrors.NotPosted;
        }

        if (command.Date < settlement.Date)
        {
            return FinanceErrors.ReversalBeforeDocument;
        }

        if (settlement.WithholdingCertificateId is not null)
        {
            return FinanceAppErrors.OnWithholdingCertificate;
        }

        var isReceipt = settlement.Direction == SettlementDirection.Receipt;
        var docType = isReceipt ? FinanceDocTypes.Receipt : FinanceDocTypes.Payment;
        var movement = await db.CashMovements.FirstOrDefaultAsync(m => m.SourceDocType == docType && m.SourceDocId == settlement.Id, cancellationToken);
        if (movement is not null)
        {
            if (movement.ReconciliationId is not null)
            {
                return FinanceErrors.Reconciled;
            }

            var drafts = await db.BankReconciliations.AsNoTracking()
                .Where(r => r.CashBankAccountId == movement.CashBankAccountId && r.Status == ReconciliationStatus.Draft).ToListAsync(cancellationToken);
            if (drafts.Any(r => r.Lines.Any(l => l.MatchedMovementId == movement.Id)))
            {
                return FinanceErrors.InReconciliation;
            }
        }

        var le = await scope.LegalEntityAsync(settlement.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var reversed = await journals.ReverseAsync(settlement.LegalEntityId, docType, settlement.Id, "post", command.Date, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        var kinds = new[] { isReceipt ? SettlementKind.Receipt : SettlementKind.Payment, SettlementKind.Advance };
        var existing = await db.Allocations
            .Where(a => a.SettlementId == settlement.Id && kinds.Contains(a.SettlementKind) && a.ReversalOfId == null).ToListAsync(cancellationToken);
        var itemIds = existing.Select(a => a.OpenItemId).Distinct().ToList();
        var items = await db.OpenItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var reference = $"REV {settlement.Number}";
        var created = new List<Allocation>();
        foreach (var allocation in existing)
        {
            items[allocation.OpenItemId].Unsettle(allocation.Amount, allocation.BaseRelieved);
            var reversal = allocation.CreateReversal(command.Date, reference);
            db.Allocations.Add(reversal);
            created.Add(reversal);
        }

        // Advance allocations moved part of the advance to the receivable/payable in their own journals: put it back.
        var advance = existing.Where(a => a.SettlementKind == SettlementKind.Advance).ToList();
        if (advance.Count > 0)
        {
            var amount = advance.Sum(a => a.AppliedSettlementAmount);
            var settlementBase = advance.Sum(a => a.SettlementBase);
            var relieved = advance.Sum(a => a.BaseRelieved);
            var gain = FinanceMath.RealizedFx(settlement.PartyType, relieved, settlementBase);
            var party = FinanceMapping.Party(items[advance[0].OpenItemId].PartyPostingGroup);
            var lines = new JournalLines();
            if (isReceipt)
            {
                lines.CreditTx(PostingRoles.CustomerAdvance, null, amount, settlementBase, party, PartyKind.Customer, settlement.PartyId)
                    .DebitTx(PostingRoles.CustomerReceivable, null, amount, relieved, party, PartyKind.Customer, settlement.PartyId);
            }
            else
            {
                lines.DebitTx(PostingRoles.SupplierAdvance, null, amount, settlementBase, party, PartyKind.Supplier, settlement.PartyId)
                    .CreditTx(PostingRoles.SupplierPayable, null, amount, relieved, party, PartyKind.Supplier, settlement.PartyId);
            }

            lines.FxBaseOnly(-gain, PostingRoles.RealizedFxGain, PostingRoles.RealizedFxLoss);
            var posted = await journals.PostAsync(new JournalRequest(settlement.LegalEntityId, command.Date, SourceModules.Finance,
                FinanceDocTypes.AdvanceAllocationReversal, settlement.Id, "reversal", $"Reversal of advance allocations of {settlement.Number}",
                settlement.Currency, settlement.ExchangeRate, lines.Lines), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }
        }

        if (movement is not null)
        {
            var account = await db.CashBankAccounts.FirstAsync(a => a.Id == movement.CashBankAccountId, cancellationToken);
            db.CashMovements.Add(CashMovement.Create(account, command.Date, -movement.Amount, -movement.BaseAmount, docType + "Reversal", settlement.Id,
                reference, command.Reason));
        }

        settlement.MarkReversed(command.Date, command.Reason);
        await allocations.AnnounceAsync(items.Values, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var all = await db.Allocations.AsNoTracking().Where(a => a.SettlementId == settlement.Id && a.SettlementKind != SettlementKind.CreditNote)
            .OrderBy(a => a.Date).ToListAsync(cancellationToken);
        return FinanceMapping.ToDto(settlement, all);
    }
}

public sealed record ReopenReconciliationCommand(Guid ReconciliationId) : ICommand<ReconciliationDto>;

/// <summary>Reopens a completed bank reconciliation: its movements become unreconciled again (matches are kept).</summary>
public sealed class ReopenReconciliationHandler(IFinanceDbContext db) : ICommandHandler<ReopenReconciliationCommand, ReconciliationDto>
{
    public async Task<Result<ReconciliationDto>> Handle(ReopenReconciliationCommand command, CancellationToken cancellationToken)
    {
        var reconciliation = await db.BankReconciliations.FirstOrDefaultAsync(r => r.Id == command.ReconciliationId, cancellationToken);
        if (reconciliation is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        reconciliation.Reopen();
        foreach (var movement in await db.CashMovements.Where(m => m.ReconciliationId == reconciliation.Id).ToListAsync(cancellationToken))
        {
            movement.ClearReconciliation();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ReconciliationQueries.ToDtoAsync(db, reconciliation, cancellationToken);
    }
}

// ======================= Withholding tax certificates =======================
public sealed record WithholdingCertificateLineDto(Guid SettlementId, string SettlementNumber, DateOnly Date, Guid? TaxGroupId, string Currency,
    decimal ExchangeRate, decimal TaxableAmount, decimal WithheldAmount, decimal TaxableBase, decimal WithheldBase);

public sealed record WithholdingCertificateDto(Guid Id, Guid LegalEntityId, string Kind, string PartyType, Guid PartyId, string Number, DateOnly PeriodFrom,
    DateOnly PeriodTo, DateOnly IssueDate, string? ExternalReference, string? Notes, string Status, decimal TotalTaxableBase, decimal TotalWithheldBase,
    IReadOnlyList<WithholdingCertificateLineDto> Lines);

internal static class WithholdingMapping
{
    public static WithholdingCertificateDto ToDto(WithholdingCertificate c) => new(c.Id, c.LegalEntityId, c.Kind.ToString(), c.PartyType.ToString(), c.PartyId,
        c.Number, c.PeriodFrom, c.PeriodTo, c.IssueDate, c.ExternalReference, c.Notes, c.Status.ToString(), c.TotalTaxableBase, c.TotalWithheldBase,
        c.Lines.Select(l => new WithholdingCertificateLineDto(l.SettlementId, l.SettlementNumber, l.Date, l.TaxGroupId, l.Currency, l.ExchangeRate,
            l.TaxableAmount, l.WithheldAmount, l.TaxableBase, l.WithheldBase)).ToList());
}

public sealed record CreateWithholdingCertificateCommand(Guid LegalEntityId, string Kind, Guid PartyId, DateOnly PeriodFrom, DateOnly PeriodTo,
    DateOnly? IssueDate, string? ExternalReference, string? Notes) : ICommand<WithholdingCertificateDto>;

public sealed class CreateWithholdingCertificateValidator : AbstractValidator<CreateWithholdingCertificateCommand>
{
    public CreateWithholdingCertificateValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.PartyId).NotEmpty();
        RuleFor(x => x.Kind).Must(k => Enum.TryParse<WithholdingCertificateKind>(k, true, out _)).WithMessage("Kind must be Issued or Received.");
        RuleFor(x => x).Must(x => x.PeriodFrom <= x.PeriodTo).WithMessage("The period start must not be after its end.");
        RuleFor(x => x.ExternalReference).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

/// <summary>
/// Creates a certificate for one party and period from the posted, not reversed receipts (Received) or payments (Issued) with tax withheld
/// that are not already on an active certificate. Numbered gapless per legal entity and year (WHT issued, WHR received).
/// </summary>
public sealed class CreateWithholdingCertificateHandler(IFinanceDbContext db, FinanceScope scope, SubledgerService subledger, IDocumentNumberGenerator numbers, IClock clock)
    : ICommandHandler<CreateWithholdingCertificateCommand, WithholdingCertificateDto>
{
    public async Task<Result<WithholdingCertificateDto>> Handle(CreateWithholdingCertificateCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var kind = Enum.Parse<WithholdingCertificateKind>(command.Kind, true);
        var direction = kind == WithholdingCertificateKind.Issued ? SettlementDirection.Payment : SettlementDirection.Receipt;
        var partyType = kind == WithholdingCertificateKind.Issued ? PartyKind.Supplier : PartyKind.Customer;
        if (await subledger.PostingGroupAsync(partyType, command.PartyId, cancellationToken) is null)
        {
            return FinanceAppErrors.PartyNotFound;
        }

        var settlements = await db.Settlements
            .Where(s => s.LegalEntityId == command.LegalEntityId && s.Direction == direction && s.PartyId == command.PartyId && s.Status == DocumentStatus.Posted
                        && s.WithholdingAmount > 0 && s.WithholdingCertificateId == null && s.Date >= command.PeriodFrom && s.Date <= command.PeriodTo)
            .ToListAsync(cancellationToken);
        var lines = settlements.Select(s => new WithholdingCertificateLine(s.Id, s.Number ?? string.Empty, s.Date, s.WithholdingTaxGroupId, s.Currency,
            s.ExchangeRate, s.WithholdingBase, s.WithholdingAmount)).ToList();
        if (lines.Count == 0)
        {
            return Error.Validation("finance.no-withholding", "No posted receipts or payments with tax withheld exist for this party and period.");
        }

        var issueDate = command.IssueDate ?? DateOnly.FromDateTime(clock.UtcNow);
        var number = await FinanceNumbers.NextAsync(numbers, le.Value.CompanyId, le.Value.Id, FinanceDocTypes.WithholdingCertificate + kind,
            kind == WithholdingCertificateKind.Issued ? "WHT" : "WHR", issueDate, cancellationToken);
        var certificate = WithholdingCertificate.Create(le.Value.CompanyId, le.Value.Id, kind, command.PartyId, number, command.PeriodFrom, command.PeriodTo,
            issueDate, command.ExternalReference, command.Notes, lines);
        db.WithholdingCertificates.Add(certificate);
        foreach (var settlement in settlements)
        {
            settlement.LinkWithholdingCertificate(certificate.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        return WithholdingMapping.ToDto(certificate);
    }
}

public sealed record CancelWithholdingCertificateCommand(Guid CertificateId) : ICommand<WithholdingCertificateDto>;

public sealed class CancelWithholdingCertificateHandler(IFinanceDbContext db) : ICommandHandler<CancelWithholdingCertificateCommand, WithholdingCertificateDto>
{
    public async Task<Result<WithholdingCertificateDto>> Handle(CancelWithholdingCertificateCommand command, CancellationToken cancellationToken)
    {
        var certificate = await db.WithholdingCertificates.FirstOrDefaultAsync(c => c.Id == command.CertificateId, cancellationToken);
        if (certificate is null)
        {
            return FinanceAppErrors.DocumentNotFound;
        }

        certificate.Cancel();
        foreach (var settlement in await db.Settlements.Where(s => s.WithholdingCertificateId == certificate.Id).ToListAsync(cancellationToken))
        {
            settlement.LinkWithholdingCertificate(null);
        }

        await db.SaveChangesAsync(cancellationToken);
        return WithholdingMapping.ToDto(certificate);
    }
}

public sealed record GetWithholdingCertificateQuery(Guid CertificateId) : IQuery<WithholdingCertificateDto>;

public sealed class GetWithholdingCertificateHandler(IFinanceDbContext db) : IQueryHandler<GetWithholdingCertificateQuery, WithholdingCertificateDto>
{
    public async Task<Result<WithholdingCertificateDto>> Handle(GetWithholdingCertificateQuery query, CancellationToken cancellationToken)
    {
        var c = await db.WithholdingCertificates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.CertificateId, cancellationToken);
        return c is null ? FinanceAppErrors.DocumentNotFound : WithholdingMapping.ToDto(c);
    }
}

public sealed record ListWithholdingCertificatesQuery(string? Kind, Guid? PartyId, PageRequest Page) : IQuery<PagedResult<WithholdingCertificateDto>>;

public sealed class ListWithholdingCertificatesHandler(IFinanceDbContext db) : IQueryHandler<ListWithholdingCertificatesQuery, PagedResult<WithholdingCertificateDto>>
{
    public async Task<Result<PagedResult<WithholdingCertificateDto>>> Handle(ListWithholdingCertificatesQuery query, CancellationToken cancellationToken)
    {
        var source = db.WithholdingCertificates.AsNoTracking();
        if (query.Kind is not null && Enum.TryParse<WithholdingCertificateKind>(query.Kind, true, out var kind))
        {
            source = source.Where(c => c.Kind == kind);
        }

        if (query.PartyId is { } partyId)
        {
            source = source.Where(c => c.PartyId == partyId);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(c => c.IssueDate).ThenByDescending(c => c.Number)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<WithholdingCertificateDto>(items.Select(WithholdingMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

/// <summary>Withholding facts for the tax reports.</summary>
public sealed class WithholdingReportQueries(IFinanceDbContext db) : IWithholdingReportQueries
{
    public async Task<IReadOnlyList<WithholdingFact>> GetWithholdingFactsAsync(DateOnly from, DateOnly to, PartyKind? partyType = null, Guid? partyId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Settlements.AsNoTracking()
            .Where(s => s.Status == DocumentStatus.Posted && s.WithholdingAmount > 0 && s.Date >= from && s.Date <= to);
        if (partyType is { } kind)
        {
            var direction = kind == PartyKind.Customer ? SettlementDirection.Receipt : SettlementDirection.Payment;
            query = query.Where(s => s.Direction == direction);
        }

        if (partyId is { } id)
        {
            query = query.Where(s => s.PartyId == id);
        }

        var rows = await query.OrderBy(s => s.Date).ThenBy(s => s.Number).ToListAsync(cancellationToken);
        var certificateIds = rows.Where(s => s.WithholdingCertificateId != null).Select(s => s.WithholdingCertificateId!.Value).Distinct().ToList();
        var numbers = await db.WithholdingCertificates.AsNoTracking().Where(c => certificateIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Number, cancellationToken);
        return rows.Select(s => new WithholdingFact(s.Direction.ToString(), s.Id, s.Number ?? string.Empty, s.Date, s.LegalEntityId, s.PartyType, s.PartyId,
            s.WithholdingTaxGroupId, s.Currency, s.WithholdingBase, s.WithholdingAmount, FinanceMath.ToBase(s.WithholdingBase, s.ExchangeRate),
            FinanceMath.ToBase(s.WithholdingAmount, s.ExchangeRate),
            s.WithholdingCertificateId is { } c ? numbers.GetValueOrDefault(c) : null)).ToList();
    }
}

internal static class WithholdingPrinting
{
    public static Erp.SharedKernel.Documents.PrintableDocument Build(WithholdingCertificate c, string functionalCurrency)
    {
        var issued = c.Kind == WithholdingCertificateKind.Issued;
        var fields = new List<Erp.SharedKernel.Documents.PrintField>
        {
            new("Period", "الفترة", $"{c.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} — {c.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"),
        };
        if (c.ExternalReference is not null)
        {
            fields.Add(new("Reference", "المرجع", c.ExternalReference));
        }

        var lines = c.Lines.Select(l => new Erp.SharedKernel.Documents.PrintLine(l.SettlementNumber,
            $"{l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · {l.Currency} {l.WithheldAmount.ToString("N2", CultureInfo.InvariantCulture)}",
            1, Net: l.TaxableBase, Tax: l.WithheldBase, Total: l.WithheldBase)).ToList();
        var totals = new List<Erp.SharedKernel.Documents.PrintTotal>
        {
            new("Taxable amount", "المبلغ الخاضع", c.TotalTaxableBase),
            new("Tax withheld", "الضريبة المخصومة", c.TotalWithheldBase, Emphasis: true),
        };
        return new Erp.SharedKernel.Documents.PrintableDocument("withholding-certificate",
            issued ? "Withholding tax certificate" : "Withholding tax certificate (received)",
            issued ? "شهادة خصم ضريبة من المنبع" : "شهادة خصم ضريبة من المنبع (مستلمة)",
            c.Number, c.IssueDate, functionalCurrency, c.Status.ToString(), c.CompanyId, c.LegalEntityId,
            new Erp.SharedKernel.Documents.PrintParty(issued ? "Supplier" : "Customer", c.PartyId), fields, lines, [], totals, c.Notes);
    }
}
