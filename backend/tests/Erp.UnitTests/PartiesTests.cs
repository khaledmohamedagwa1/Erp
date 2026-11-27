using Erp.Modules.Parties.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class PaymentTermsTests
{
    private static readonly Guid Company = Guid.NewGuid();

    [Fact]
    public void Installments_must_add_up_to_exactly_100_percent() =>
        Should.Throw<DomainException>(() => PaymentTerms.Create(Company, "N30", "Net 30", "صافي 30",
            [new PaymentInstallment(50, 30, DueDateBasis.InvoiceDate), new PaymentInstallment(49.99m, 60, DueDateBasis.InvoiceDate)]))
            .Error.Code.ShouldBe("parties.installments-not-100");

    [Fact]
    public void Terms_need_at_least_one_installment() =>
        Should.Throw<DomainException>(() => PaymentTerms.Create(Company, "N30", "Net 30", "صافي 30", []));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Installment_percent_must_be_between_0_and_100(decimal percent) =>
        Should.Throw<DomainException>(() => new PaymentInstallment(percent, 30, DueDateBasis.InvoiceDate));

    [Fact]
    public void Schedule_rounds_each_installment_and_the_last_takes_the_remainder()
    {
        var terms = PaymentTerms.Create(Company, "THIRDS", "Thirds", "أثلاث",
        [
            new PaymentInstallment(33.333333m, 30, DueDateBasis.InvoiceDate),
            new PaymentInstallment(33.333333m, 60, DueDateBasis.InvoiceDate),
            new PaymentInstallment(33.333334m, 90, DueDateBasis.InvoiceDate),
        ]);

        var schedule = terms.Schedule(new DateOnly(2026, 11, 27), 100m);

        schedule.Select(s => s.Amount).ShouldBe([33.33m, 33.33m, 33.34m]);
        schedule.Sum(s => s.Amount).ShouldBe(100m);
        schedule.Select(s => s.DueDate).ShouldBe([new DateOnly(2026, 12, 27), new DateOnly(2027, 1, 26), new DateOnly(2027, 2, 25)]);
    }

    [Fact]
    public void Rounding_remainder_never_loses_a_cent()
    {
        var terms = PaymentTerms.Create(Company, "HALF", "Half", "نصف",
            [new PaymentInstallment(50, 0, DueDateBasis.InvoiceDate), new PaymentInstallment(50, 30, DueDateBasis.InvoiceDate)]);
        var schedule = terms.Schedule(new DateOnly(2026, 1, 1), 100.01m);
        schedule.Select(s => s.Amount).ShouldBe([50.01m, 50.00m]);
        schedule.Sum(s => s.Amount).ShouldBe(100.01m);
    }

    [Fact]
    public void End_of_month_counts_from_the_last_day_of_the_invoice_month()
    {
        var terms = PaymentTerms.Create(Company, "EOM30", "EOM + 30", "نهاية الشهر + 30", [new PaymentInstallment(100, 30, DueDateBasis.EndOfMonth)]);
        terms.Schedule(new DateOnly(2026, 1, 15), 10m).Single().DueDate.ShouldBe(new DateOnly(2026, 3, 2));
        terms.Schedule(new DateOnly(2028, 2, 3), 10m).Single().DueDate.ShouldBe(new DateOnly(2028, 3, 30));
    }

    [Fact]
    public void Installments_due_on_the_same_day_are_merged()
    {
        var terms = PaymentTerms.Create(Company, "SPLIT", "Split", "مقسم",
            [new PaymentInstallment(40, 30, DueDateBasis.InvoiceDate), new PaymentInstallment(60, 30, DueDateBasis.InvoiceDate)]);
        terms.Schedule(new DateOnly(2026, 1, 1), 1000m).ShouldHaveSingleItem().Amount.ShouldBe(1000m);
    }

    [Fact]
    public void Without_terms_everything_is_due_on_the_invoice_date() =>
        PaymentTerms.BuildSchedule([], new DateOnly(2026, 5, 5), 99.99m, 2).ShouldHaveSingleItem().ShouldBe((new DateOnly(2026, 5, 5), 99.99m));
}

public class CustomerTests
{
    private static Customer NewCustomer() =>
        Customer.Create(Guid.NewGuid(), "c-001", CustomerKind.Business, "Delta Retail", "دلتا", null, "egp", null, null, null, null);

    [Fact]
    public void Codes_and_currency_are_normalised_and_defaults_applied()
    {
        var customer = NewCustomer();
        customer.Code.ShouldBe("C-001");
        customer.Currency.ShouldBe("EGP");
        customer.PostingGroup.ShouldBe(PartyRules.DefaultPostingGroup);
        customer.CreditLimit.ShouldBe(0m);
        customer.CreditAction.ShouldBe(CreditAction.RequireApproval);
        customer.DomainEvents.OfType<CustomerCreated>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Credit_limit_cannot_be_negative() =>
        Should.Throw<DomainException>(() => NewCustomer().SetCreditProfile(-1m, CreditAction.Block, false)).Error.Code.ShouldBe("parties.credit-limit-negative");

    [Fact]
    public void Exposure_above_the_limit_returns_the_configured_action_and_hold_always_blocks()
    {
        var customer = NewCustomer();
        customer.SetCreditProfile(500_000m, CreditAction.Warn, false);
        customer.EvaluateExposure(499_999.99m).ShouldBeNull();
        customer.EvaluateExposure(500_000.01m).ShouldBe(CreditAction.Warn);

        customer.SetCreditProfile(500_000m, CreditAction.Warn, true);
        customer.EvaluateExposure(1m).ShouldBe(CreditAction.Block);
        customer.DomainEvents.OfType<CustomerCreditProfileChanged>().Count().ShouldBe(2);
    }

    [Fact]
    public void Walk_in_customer_is_locked()
    {
        var walkIn = Customer.CreateWalkIn(Guid.NewGuid(), "EGP");
        walkIn.IsWalkIn.ShouldBeTrue();
        walkIn.Code.ShouldBe(Customer.WalkInCode);
        Should.Throw<DomainException>(() => walkIn.Deactivate());
        Should.Throw<DomainException>(() => walkIn.SetCreditProfile(100m, CreditAction.Warn, false));
        Should.Throw<DomainException>(() => walkIn.Update(CustomerKind.Business, "x", "y", null, "EGP", null, null, null, null));
    }

    [Fact]
    public void Only_one_default_address_per_type()
    {
        var customer = NewCustomer();
        var first = customer.AddAddress(new PartyAddress(AddressType.Billing, "1 Nile St", null, "Cairo", null, null, "eg"), makeDefault: false);
        first.IsDefault.ShouldBeTrue();
        first.Country.ShouldBe("EG");

        var shipping = customer.AddAddress(new PartyAddress(AddressType.Shipping, "Warehouse 4", null, "Giza", null, null, "EG"), makeDefault: false);
        shipping.IsDefault.ShouldBeTrue();

        var second = customer.AddAddress(new PartyAddress(AddressType.Billing, "2 Tahrir Sq", null, "Cairo", null, null, "EG"), makeDefault: true);
        second.IsDefault.ShouldBeTrue();
        first.IsDefault.ShouldBeFalse();
        customer.Addresses.Count(a => a.Type == AddressType.Billing && a.IsDefault).ShouldBe(1);
        shipping.IsDefault.ShouldBeTrue();

        customer.RemoveAddress(second.Uid).ShouldBeTrue();
        first.IsDefault.ShouldBeTrue();
        customer.DefaultAddress(AddressType.Billing).ShouldBe(first);
    }

    [Fact]
    public void Invalid_country_codes_are_rejected() =>
        Should.Throw<DomainException>(() => new PartyAddress(AddressType.Billing, "x", null, "Cairo", null, null, "EGY"));

    [Fact]
    public void Contacts_are_added_and_removed_by_id()
    {
        var supplier = Supplier.Create(Guid.NewGuid(), "SUP-1", "Sinai Olive Groves", "بساتين سيناء", null, "EGP", null, "IMPORT");
        var contact = supplier.AddContact("Hassan", "+20 100 000 0000", "h@sinai.example", "Sales");
        supplier.PostingGroup.ShouldBe("IMPORT");
        supplier.RemoveContact(Guid.NewGuid()).ShouldBeFalse();
        supplier.RemoveContact(contact.Uid).ShouldBeTrue();
        supplier.Contacts.ShouldBeEmpty();
    }
}
