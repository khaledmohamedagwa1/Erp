using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

// ---------- DTOs ----------
public sealed record StatementLineDto(Guid? AccountId, string Code, string NameEn, string NameAr, decimal Amount, decimal? Ytd, decimal? Comparative);

public sealed record StatementSectionDto(string Key, string TitleEn, string TitleAr, IReadOnlyList<StatementLineDto> Lines, decimal Total, decimal? TotalYtd,
    decimal? TotalComparative);

public sealed record StatementFigureDto(decimal Amount, decimal? Ytd, decimal? Comparative);

public sealed record IncomeStatementDto(Guid LegalEntityId, string Currency, DateOnly From, DateOnly To, DateOnly? YtdFrom, DateOnly? ComparativeFrom,
    DateOnly? ComparativeTo, Guid? CostCenterId, IReadOnlyList<StatementSectionDto> Sections, StatementFigureDto NetRevenue, StatementFigureDto GrossProfit,
    StatementFigureDto NetProfit, Guid? ProfitCenterId = null);

public sealed record BalanceSheetDto(Guid LegalEntityId, string Currency, DateOnly AsOf, DateOnly? ComparativeAsOf, IReadOnlyList<StatementSectionDto> Sections,
    StatementFigureDto TotalAssets, StatementFigureDto TotalLiabilities, StatementFigureDto TotalEquity, StatementFigureDto TotalLiabilitiesAndEquity, bool IsBalanced);

public sealed record CashFlowStatementDto(Guid LegalEntityId, string Currency, DateOnly From, DateOnly To, decimal NetProfit,
    IReadOnlyList<StatementSectionDto> Sections, decimal NetCashFromOperating, decimal NetCashFromInvesting, decimal NetCashFromFinancing,
    decimal NetChangeInCash, decimal OpeningCash, decimal ClosingCash, bool Reconciles);

/// <summary>What the statement builder needs to know about an account.</summary>
public sealed record StatementAccount(Guid Id, string Code, string NameEn, string NameAr, AccountType Type, CashFlowCategory CashFlow,
    bool IsOperatingRevenue, bool IsCostOfSales);

/// <summary>
/// Pure statement arithmetic over per-account amounts (base currency, debit positive). Kept free of EF so the rules are unit-tested.
/// Presentation signs: revenue/income and liabilities/equity are shown credit-positive; expenses and assets debit-positive.
/// </summary>
public static class FinancialStatementBuilder
{
    /// <summary>Revenue roles shown as net revenue; other revenue-type accounts are "other income".</summary>
    public static readonly IReadOnlyList<string> OperatingRevenueRoles =
        [PostingRoles.SalesRevenue, PostingRoles.SalesReturns, PostingRoles.SalesDiscount, PostingRoles.ServiceRevenue];

    /// <summary>Expense roles shown as cost of sales (above gross profit); other expense accounts are operating expenses.</summary>
    public static readonly IReadOnlyList<string> CostOfSalesRoles =
        [PostingRoles.CostOfGoodsSold, PostingRoles.PurchasePriceVariance, PostingRoles.CostTrueUp, PostingRoles.ProductionVariance, PostingRoles.AbnormalScrap];

    public static (IReadOnlyList<StatementSectionDto> Sections, StatementFigureDto NetRevenue, StatementFigureDto GrossProfit, StatementFigureDto NetProfit)
        IncomeStatement(IReadOnlyCollection<StatementAccount> accounts, IReadOnlyDictionary<Guid, decimal> current,
            IReadOnlyDictionary<Guid, decimal>? ytd, IReadOnlyDictionary<Guid, decimal>? comparative)
    {
        var revenue = Section("revenue", "Revenue", "الإيرادات", accounts.Where(a => a.Type == AccountType.Revenue && a.IsOperatingRevenue), -1, current, ytd, comparative);
        var cost = Section("cost-of-sales", "Cost of sales", "تكلفة المبيعات", accounts.Where(a => a.Type == AccountType.Expense && a.IsCostOfSales), 1, current, ytd, comparative);
        var other = Section("other-income", "Other income", "إيرادات أخرى", accounts.Where(a => a.Type == AccountType.Revenue && !a.IsOperatingRevenue), -1, current, ytd, comparative);
        var expenses = Section("operating-expenses", "Operating expenses", "المصروفات التشغيلية", accounts.Where(a => a.Type == AccountType.Expense && !a.IsCostOfSales), 1, current, ytd, comparative);

        var netRevenue = new StatementFigureDto(revenue.Total, revenue.TotalYtd, revenue.TotalComparative);
        var gross = Combine(revenue, cost, (r, c) => r - c);
        var net = new StatementFigureDto(
            gross.Amount + other.Total - expenses.Total,
            gross.Ytd is null ? null : gross.Ytd + other.TotalYtd - expenses.TotalYtd,
            gross.Comparative is null ? null : gross.Comparative + other.TotalComparative - expenses.TotalComparative);
        return ([revenue, cost, other, expenses], netRevenue, gross, net);
    }

    /// <param name="current">Cumulative balances up to the as-of date, all posted journals (closing entries included).</param>
    public static (IReadOnlyList<StatementSectionDto> Sections, StatementFigureDto Assets, StatementFigureDto Liabilities, StatementFigureDto Equity, StatementFigureDto LiabilitiesAndEquity)
        BalanceSheet(IReadOnlyCollection<StatementAccount> accounts, IReadOnlyDictionary<Guid, decimal> current, IReadOnlyDictionary<Guid, decimal>? comparative)
    {
        var assets = Section("assets", "Assets", "الأصول", accounts.Where(a => a.Type == AccountType.Asset), 1, current, null, comparative);
        var liabilities = Section("liabilities", "Liabilities", "الالتزامات", accounts.Where(a => a.Type == AccountType.Liability), -1, current, null, comparative);
        var equityAccounts = Section("equity", "Equity", "حقوق الملكية", accounts.Where(a => a.Type == AccountType.Equity), -1, current, null, comparative);

        // Revenue/expense balances not yet closed to retained earnings (current year, plus any unclosed prior year).
        var pl = accounts.Where(a => !IsBalanceSheet(a.Type)).Select(a => a.Id).ToList();
        var earnings = -pl.Sum(id => current.GetValueOrDefault(id));
        decimal? earningsComparative = comparative is null ? null : -pl.Sum(id => comparative.GetValueOrDefault(id));
        var equityLines = equityAccounts.Lines.ToList();
        if (earnings != 0 || (earningsComparative ?? 0) != 0)
        {
            equityLines.Add(new StatementLineDto(null, string.Empty, "Current and unclosed earnings", "أرباح الفترة وأرباح غير مقفلة", earnings, null, earningsComparative));
        }

        var equity = equityAccounts with
        {
            Lines = equityLines,
            Total = equityAccounts.Total + earnings,
            TotalComparative = comparative is null ? null : equityAccounts.TotalComparative + earningsComparative,
        };
        var totalLe = new StatementFigureDto(liabilities.Total + equity.Total, null,
            comparative is null ? null : liabilities.TotalComparative + equity.TotalComparative);
        return ([assets, liabilities, equity], Figure(assets), Figure(liabilities), Figure(equity), totalLe);
    }

    /// <param name="netProfit">Profit for the range (closing entries excluded).</param>
    /// <param name="changes">Movement per account in the range, debit positive, closing entries excluded.</param>
    public static (IReadOnlyList<StatementSectionDto> Sections, decimal Operating, decimal Investing, decimal Financing, decimal CashChange)
        CashFlow(IReadOnlyCollection<StatementAccount> accounts, decimal netProfit, IReadOnlyDictionary<Guid, decimal> changes)
    {
        var balanceSheet = accounts.Where(a => IsBalanceSheet(a.Type)).ToList();

        // A rise in a non-cash asset consumes cash; a rise in a liability or equity provides it: effect = −(debit-positive change).
        StatementSectionDto Activities(string key, string en, string ar, Func<StatementAccount, bool> filter) =>
            Section(key, en, ar, balanceSheet.Where(filter), -1, changes, null, null);

        var operating = Activities("operating", "Operating activities (adjustments to net profit)", "الأنشطة التشغيلية (تسويات صافي الربح)",
            a => a.CashFlow == CashFlowCategory.Operating);
        var investing = Activities("investing", "Investing activities", "الأنشطة الاستثمارية", a => a.CashFlow == CashFlowCategory.Investing);
        var financing = Activities("financing", "Financing activities", "الأنشطة التمويلية", a => a.CashFlow == CashFlowCategory.Financing);

        // Unclassified balance-sheet accounts are counted with operating activities (so the statement ties) but listed separately for review.
        var unclassified = Activities("unclassified", "Unclassified movements (counted as operating — set a cash-flow category)",
            "حركات غير مصنفة (محسوبة ضمن التشغيلية — حدد فئة التدفق النقدي)", a => a.CashFlow == CashFlowCategory.None);
        var cashChange = balanceSheet.Where(a => a.CashFlow == CashFlowCategory.Cash).Sum(a => changes.GetValueOrDefault(a.Id));
        IReadOnlyList<StatementSectionDto> sections = unclassified.Lines.Count == 0 ? [operating, investing, financing] : [operating, unclassified, investing, financing];
        return (sections, netProfit + operating.Total + unclassified.Total, investing.Total, financing.Total, cashChange);
    }

    public static bool IsBalanceSheet(AccountType type) => type is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    private static StatementSectionDto Section(string key, string en, string ar, IEnumerable<StatementAccount> accounts, int sign,
        IReadOnlyDictionary<Guid, decimal> current, IReadOnlyDictionary<Guid, decimal>? ytd, IReadOnlyDictionary<Guid, decimal>? comparative)
    {
        var lines = accounts
            .Select(a => new StatementLineDto(a.Id, a.Code, a.NameEn, a.NameAr,
                sign * current.GetValueOrDefault(a.Id),
                ytd is null ? null : sign * ytd.GetValueOrDefault(a.Id),
                comparative is null ? null : sign * comparative.GetValueOrDefault(a.Id)))
            .Where(l => l.Amount != 0 || (l.Ytd ?? 0) != 0 || (l.Comparative ?? 0) != 0)
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToList();
        return new StatementSectionDto(key, en, ar, lines, lines.Sum(l => l.Amount),
            ytd is null ? null : lines.Sum(l => l.Ytd ?? 0),
            comparative is null ? null : lines.Sum(l => l.Comparative ?? 0));
    }

    private static StatementFigureDto Figure(StatementSectionDto s) => new(s.Total, s.TotalYtd, s.TotalComparative);

    private static StatementFigureDto Combine(StatementSectionDto a, StatementSectionDto b, Func<decimal, decimal, decimal> f) => new(
        f(a.Total, b.Total),
        a.TotalYtd is null ? null : f(a.TotalYtd.Value, b.TotalYtd ?? 0),
        a.TotalComparative is null ? null : f(a.TotalComparative.Value, b.TotalComparative ?? 0));
}

/// <summary>Loads ledger amounts for the statements. Everything comes from posted journal lines (Part 12.9), in functional currency.</summary>
public sealed class FinancialStatementService(IAccountingDbContext db, LedgerQueries ledger)
{
    /// <summary>
    /// Posting accounts with their stored income-statement group. Accounts without a group (created before groups existed) fall
    /// back to the posting-rule classification.
    /// </summary>
    public async Task<IReadOnlyList<StatementAccount>> AccountsAsync(Guid legalEntityId, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsPostingAccount).ToListAsync(ct);
        HashSet<Guid> revenue = [], cost = [];
        if (accounts.Any(a => !a.IsBalanceSheet && a.ReportGroup == IncomeStatementGroup.None))
        {
            var rules = await db.PostingRules.AsNoTracking()
                .Where(r => r.LegalEntityId == null || r.LegalEntityId == legalEntityId)
                .Select(r => new { r.Role, r.AccountId })
                .ToListAsync(ct);
            revenue = rules.Where(r => FinancialStatementBuilder.OperatingRevenueRoles.Contains(r.Role)).Select(r => r.AccountId).ToHashSet();
            cost = rules.Where(r => FinancialStatementBuilder.CostOfSalesRoles.Contains(r.Role)).Select(r => r.AccountId).ToHashSet();
        }

        return accounts.Select(a => new StatementAccount(a.Id, a.Code, a.NameEn, a.NameAr, a.Type, a.CashFlowCategory,
                a.ReportGroup == IncomeStatementGroup.None ? revenue.Contains(a.Id) : a.ReportGroup == IncomeStatementGroup.Revenue,
                a.ReportGroup == IncomeStatementGroup.None ? cost.Contains(a.Id) : a.ReportGroup == IncomeStatementGroup.CostOfSales))
            .ToList();
    }

    /// <summary>Debit-positive amount per account for journals dated in [from, to] (from = null: since inception).</summary>
    public async Task<Dictionary<Guid, decimal>> SumAsync(Guid legalEntityId, DateOnly? from, DateOnly to, bool excludeClosing, Guid? costCenterId, CancellationToken ct,
        Guid? profitCenterId = null)
    {
        var rows = ledger.PostedLines(legalEntityId).Where(r => r.JournalDate <= to);
        if (from is { } start)
        {
            rows = rows.Where(r => r.JournalDate >= start);
        }

        if (excludeClosing)
        {
            rows = rows.Where(r => r.SourceDocType != AccountingDocumentTypes.YearEndClose);
        }

        if (costCenterId is { } cc)
        {
            rows = rows.Where(r => r.CostCenterId == cc);
        }

        if (profitCenterId is { } pc)
        {
            rows = rows.Where(r => r.ProfitCenterId == pc);
        }

        var totals = await rows.GroupBy(r => r.AccountId)
            .Select(g => new { AccountId = g.Key, Amount = g.Sum(r => r.BaseDebit - r.BaseCredit) })
            .ToListAsync(ct);
        return totals.ToDictionary(t => t.AccountId, t => t.Amount);
    }

    public Task<FiscalYear?> FiscalYearAtAsync(Guid legalEntityId, DateOnly date, CancellationToken ct) =>
        db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(y => y.LegalEntityId == legalEntityId && y.StartDate <= date && y.EndDate >= date, ct);
}

// ---------- Queries ----------
public static class StatementErrors
{
    public static readonly Error InvalidRange = Error.Validation("accounting.invalid-date-range", "The start date must not be after the end date.");
}

public sealed record IncomeStatementQuery(Guid LegalEntityId, DateOnly From, DateOnly To, DateOnly? ComparativeFrom = null, DateOnly? ComparativeTo = null,
    Guid? CostCenterId = null, Guid? ProfitCenterId = null) : IQuery<IncomeStatementDto>;

public sealed class IncomeStatementValidator : AbstractValidator<IncomeStatementQuery>
{
    public IncomeStatementValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x).Must(x => x.From <= x.To).WithMessage("The start date must not be after the end date.");
        RuleFor(x => x).Must(x => (x.ComparativeFrom is null) == (x.ComparativeTo is null) && (x.ComparativeFrom is null || x.ComparativeFrom <= x.ComparativeTo))
            .WithMessage("Give both comparative dates, start not after end.");
    }
}

public sealed class IncomeStatementHandler(FinancialStatementService statements, CompanyLegalEntities legalEntities)
    : IQueryHandler<IncomeStatementQuery, IncomeStatementDto>
{
    public async Task<Result<IncomeStatementDto>> Handle(IncomeStatementQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var accounts = await statements.AccountsAsync(query.LegalEntityId, cancellationToken);
        var current = await statements.SumAsync(query.LegalEntityId, query.From, query.To, true, query.CostCenterId, cancellationToken, query.ProfitCenterId);
        var year = await statements.FiscalYearAtAsync(query.LegalEntityId, query.To, cancellationToken);
        var ytd = year is null
            ? null
            : await statements.SumAsync(query.LegalEntityId, year.StartDate, query.To, true, query.CostCenterId, cancellationToken, query.ProfitCenterId);
        var comparative = query.ComparativeFrom is { } cf && query.ComparativeTo is { } ctTo
            ? await statements.SumAsync(query.LegalEntityId, cf, ctTo, true, query.CostCenterId, cancellationToken, query.ProfitCenterId)
            : null;

        var (sections, netRevenue, gross, net) = FinancialStatementBuilder.IncomeStatement(accounts, current, ytd, comparative);
        return new IncomeStatementDto(query.LegalEntityId, le.Value.FunctionalCurrency, query.From, query.To, year?.StartDate, query.ComparativeFrom,
            query.ComparativeTo, query.CostCenterId, sections, netRevenue, gross, net, query.ProfitCenterId);
    }
}

public sealed record BalanceSheetQuery(Guid LegalEntityId, DateOnly AsOf, DateOnly? ComparativeAsOf = null) : IQuery<BalanceSheetDto>;

public sealed class BalanceSheetHandler(FinancialStatementService statements, CompanyLegalEntities legalEntities)
    : IQueryHandler<BalanceSheetQuery, BalanceSheetDto>
{
    public async Task<Result<BalanceSheetDto>> Handle(BalanceSheetQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var accounts = await statements.AccountsAsync(query.LegalEntityId, cancellationToken);
        var current = await statements.SumAsync(query.LegalEntityId, null, query.AsOf, false, null, cancellationToken);
        var comparative = query.ComparativeAsOf is { } c ? await statements.SumAsync(query.LegalEntityId, null, c, false, null, cancellationToken) : null;
        var (sections, assets, liabilities, equity, total) = FinancialStatementBuilder.BalanceSheet(accounts, current, comparative);
        return new BalanceSheetDto(query.LegalEntityId, le.Value.FunctionalCurrency, query.AsOf, query.ComparativeAsOf, sections, assets, liabilities, equity,
            total, assets.Amount == total.Amount && (assets.Comparative ?? 0) == (total.Comparative ?? 0));
    }
}

public sealed record CashFlowQuery(Guid LegalEntityId, DateOnly From, DateOnly To) : IQuery<CashFlowStatementDto>;

public sealed class CashFlowValidator : AbstractValidator<CashFlowQuery>
{
    public CashFlowValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x).Must(x => x.From <= x.To).WithMessage("The start date must not be after the end date.");
    }
}

public sealed class CashFlowHandler(FinancialStatementService statements, CompanyLegalEntities legalEntities)
    : IQueryHandler<CashFlowQuery, CashFlowStatementDto>
{
    public async Task<Result<CashFlowStatementDto>> Handle(CashFlowQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var accounts = await statements.AccountsAsync(query.LegalEntityId, cancellationToken);
        var changes = await statements.SumAsync(query.LegalEntityId, query.From, query.To, true, null, cancellationToken);
        var (_, _, _, netProfit) = FinancialStatementBuilder.IncomeStatement(accounts, changes, null, null);
        var (sections, operating, investing, financing, cashChange) = FinancialStatementBuilder.CashFlow(accounts, netProfit.Amount, changes);

        var cashIds = accounts.Where(a => a.CashFlow == CashFlowCategory.Cash).Select(a => a.Id).ToHashSet();
        var before = await statements.SumAsync(query.LegalEntityId, null, query.From.AddDays(-1), false, null, cancellationToken);
        var opening = before.Where(kv => cashIds.Contains(kv.Key)).Sum(kv => kv.Value);
        var net = operating + investing + financing;
        return new CashFlowStatementDto(query.LegalEntityId, le.Value.FunctionalCurrency, query.From, query.To, netProfit.Amount, sections,
            operating, investing, financing, net, opening, opening + cashChange, net == cashChange);
    }
}
