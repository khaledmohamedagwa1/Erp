using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

// ---------- DTOs ----------
public sealed record AccountDto(Guid Id, string Code, string NameEn, string NameAr, string Type, string NormalBalance, Guid? ParentId,
    bool IsPostingAccount, string ControlSubledger, bool AllowManualPosting, string Status, string CashFlowCategory = "None");

public sealed record FiscalPeriodDto(Guid Id, int PeriodNo, DateOnly StartDate, DateOnly EndDate, string Status);

public sealed record FiscalYearDto(Guid Id, Guid LegalEntityId, string Code, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<FiscalPeriodDto> Periods);

public sealed record CostCenterDto(Guid Id, string Code, string Name, Guid? ParentId, bool IsActive);

public sealed record PostingRuleDto(Guid Id, string Role, Guid AccountId, string AccountCode, Guid? LegalEntityId, string? ProductPostingGroup,
    string? PartyPostingGroup, Guid? TaxCodeId, Guid? WarehouseId, int Priority);

public sealed record JournalLineDto(int LineNo, Guid AccountId, string AccountCode, string AccountNameEn, decimal Debit, decimal Credit,
    decimal BaseDebit, decimal BaseCredit, Guid? CostCenterId, string? PartyType, Guid? PartyId, string? Description);

public sealed record JournalDto(Guid Id, Guid LegalEntityId, string? Number, DateOnly JournalDate, string Status, string SourceModule, string SourceDocType,
    Guid SourceDocId, string Purpose, string Description, string Currency, decimal ExchangeRate, decimal TotalBaseDebit, decimal TotalBaseCredit,
    Guid? ReversalOfId, Guid? ReversedById, DateTime? PostedAtUtc, IReadOnlyList<JournalLineDto> Lines);

public sealed record JournalSummaryDto(Guid Id, string? Number, DateOnly JournalDate, string Status, string SourceModule, string SourceDocType,
    string Description, string Currency, decimal TotalBaseDebit);

public sealed record GeneralLedgerLineDto(DateOnly JournalDate, Guid JournalId, string? Number, string? Description, decimal Debit, decimal Credit, decimal Balance);

public sealed record GeneralLedgerDto(Guid AccountId, string AccountCode, string AccountNameEn, DateOnly From, DateOnly To, decimal OpeningBalance,
    IReadOnlyList<GeneralLedgerLineDto> Lines, decimal ClosingBalance);

public sealed record SeedResultDto(int AccountsCreated, int RulesCreated, IReadOnlyList<string> UnresolvedRoles);

internal static class AccountingMapping
{
    public static AccountDto ToDto(Account a) => new(a.Id, a.Code, a.NameEn, a.NameAr, a.Type.ToString(), a.NormalBalance.ToString(), a.ParentId,
        a.IsPostingAccount, a.ControlSubledger.ToString(), a.AllowManualPosting, a.Status.ToString(), a.CashFlowCategory.ToString());

    public static FiscalYearDto ToDto(FiscalYear y) => new(y.Id, y.LegalEntityId, y.Code, y.StartDate, y.EndDate,
        y.Periods.Select(p => new FiscalPeriodDto(p.Id, p.PeriodNo, p.StartDate, p.EndDate, p.Status.ToString())).ToList());

    public static async Task<JournalDto> ToDtoAsync(IAccountingDbContext db, JournalEntry j, CancellationToken ct)
    {
        var ids = j.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return new JournalDto(j.Id, j.LegalEntityId, j.Number, j.JournalDate, j.Status.ToString(), j.SourceModule, j.SourceDocType, j.SourceDocId,
            j.Purpose, j.Description, j.Currency, j.ExchangeRate, j.TotalBaseDebit, j.TotalBaseCredit, j.ReversalOfId, j.ReversedById, j.PostedAtUtc,
            j.Lines.Select(l => new JournalLineDto(l.LineNo, l.AccountId, accounts.GetValueOrDefault(l.AccountId)?.Code ?? "?",
                accounts.GetValueOrDefault(l.AccountId)?.NameEn ?? "?", l.Debit, l.Credit, l.BaseDebit, l.BaseCredit, l.CostCenterId, l.PartyType,
                l.PartyId, l.Description)).ToList());
    }
}

/// <summary>Resolves a legal entity of the active company (company filter semantics: others are "not found").</summary>
public sealed class CompanyLegalEntities(IOrganizationQueries organization, ITenantContext tenant)
{
    public async Task<Result<LegalEntitySummary>> GetAsync(Guid legalEntityId, CancellationToken ct)
    {
        var le = await organization.GetLegalEntityAsync(legalEntityId, ct);
        return le is null || le.CompanyId != tenant.CompanyId ? PostingErrors.LegalEntityNotFound : le;
    }
}

// ---------- Accounts ----------
public sealed record ListAccountsQuery : IQuery<IReadOnlyList<AccountDto>>;

public sealed class ListAccountsHandler(IAccountingDbContext db) : IQueryHandler<ListAccountsQuery, IReadOnlyList<AccountDto>>
{
    public async Task<Result<IReadOnlyList<AccountDto>>> Handle(ListAccountsQuery query, CancellationToken cancellationToken)
    {
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Code).ToListAsync(cancellationToken);
        return accounts.Select(AccountingMapping.ToDto).ToList();
    }
}

public sealed record CreateAccountCommand(string Code, string NameEn, string NameAr, string Type, Guid? ParentId, bool IsPostingAccount,
    string? ControlSubledger, bool AllowManualPosting = true, string? CashFlowCategory = null) : ICommand<AccountDto>;

public sealed class CreateAccountValidator : AbstractValidator<CreateAccountCommand>
{
    public CreateAccountValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Type).Must(t => Enum.TryParse<AccountType>(t, true, out _)).WithMessage("Unknown account type.");
        RuleFor(x => x.ControlSubledger).Must(c => c is null || Enum.TryParse<ControlSubledger>(c, true, out _)).WithMessage("Unknown control sub-ledger.");
        RuleFor(x => x.CashFlowCategory).Must(c => c is null || Enum.TryParse<CashFlowCategory>(c, true, out _)).WithMessage("Unknown cash-flow category.");
    }
}

public sealed class CreateAccountHandler(IAccountingDbContext db, ITenantContext tenant) : ICommandHandler<CreateAccountCommand, AccountDto>
{
    public async Task<Result<AccountDto>> Handle(CreateAccountCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Accounts.AnyAsync(a => a.Code == code, cancellationToken))
        {
            return AccountingErrors.AccountCodeTaken;
        }

        Account? parent = null;
        if (command.ParentId is { } parentId)
        {
            parent = await db.Accounts.FirstOrDefaultAsync(a => a.Id == parentId, cancellationToken);
            if (parent is null)
            {
                return AccountingErrors.AccountNotFound;
            }
        }

        var account = Account.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr, Enum.Parse<AccountType>(command.Type, true), parent,
            command.IsPostingAccount, command.ControlSubledger is null ? ControlSubledger.None : Enum.Parse<ControlSubledger>(command.ControlSubledger, true),
            command.AllowManualPosting);
        account.SetCashFlowCategory(command.CashFlowCategory is null
            ? (account.IsBalanceSheet && account.IsPostingAccount ? CashFlowCategory.Operating : CashFlowCategory.None)
            : Enum.Parse<CashFlowCategory>(command.CashFlowCategory, true));
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return AccountingMapping.ToDto(account);
    }
}

public sealed record UpdateAccountCommand(Guid AccountId, string NameEn, string NameAr, bool AllowManualPosting) : ICommand<AccountDto>;

public sealed class UpdateAccountHandler(IAccountingDbContext db) : ICommandHandler<UpdateAccountCommand, AccountDto>
{
    public async Task<Result<AccountDto>> Handle(UpdateAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == command.AccountId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        account.Update(command.NameEn, command.NameAr, command.AllowManualPosting);
        await db.SaveChangesAsync(cancellationToken);
        return AccountingMapping.ToDto(account);
    }
}

public sealed record SetAccountCashFlowCategoryCommand(Guid AccountId, string Category) : ICommand<AccountDto>;

public sealed class SetAccountCashFlowCategoryValidator : AbstractValidator<SetAccountCashFlowCategoryCommand>
{
    public SetAccountCashFlowCategoryValidator() =>
        RuleFor(x => x.Category).Must(c => Enum.TryParse<CashFlowCategory>(c, true, out _))
            .WithMessage("Category must be None, Operating, Investing, Financing or Cash.");
}

public sealed class SetAccountCashFlowCategoryHandler(IAccountingDbContext db) : ICommandHandler<SetAccountCashFlowCategoryCommand, AccountDto>
{
    public async Task<Result<AccountDto>> Handle(SetAccountCashFlowCategoryCommand command, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == command.AccountId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        account.SetCashFlowCategory(Enum.Parse<CashFlowCategory>(command.Category, true));
        await db.SaveChangesAsync(cancellationToken);
        return AccountingMapping.ToDto(account);
    }
}

public sealed record SetAccountActiveCommand(Guid AccountId, bool Active) : ICommand<AccountDto>;

public sealed class SetAccountActiveHandler(IAccountingDbContext db) : ICommandHandler<SetAccountActiveCommand, AccountDto>
{
    public async Task<Result<AccountDto>> Handle(SetAccountActiveCommand command, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == command.AccountId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        if (command.Active)
        {
            account.Activate();
        }
        else
        {
            account.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);
        return AccountingMapping.ToDto(account);
    }
}

// ---------- Chart template ----------
public sealed record SeedGenericChartCommand : ICommand<SeedResultDto>;

public sealed class SeedGenericChartHandler(IAccountingDbContext db, ITenantContext tenant) : ICommandHandler<SeedGenericChartCommand, SeedResultDto>
{
    public async Task<Result<SeedResultDto>> Handle(SeedGenericChartCommand command, CancellationToken cancellationToken)
    {
        var companyId = tenant.CompanyId!.Value;
        var byCode = await db.Accounts.ToDictionaryAsync(a => a.Code, cancellationToken);
        var created = 0;
        foreach (var template in GenericChartTemplate.Accounts)
        {
            if (byCode.ContainsKey(template.Code))
            {
                continue;
            }

            var parent = template.Parent is null ? null : byCode[template.Parent];
            var account = Account.Create(companyId, template.Code, template.NameEn, template.NameAr, template.Type, parent, template.Posting, template.Control);
            account.SetCashFlowCategory(GenericChartTemplate.CategoryFor(template));
            db.Accounts.Add(account);
            byCode[template.Code] = account;
            created++;
        }

        var rules = await db.PostingRules.ToListAsync(cancellationToken);
        var rulesCreated = 0;
        foreach (var (role, code) in GenericChartTemplate.RoleAccounts)
        {
            if (rules.Any(r => r.Role == role && r.Specificity == 0) || !byCode.TryGetValue(code, out var account))
            {
                continue;
            }

            db.PostingRules.Add(AccountDetermination.Create(companyId, role, account.Id));
            rulesCreated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        var unresolved = PostingRoles.All.Where(r => !GenericChartTemplate.RoleAccounts.ContainsKey(r)
            && !rules.Any(x => x.Role == r)).ToList();
        return new SeedResultDto(created, rulesCreated, unresolved);
    }
}

// ---------- Fiscal years & periods ----------
public sealed record CreateFiscalYearCommand(Guid LegalEntityId, DateOnly StartDate, int Months = 12) : ICommand<FiscalYearDto>;

public sealed class CreateFiscalYearHandler(IAccountingDbContext db, CompanyLegalEntities legalEntities) : ICommandHandler<CreateFiscalYearCommand, FiscalYearDto>
{
    public async Task<Result<FiscalYearDto>> Handle(CreateFiscalYearCommand command, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var year = FiscalYear.Create(le.Value.CompanyId, command.LegalEntityId, command.StartDate, command.Months);
        var overlaps = await db.FiscalYears.AnyAsync(y => y.LegalEntityId == command.LegalEntityId && y.StartDate <= year.EndDate && y.EndDate >= year.StartDate, cancellationToken);
        if (overlaps)
        {
            return AccountingErrors.FiscalYearOverlaps;
        }

        db.FiscalYears.Add(year);
        await db.SaveChangesAsync(cancellationToken);
        return AccountingMapping.ToDto(year);
    }
}

public sealed record ListFiscalYearsQuery(Guid? LegalEntityId) : IQuery<IReadOnlyList<FiscalYearDto>>;

public sealed class ListFiscalYearsHandler(IAccountingDbContext db) : IQueryHandler<ListFiscalYearsQuery, IReadOnlyList<FiscalYearDto>>
{
    public async Task<Result<IReadOnlyList<FiscalYearDto>>> Handle(ListFiscalYearsQuery query, CancellationToken cancellationToken)
    {
        var source = db.FiscalYears.AsNoTracking().Include(y => y.Periods).AsQueryable();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(y => y.LegalEntityId == le);
        }

        var years = await source.OrderBy(y => y.StartDate).ToListAsync(cancellationToken);
        return years.Select(AccountingMapping.ToDto).ToList();
    }
}

public sealed record SetPeriodStatusCommand(Guid PeriodId, string Status) : ICommand<FiscalPeriodDto>;

public sealed class SetPeriodStatusValidator : AbstractValidator<SetPeriodStatusCommand>
{
    public SetPeriodStatusValidator() =>
        RuleFor(x => x.Status).Must(s => Enum.TryParse<PeriodStatus>(s, true, out _)).WithMessage("Status must be Open, SoftClosed or Closed.");
}

public sealed class SetPeriodStatusHandler(IAccountingDbContext db) : ICommandHandler<SetPeriodStatusCommand, FiscalPeriodDto>
{
    public async Task<Result<FiscalPeriodDto>> Handle(SetPeriodStatusCommand command, CancellationToken cancellationToken)
    {
        var year = await db.FiscalYears.Include(y => y.Periods).FirstOrDefaultAsync(y => y.Periods.Any(p => p.Id == command.PeriodId), cancellationToken);
        if (year is null)
        {
            return AccountingErrors.PeriodNotFound;
        }

        var result = year.SetPeriodStatus(command.PeriodId, Enum.Parse<PeriodStatus>(command.Status, true));
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        var p = year.Periods.Single(x => x.Id == command.PeriodId);
        return new FiscalPeriodDto(p.Id, p.PeriodNo, p.StartDate, p.EndDate, p.Status.ToString());
    }
}

// ---------- Cost centres ----------
public sealed record CreateCostCenterCommand(string Code, string Name, Guid? ParentId) : ICommand<CostCenterDto>;

public sealed class CreateCostCenterHandler(IAccountingDbContext db, ITenantContext tenant) : ICommandHandler<CreateCostCenterCommand, CostCenterDto>
{
    public async Task<Result<CostCenterDto>> Handle(CreateCostCenterCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.CostCenters.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return Error.Conflict("accounting.cost-center-code-taken", "A cost centre with this code already exists.");
        }

        if (command.ParentId is { } parentId && !await db.CostCenters.AnyAsync(c => c.Id == parentId, cancellationToken))
        {
            return Error.NotFound("accounting.cost-center-not-found", "The parent cost centre was not found.");
        }

        var center = CostCenter.Create(tenant.CompanyId!.Value, code, command.Name, command.ParentId);
        db.CostCenters.Add(center);
        await db.SaveChangesAsync(cancellationToken);
        return new CostCenterDto(center.Id, center.Code, center.Name, center.ParentId, center.IsActive);
    }
}

public sealed record ListCostCentersQuery : IQuery<IReadOnlyList<CostCenterDto>>;

public sealed class ListCostCentersHandler(IAccountingDbContext db) : IQueryHandler<ListCostCentersQuery, IReadOnlyList<CostCenterDto>>
{
    public async Task<Result<IReadOnlyList<CostCenterDto>>> Handle(ListCostCentersQuery query, CancellationToken cancellationToken) =>
        await db.CostCenters.AsNoTracking().OrderBy(c => c.Code)
            .Select(c => new CostCenterDto(c.Id, c.Code, c.Name, c.ParentId, c.IsActive)).ToListAsync(cancellationToken);
}

// ---------- Posting rules ----------
public sealed record ListPostingRulesQuery : IQuery<IReadOnlyList<PostingRuleDto>>;

public sealed class ListPostingRulesHandler(IAccountingDbContext db) : IQueryHandler<ListPostingRulesQuery, IReadOnlyList<PostingRuleDto>>
{
    public async Task<Result<IReadOnlyList<PostingRuleDto>>> Handle(ListPostingRulesQuery query, CancellationToken cancellationToken)
    {
        var rules = await db.PostingRules.AsNoTracking().OrderBy(r => r.Role).ThenBy(r => r.Priority).ToListAsync(cancellationToken);
        var ids = rules.Select(r => r.AccountId).Distinct().ToList();
        var codes = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code, cancellationToken);
        return rules.Select(r => new PostingRuleDto(r.Id, r.Role, r.AccountId, codes.GetValueOrDefault(r.AccountId, "?"), r.LegalEntityId,
            r.ProductPostingGroup, r.PartyPostingGroup, r.TaxCodeId, r.WarehouseId, r.Priority)).ToList();
    }
}

public sealed record UpsertPostingRuleCommand(string Role, Guid AccountId, Guid? LegalEntityId, string? ProductPostingGroup, string? PartyPostingGroup,
    Guid? TaxCodeId, Guid? WarehouseId, int Priority = 0) : ICommand<PostingRuleDto>;

public sealed class UpsertPostingRuleHandler(IAccountingDbContext db, ITenantContext tenant, CompanyLegalEntities legalEntities)
    : ICommandHandler<UpsertPostingRuleCommand, PostingRuleDto>
{
    public async Task<Result<PostingRuleDto>> Handle(UpsertPostingRuleCommand command, CancellationToken cancellationToken)
    {
        if (!PostingRoles.IsKnown(command.Role))
        {
            return PostingErrors.UnknownRole(command.Role);
        }

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == command.AccountId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        if (!account.IsPostingAccount)
        {
            return AccountingErrors.NotPostingAccount;
        }

        if (command.LegalEntityId is { } le && (await legalEntities.GetAsync(le, cancellationToken)).IsFailure)
        {
            return PostingErrors.LegalEntityNotFound;
        }

        var rules = await db.PostingRules.Where(r => r.Role == command.Role).ToListAsync(cancellationToken);
        var rule = rules.FirstOrDefault(r => r.SameKey(command.LegalEntityId, command.ProductPostingGroup, command.PartyPostingGroup, command.TaxCodeId, command.WarehouseId));
        if (rule is null)
        {
            rule = AccountDetermination.Create(tenant.CompanyId!.Value, command.Role, command.AccountId, command.LegalEntityId,
                command.ProductPostingGroup, command.PartyPostingGroup, command.TaxCodeId, command.WarehouseId, command.Priority);
            db.PostingRules.Add(rule);
        }
        else
        {
            rule.Retarget(command.AccountId, command.Priority);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PostingRuleDto(rule.Id, rule.Role, rule.AccountId, account.Code, rule.LegalEntityId, rule.ProductPostingGroup,
            rule.PartyPostingGroup, rule.TaxCodeId, rule.WarehouseId, rule.Priority);
    }
}

// ---------- Manual journals ----------
public sealed record ManualJournalLine(Guid AccountId, decimal Debit, decimal Credit, Guid? CostCenterId, string? Description);

public sealed record CreateManualJournalCommand(Guid LegalEntityId, DateOnly JournalDate, string Description, string? Currency, decimal? ExchangeRate,
    IReadOnlyList<ManualJournalLine> Lines) : ICommand<JournalDto>;

public sealed class CreateManualJournalValidator : AbstractValidator<CreateManualJournalCommand>
{
    public CreateManualJournalValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Description).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Lines).NotEmpty();
        RuleFor(x => x.Currency).Length(3).When(x => x.Currency is not null);
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.AccountId).NotEmpty();
            line.RuleFor(l => l).Must(l => l.Debit >= 0 && l.Credit >= 0 && (l.Debit > 0) != (l.Credit > 0))
                .WithMessage("Each line needs a positive debit or a positive credit, not both.");
        });
    }
}

public sealed class CreateManualJournalHandler(IAccountingDbContext db, CompanyLegalEntities legalEntities)
    : ICommandHandler<CreateManualJournalCommand, JournalDto>
{
    public const string DocumentType = "ManualJournal";
    public const string Purpose = "manual";

    public async Task<Result<JournalDto>> Handle(CreateManualJournalCommand command, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var currency = (command.Currency ?? le.Value.FunctionalCurrency).ToUpperInvariant();
        var rate = currency == le.Value.FunctionalCurrency ? 1m : command.ExchangeRate ?? 0m;
        if (rate <= 0)
        {
            return AccountingErrors.InvalidRate;
        }

        var accountIds = command.Lines.Select(l => l.AccountId).Distinct().ToList();
        if (await db.Accounts.CountAsync(a => accountIds.Contains(a.Id), cancellationToken) != accountIds.Count)
        {
            return AccountingErrors.AccountNotFound;
        }

        var journal = JournalEntry.CreateDraft(le.Value.CompanyId, command.LegalEntityId, command.JournalDate, SourceModules.Accounting, DocumentType,
            SequentialGuid.New(), Purpose, command.Description, currency, rate);
        foreach (var line in command.Lines)
        {
            journal.AddLine(line.AccountId, line.Debit, line.Credit, line.CostCenterId, null, null, line.Description);
        }

        db.Journals.Add(journal);
        await db.SaveChangesAsync(cancellationToken);
        return await AccountingMapping.ToDtoAsync(db, journal, cancellationToken);
    }
}

public sealed record PostManualJournalCommand(Guid JournalId) : ICommand<JournalDto>;

public sealed class PostManualJournalHandler(IAccountingDbContext db, JournalPoster poster) : ICommandHandler<PostManualJournalCommand, JournalDto>
{
    public async Task<Result<JournalDto>> Handle(PostManualJournalCommand command, CancellationToken cancellationToken)
    {
        var journal = await db.Journals.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == command.JournalId, cancellationToken);
        if (journal is null)
        {
            return AccountingErrors.JournalNotFound;
        }

        if (journal.SourceModule != SourceModules.Accounting)
        {
            return PostingErrors.ReverseFromSource with { Code = "accounting.not-a-manual-journal", Message = "Only manual journals can be posted here." };
        }

        if (journal.Status != JournalStatus.Draft)
        {
            return AccountingErrors.NotDraft;
        }

        var posted = await poster.PostDraftAsync(journal, manual: true, cancellationToken);
        return posted.IsFailure ? posted.Error : await AccountingMapping.ToDtoAsync(db, journal, cancellationToken);
    }
}

public sealed record ReverseJournalCommand(Guid JournalId, DateOnly ReversalDate) : ICommand<JournalDto>;

public sealed class ReverseJournalHandler(IAccountingDbContext db, JournalPoster poster) : ICommandHandler<ReverseJournalCommand, JournalDto>
{
    public async Task<Result<JournalDto>> Handle(ReverseJournalCommand command, CancellationToken cancellationToken)
    {
        var journal = await db.Journals.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == command.JournalId, cancellationToken);
        if (journal is null)
        {
            return AccountingErrors.JournalNotFound;
        }

        if (journal.SourceModule != SourceModules.Accounting)
        {
            return PostingErrors.ReverseFromSource;
        }

        if (journal.Status == JournalStatus.Reversed)
        {
            return AccountingErrors.AlreadyReversed;
        }

        var reversal = await poster.ReverseAsync(journal, command.ReversalDate, cancellationToken);
        return reversal.IsFailure ? reversal.Error : await AccountingMapping.ToDtoAsync(db, reversal.Value, cancellationToken);
    }
}

public sealed record GetJournalQuery(Guid JournalId) : IQuery<JournalDto>;

public sealed class GetJournalHandler(IAccountingDbContext db) : IQueryHandler<GetJournalQuery, JournalDto>
{
    public async Task<Result<JournalDto>> Handle(GetJournalQuery query, CancellationToken cancellationToken)
    {
        var journal = await db.Journals.AsNoTracking().Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == query.JournalId, cancellationToken);
        return journal is null ? AccountingErrors.JournalNotFound : await AccountingMapping.ToDtoAsync(db, journal, cancellationToken);
    }
}

public sealed record ListJournalsQuery(Guid? LegalEntityId, DateOnly? From, DateOnly? To, string? Status, string? SourceModule, PageRequest Page)
    : IQuery<PagedResult<JournalSummaryDto>>;

public sealed class ListJournalsHandler(IAccountingDbContext db) : IQueryHandler<ListJournalsQuery, PagedResult<JournalSummaryDto>>
{
    public async Task<Result<PagedResult<JournalSummaryDto>>> Handle(ListJournalsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Journals.AsNoTracking();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(j => j.LegalEntityId == le);
        }

        if (query.From is { } from)
        {
            source = source.Where(j => j.JournalDate >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(j => j.JournalDate <= to);
        }

        if (query.Status is not null && Enum.TryParse<JournalStatus>(query.Status, true, out var status))
        {
            source = source.Where(j => j.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceModule))
        {
            source = source.Where(j => j.SourceModule == query.SourceModule);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(j => j.JournalDate).ThenByDescending(j => j.Number)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(j => new JournalSummaryDto(j.Id, j.Number, j.JournalDate, j.Status.ToString(), j.SourceModule, j.SourceDocType, j.Description,
                j.Currency, db.JournalLines.Where(l => l.JournalEntryId == j.Id).Sum(l => l.BaseDebit)))
            .ToListAsync(cancellationToken);
        return new PagedResult<JournalSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ---------- Reports ----------
public sealed record TrialBalanceQuery(Guid LegalEntityId, DateOnly From, DateOnly To) : IQuery<TrialBalance>;

public sealed class TrialBalanceHandler(ILedgerQueries ledger, CompanyLegalEntities legalEntities) : IQueryHandler<TrialBalanceQuery, TrialBalance>
{
    public async Task<Result<TrialBalance>> Handle(TrialBalanceQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        return await ledger.GetTrialBalanceAsync(query.LegalEntityId, query.From, query.To, cancellationToken);
    }
}

public sealed record GeneralLedgerQuery(Guid LegalEntityId, Guid AccountId, DateOnly From, DateOnly To) : IQuery<GeneralLedgerDto>;

public sealed class GeneralLedgerHandler(IAccountingDbContext db, LedgerQueries ledger, CompanyLegalEntities legalEntities)
    : IQueryHandler<GeneralLedgerQuery, GeneralLedgerDto>
{
    public async Task<Result<GeneralLedgerDto>> Handle(GeneralLedgerQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.AccountId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        var rows = ledger.PostedLines(query.LegalEntityId).Where(r => r.AccountId == query.AccountId);
        var opening = await rows.Where(r => r.JournalDate < query.From).SumAsync(r => r.BaseDebit - r.BaseCredit, cancellationToken);
        var period = await rows.Where(r => r.JournalDate >= query.From && r.JournalDate <= query.To)
            .OrderBy(r => r.JournalDate).ThenBy(r => r.Number).ThenBy(r => r.LineNo).ToListAsync(cancellationToken);
        var running = opening;
        var lines = new List<GeneralLedgerLineDto>();
        foreach (var r in period)
        {
            running += r.BaseDebit - r.BaseCredit;
            lines.Add(new GeneralLedgerLineDto(r.JournalDate, r.JournalId, r.Number, r.LineDescription ?? r.JournalDescription, r.BaseDebit, r.BaseCredit, running));
        }

        return new GeneralLedgerDto(account.Id, account.Code, account.NameEn, query.From, query.To, opening, lines, running);
    }
}
