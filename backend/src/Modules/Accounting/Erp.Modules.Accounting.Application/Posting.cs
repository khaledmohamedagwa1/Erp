using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

public interface IAccountingDbContext
{
    DbSet<Account> Accounts { get; }

    DbSet<CostCenter> CostCenters { get; }

    DbSet<ProfitCenter> ProfitCenters { get; }

    DbSet<FiscalYear> FiscalYears { get; }

    DbSet<JournalEntry> Journals { get; }

    DbSet<JournalLine> JournalLines { get; }

    DbSet<AccountDetermination> PostingRules { get; }

    DbSet<AccountBalance> AccountBalances { get; }

    DbSet<OpeningBalance> OpeningBalances { get; }

    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Entry(object entity);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Adds a posted journal's lines to the running balances, inside the posting transaction (set-based, locked).</summary>
public interface IAccountBalanceWriter
{
    Task ApplyAsync(JournalEntry journal, CancellationToken cancellationToken);
}

public static class PostingErrors
{
    public static readonly Error TransactionRequired = Error.Failure("accounting.transaction-required",
        "Journals must be posted inside the caller's transaction.");

    public static readonly Error LegalEntityNotFound = Error.NotFound("accounting.legal-entity-not-found", "The legal entity was not found.");

    public static readonly Error LegalEntityNotInCompany = Error.Forbidden("accounting.legal-entity-not-in-company",
        "The legal entity does not belong to the active company.");

    public static readonly Error ReverseFromSource = Error.Conflict("accounting.reverse-from-source-document",
        "Journals created by another module are reversed by cancelling their source document.");

    public static Error RuleMissing(string role, PostingContext context) => Error.Validation("accounting.posting-rule-missing",
        $"No posting rule maps role '{role}' ({context}) to an account.");

    public static Error UnknownRole(string role) => Error.Validation("accounting.unknown-posting-role", $"Unknown posting role '{role}'.");
}

public sealed record PostablePeriod(FiscalYear Year, FiscalPeriod Period);

/// <summary>Finds the fiscal period for a date and decides whether the current caller may post into it.</summary>
public sealed class PeriodGuard(IAccountingDbContext db, ICurrentUser currentUser, IPermissionService permissions) : IPeriodGuard
{
    /// <param name="allowSoftClosed">Year-end close: the closing entry is posted into the soft-closed last period by the system.</param>
    public async Task<Result<PostablePeriod>> FindPostablePeriodAsync(Guid legalEntityId, DateOnly date, CancellationToken cancellationToken, bool allowSoftClosed = false)
    {
        var year = await db.FiscalYears.Include(y => y.Periods)
            .FirstOrDefaultAsync(y => y.LegalEntityId == legalEntityId && y.StartDate <= date && y.EndDate >= date, cancellationToken);
        var period = year?.FindPeriod(date);
        if (year is null || period is null)
        {
            return AccountingErrors.PeriodNotFound;
        }

        switch (period.Status)
        {
            case PeriodStatus.Open:
            case PeriodStatus.SoftClosed when allowSoftClosed:
                return new PostablePeriod(year, period);
            case PeriodStatus.SoftClosed when currentUser.UserId is { } userId &&
                                              await permissions.HasPermissionAsync(userId, AccountingPermissions.PostInSoftClosedPeriod, cancellationToken):
                return new PostablePeriod(year, period);
            default:
                return AccountingErrors.PeriodClosed;
        }
    }

    public async Task<Result> EnsureOpenAsync(Guid legalEntityId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var result = await FindPostablePeriodAsync(legalEntityId, date, cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }
}

/// <summary>
/// The single place where a draft journal becomes posted: account checks, balance and rounding, period, gapless number,
/// status, persistence and running balances. Used by both the contract (module postings) and manual journals.
/// </summary>
public sealed class JournalPoster(
    IAccountingDbContext db,
    IUnitOfWork unitOfWork,
    IDocumentNumberGenerator numbers,
    IAccountBalanceWriter balances,
    PeriodGuard periods,
    ICurrentUser currentUser,
    IClock clock)
{
    public const string NumberDocumentType = "JournalEntry";
    public const string NumberPrefix = "JV";

    public async Task<Result> PostDraftAsync(JournalEntry journal, bool manual, CancellationToken cancellationToken, bool allowSoftClosed = false)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return PostingErrors.TransactionRequired;
        }

        var accountIds = journal.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        foreach (var accountId in accountIds)
        {
            if (!accounts.TryGetValue(accountId, out var account))
            {
                return AccountingErrors.AccountNotFound;
            }

            var allowed = account.CanPost(journal.SourceModule, manual);
            if (allowed.IsFailure)
            {
                return allowed.Error with { Message = $"{allowed.Error.Message} ({account.Code})" };
            }
        }

        var balance = journal.CheckBalance();
        if (balance.IsFailure)
        {
            return balance.Error;
        }

        if (balance.Value)
        {
            var rules = await db.PostingRules.Where(r => r.Role == PostingRoles.FxRoundingDifference).ToListAsync(cancellationToken);
            var rule = PostingRuleResolver.Resolve(rules, PostingRoles.FxRoundingDifference, journal.LegalEntityId, null, null, null, null);
            if (rule is null)
            {
                return PostingErrors.RuleMissing(PostingRoles.FxRoundingDifference, PostingContext.Empty);
            }

            journal.AddRoundingLine(rule.AccountId);
        }

        var period = await periods.FindPostablePeriodAsync(journal.LegalEntityId, journal.JournalDate, cancellationToken, allowSoftClosed);
        if (period.IsFailure)
        {
            return period.Error;
        }

        var number = await numbers.NextAsync(new NumberScope(journal.CompanyId, NumberDocumentType, NumberPrefix,
            LegalEntityId: journal.LegalEntityId, FiscalYear: period.Value.Year.NumberingYear), cancellationToken);
        journal.Post(number, period.Value.Period.Id, currentUser.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await balances.ApplyAsync(journal, cancellationToken);
        return Result.Success();
    }

    /// <summary>Stops tracking an unposted journal and its lines so a failed posting leaves nothing behind.</summary>
    public static void Detach(IAccountingDbContext db, JournalEntry journal)
    {
        foreach (var line in journal.Lines)
        {
            db.Entry(line).State = EntityState.Detached;
        }

        db.Entry(journal).State = EntityState.Detached;
    }

    /// <summary>Posts the reversal of a posted journal and marks the original reversed. Idempotent.</summary>
    public async Task<Result<JournalEntry>> ReverseAsync(JournalEntry original, DateOnly reversalDate, CancellationToken cancellationToken, bool allowSoftClosed = false)
    {
        var reversalPurpose = original.Purpose + JournalEntry.ReversalSuffix;
        var existing = await db.Journals.Include(j => j.Lines).FirstOrDefaultAsync(j =>
            j.LegalEntityId == original.LegalEntityId && j.SourceDocType == original.SourceDocType &&
            j.SourceDocId == original.SourceDocId && j.Purpose == reversalPurpose, cancellationToken);
        if (existing is { Status: not JournalStatus.Draft })
        {
            return existing;
        }

        JournalEntry reversal;
        try
        {
            reversal = original.CreateReversal(reversalDate);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.Journals.Add(reversal);
        // Accounting's own system documents (opening balances, year-end close) reverse like module postings: they may touch
        // accounts that manual journals cannot.
        var manual = original.SourceModule == SourceModules.Accounting && original.SourceDocType == AccountingDocumentTypes.ManualJournal;
        var posted = await PostDraftAsync(reversal, manual, cancellationToken, allowSoftClosed);
        if (posted.IsFailure)
        {
            Detach(db, reversal);
            return posted.Error;
        }

        original.MarkReversed(reversal.Id);
        await db.SaveChangesAsync(cancellationToken);
        return reversal;
    }
}

/// <summary>
/// Profit-center defaulting for module postings (ADR-036): explicit line profit centers must exist and be active; lines without one take
/// the active profit center mapped to the request's branch (none when the branch is unmapped).
/// </summary>
public static class ProfitCenterDefaults
{
    public static readonly Error ProfitCenterInvalid = Error.Validation("accounting.profit-center-invalid", "The profit center does not exist or is inactive.");

    public static async Task<Result<Guid?>> ResolveAsync(IAccountingDbContext db, JournalRequest request, CancellationToken ct)
    {
        var explicitIds = request.Lines.Where(l => l.ProfitCenterId != null).Select(l => l.ProfitCenterId!.Value).Distinct().ToList();
        if (explicitIds.Count > 0 && await db.ProfitCenters.CountAsync(p => explicitIds.Contains(p.Id) && p.IsActive, ct) != explicitIds.Count)
        {
            return ProfitCenterInvalid;
        }

        if (request.BranchId is not { } branchId || request.Lines.All(l => l.ProfitCenterId != null))
        {
            return (Guid?)null;
        }

        return await db.ProfitCenters.AsNoTracking().Where(p => p.BranchId == branchId && p.IsActive).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
    }
}

/// <summary>The posting engine contract used by every operational module (ADR-006, ADR-011).</summary>
public sealed class JournalPostingService(
    IAccountingDbContext db,
    IUnitOfWork unitOfWork,
    IOrganizationQueries organization,
    ITenantContext tenantContext,
    JournalPoster poster) : IJournalPostingService
{
    public async Task<Result<PostedJournal>> PostAsync(JournalRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return PostingErrors.TransactionRequired;
        }

        var legalEntity = await ResolveLegalEntityAsync(request.LegalEntityId, cancellationToken);
        if (legalEntity.IsFailure)
        {
            return legalEntity.Error;
        }

        var existing = await FindAsync(request.LegalEntityId, request.SourceDocType, request.SourceDocId, request.Purpose, cancellationToken);
        if (existing is not null)
        {
            return existing.Status == JournalStatus.Draft
                ? Error.Conflict("accounting.journal-draft-exists", "A draft journal already exists for this source document.")
                : new PostedJournal(existing.Id, existing.Number!, existing.TotalBaseDebit, AlreadyPosted: true);
        }

        var currency = request.Currency.Trim().ToUpperInvariant();
        var isFunctional = currency == legalEntity.Value.FunctionalCurrency;
        if (request.ExchangeRate <= 0 || (isFunctional && request.ExchangeRate != 1m))
        {
            return AccountingErrors.InvalidRate;
        }

        var rules = await db.PostingRules.ToListAsync(cancellationToken);
        var profitCenters = await ProfitCenterDefaults.ResolveAsync(db, request, cancellationToken);
        if (profitCenters.IsFailure)
        {
            return profitCenters.Error;
        }

        var branchProfitCenter = profitCenters.Value;
        JournalEntry journal;
        try
        {
            journal = JournalEntry.CreateDraft(legalEntity.Value.CompanyId, request.LegalEntityId, request.JournalDate, request.SourceModule,
                request.SourceDocType, request.SourceDocId, request.Purpose, request.Description, currency, request.ExchangeRate);
            foreach (var line in request.Lines)
            {
                var accountId = line.AccountId;
                if (accountId is null)
                {
                    if (string.IsNullOrWhiteSpace(line.Role) || !PostingRoles.IsKnown(line.Role))
                    {
                        return PostingErrors.UnknownRole(line.Role ?? "(none)");
                    }

                    var context = line.Context ?? PostingContext.Empty;
                    var rule = PostingRuleResolver.Resolve(rules, line.Role, request.LegalEntityId, context.ProductPostingGroup,
                        context.PartyPostingGroup, context.TaxCodeId, context.WarehouseId);
                    if (rule is null)
                    {
                        return PostingErrors.RuleMissing(line.Role, context);
                    }

                    accountId = rule.AccountId;
                }

                var role = line.AccountId is null ? line.Role : null;
                var profitCenterId = line.ProfitCenterId ?? branchProfitCenter;
                if (line.BaseOnly)
                {
                    journal.AddBaseOnlyLine(accountId.Value, line.Debit, line.Credit, line.CostCenterId, line.PartyType, line.PartyId, line.Description, role,
                        profitCenterId);
                }
                else
                {
                    journal.AddLine(accountId.Value, line.Debit, line.Credit, line.CostCenterId, line.PartyType, line.PartyId, line.Description, line.BaseAmount,
                        role, line.Context?.TaxCodeId, line.TaxableAmount, profitCenterId);
                }
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.Journals.Add(journal);
        var posted = await poster.PostDraftAsync(journal, manual: false, cancellationToken);
        if (posted.IsFailure)
        {
            JournalPoster.Detach(db, journal);
            return posted.Error;
        }

        return new PostedJournal(journal.Id, journal.Number!, journal.TotalBaseDebit, AlreadyPosted: false);
    }

    public async Task<Result<PostedJournal>> ReverseAsync(Guid legalEntityId, string sourceDocType, Guid sourceDocId, string purpose,
        DateOnly reversalDate, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return PostingErrors.TransactionRequired;
        }

        var legalEntity = await ResolveLegalEntityAsync(legalEntityId, cancellationToken);
        if (legalEntity.IsFailure)
        {
            return legalEntity.Error;
        }

        var original = await FindAsync(legalEntityId, sourceDocType, sourceDocId, purpose, cancellationToken);
        if (original is null || original.Status == JournalStatus.Draft)
        {
            return AccountingErrors.JournalNotFound;
        }

        var reversal = await poster.ReverseAsync(original, reversalDate, cancellationToken);
        return reversal.IsFailure
            ? reversal.Error
            : new PostedJournal(reversal.Value.Id, reversal.Value.Number!, reversal.Value.TotalBaseDebit, AlreadyPosted: false);
    }

    private Task<JournalEntry?> FindAsync(Guid legalEntityId, string sourceDocType, Guid sourceDocId, string purpose, CancellationToken cancellationToken) =>
        db.Journals.Include(j => j.Lines).FirstOrDefaultAsync(j =>
            j.LegalEntityId == legalEntityId && j.SourceDocType == sourceDocType && j.SourceDocId == sourceDocId && j.Purpose == purpose, cancellationToken);

    /// <summary>Internal callers may post for any company of the tenant: the company context follows the legal entity when unset.</summary>
    private async Task<Result<LegalEntitySummary>> ResolveLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken)
    {
        var legalEntity = await organization.GetLegalEntityAsync(legalEntityId, cancellationToken);
        if (legalEntity is null)
        {
            return PostingErrors.LegalEntityNotFound;
        }

        if (tenantContext.CompanyId is null)
        {
            tenantContext.SetCompany(legalEntity.CompanyId);
        }
        else if (tenantContext.CompanyId != legalEntity.CompanyId)
        {
            return PostingErrors.LegalEntityNotInCompany;
        }

        return legalEntity;
    }
}

/// <summary>Chart-of-accounts lookups for other modules. The company query filter keeps it within the active company.</summary>
public sealed class AccountQueries(IAccountingDbContext db) : IAccountQueries
{
    public async Task<AccountInfo?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        await db.Accounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => new AccountInfo(a.Id, a.Code, a.NameEn, a.NameAr, a.Type.ToString(), a.IsPostingAccount,
                a.ControlSubledger != ControlSubledger.None, a.ControlSubledger.ToString(), a.AllowManualPosting, a.Status == AccountStatus.Active))
            .FirstOrDefaultAsync(cancellationToken);
}

public sealed class LedgerQueries(IAccountingDbContext db) : ILedgerQueries
{
    public async Task<decimal> GetAccountBalanceAsync(Guid legalEntityId, Guid accountId, DateOnly asOf, CancellationToken cancellationToken = default) =>
        await PostedLines(legalEntityId)
            .Where(x => x.AccountId == accountId && x.JournalDate <= asOf)
            .SumAsync(x => x.BaseDebit - x.BaseCredit, cancellationToken);

    public async Task<decimal> GetRoleBalanceAsync(Guid legalEntityId, string role, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var rules = await db.PostingRules.AsNoTracking().Where(r => r.Role == role).ToListAsync(cancellationToken);
        var rule = PostingRuleResolver.Resolve(rules, role, legalEntityId, null, null, null, null);
        return rule is null ? 0m : await GetAccountBalanceAsync(legalEntityId, rule.AccountId, asOf, cancellationToken);
    }

    public async Task<TrialBalance> GetTrialBalanceAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var totals = await PostedLines(legalEntityId)
            .Where(x => x.JournalDate <= to)
            .GroupBy(x => x.AccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Opening = g.Sum(x => x.JournalDate < from ? x.BaseDebit - x.BaseCredit : 0m),
                Debit = g.Sum(x => x.JournalDate >= from ? x.BaseDebit : 0m),
                Credit = g.Sum(x => x.JournalDate >= from ? x.BaseCredit : 0m),
            })
            .ToListAsync(cancellationToken);

        var ids = totals.Select(t => t.AccountId).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var lines = totals
            .Where(t => t.Opening != 0 || t.Debit != 0 || t.Credit != 0)
            .Select(t =>
            {
                var a = accounts[t.AccountId];
                return new TrialBalanceLine(a.Id, a.Code, a.NameEn, a.NameAr, a.Type.ToString(), t.Opening, t.Debit, t.Credit, t.Opening + t.Debit - t.Credit);
            })
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToList();
        return new TrialBalance(legalEntityId, from, to, lines, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    public async Task<IReadOnlyList<string>> GetUnresolvedRolesAsync(Guid legalEntityId, IEnumerable<string> roles, CancellationToken cancellationToken = default)
    {
        var rules = await db.PostingRules.AsNoTracking().ToListAsync(cancellationToken);
        return roles.Where(role => PostingRuleResolver.Resolve(rules, role, legalEntityId, null, null, null, null) is null).ToList();
    }

    internal IQueryable<LedgerRow> PostedLines(Guid legalEntityId) =>
        from line in db.JournalLines.AsNoTracking()
        join journal in db.Journals.AsNoTracking() on line.JournalEntryId equals journal.Id
        where journal.LegalEntityId == legalEntityId && journal.Status != JournalStatus.Draft
        select new LedgerRow
        {
            AccountId = line.AccountId,
            JournalId = journal.Id,
            SourceDocType = journal.SourceDocType,
            CostCenterId = line.CostCenterId,
            ProfitCenterId = line.ProfitCenterId,
            Number = journal.Number,
            JournalDate = journal.JournalDate,
            JournalDescription = journal.Description,
            LineDescription = line.Description,
            BaseDebit = line.BaseDebit,
            BaseCredit = line.BaseCredit,
            LineNo = line.LineNo,
        };
}

/// <summary>Detailed ledger reads for Finance (bank reconciliation against the GL) and tax reporting.</summary>
public sealed class AccountLedgerQueries(IAccountingDbContext db) : IAccountLedgerQueries
{
    private static readonly string[] TaxRoles = [PostingRoles.OutputTax, PostingRoles.InputTax];

    public async Task<IReadOnlyList<AccountLedgerLine>> GetAccountLinesAsync(Guid legalEntityId, Guid accountId, DateOnly upTo, string? excludeSourceModule,
        CancellationToken cancellationToken = default)
    {
        var query = from line in db.JournalLines.AsNoTracking()
                    join journal in db.Journals.AsNoTracking() on line.JournalEntryId equals journal.Id
                    where journal.LegalEntityId == legalEntityId && journal.Status != JournalStatus.Draft && line.AccountId == accountId && journal.JournalDate <= upTo
                    select new { line, journal };
        if (excludeSourceModule is not null)
        {
            query = query.Where(x => x.journal.SourceModule != excludeSourceModule);
        }

        var rows = await query.OrderBy(x => x.journal.JournalDate).ThenBy(x => x.journal.Number).ThenBy(x => x.line.LineNo).ToListAsync(cancellationToken);
        return rows.Select(x => new AccountLedgerLine(x.journal.Id, x.line.Id, x.journal.Number ?? string.Empty, x.journal.JournalDate, x.journal.SourceModule,
            x.journal.SourceDocType, x.journal.SourceDocId, x.journal.Currency, x.journal.ExchangeRate, x.line.Debit, x.line.Credit, x.line.BaseDebit, x.line.BaseCredit,
            x.line.Description ?? x.journal.Description)).ToList();
    }

    public async Task<Guid?> ResolveRoleAccountAsync(Guid legalEntityId, string role, CancellationToken cancellationToken = default)
    {
        var rules = await db.PostingRules.AsNoTracking().Where(r => r.Role == role).ToListAsync(cancellationToken);
        return PostingRuleResolver.Resolve(rules, role, legalEntityId, null, null, null, null)?.AccountId;
    }

    public async Task<IReadOnlyList<TaxLedgerLine>> GetTaxLinesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var (start, end) = (from, to);
        var rows = await (from line in db.JournalLines.AsNoTracking()
                          join journal in db.Journals.AsNoTracking() on line.JournalEntryId equals journal.Id
                          where journal.Status != JournalStatus.Draft && journal.JournalDate >= start && journal.JournalDate <= end
                                && journal.SourceDocType != AccountingDocumentTypes.YearEndClose
                                && line.PostingRole != null && TaxRoles.Contains(line.PostingRole)
                          orderby journal.JournalDate, journal.Number, line.LineNo
                          select new
                          {
                              journal.LegalEntityId, journal.JournalDate, journal.Number, journal.SourceModule, journal.SourceDocType, journal.SourceDocId,
                              line.PostingRole, line.TaxCodeId, line.BaseDebit, line.BaseCredit, line.TaxableBase,
                          }).ToListAsync(cancellationToken);
        return rows.Select(r =>
        {
            // Output tax grows on the credit side; input tax on the debit side.
            var sign = r.PostingRole == PostingRoles.OutputTax ? (r.BaseCredit > 0 ? 1 : -1) : (r.BaseDebit > 0 ? 1 : -1);
            var amount = r.PostingRole == PostingRoles.OutputTax ? r.BaseCredit - r.BaseDebit : r.BaseDebit - r.BaseCredit;
            return new TaxLedgerLine(r.LegalEntityId, r.JournalDate, r.Number ?? string.Empty, r.SourceModule, r.SourceDocType, r.SourceDocId, r.PostingRole!,
                r.TaxCodeId, amount, r.TaxableBase is null ? null : sign * r.TaxableBase.Value);
        }).ToList();
    }

    public async Task<IReadOnlyList<TaxAccountMovement>> GetTaxAccountMovementsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var (start, end) = (from, to);
        var mapped = await db.PostingRules.AsNoTracking().Where(r => TaxRoles.Contains(r.Role))
            .Select(r => new { r.Role, r.AccountId }).Distinct().ToListAsync(cancellationToken);
        var accountIds = mapped.Select(m => m.AccountId).Distinct().ToList();
        var totals = await (from line in db.JournalLines.AsNoTracking()
                            join journal in db.Journals.AsNoTracking() on line.JournalEntryId equals journal.Id
                            where journal.Status != JournalStatus.Draft && journal.JournalDate >= start && journal.JournalDate <= end
                                  && journal.SourceDocType != AccountingDocumentTypes.YearEndClose && accountIds.Contains(line.AccountId)
                            group line by line.AccountId into g
                            select new { AccountId = g.Key, NetCredit = g.Sum(l => l.BaseCredit - l.BaseDebit) }).ToListAsync(cancellationToken);
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        return accountIds.Select(id => new TaxAccountMovement(id, accounts[id].Code, accounts[id].NameEn,
                string.Join(",", mapped.Where(m => m.AccountId == id).Select(m => m.Role).Distinct().Order(StringComparer.Ordinal)),
                totals.FirstOrDefault(t => t.AccountId == id)?.NetCredit ?? 0m))
            .OrderBy(m => m.Code, StringComparer.Ordinal).ToList();
    }
}

/// <summary>Flat ledger row (member-initialised so EF can group and aggregate over it).</summary>
public sealed class LedgerRow
{
    public Guid AccountId { get; init; }

    public Guid JournalId { get; init; }

    public string SourceDocType { get; init; } = string.Empty;

    public Guid? CostCenterId { get; init; }

    public Guid? ProfitCenterId { get; init; }

    public string? Number { get; init; }

    public DateOnly JournalDate { get; init; }

    public string JournalDescription { get; init; } = string.Empty;

    public string? LineDescription { get; init; }

    public decimal BaseDebit { get; init; }

    public decimal BaseCredit { get; init; }

    public int LineNo { get; init; }
}
