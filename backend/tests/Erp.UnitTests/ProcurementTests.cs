using Erp.Modules.Procurement.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class ProcurementMatchTests
{
    [Theory]
    [InlineData(10, 10.20, false)] // +2.00 % is inside a 2 % tolerance
    [InlineData(10, 10.21, true)]  // +2.10 % is outside
    [InlineData(10, 9.79, true)]   // -2.10 % is outside too
    [InlineData(10, 9.80, false)]
    public void Price_tolerance_is_a_percentage_of_the_order_price_either_way(decimal orderPrice, decimal invoicePrice, bool exception)
    {
        var match = ThreeWayMatch.Evaluate(5, invoicePrice, 5, orderPrice, 0, 2);
        match.PriceException.ShouldBe(exception);
        match.QuantityException.ShouldBeFalse();
    }

    [Fact]
    public void Quantity_beyond_the_invoiceable_quantity_is_an_exception_unless_tolerated()
    {
        ThreeWayMatch.Evaluate(61, 10, 60, 10, 0, 2).QuantityException.ShouldBeTrue();
        ThreeWayMatch.Evaluate(60, 10, 60, 10, 0, 2).HasException.ShouldBeFalse();
        ThreeWayMatch.Evaluate(61, 10, 60, 10, 5, 2).QuantityException.ShouldBeFalse(); // 60 × 1.05 = 63
        ThreeWayMatch.Evaluate(1, 10, 0, 10, 0, 2).QuantityException.ShouldBeTrue();    // nothing left to invoice
    }

    [Fact]
    public void Variance_percent_is_reported_and_a_free_order_line_is_an_exception_when_charged()
    {
        ThreeWayMatch.Evaluate(1, 10.5m, 1, 10, 0, 2).PriceVariancePercent.ShouldBe(5m);
        ThreeWayMatch.Evaluate(1, 1, 1, 0, 0, 2).PriceException.ShouldBeTrue();
        ThreeWayMatch.Evaluate(1, 0, 1, 0, 0, 2).PriceException.ShouldBeFalse();
    }
}

public class ProcurementAllocationTests
{
    [Fact]
    public void Landed_cost_by_value_is_proportional_and_sums_exactly()
    {
        var shares = Allocator.LandedCost(100m, [(10m, 600m), (5m, 300m), (20m, 100m)], AllocationBasis.Value);
        shares.ShouldBe([60m, 30m, 10m]);
    }

    [Fact]
    public void Landed_cost_by_quantity_puts_the_rounding_residual_on_the_largest_share()
    {
        var shares = Allocator.LandedCost(100m, [(1m, 900m), (1m, 50m), (1m, 50m)], AllocationBasis.Quantity);
        shares.Sum().ShouldBe(100m);
        shares.ShouldBe([33.34m, 33.33m, 33.33m]);
    }

    [Fact]
    public void Allocation_needs_positive_weights()
    {
        Should.Throw<ArgumentException>(() => Allocator.Distribute(10m, [0m, 0m]));
        Allocator.Distribute(10m, []).ShouldBeEmpty();
    }

    [Fact]
    public void Base_amounts_are_fitted_to_the_open_item_base_total()
    {
        // Each line rounds to 1030.05 (3090.15 in total) but the open item's base is round(100.005 × 30.9) = 3090.15 / 3090.16: the journal must equal it.
        var parts = BaseAmounts.FitToTotal([33.335m, 33.335m, 33.335m], 30.9m, Money.Round(100.005m * 30.9m));
        parts.Sum().ShouldBe(Money.Round(100.005m * 30.9m));
    }
}

public class PurchaseOrderStateTests
{
    private static PurchaseOrder NewOrder(params decimal[] quantities) => new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "PO-1",
        new DateOnly(2026, 5, 1), null, "EGP", 1m, null, null, null,
        quantities.Select(q => new PurchaseOrderLine(Guid.Empty, Guid.NewGuid(), null, Guid.NewGuid(), q, 10m, 0, null, q * 10m, 0, null, null)));

    [Fact]
    public void Receipts_move_the_order_through_partially_and_fully_received_and_back()
    {
        var order = NewOrder(100, 50);
        order.Submit(approvalRequired: false);
        order.Status.ShouldBe(PurchaseOrderStatus.Approved);
        order.Send();

        var (first, second) = (order.Lines[0].LineId, order.Lines[1].LineId);
        order.ApplyReceipt(first, 60, 0);
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        order.ApplyReceipt(first, 40, 0);
        order.ApplyReceipt(second, 50, 0);
        order.Status.ShouldBe(PurchaseOrderStatus.FullyReceived);

        order.ApplyReceipt(second, -50, 0); // receipt reversed / returned
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        order.ApplyReceipt(first, -100, 0);
        order.Status.ShouldBe(PurchaseOrderStatus.Sent);
    }

    [Fact]
    public void Over_receipt_is_refused_beyond_the_tolerance_and_cancel_after_receipt_too()
    {
        var order = NewOrder(100);
        order.Submit(false);
        var line = order.Lines[0].LineId;
        Should.Throw<DomainException>(() => order.ApplyReceipt(line, 101, 0)).Error.Code.ShouldBe("procurement.over-receipt");
        order.ApplyReceipt(line, 101, 1);
        Should.Throw<DomainException>(() => order.Cancel());
        order.Close();
        order.Status.ShouldBe(PurchaseOrderStatus.Closed);
    }

    [Fact]
    public void Draft_orders_cannot_be_received_and_approval_can_send_back_to_draft()
    {
        var order = NewOrder(10);
        Should.Throw<DomainException>(() => order.ApplyReceipt(order.Lines[0].LineId, 1, 0));
        order.Submit(approvalRequired: true);
        order.Status.ShouldBe(PurchaseOrderStatus.PendingApproval);
        order.CompleteApproval(false);
        order.Status.ShouldBe(PurchaseOrderStatus.Draft);
    }

    [Fact]
    public void Invoiced_not_received_pool_is_consumed_at_average_value_and_cannot_be_reverted_once_received()
    {
        var order = NewOrder(10);
        order.Submit(false);
        var line = order.Lines[0].LineId;
        order.ApplyInvoiceBeforeReceipt(line, 3, 100m);
        order.ConsumeInvoicedNotReceived(line, 1).ShouldBe(33.33m);
        order.ConsumeInvoicedNotReceived(line, 2).ShouldBe(66.67m);
        Should.Throw<DomainException>(() => order.RevertInvoiceBeforeReceipt(line, 3, 100m)).Error.Code.ShouldBe("procurement.invoice-goods-received");
    }
}

public class GoodsReceiptTests
{
    private static GoodsReceipt PostedReceipt(decimal accepted, decimal baseValue)
    {
        var receipt = new GoodsReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GRN-1", new DateOnly(2026, 5, 2), "EGP", 1m, null,
            [new GoodsReceiptLine(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), accepted, accepted, 0, null, null, null, null, 10m)]);
        receipt.Approve();
        receipt.MarkPosted(receipt.Lines.Select(l => l with { MovementId = Guid.NewGuid(), BaseQuantity = accepted, BaseValue = baseValue, CounterRole = "GoodsReceivedNotInvoiced" })
            .ToList(), null);
        return receipt;
    }

    [Fact]
    public void Invoices_clear_grni_in_shares_and_the_last_one_clears_the_remainder()
    {
        var receipt = PostedReceipt(3, 100m);
        var line = receipt.Lines[0].LineId;
        receipt.ApplyInvoice(line, 1, 10m, 1m, null).ShouldBe(33.33m);
        receipt.ApplyInvoice(line, 2, 10m, 1m, null).ShouldBe(66.67m);
        receipt.Line(line).InvoiceableQuantity.ShouldBe(0);
        Should.Throw<DomainException>(() => receipt.Reverse()).Error.Code.ShouldBe("procurement.receipt-invoiced");
    }

    [Fact]
    public void Returns_take_uninvoiced_quantity_first()
    {
        var receipt = PostedReceipt(10, 100m);
        var line = receipt.Lines[0].LineId;
        receipt.ApplyInvoice(line, 6, 10m, 1m, null).ShouldBe(60m);
        var (uninvoiced, invoiced, relief) = receipt.ApplyReturn(line, 8);
        (uninvoiced, invoiced, relief).ShouldBe((4m, 4m, 40m));
        receipt.Line(line).ReturnableQuantity.ShouldBe(2);
        Should.Throw<DomainException>(() => receipt.ApplyReturn(line, 3)).Error.Code.ShouldBe("procurement.over-return");
    }

    [Fact]
    public void Inspection_splits_accepted_and_rejected_quantity()
    {
        var receipt = new GoodsReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GRN-2", new DateOnly(2026, 5, 2), "EGP", 1m, null,
            [new GoodsReceiptLine(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), 10, 0, 0, null, null, null, null)]);
        var line = receipt.Lines[0].LineId;
        receipt.Lines[0].AcceptedQuantity.ShouldBe(10);
        receipt.Inspect(new Dictionary<Guid, decimal> { [line] = 7 }, "3 damaged");
        receipt.Line(line).RejectedQuantity.ShouldBe(3);
        receipt.Status.ShouldBe(GoodsReceiptStatus.Inspected);
        Should.Throw<DomainException>(() => receipt.SetLines([])).ShouldNotBeNull(); // inspected receipts are no longer editable
    }
}

public class PurchaseInvoiceTests
{
    [Fact]
    public void Match_exceptions_need_an_approval_rule()
    {
        var invoice = new PurchaseInvoice(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "INV-77", new DateOnly(2026, 5, 3), "EGP", 1m, null, null,
            [new PurchaseInvoiceLine(Guid.Empty, InvoiceLineKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, null, 1, 10.5m, null, 10.5m)]);
        var matched = invoice.Lines.Select(l => l with { PriceException = true }).ToList();
        Should.Throw<DomainException>(() => invoice.Submit(matched, approvalRequired: false)).Error.Code.ShouldBe("procurement.match-exceptions");
        invoice.Submit(matched, approvalRequired: true);
        invoice.Status.ShouldBe(PurchaseInvoiceStatus.PendingApproval);
        invoice.MatchStatus.ShouldBe(MatchStatus.Exceptions);
        invoice.CompleteApproval(true);
        invoice.Status.ShouldBe(PurchaseInvoiceStatus.Approved);
    }
}
