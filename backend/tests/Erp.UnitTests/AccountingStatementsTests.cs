using Erp.Modules.Accounting.Application;
using Erp.Modules.Accounting.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class AccountingStatementsTests
{
    private static readonly StatementAccount Cash = A("1110", AccountType.Asset, CashFlowCategory.Cash);
    private static readonly StatementAccount Receivables = A("1210", AccountType.Asset, CashFlowCategory.Operating);
    private static readonly StatementAccount Inventory = A("1310", AccountType.Asset, CashFlowCategory.Operating);
    private static readonly StatementAccount Ppe = A("1510", AccountType.Asset, CashFlowCategory.Investing);
    private static readonly StatementAccount AccumulatedDepreciation = A("1590", AccountType.Asset, CashFlowCategory.Operating);
    private static readonly StatementAccount Payables = A("2110", AccountType.Liability, CashFlowCategory.Operating);
    private static readonly StatementAccount Capital = A("3100", AccountType.Equity, CashFlowCategory.Financing);
    private static readonly StatementAccount Revenue = A("4110", AccountType.Revenue, CashFlowCategory.None, operatingRevenue: true);
    private static readonly StatementAccount FxGain = A("4920", AccountType.Revenue, CashFlowCategory.None);
    private static readonly StatementAccount Cogs = A("5110", AccountType.Expense, CashFlowCategory.None, costOfSales: true);
    private static readonly StatementAccount Rent = A("5310", AccountType.Expense, CashFlowCategory.None);
    private static readonly StatementAccount Depreciation = A("5510", AccountType.Expense, CashFlowCategory.None);

    private static readonly StatementAccount[] Chart =
        [Cash, Receivables, Inventory, Ppe, AccumulatedDepreciation, Payables, Capital, Revenue, FxGain, Cogs, Rent, Depreciation];

    /// <summary>
    /// Capital 100,000 into cash; buy stock 30,000 on credit; sell for 50,000 on credit at cost 20,000; pay rent 5,000;
    /// collect 40,000; buy equipment 12,000 cash; depreciate 1,000; FX gain 200 credited to cash. Debit positive.
    /// </summary>
    private static readonly Dictionary<Guid, decimal> Year = new()
    {
        [Cash.Id] = 100_000m - 5_000m + 40_000m - 12_000m + 200m,
        [Receivables.Id] = 50_000m - 40_000m,
        [Inventory.Id] = 30_000m - 20_000m,
        [Ppe.Id] = 12_000m,
        [AccumulatedDepreciation.Id] = -1_000m,
        [Payables.Id] = -30_000m,
        [Capital.Id] = -100_000m,
        [Revenue.Id] = -50_000m,
        [FxGain.Id] = -200m,
        [Cogs.Id] = 20_000m,
        [Rent.Id] = 5_000m,
        [Depreciation.Id] = 1_000m,
    };

    private static StatementAccount A(string code, AccountType type, CashFlowCategory category, bool operatingRevenue = false, bool costOfSales = false) =>
        new(SequentialGuid.New(), code, code, code, type, category, operatingRevenue, costOfSales);

    [Fact]
    public void Scenario_journals_are_balanced() => Year.Values.Sum().ShouldBe(0m);

    [Fact]
    public void Income_statement_computes_gross_and_net_profit()
    {
        var (sections, netRevenue, gross, net) = FinancialStatementBuilder.IncomeStatement(Chart, Year, null, null);
        netRevenue.Amount.ShouldBe(50_000m);
        sections.Single(s => s.Key == "cost-of-sales").Total.ShouldBe(20_000m);
        gross.Amount.ShouldBe(30_000m);
        sections.Single(s => s.Key == "other-income").Total.ShouldBe(200m);
        sections.Single(s => s.Key == "operating-expenses").Total.ShouldBe(6_000m);
        net.Amount.ShouldBe(24_200m);
        net.Ytd.ShouldBeNull();
    }

    [Fact]
    public void Income_statement_fills_ytd_and_comparative_columns()
    {
        var comparative = new Dictionary<Guid, decimal> { [Revenue.Id] = -10_000m, [Rent.Id] = 4_000m };
        var (_, _, gross, net) = FinancialStatementBuilder.IncomeStatement(Chart, Year, Year, comparative);
        net.Ytd.ShouldBe(24_200m);
        gross.Comparative.ShouldBe(10_000m);
        net.Comparative.ShouldBe(6_000m);
    }

    [Fact]
    public void Balance_sheet_balances_with_unclosed_earnings_in_equity()
    {
        var (sections, assets, liabilities, equity, total) = FinancialStatementBuilder.BalanceSheet(Chart, Year, null);
        assets.Amount.ShouldBe(123_200m + 10_000m + 10_000m + 12_000m - 1_000m);
        liabilities.Amount.ShouldBe(30_000m);
        equity.Amount.ShouldBe(124_200m);
        total.Amount.ShouldBe(assets.Amount);
        sections.Single(s => s.Key == "equity").Lines.ShouldContain(l => l.AccountId == null && l.Amount == 24_200m);
    }

    [Fact]
    public void After_year_end_close_retained_earnings_replace_the_earnings_line()
    {
        // Closing entry: revenue/expense balances to zero, net 24,200 credited to retained earnings.
        var retained = A("3200", AccountType.Equity, CashFlowCategory.Financing);
        var closed = new Dictionary<Guid, decimal>(Year)
        {
            [Revenue.Id] = 0m, [FxGain.Id] = 0m, [Cogs.Id] = 0m, [Rent.Id] = 0m, [Depreciation.Id] = 0m, [retained.Id] = -24_200m,
        };
        var (sections, assets, _, equity, total) = FinancialStatementBuilder.BalanceSheet([.. Chart, retained], closed, null);
        total.Amount.ShouldBe(assets.Amount);
        equity.Amount.ShouldBe(124_200m);
        sections.Single(s => s.Key == "equity").Lines.ShouldNotContain(l => l.AccountId == null);
    }

    [Fact]
    public void Cash_flow_ties_net_profit_to_the_change_in_cash()
    {
        var (sections, operating, investing, financing, cashChange) = FinancialStatementBuilder.CashFlow(Chart, 24_200m, Year);
        sections.Single(s => s.Key == "operating").Lines.Single(l => l.Code == "1590").Amount.ShouldBe(1_000m); // depreciation add-back
        operating.ShouldBe(24_200m - 10_000m - 10_000m + 30_000m + 1_000m);
        investing.ShouldBe(-12_000m);
        financing.ShouldBe(100_000m);
        (operating + investing + financing).ShouldBe(cashChange);
        cashChange.ShouldBe(123_200m);
    }

    [Fact]
    public void Unclassified_balance_sheet_accounts_fall_into_operating_so_the_statement_still_ties()
    {
        var suspense = A("1999", AccountType.Asset, CashFlowCategory.None);
        var changes = new Dictionary<Guid, decimal> { [Cash.Id] = -500m, [suspense.Id] = 500m };
        var (_, operating, _, _, cashChange) = FinancialStatementBuilder.CashFlow([Cash, suspense], 0m, changes);
        operating.ShouldBe(-500m);
        operating.ShouldBe(cashChange);
    }

    [Fact]
    public void Csv_escapes_commas_quotes_and_line_breaks()
    {
        AccountingCsv.Escape("plain").ShouldBe("plain");
        AccountingCsv.Escape("a,b").ShouldBe("\"a,b\"");
        AccountingCsv.Escape("say \"hi\"").ShouldBe("\"say \"\"hi\"\"\"");
        AccountingCsv.Escape("x\ny").ShouldBe("\"x\ny\"");
        AccountingCsv.ToBytes("x").Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
    }
}

public class YearEndAndOpeningDomainTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Le = Guid.NewGuid();

    private static FiscalYear YearReadyToClose()
    {
        var year = FiscalYear.Create(Company, Le, new DateOnly(2026, 1, 1));
        foreach (var p in year.Periods.Take(11))
        {
            year.SetPeriodStatus(p.Id, PeriodStatus.Closed).IsSuccess.ShouldBeTrue();
        }

        year.SetPeriodStatus(year.LastPeriod.Id, PeriodStatus.SoftClosed).IsSuccess.ShouldBeTrue();
        return year;
    }

    [Fact]
    public void Year_end_close_requires_earlier_periods_closed_and_the_last_soft_closed()
    {
        var year = FiscalYear.Create(Company, Le, new DateOnly(2026, 1, 1));
        year.CanClose().Error.ShouldBe(AccountingErrors.YearEndPeriodsNotReady);
        YearReadyToClose().CanClose().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Closing_locks_the_year_and_reopening_restores_a_soft_closed_last_period()
    {
        var year = YearReadyToClose();
        var journal = Guid.NewGuid();
        year.MarkClosed(journal, DateTime.UtcNow);
        year.IsClosed.ShouldBeTrue();
        year.ClosingJournalId.ShouldBe(journal);
        year.Periods.ShouldAllBe(p => p.Status == PeriodStatus.Closed);
        year.SetPeriodStatus(year.LastPeriod.Id, PeriodStatus.Open).Error.ShouldBe(AccountingErrors.FiscalYearClosed);

        year.Reopen();
        year.IsClosed.ShouldBeFalse();
        year.LastPeriod.Status.ShouldBe(PeriodStatus.SoftClosed);
        Should.Throw<DomainException>(year.Reopen).Error.ShouldBe(AccountingErrors.FiscalYearNotClosed);
    }

    [Fact]
    public void Opening_balances_refuse_control_accounts_and_book_the_difference_to_equity()
    {
        var bank = Account.Create(Company, "1120", "Bank", "بنك", AccountType.Asset, null, true);
        var receivables = Account.Create(Company, "1210", "AR", "عملاء", AccountType.Asset, null, true, ControlSubledger.Receivables);
        var accrued = Account.Create(Company, "2410", "Accrued", "مستحق", AccountType.Liability, null, true);
        var doc = OpeningBalance.Create(Company, Le, new DateOnly(2026, 1, 1), null);

        Should.Throw<DomainException>(() => doc.AddLine(receivables, 100m, 0m, null)).Error.Code.ShouldBe("accounting.opening-control-account");
        doc.AddLine(bank, 5_000m, 0m, null);
        doc.AddLine(accrued, 0m, 1_000m, null);
        doc.EquityDifference.ShouldBe(4_000m);

        doc.MarkPosted(Guid.NewGuid());
        Should.Throw<DomainException>(() => doc.AddLine(bank, 1m, 0m, null)).Error.ShouldBe(AccountingErrors.NotDraft);
        doc.MarkReversed();
        doc.Status.ShouldBe(OpeningBalanceStatus.Reversed);
    }

    [Fact]
    public void Cash_flow_categories_apply_to_balance_sheet_accounts_only()
    {
        var revenue = Account.Create(Company, "4110", "Sales", "مبيعات", AccountType.Revenue, null, true);
        Should.Throw<DomainException>(() => revenue.SetCashFlowCategory(CashFlowCategory.Operating));
        var bank = Account.Create(Company, "1120", "Bank", "بنك", AccountType.Asset, null, true);
        bank.SetCashFlowCategory(CashFlowCategory.Cash);
        bank.CashFlowCategory.ShouldBe(CashFlowCategory.Cash);
    }

    [Fact]
    public void Generic_template_classifies_cash_investing_and_financing_accounts()
    {
        var byCode = GenericChartTemplate.Accounts.ToDictionary(a => a.Code);
        GenericChartTemplate.CategoryFor(byCode["1120"]).ShouldBe(CashFlowCategory.Cash);
        GenericChartTemplate.CategoryFor(byCode["1510"]).ShouldBe(CashFlowCategory.Investing);
        GenericChartTemplate.CategoryFor(byCode["3100"]).ShouldBe(CashFlowCategory.Financing);
        GenericChartTemplate.CategoryFor(byCode["1210"]).ShouldBe(CashFlowCategory.Operating);
        GenericChartTemplate.CategoryFor(byCode["4110"]).ShouldBe(CashFlowCategory.None);
        GenericChartTemplate.CategoryFor(byCode["1"]).ShouldBe(CashFlowCategory.None);
    }
}
