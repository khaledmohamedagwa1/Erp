using Erp.Modules.Tax.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class TaxEngineTests
{
    private static TaxComponentInput Vat(decimal rate = 14m, bool compound = false) => new(Guid.NewGuid(), "VAT", TaxKind.Output, rate, compound, false);

    private static TaxComponentInput Table(decimal rate) => new(Guid.NewGuid(), "TABLE", TaxKind.Output, rate, false, false);

    private static TaxComponentInput Wht(decimal rate) => new(Guid.NewGuid(), "WHT", TaxKind.Withholding, rate, false, false);

    [Fact]
    public void Exclusive_amount_adds_tax_on_top()
    {
        var r = TaxEngine.Compute(1000m, false, [Vat()], 2);
        r.Net.ShouldBe(1000m);
        r.TotalTax.ShouldBe(140m);
        r.Gross.ShouldBe(1140m);
        r.Lines.Single().Base.ShouldBe(1000m);
    }

    [Fact]
    public void Each_line_is_rounded_half_away_from_zero()
    {
        TaxEngine.Compute(33.33m, false, [Vat()], 2).TotalTax.ShouldBe(4.67m);
        TaxEngine.Compute(0.25m, false, [Vat(10m)], 2).TotalTax.ShouldBe(0.03m);
        TaxEngine.Compute(-0.25m, false, [Vat(10m)], 2).TotalTax.ShouldBe(-0.03m);
    }

    [Fact]
    public void Inclusive_amount_is_back_calculated()
    {
        var r = TaxEngine.Compute(1140m, true, [Vat()], 2);
        r.Net.ShouldBe(1000m);
        r.TotalTax.ShouldBe(140m);

        var odd = TaxEngine.Compute(100m, true, [Vat()], 2);
        odd.Net.ShouldBe(87.72m);
        odd.TotalTax.ShouldBe(12.28m);
        odd.Gross.ShouldBe(100m);
    }

    [Fact]
    public void Inclusive_back_calculation_always_reconciles_exactly()
    {
        TaxComponentInput[] compound = [Table(8m), Vat(14m, compound: true)];
        for (var cents = 1; cents <= 20000; cents += 7)
        {
            var amount = cents / 100m;
            foreach (var components in new[] { new[] { Vat() }, compound, new[] { Vat(5m), Vat(14m) } })
            {
                var r = TaxEngine.Compute(amount, true, components, 2);
                (r.Net + r.TotalTax).ShouldBe(r.Gross, $"amount {amount}");
                r.Gross.ShouldBe(amount);
                r.Lines.Sum(l => l.Amount).ShouldBe(r.TotalTax);
            }
        }
    }

    [Fact]
    public void Compound_component_is_charged_on_net_plus_previous_taxes()
    {
        var r = TaxEngine.Compute(1000m, false, [Table(10m), Vat(14m, compound: true)], 2);
        r.Lines[0].Amount.ShouldBe(100m);
        r.Lines[1].Base.ShouldBe(1100m);
        r.Lines[1].Amount.ShouldBe(154m);
        r.Gross.ShouldBe(1254m);
    }

    [Fact]
    public void Independent_components_each_use_the_net()
    {
        var r = TaxEngine.Compute(200m, false, [Vat(5m), Vat(14m)], 2);
        r.Lines.Select(l => l.Amount).ShouldBe([10m, 28m]);
        r.Gross.ShouldBe(238m);
    }

    [Fact]
    public void Withholding_is_reported_but_not_added_to_gross()
    {
        var r = TaxEngine.Compute(1000m, false, [Vat(), Wht(1m)], 2);
        r.TotalTax.ShouldBe(140m);
        r.Gross.ShouldBe(1140m);
        r.TotalWithholding.ShouldBe(10m);
        r.Lines.Single(l => l.Kind == TaxKind.Withholding).Base.ShouldBe(1000m);

        var inclusive = TaxEngine.Compute(1140m, true, [Vat(), Wht(1m)], 2);
        inclusive.Net.ShouldBe(1000m);
        inclusive.TotalWithholding.ShouldBe(10m);
    }

    [Theory]
    [InlineData(0, 999, 14, 140)]
    [InlineData(0, 1000, 14.5, 145)]
    [InlineData(2, 999, 14, 139.86)]
    [InlineData(3, 1.2345, 14, 0.173)]
    public void Rounding_follows_the_currency_minor_units(int minorUnits, decimal amount, decimal rate, decimal expectedTax) =>
        TaxEngine.Compute(amount, false, [Vat(rate)], minorUnits).TotalTax.ShouldBe(expectedTax);

    [Fact]
    public void Amount_itself_is_rounded_to_minor_units() =>
        TaxEngine.Compute(1.2345m, false, [Vat()], 3).Net.ShouldBe(1.235m);
}

public class TaxDomainTests
{
    private static readonly DateOnly Vat14Start = new(2017, 7, 1);

    [Fact]
    public void Rates_are_selected_by_validity_and_a_new_rate_closes_the_open_one()
    {
        var code = TaxCode.Create(Guid.NewGuid(), "vat14-out", "VAT", "ضريبة", TaxKind.Output, false, null, "EG", 14m, Vat14Start);
        code.Code.ShouldBe("VAT14-OUT");
        code.AddRate(15m, new DateOnly(2027, 1, 1), null);

        code.RateOn(new DateOnly(2017, 6, 30)).ShouldBeNull();
        code.RateOn(Vat14Start).ShouldBe(14m);
        code.RateOn(new DateOnly(2026, 12, 31)).ShouldBe(14m);
        code.RateOn(new DateOnly(2027, 1, 1)).ShouldBe(15m);
        code.Rates.Single(r => r.Rate == 14m).ValidTo.ShouldBe(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Overlapping_rate_periods_are_rejected()
    {
        var code = TaxCode.Create(Guid.NewGuid(), "VAT", "VAT", "ضريبة", TaxKind.Output, false, null, null, 14m, Vat14Start);
        code.AddRate(15m, new DateOnly(2027, 1, 1), null);
        Should.Throw<DomainException>(() => code.AddRate(10m, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            .Error.Code.ShouldBe("tax.rate-period-overlap");
        Should.Throw<DomainException>(() => code.AddRate(10m, Vat14Start, null));
    }

    [Fact]
    public void Output_tax_is_never_recoverable() =>
        TaxCode.Create(Guid.NewGuid(), "OUT", "Out", "خارج", TaxKind.Output, true, null, null, 14m, Vat14Start).Recoverable.ShouldBeFalse();

    [Fact]
    public void Tax_groups_validate_their_components()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Should.Throw<DomainException>(() => TaxGroup.Create(Guid.NewGuid(), "G", "G", "ج", [])).Error.Code.ShouldBe("tax.group-empty");
        Should.Throw<DomainException>(() => TaxGroup.Create(Guid.NewGuid(), "G", "G", "ج", [(a, 10, true)])).Error.Code.ShouldBe("tax.group-compound-first");
        Should.Throw<DomainException>(() => TaxGroup.Create(Guid.NewGuid(), "G", "G", "ج", [(a, 10, false), (a, 20, false)])).Error.Code.ShouldBe("tax.group-duplicate");
        TaxGroup.Create(Guid.NewGuid(), "G", "G", "ج", [(b, 20, true), (a, 10, false)]).Components.Select(c => c.TaxCodeId).ShouldBe([a, b]);
    }

    [Fact]
    public void Currencies_and_exchange_rates_are_validated()
    {
        Should.Throw<DomainException>(() => Currency.Create("EGP", "Pound", "جنيه", 5, null));
        Should.Throw<DomainException>(() => Currency.Create("EG", "Pound", "جنيه", 2, null));
        Should.Throw<DomainException>(() => Currency.Create("EGP", "Pound", "جنيه", 2, 0.001m));
        Currency.Create("kwd", "Dinar", "دينار", 3, null).CashRoundingIncrement.ShouldBe(0.001m);
        Should.Throw<DomainException>(() => ExchangeRate.Create("USD", "usd", RateType.Spot, Vat14Start, 1m));
        Should.Throw<DomainException>(() => ExchangeRate.Create("USD", "EGP", RateType.Spot, Vat14Start, 0m));
        ExchangeRate.Create("USD", "EGP", RateType.Spot, Vat14Start, 48.123456789m).Rate.ShouldBe(48.12345679m);
    }
}
