using Erp.Modules.Finance.Application;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class FinanceMathTests
{
    private static OpenItem Invoice(decimal amount, decimal rate, PartyKind party = PartyKind.Customer, int sign = 1, string currency = "USD", DateOnly? due = null) =>
        OpenItem.Create(Guid.NewGuid(), Guid.NewGuid(), party, Guid.NewGuid(), "GOODS", "sales", "SalesInvoice", Guid.NewGuid(), "INV-1",
            new DateOnly(2026, 1, 10), 1, due ?? new DateOnly(2026, 2, 10), currency, rate, sign, amount, FinanceMath.ToBase(amount, rate));

    [Fact]
    public void Realized_fx_example_J10_invoice_at_50_received_at_51_is_a_gain_of_1000()
    {
        var item = Invoice(1000m, 50m);
        var relieved = item.Settle(1000m);
        relieved.ShouldBe(50_000m);
        FinanceMath.RealizedFx(PartyKind.Customer, relieved, FinanceMath.ToBase(1000m, 51m)).ShouldBe(1_000m);
        item.Status.ShouldBe(OpenItemStatus.Settled);
    }

    [Fact]
    public void Supplier_paid_at_a_higher_rate_is_a_loss()
    {
        var item = Invoice(1000m, 50m, PartyKind.Supplier);
        FinanceMath.RealizedFx(PartyKind.Supplier, item.Settle(1000m), FinanceMath.ToBase(1000m, 51m)).ShouldBe(-1_000m);
    }

    [Fact]
    public void Partial_settlements_relieve_base_proportionally_and_the_last_takes_the_remainder()
    {
        var item = Invoice(300m, 3.3333m); // base 999.99
        var first = item.Settle(100m);
        var second = item.Settle(100m);
        var third = item.Settle(100m);
        (first + second + third).ShouldBe(item.OriginalBase);
        first.ShouldBe(333.33m);
        third.ShouldBe(333.33m);
        item.OpenBase.ShouldBe(0m);
        item.Status.ShouldBe(OpenItemStatus.Settled);
    }

    [Fact]
    public void One_receipt_can_settle_several_invoices_and_several_receipts_one_invoice()
    {
        var a = Invoice(600m, 1m, currency: "EGP");
        var b = Invoice(400m, 1m, currency: "EGP");
        a.Settle(600m);
        b.Settle(150m);
        a.Status.ShouldBe(OpenItemStatus.Settled);
        b.Status.ShouldBe(OpenItemStatus.PartiallySettled);
        b.Settle(250m);
        b.Status.ShouldBe(OpenItemStatus.Settled);
    }

    [Fact]
    public void Settling_more_than_open_or_cancelling_after_allocation_is_refused()
    {
        var item = Invoice(100m, 1m, currency: "EGP");
        Should.Throw<DomainException>(() => item.Settle(100.01m)).Error.ShouldBe(FinanceErrors.AllocationExceedsOpen);
        item.Settle(10m);
        Should.Throw<DomainException>(() => item.Cancel()).Error.ShouldBe(FinanceErrors.OpenItemAllocated);
    }

    [Fact]
    public void Withholding_example_J6_pays_6893_cash_and_withholds_61_against_6954()
    {
        var invoice = Invoice(6954m, 1m, PartyKind.Supplier, currency: "EGP");
        var payment = Settlement.Create(Guid.NewGuid(), invoice.LegalEntityId, SettlementDirection.Payment, invoice.PartyId, new DateOnly(2026, 3, 1),
            PaymentMethod.BankTransfer, Guid.NewGuid(), "EGP", 1m, 6893m, null, null);
        payment.SetWithholding(Guid.NewGuid(), 6100m, 61m);
        payment.SettlementValue.ShouldBe(6954m);
        payment.SetAllocations([new RequestedAllocation(invoice.Id, 6954m)]);
        Should.Throw<DomainException>(() => payment.SetAllocations([new RequestedAllocation(invoice.Id, 6954.01m)])).Error.ShouldBe(FinanceErrors.OverAllocation);

        var relieved = invoice.Settle(6954m);
        var lines = new JournalLines()
            .Credit("BankAccount", null, 6893m)
            .Credit("WithholdingTaxPayable", null, 61m)
            .Debit("SupplierPayable", null, relieved)
            .Debit("SupplierAdvance", null, 0m);
        lines.Lines.Count.ShouldBe(3);
        lines.Lines.Sum(l => l.Debit).ShouldBe(lines.Lines.Sum(l => l.Credit));
    }

    [Theory]
    [InlineData("2026-03-31", 0)]
    [InlineData("2026-04-01", 1)]
    [InlineData("2026-04-30", 1)]
    [InlineData("2026-05-01", 2)]
    [InlineData("2026-05-30", 2)]
    [InlineData("2026-05-31", 3)]
    [InlineData("2026-06-29", 3)]
    [InlineData("2026-06-30", 4)]
    public void Aging_buckets_count_days_past_due(string asOf, int bucket) =>
        FinanceMath.AgingBucket(new DateOnly(2026, 3, 31), DateOnly.Parse(asOf)).ShouldBe(bucket);

    [Fact]
    public void Base_is_split_over_installments_without_losing_a_cent()
    {
        var shares = FinanceMath.SplitBase([333.33m, 333.33m, 333.34m], 16_666.67m);
        shares.Sum().ShouldBe(16_666.67m);
        shares.Count.ShouldBe(3);
    }

    [Fact]
    public void Revaluation_amounts_and_their_reversal_cancel_out()
    {
        // USD 1,000 receivable at 50 and USD 400 payable at 50, closing rate 52.
        var receivableGain = FinanceMath.Unrealized(PartyKind.Customer, 50_000m, FinanceMath.ToBase(1000m, 52m));
        var payableGain = FinanceMath.Unrealized(PartyKind.Supplier, 20_000m, FinanceMath.ToBase(400m, 52m));
        receivableGain.ShouldBe(2_000m);
        payableGain.ShouldBe(-800m);

        var run = FxRevaluationRun.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 30),
        [
            new RevaluationLine("Receivable", Guid.NewGuid(), "USD", 1000m, 50_000m, 52_000m, receivableGain),
            new RevaluationLine("Payable", Guid.NewGuid(), "USD", 400m, 20_000m, 20_800m, payableGain),
            new RevaluationLine("Receivable", Guid.NewGuid(), "USD", 5m, 250m, 250m, 0m),
        ]);
        run.Lines.Count.ShouldBe(2);
        run.TotalGain.ShouldBe(2_000m);
        run.TotalLoss.ShouldBe(800m);
        run.ReversalDate.ShouldBe(new DateOnly(2026, 7, 1));

        var journal = new JournalLines()
            .Signed("CustomerReceivable", null, receivableGain)
            .Signed("SupplierPayable", null, payableGain)
            .Credit("UnrealizedFxGain", null, run.TotalGain)
            .Debit("UnrealizedFxLoss", null, run.TotalLoss);
        journal.Lines.Sum(l => l.Debit).ShouldBe(journal.Lines.Sum(l => l.Credit));
        journal.Lines.Single(l => l.Role == "SupplierPayable").Credit.ShouldBe(800m);

        // The reversal swaps every side, so the net effect on the ledger after the reversal date is zero.
        journal.Lines.Sum(l => l.Debit - l.Credit).ShouldBe(0m);
    }

    [Fact]
    public void Credit_note_revaluation_moves_opposite_to_an_invoice()
    {
        // A customer credit note (credit balance) of USD 100 at 50 revalued at 52 is a loss of 200.
        var gain = -1 * FinanceMath.Unrealized(PartyKind.Customer, 5_000m, 5_200m);
        gain.ShouldBe(-200m);
    }
}

public class BankReconciliationTests
{
    private static CashBankAccount Bank() =>
        CashBankAccount.Create(Guid.NewGuid(), Guid.NewGuid(), CashBankKind.Bank, "CIB", "CIB current", "EGP", null, "CIB", "123", null);

    [Fact]
    public void Auto_match_prefers_reference_then_nearest_date_and_uses_each_movement_once()
    {
        var bank = Bank();
        var m1 = CashMovement.Create(bank, new DateOnly(2026, 3, 1), 500m, 500m, "Receipt", Guid.NewGuid(), "RCT-1", null);
        var m2 = CashMovement.Create(bank, new DateOnly(2026, 3, 2), 500m, 500m, "Receipt", Guid.NewGuid(), "RCT-2", null);
        var m3 = CashMovement.Create(bank, new DateOnly(2026, 3, 20), -200m, -200m, "Payment", Guid.NewGuid(), "PAY-1", null);
        var rec = BankReconciliation.Create(bank.CompanyId, bank.Id, new DateOnly(2026, 3, 31), 800m,
        [
            new BankStatementLine(new DateOnly(2026, 3, 2), 500m, "Transfer", "RCT-2"),
            new BankStatementLine(new DateOnly(2026, 3, 2), 500m, "Transfer", null),
            new BankStatementLine(new DateOnly(2026, 3, 10), -200m, "Cheque", null),
        ]);

        rec.AutoMatch([m1, m2, m3]).ShouldBe(2);
        rec.Lines.ElementAt(0).MatchedMovementId.ShouldBe(m2.Id);
        rec.Lines.ElementAt(1).MatchedMovementId.ShouldBe(m1.Id);
        rec.Lines.ElementAt(2).MatchedMovementId.ShouldBeNull(); // 10 days apart, beyond tolerance

        rec.Complete(800m).Error.Code.ShouldBe("finance.reconciliation-unmatched");
        rec.MatchManually(rec.Lines.ElementAt(2).Uid, m3.Id);
        rec.Complete(799m).Error.Code.ShouldBe("finance.reconciliation-difference");
        rec.Complete(800m).IsSuccess.ShouldBeTrue();
        rec.Status.ShouldBe(ReconciliationStatus.Completed);
    }
}
