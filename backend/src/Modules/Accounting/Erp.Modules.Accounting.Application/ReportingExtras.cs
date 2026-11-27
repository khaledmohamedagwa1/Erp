using System.Globalization;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

public sealed record AccountingReadinessDto(Guid LegalEntityId, IReadOnlyList<string> MissingRoles, bool HasChartOfAccounts, bool HasFiscalYearForToday,
    string? TodayPeriodStatus, bool IsReady);

/// <summary>
/// Accounting readiness of a legal entity: which posting roles do not resolve to an account, and whether today falls in a fiscal
/// period. Operational modules should be enabled only when this is ready.
/// </summary>
public sealed record AccountingReadinessQuery(Guid LegalEntityId, IReadOnlyList<string>? Roles = null) : IQuery<AccountingReadinessDto>;

public sealed class AccountingReadinessHandler(IAccountingDbContext db, ILedgerQueries ledger, CompanyLegalEntities legalEntities, IClock clock)
    : IQueryHandler<AccountingReadinessQuery, AccountingReadinessDto>
{
    public async Task<Result<AccountingReadinessDto>> Handle(AccountingReadinessQuery query, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var roles = query.Roles is { Count: > 0 } requested ? requested : PostingRoles.All;
        var unknown = roles.Where(r => !PostingRoles.IsKnown(r)).ToList();
        if (unknown.Count > 0)
        {
            return PostingErrors.UnknownRole(string.Join(", ", unknown));
        }

        var missing = await ledger.GetUnresolvedRolesAsync(query.LegalEntityId, roles, cancellationToken);
        var hasAccounts = await db.Accounts.AnyAsync(cancellationToken);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var year = await db.FiscalYears.AsNoTracking().Include(y => y.Periods)
            .FirstOrDefaultAsync(y => y.LegalEntityId == query.LegalEntityId && y.StartDate <= today && y.EndDate >= today, cancellationToken);
        var period = year?.FindPeriod(today);
        return new AccountingReadinessDto(query.LegalEntityId, missing, hasAccounts, year is not null, period?.Status.ToString(),
            hasAccounts && missing.Count == 0 && year is not null);
    }
}

/// <summary>
/// Minimal RFC 4180 CSV writer (no external packages). UTF-8 with BOM so spreadsheet tools show Arabic names correctly.
/// Amounts use the invariant culture with two decimals.
/// </summary>
public static class AccountingCsv
{
    public static byte[] ToBytes(string csv) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];

    public static string TrialBalance(TrialBalance tb)
    {
        var sb = new StringBuilder();
        Row(sb, "Code", "Account (EN)", "Account (AR)", "Type", "Opening", "Debit", "Credit", "Closing");
        foreach (var l in tb.Lines)
        {
            Row(sb, l.Code, l.NameEn, l.NameAr, l.Type, Amount(l.OpeningBalance), Amount(l.Debit), Amount(l.Credit), Amount(l.ClosingBalance));
        }

        Row(sb, string.Empty, "Total", "الإجمالي", string.Empty, string.Empty, Amount(tb.TotalDebit), Amount(tb.TotalCredit), string.Empty);
        return sb.ToString();
    }

    public static string IncomeStatement(IncomeStatementDto s)
    {
        var sb = new StringBuilder();
        Row(sb, "Section", "Code", "Account (EN)", "Account (AR)", "Period", "Year to date", "Comparative");
        Sections(sb, s.Sections);
        Row(sb, "gross-profit", string.Empty, "Gross profit", "مجمل الربح", Amount(s.GrossProfit.Amount), Amount(s.GrossProfit.Ytd), Amount(s.GrossProfit.Comparative));
        Row(sb, "net-profit", string.Empty, "Net profit", "صافي الربح", Amount(s.NetProfit.Amount), Amount(s.NetProfit.Ytd), Amount(s.NetProfit.Comparative));
        return sb.ToString();
    }

    public static string BalanceSheet(BalanceSheetDto s)
    {
        var sb = new StringBuilder();
        Row(sb, "Section", "Code", "Account (EN)", "Account (AR)", "As of", string.Empty, "Comparative");
        Sections(sb, s.Sections);
        Row(sb, "total-assets", string.Empty, "Total assets", "إجمالي الأصول", Amount(s.TotalAssets.Amount), string.Empty, Amount(s.TotalAssets.Comparative));
        Row(sb, "total-liabilities-equity", string.Empty, "Total liabilities and equity", "إجمالي الالتزامات وحقوق الملكية",
            Amount(s.TotalLiabilitiesAndEquity.Amount), string.Empty, Amount(s.TotalLiabilitiesAndEquity.Comparative));
        return sb.ToString();
    }

    public static string CashFlow(CashFlowStatementDto s)
    {
        var sb = new StringBuilder();
        Row(sb, "Section", "Code", "Account (EN)", "Account (AR)", "Amount");
        Row(sb, "operating", string.Empty, "Net profit", "صافي الربح", Amount(s.NetProfit));
        foreach (var section in s.Sections)
        {
            foreach (var l in section.Lines)
            {
                Row(sb, section.Key, l.Code, l.NameEn, l.NameAr, Amount(l.Amount));
            }
        }

        Row(sb, "operating-total", string.Empty, "Net cash from operating activities", "صافي النقد من الأنشطة التشغيلية", Amount(s.NetCashFromOperating));
        Row(sb, "investing-total", string.Empty, "Net cash from investing activities", "صافي النقد من الأنشطة الاستثمارية", Amount(s.NetCashFromInvesting));
        Row(sb, "financing-total", string.Empty, "Net cash from financing activities", "صافي النقد من الأنشطة التمويلية", Amount(s.NetCashFromFinancing));
        Row(sb, "net-change", string.Empty, "Net change in cash", "صافي التغير في النقدية", Amount(s.NetChangeInCash));
        Row(sb, "opening-cash", string.Empty, "Opening cash", "النقدية أول المدة", Amount(s.OpeningCash));
        Row(sb, "closing-cash", string.Empty, "Closing cash", "النقدية آخر المدة", Amount(s.ClosingCash));
        return sb.ToString();
    }

    public static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static void Sections(StringBuilder sb, IEnumerable<StatementSectionDto> sections)
    {
        foreach (var section in sections)
        {
            foreach (var l in section.Lines)
            {
                Row(sb, section.Key, l.Code, l.NameEn, l.NameAr, Amount(l.Amount), Amount(l.Ytd), Amount(l.Comparative));
            }

            Row(sb, section.Key + "-total", string.Empty, section.TitleEn, section.TitleAr, Amount(section.Total), Amount(section.TotalYtd), Amount(section.TotalComparative));
        }
    }

    private static string Amount(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Row(StringBuilder sb, params string?[] cells) => sb.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
}
