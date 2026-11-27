using Erp.Modules.Accounting.Application;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class AccountingDomainTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Le = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 27);

    private static JournalEntry Draft(string currency = "EGP", decimal rate = 1m) =>
        JournalEntry.CreateDraft(Company, Le, Day, "sales", "SalesInvoice", Guid.NewGuid(), "invoice", "test", currency, rate);

    [Fact]
    public void Lines_must_be_one_sided_and_positive()
    {
        var j = Draft();
        Should.Throw<DomainException>(() => j.AddLine(Guid.NewGuid(), 10, 10)).Error.ShouldBe(AccountingErrors.LineOneSided);
        Should.Throw<DomainException>(() => j.AddLine(Guid.NewGuid(), 0, 0));
        Should.Throw<DomainException>(() => j.AddLine(Guid.NewGuid(), -5, 0));
    }

    [Fact]
    public void Journal_posts_only_when_balanced_and_becomes_immutable()
    {
        var j = Draft();
        j.AddLine(Guid.NewGuid(), 1140, 0);
        j.AddLine(Guid.NewGuid(), 0, 1000);
        j.CheckBalance().Error.ShouldBe(AccountingErrors.Unbalanced);
        j.AddLine(Guid.NewGuid(), 0, 140);
        j.CheckBalance().Value.ShouldBeFalse();

        j.Post("JV-2026-000001", Guid.NewGuid(), null, DateTime.UtcNow);
        j.Status.ShouldBe(JournalStatus.Posted);
        j.DomainEvents.OfType<JournalPosted>().ShouldHaveSingleItem();
        Should.Throw<DomainException>(() => j.AddLine(Guid.NewGuid(), 1, 0)).Error.ShouldBe(AccountingErrors.NotDraft);
    }

    [Fact]
    public void A_single_line_journal_is_rejected() =>
        Draft().Also(j => j.AddLine(Guid.NewGuid(), 5, 0)).CheckBalance().Error.ShouldBe(AccountingErrors.TooFewLines);

    [Fact]
    public void Foreign_currency_rounding_residual_of_one_cent_gets_a_rounding_line()
    {
        // Three debits of 0.01 at 1.5 round to 0.02 each (0.06); one credit of 0.03 rounds to 0.05 → 0.01 residual.
        var j = Draft("USD", 1.5m);
        j.AddLine(Guid.NewGuid(), 0.01m, 0);
        j.AddLine(Guid.NewGuid(), 0.01m, 0);
        j.AddLine(Guid.NewGuid(), 0.01m, 0);
        j.AddLine(Guid.NewGuid(), 0, 0.03m);
        j.TotalBaseDebit.ShouldBe(0.06m);
        j.TotalBaseCredit.ShouldBe(0.05m);
        j.CheckBalance().Value.ShouldBeTrue();

        var rounding = Guid.NewGuid();
        j.AddRoundingLine(rounding);
        j.TotalBaseDebit.ShouldBe(j.TotalBaseCredit);
        j.Lines.Last().AccountId.ShouldBe(rounding);
        j.Lines.Last().BaseCredit.ShouldBe(0.01m);
        j.Post("JV-1", Guid.NewGuid(), null, DateTime.UtcNow);
    }

    [Fact]
    public void Base_conversion_uses_half_away_from_zero_per_line() =>
        JournalEntry.ToBase(10.005m, 1m).ShouldBe(10.01m);

    [Fact]
    public void Reversal_swaps_every_line_and_keeps_the_source()
    {
        var j = Draft();
        var ar = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        j.AddLine(ar, 500, 0);
        j.AddLine(revenue, 0, 500);
        j.Post("JV-1", Guid.NewGuid(), null, DateTime.UtcNow);

        var r = j.CreateReversal(Day.AddDays(1));
        r.Purpose.ShouldBe("invoice:reversal");
        r.SourceDocId.ShouldBe(j.SourceDocId);
        r.ReversalOfId.ShouldBe(j.Id);
        r.Lines.Single(l => l.AccountId == ar).Credit.ShouldBe(500);
        r.Lines.Single(l => l.AccountId == revenue).Debit.ShouldBe(500);

        j.MarkReversed(r.Id);
        j.Status.ShouldBe(JournalStatus.Reversed);
        Should.Throw<DomainException>(() => j.CreateReversal(Day)).Error.ShouldBe(AccountingErrors.AlreadyReversed);
    }

    [Fact]
    public void Drafts_cannot_be_reversed() =>
        Should.Throw<DomainException>(() => Draft().CreateReversal(Day)).Error.ShouldBe(AccountingErrors.NotPosted);

    [Fact]
    public void Fiscal_year_has_contiguous_monthly_periods()
    {
        var y = FiscalYear.Create(Company, Le, new DateOnly(2026, 7, 1));
        y.Code.ShouldBe("FY2026-27");
        y.Periods.Count.ShouldBe(12);
        y.EndDate.ShouldBe(new DateOnly(2027, 6, 30));
        y.Periods.Zip(y.Periods.Skip(1)).ShouldAllBe(p => p.First.EndDate.AddDays(1) == p.Second.StartDate);
        y.FindPeriod(new DateOnly(2027, 2, 28))!.PeriodNo.ShouldBe(8);
        Should.Throw<DomainException>(() => FiscalYear.Create(Company, Le, new DateOnly(2026, 1, 15)));
    }

    [Fact]
    public void Periods_close_in_order_and_reopen_in_reverse_order()
    {
        var y = FiscalYear.Create(Company, Le, new DateOnly(2026, 1, 1));
        var jan = y.Periods[0].Id;
        var feb = y.Periods[1].Id;

        y.SetPeriodStatus(feb, PeriodStatus.Closed).Error.ShouldBe(AccountingErrors.PeriodOrder);
        y.SetPeriodStatus(feb, PeriodStatus.SoftClosed).IsSuccess.ShouldBeTrue();
        y.SetPeriodStatus(jan, PeriodStatus.Closed).IsSuccess.ShouldBeTrue();
        y.SetPeriodStatus(feb, PeriodStatus.Closed).IsSuccess.ShouldBeTrue();

        y.SetPeriodStatus(jan, PeriodStatus.Open).Error.ShouldBe(AccountingErrors.PeriodOrder);
        y.SetPeriodStatus(feb, PeriodStatus.Open).IsSuccess.ShouldBeTrue();
        y.SetPeriodStatus(jan, PeriodStatus.Open).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Most_specific_posting_rule_wins_then_priority()
    {
        var general = AccountDetermination.Create(Company, PostingRoles.Inventory, Guid.NewGuid());
        var trading = AccountDetermination.Create(Company, PostingRoles.Inventory, Guid.NewGuid(), productPostingGroup: "trading");
        var tradingHigh = AccountDetermination.Create(Company, PostingRoles.Inventory, Guid.NewGuid(), productPostingGroup: "TRADING", priority: 5);
        var tradingWarehouse = AccountDetermination.Create(Company, PostingRoles.Inventory, Guid.NewGuid(), productPostingGroup: "trading", warehouseId: Guid.Empty);
        var rules = new[] { general, trading, tradingHigh, tradingWarehouse };

        PostingRuleResolver.Resolve(rules, PostingRoles.Inventory, Le, null, null, null, null).ShouldBe(general);
        PostingRuleResolver.Resolve(rules, PostingRoles.Inventory, Le, "trading", null, null, null).ShouldBe(tradingHigh);
        PostingRuleResolver.Resolve(rules, PostingRoles.Inventory, Le, "Trading", null, null, Guid.Empty).ShouldBe(tradingWarehouse);
        PostingRuleResolver.Resolve(rules, PostingRoles.CostOfGoodsSold, Le, null, null, null, null).ShouldBeNull();

        var otherLe = AccountDetermination.Create(Company, PostingRoles.Inventory, Guid.NewGuid(), legalEntityId: Guid.NewGuid());
        PostingRuleResolver.Resolve([otherLe], PostingRoles.Inventory, Le, null, null, null, null).ShouldBeNull();
    }

    [Fact]
    public void Control_accounts_accept_only_their_subledger_and_never_manual_journals()
    {
        var receivables = Account.Create(Company, "1210", "AR", "عملاء", AccountType.Asset, null, true, ControlSubledger.Receivables);
        receivables.AllowManualPosting.ShouldBeFalse();
        receivables.CanPost("sales", manual: false).IsSuccess.ShouldBeTrue();
        receivables.CanPost("inventory", manual: false).Error.ShouldBe(AccountingErrors.ControlAccountRestricted);
        receivables.CanPost("accounting", manual: true).Error.ShouldBe(AccountingErrors.ControlAccountRestricted);

        var group = Account.Create(Company, "1", "Assets", "أصول", AccountType.Asset, null, false);
        group.CanPost("accounting", manual: true).Error.ShouldBe(AccountingErrors.NotPostingAccount);
        Should.Throw<DomainException>(() => Account.Create(Company, "2", "x", "x", AccountType.Liability, group, true));
        receivables.NormalBalance.ShouldBe(NormalBalance.Debit);
    }

    [Fact]
    public void Template_maps_every_posting_role_to_a_posting_account()
    {
        PostingRoles.All.Count.ShouldBe(45);
        foreach (var role in PostingRoles.All)
        {
            GenericChartTemplate.RoleAccounts.ShouldContainKey(role);
            var code = GenericChartTemplate.RoleAccounts[role];
            GenericChartTemplate.Accounts.Single(a => a.Code == code).Posting.ShouldBeTrue(role);
        }

        // Parents precede children so the template can be seeded in one pass.
        var seen = new HashSet<string>();
        foreach (var a in GenericChartTemplate.Accounts)
        {
            (a.Parent is null || seen.Contains(a.Parent)).ShouldBeTrue(a.Code);
            seen.Add(a.Code);
        }
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
