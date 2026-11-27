using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

public sealed record YearEndResultDto(Guid FiscalYearId, string Code, bool IsClosed, Guid? ClosingJournalId, string? ClosingJournalNumber, decimal NetProfit);

public static class YearEndErrors
{
    public static readonly Error EarlierYearOpen = Error.Conflict("accounting.earlier-year-open", "Close the earlier fiscal years first.");
}

/// <summary>
/// Year-end close (OQ-ACC-4): posts one closing journal, dated the last day of the year, that brings every revenue and expense
/// balance of the year (per account and cost centre) to zero against the RetainedEarnings role, then closes the last period.
/// Preconditions: earlier years closed; periods 1..n−1 closed; the last period soft-closed (it stays postable for the system
/// while ordinary users are locked out). Idempotent: closing a closed year returns the existing result.
/// Balance-sheet accounts need no carry-forward entry: their balances are cumulative across years.
/// </summary>
public sealed record CloseFiscalYearCommand(Guid FiscalYearId) : ICommand<YearEndResultDto>;

public sealed class CloseFiscalYearHandler(
    IAccountingDbContext db,
    LedgerQueries ledger,
    JournalPoster poster,
    CompanyLegalEntities legalEntities,
    IClock clock) : ICommandHandler<CloseFiscalYearCommand, YearEndResultDto>
{
    public const string PurposePrefix = "close-";

    public async Task<Result<YearEndResultDto>> Handle(CloseFiscalYearCommand command, CancellationToken cancellationToken)
    {
        var year = await db.FiscalYears.Include(y => y.Periods).FirstOrDefaultAsync(y => y.Id == command.FiscalYearId, cancellationToken);
        if (year is null)
        {
            return AccountingErrors.FiscalYearNotFound;
        }

        var le = await legalEntities.GetAsync(year.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (year.IsClosed)
        {
            return await ResultAsync(year, cancellationToken);
        }

        if (await db.FiscalYears.AnyAsync(y => y.LegalEntityId == year.LegalEntityId && y.EndDate < year.StartDate && !y.IsClosed, cancellationToken))
        {
            return YearEndErrors.EarlierYearOpen;
        }

        var ready = year.CanClose();
        if (ready.IsFailure)
        {
            return ready.Error;
        }

        var plAccounts = await db.Accounts.AsNoTracking()
            .Where(a => a.Type == AccountType.Revenue || a.Type == AccountType.Expense)
            .Select(a => a.Id).ToListAsync(cancellationToken);
        var balances = await ledger.PostedLines(year.LegalEntityId)
            .Where(r => r.JournalDate >= year.StartDate && r.JournalDate <= year.EndDate && plAccounts.Contains(r.AccountId))
            .GroupBy(r => new { r.AccountId, r.CostCenterId })
            .Select(g => new { g.Key.AccountId, g.Key.CostCenterId, Balance = g.Sum(r => r.BaseDebit - r.BaseCredit) })
            .ToListAsync(cancellationToken);
        var toClose = balances.Where(b => b.Balance != 0).OrderBy(b => b.AccountId).ThenBy(b => b.CostCenterId).ToList();

        Guid? journalId = null;
        if (toClose.Count > 0)
        {
            var rules = await db.PostingRules.Where(r => r.Role == PostingRoles.RetainedEarnings).ToListAsync(cancellationToken);
            var retained = PostingRuleResolver.Resolve(rules, PostingRoles.RetainedEarnings, year.LegalEntityId, null, null, null, null);
            if (retained is null)
            {
                return PostingErrors.RuleMissing(PostingRoles.RetainedEarnings, PostingContext.Empty);
            }

            // A reopened year keeps its reversed closing journal; each new close gets the next purpose number (the source key is unique).
            var previousCloses = await db.Journals.CountAsync(j => j.LegalEntityId == year.LegalEntityId &&
                j.SourceDocType == AccountingDocumentTypes.YearEndClose && j.SourceDocId == year.Id && !j.Purpose.EndsWith(JournalEntry.ReversalSuffix), cancellationToken);

            JournalEntry journal;
            try
            {
                journal = JournalEntry.CreateDraft(le.Value.CompanyId, year.LegalEntityId, year.EndDate, SourceModules.Accounting,
                    AccountingDocumentTypes.YearEndClose, year.Id, PurposePrefix + (previousCloses + 1), $"Year-end close {year.Code}", le.Value.FunctionalCurrency, 1m);
                foreach (var b in toClose)
                {
                    journal.AddLine(b.AccountId, b.Balance < 0 ? -b.Balance : 0m, b.Balance > 0 ? b.Balance : 0m, b.CostCenterId, null, null, "Year-end close");
                }

                var net = toClose.Sum(b => b.Balance); // debit positive: > 0 is a loss, < 0 a profit
                if (net != 0)
                {
                    journal.AddLine(retained.AccountId, net > 0 ? net : 0m, net < 0 ? -net : 0m, null, null, null,
                        net < 0 ? "Profit for the year" : "Loss for the year");
                }
            }
            catch (DomainException ex)
            {
                return ex.Error;
            }

            db.Journals.Add(journal);
            var posted = await poster.PostDraftAsync(journal, manual: false, cancellationToken, allowSoftClosed: true);
            if (posted.IsFailure)
            {
                JournalPoster.Detach(db, journal);
                return posted.Error;
            }

            journalId = journal.Id;
        }

        year.MarkClosed(journalId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await ResultAsync(year, cancellationToken);
    }

    private async Task<YearEndResultDto> ResultAsync(FiscalYear year, CancellationToken ct)
    {
        string? number = null;
        decimal profit = 0m;
        if (year.ClosingJournalId is { } id)
        {
            var journal = await db.Journals.AsNoTracking().Include(j => j.Lines).FirstAsync(j => j.Id == id, ct);
            number = journal.Number;
            var plIds = await db.Accounts.AsNoTracking().Where(a => a.Type == AccountType.Revenue || a.Type == AccountType.Expense).Select(a => a.Id).ToListAsync(ct);

            // The closing lines are the mirror image of the year's P&L balances: their debit-positive sum equals the profit.
            profit = journal.Lines.Where(l => plIds.Contains(l.AccountId)).Sum(l => l.BaseDebit - l.BaseCredit);
        }

        return new YearEndResultDto(year.Id, year.Code, year.IsClosed, year.ClosingJournalId, number, profit);
    }
}

/// <summary>Undoes a year-end close: reverses the closing journal on the last day and leaves the last period soft-closed.</summary>
public sealed record ReopenFiscalYearCommand(Guid FiscalYearId) : ICommand<YearEndResultDto>;

public sealed class ReopenFiscalYearHandler(IAccountingDbContext db, JournalPoster poster, CompanyLegalEntities legalEntities)
    : ICommandHandler<ReopenFiscalYearCommand, YearEndResultDto>
{
    public async Task<Result<YearEndResultDto>> Handle(ReopenFiscalYearCommand command, CancellationToken cancellationToken)
    {
        var year = await db.FiscalYears.Include(y => y.Periods).FirstOrDefaultAsync(y => y.Id == command.FiscalYearId, cancellationToken);
        if (year is null)
        {
            return AccountingErrors.FiscalYearNotFound;
        }

        var le = await legalEntities.GetAsync(year.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (!year.IsClosed)
        {
            return AccountingErrors.FiscalYearNotClosed;
        }

        if (await db.FiscalYears.AnyAsync(y => y.LegalEntityId == year.LegalEntityId && y.StartDate > year.EndDate && y.IsClosed, cancellationToken))
        {
            return AccountingErrors.LaterYearClosed;
        }

        var closingJournalId = year.ClosingJournalId;
        year.Reopen();
        if (closingJournalId is { } id)
        {
            var journal = await db.Journals.Include(j => j.Lines).FirstAsync(j => j.Id == id, cancellationToken);
            var reversal = await poster.ReverseAsync(journal, year.EndDate, cancellationToken, allowSoftClosed: true);
            if (reversal.IsFailure)
            {
                return reversal.Error;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new YearEndResultDto(year.Id, year.Code, year.IsClosed, null, null, 0m);
    }
}
