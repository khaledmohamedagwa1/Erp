using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Pos.Application;
using Erp.Modules.Pos.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class PosMathTests
{
    private static Settlement Sale(decimal gross, decimal increment, params (PaymentMethod Method, decimal Amount)[] payments) =>
        PosMath.SettleSale(gross, payments.Select(p => new PaymentInput(p.Method, p.Amount)).ToList(), increment, 2);

    private static string Error(Action action) => Should.Throw<DomainException>(action).Error.Code;

    [Fact]
    public void Cash_over_tender_gives_change_and_keeps_only_the_amount_due()
    {
        var s = Sale(114m, 0m, (PaymentMethod.Cash, 200m));
        s.AmountDue.ShouldBe(114m);
        s.Tendered.ShouldBe(200m);
        s.Change.ShouldBe(86m);
        s.CashNet.ShouldBe(114m);
    }

    [Fact]
    public void Split_payment_change_comes_only_from_cash()
    {
        var s = Sale(114m, 0m, (PaymentMethod.Card, 100m), (PaymentMethod.Cash, 20m));
        s.Change.ShouldBe(6m);
        s.CashNet.ShouldBe(14m);
        s.Tendered.ShouldBe(120m);
    }

    [Fact]
    public void Non_cash_cannot_exceed_the_amount_due_and_payments_must_cover_it()
    {
        Error(() => Sale(114m, 0m, (PaymentMethod.Card, 120m))).ShouldBe("pos.overpaid-non-cash");
        Error(() => Sale(114m, 0m, (PaymentMethod.Card, 100m), (PaymentMethod.Wallet, 10m))).ShouldBe("pos.underpaid");
        Error(() => Sale(114m, 0m)).ShouldBe("pos.payments-required");
        Error(() => Sale(114m, 0m, (PaymentMethod.Cash, 0m))).ShouldBe("pos.invalid-amount");
        Error(() => Sale(114m, 0m, ((PaymentMethod)9, 200m))).ShouldBe("pos.invalid-payment-method");
    }

    [Theory]
    [InlineData(114.02, 0.05, 114.00)]
    [InlineData(114.03, 0.05, 114.05)]
    [InlineData(114.025, 0.05, 114.05)]
    [InlineData(114.49, 0.50, 114.50)]
    [InlineData(114.24, 0.50, 114.00)]
    [InlineData(114.37, 0, 114.37)]
    public void Cash_rounding_rounds_half_away_from_zero_to_the_increment(double amount, double increment, double expected) =>
        PosMath.CashRound((decimal)amount, (decimal)increment).ShouldBe((decimal)expected);

    [Fact]
    public void Cash_rounding_applies_to_all_cash_sales_only()
    {
        var cash = Sale(114.02m, 0.05m, (PaymentMethod.Cash, 120m));
        cash.AmountDue.ShouldBe(114.00m);
        cash.RoundingAdjustment.ShouldBe(-0.02m);
        cash.Change.ShouldBe(6m);

        var card = Sale(114.02m, 0.05m, (PaymentMethod.Card, 114.02m));
        card.AmountDue.ShouldBe(114.02m);
        card.RoundingAdjustment.ShouldBe(0m);
    }

    [Fact]
    public void Refunds_default_to_cash_and_must_match_exactly()
    {
        var (settlement, payments) = PosMath.SettleRefund(57.03m, null, 0.05m, 2);
        settlement.AmountDue.ShouldBe(57.05m);
        payments.Single().ShouldBe(new PaymentInput(PaymentMethod.Cash, 57.05m));

        Should.Throw<DomainException>(() => PosMath.SettleRefund(57m, [new PaymentInput(PaymentMethod.Card, 60m)], 0m, 2)).Error.Code.ShouldBe("pos.refund-payment-mismatch");
        PosMath.SettleRefund(57m, [new PaymentInput(PaymentMethod.Card, 57m)], 0.05m, 2).Settlement.CashNet.ShouldBe(0m);
    }
}

public class PosRefundTests
{
    private static PosReceiptLine Line(decimal quantity, decimal net, decimal tax)
    {
        var line = new PosReceiptLine(Guid.NewGuid(), null, "PLATE", "Plate", Guid.NewGuid(), quantity, quantity, net / quantity, 0, true, false, "GOODS", null);
        line.SetAmounts(net, 0, net, [new PosLineTax(Guid.NewGuid(), "VAT14", 14, net, tax)]);
        return line;
    }

    [Fact]
    public void Partial_refunds_prorate_and_the_last_one_takes_the_remainder()
    {
        var line = Line(3, 100m, 14m);
        var first = line.Refund(1, 2);
        first.NetAmount.ShouldBe(33.33m);
        first.TaxAmount.ShouldBe(4.67m);
        first.OriginalLineUid.ShouldBe(line.Uid);
        var second = line.Refund(1, 2);
        second.NetAmount.ShouldBe(33.33m);
        var last = line.Refund(1, 2);
        last.NetAmount.ShouldBe(33.34m);
        last.TaxAmount.ShouldBe(4.66m);
        (first.GrossAmount + second.GrossAmount + last.GrossAmount).ShouldBe(114m);
        line.RefundableQuantity.ShouldBe(0m);
    }

    [Fact]
    public void Refund_quantity_cannot_exceed_what_remains()
    {
        var line = Line(2, 200m, 28m);
        line.Refund(1.5m, 2);
        Should.Throw<DomainException>(() => line.Refund(1m, 2)).Error.Code.ShouldBe("pos.refund-exceeds-sold");
        Should.Throw<DomainException>(() => line.Refund(0m, 2)).Error.Code.ShouldBe("pos.invalid-quantity");
        line.RefundedQuantity.ShouldBe(1.5m);
    }

    [Fact]
    public void Receipt_status_follows_refunds()
    {
        var receipt = new PosReceipt(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReceiptKind.Sale,
            Guid.NewGuid(), "EGP", Guid.NewGuid(), "v1", false, new DateOnly(2026, 6, 1), null, DateTime.UtcNow, null);
        receipt.SetLines([Line(2, 200m, 28m), Line(1, 50m, 7m)]);
        receipt.Lines[0].Refund(1, 2);
        receipt.UpdateRefundStatus();
        receipt.Status.ShouldBe(ReceiptStatus.PartiallyRefunded);
        receipt.Lines[0].Refund(1, 2);
        receipt.Lines[1].Refund(1, 2);
        receipt.UpdateRefundStatus();
        receipt.Status.ShouldBe(ReceiptStatus.Refunded);
    }
}

public class CashierShiftTests
{
    private static CashierShift Open(decimal openingFloat = 500m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), openingFloat, new DateOnly(2026, 6, 1), "EGP", DateTime.UtcNow);

    [Fact]
    public void Shift_goes_open_closing_closed_and_never_back()
    {
        var shift = Open();
        shift.Status.ShouldBe(ShiftStatus.Open);
        shift.StartClosing(DateTime.UtcNow);
        shift.Status.ShouldBe(ShiftStatus.Closing);
        Should.Throw<DomainException>(() => shift.StartClosing(DateTime.UtcNow)).Error.Code.ShouldBe("pos.invalid-state-transition");
        shift.Close(new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = 500m }, new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = 500m }, null, DateTime.UtcNow);
        shift.Status.ShouldBe(ShiftStatus.Closed);
        Should.Throw<DomainException>(() => shift.Close(new Dictionary<PaymentMethod, decimal>(), new Dictionary<PaymentMethod, decimal>(), null, DateTime.UtcNow))
            .Error.Code.ShouldBe("pos.invalid-state-transition");
        Should.Throw<DomainException>(() => shift.AddCashMovement(CashMovementKind.PayIn, 10m, "late", null, DateTime.UtcNow)).Error.Code.ShouldBe("pos.invalid-state-transition");
    }

    [Fact]
    public void Open_shift_can_close_directly_and_negative_values_are_refused()
    {
        Should.Throw<DomainException>(() => Open(-1m)).Error.Code.ShouldBe("pos.invalid-float");
        var shift = Open();
        Should.Throw<DomainException>(() => shift.Close(new Dictionary<PaymentMethod, decimal>(), new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = -5m }, null, DateTime.UtcNow))
            .Error.Code.ShouldBe("pos.invalid-count");
        shift.Close(new Dictionary<PaymentMethod, decimal>(), new Dictionary<PaymentMethod, decimal>(), null, DateTime.UtcNow);
        shift.Status.ShouldBe(ShiftStatus.Closed);
    }

    [Fact]
    public void Expected_cash_includes_float_sales_refunds_and_movements_and_the_variance_is_counted_minus_expected()
    {
        var shift = Open(500m);
        shift.AddCashMovement(CashMovementKind.PayIn, 100m, "Float top-up", null, DateTime.UtcNow);
        shift.AddCashMovement(CashMovementKind.PayOut, 300m, "Safe drop", null, DateTime.UtcNow);
        var sales = new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = 1000m, [PaymentMethod.Card] = 400m };
        var refunds = new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = 50m };
        var expected = ShiftTotals.Expected(shift, sales, refunds);
        expected[PaymentMethod.Cash].ShouldBe(1250m);
        expected[PaymentMethod.Card].ShouldBe(400m);

        var variance = shift.Close(expected, new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = 1230m, [PaymentMethod.Card] = 400m }, null, DateTime.UtcNow);
        variance.ShouldBe(-20m);
        shift.CashVariance.ShouldBe(-20m);
        shift.Counts.Single(c => c.Method == PaymentMethod.Card).Variance.ShouldBe(0m);
        shift.Counts.Single(c => c.Method == PaymentMethod.Cash).Expected.ShouldBe(1250m);
    }

    [Fact]
    public void Cash_movements_need_a_positive_amount_and_a_reason()
    {
        var shift = Open();
        Should.Throw<DomainException>(() => shift.AddCashMovement(CashMovementKind.PayOut, 0m, "x", null, DateTime.UtcNow)).Error.Code.ShouldBe("pos.invalid-amount");
        Should.Throw<DomainException>(() => shift.AddCashMovement(CashMovementKind.PayOut, 5m, " ", null, DateTime.UtcNow));
    }
}

public class PosJournalTests
{
    private static readonly Dictionary<PaymentMethod, PaymentTarget> Targets = new()
    {
        [PaymentMethod.Cash] = new(null, Guid.Parse("11111111-1111-1111-1111-111111111111")),
        [PaymentMethod.Card] = new(PostingRoles.BankAccount, null),
        [PaymentMethod.Wallet] = new(PostingRoles.BankAccount, null),
        [PaymentMethod.Other] = new(PostingRoles.BankAccount, null),
    };

    [Fact]
    public void Sale_journal_balances_with_cash_rounding_and_split_payments()
    {
        var receipt = new PosReceipt(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReceiptKind.Sale,
            Guid.NewGuid(), "EGP", Guid.NewGuid(), "v1", false, new DateOnly(2026, 6, 1), null, DateTime.UtcNow, null);
        var line = new PosReceiptLine(Guid.NewGuid(), null, "PLATE", "Plate", Guid.NewGuid(), 1, 1, 100.02m, 0, true, false, "GOODS", null);
        line.SetAmounts(100.02m, 0, 100.02m, [new PosLineTax(Guid.NewGuid(), "VAT14", 14, 100.02m, 14.00m)]);
        receipt.SetLines([line]);
        receipt.Settle(PosMath.SettleSale(receipt.GrossTotal, [new PaymentInput(PaymentMethod.Cash, 120m)], 0.05m, 2), [new PosPayment(PaymentMethod.Cash, 120m, null)]);
        receipt.AmountDue.ShouldBe(114.00m);

        var lines = PosJournal.ForReceipt(receipt, Targets).Lines;
        lines.Sum(l => l.Debit).ShouldBe(lines.Sum(l => l.Credit));
        lines.Single(l => l.AccountId is not null).Debit.ShouldBe(114.00m);
        lines.Single(l => l.Role == PostingRoles.SalesDiscount).Debit.ShouldBe(0.02m);
        lines.Single(l => l.Role == PostingRoles.SalesRevenue).Credit.ShouldBe(100.02m);
        lines.Single(l => l.Role == PostingRoles.OutputTax).Credit.ShouldBe(14.00m);
        lines.ShouldNotContain(l => l.Role == PostingRoles.CustomerReceivable);
    }
}
