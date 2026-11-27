using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class WeightedAverageTests
{
    private static readonly ValuationState Empty = new(0, 0, 0, 0);

    [Fact]
    public void Receipts_average_the_cost_and_issues_use_the_average()
    {
        var a = WeightedAverage.Receive(Empty, 100, 10m).State;          // 100 @ 10
        var b = WeightedAverage.Receive(a, 50, 13m).State;               // + 50 @ 13 → 150, value 1650, avg 11
        b.AverageCost.ShouldBe(11m);
        b.Value.ShouldBe(1650m);

        var issue = WeightedAverage.Issue(b, 30, allowNegative: false).Value;
        issue.UnitCost.ShouldBe(11m);
        issue.TotalCost.ShouldBe(330m);
        issue.State.Quantity.ShouldBe(120m);
        issue.State.Value.ShouldBe(1320m);
    }

    [Fact]
    public void The_issue_that_empties_the_item_takes_the_exact_remaining_value()
    {
        var s = WeightedAverage.Receive(Empty, 3, 10m).State;
        s = WeightedAverage.Receive(s, 3, 10.01m).State;                  // value 60.03, avg 10.005
        var first = WeightedAverage.Issue(s, 5, false).Value;            // 5 × 10.005 = 50.025
        var last = WeightedAverage.Issue(first.State, 1, false).Value;
        (first.TotalCost + last.TotalCost).ShouldBe(60.03m);
        last.State.Value.ShouldBe(0m);
        last.State.Quantity.ShouldBe(0m);
    }

    [Fact]
    public void Issue_beyond_stock_is_refused_when_negative_stock_is_off() =>
        WeightedAverage.Issue(WeightedAverage.Receive(Empty, 5, 10m).State, 6, allowNegative: false).Error.Code.ShouldBe("inventory.insufficient-stock");

    [Fact]
    public void Negative_stock_issues_at_last_average_and_the_covering_receipt_trues_up()
    {
        var s = WeightedAverage.Receive(Empty, 10, 10m).State;
        var issue = WeightedAverage.Issue(s, 15, allowNegative: true).Value; // −5, valued at 10
        issue.State.Quantity.ShouldBe(-5m);
        issue.TotalCost.ShouldBe(150m);
        issue.State.Value.ShouldBe(-50m);

        var receipt = WeightedAverage.Receive(issue.State, 20, 12m);     // covers 5 short units at 12 instead of 10
        receipt.TrueUp.ShouldBe(10m);                                      // 5 × (12 − 10) expensed
        receipt.TotalCost.ShouldBe(240m);
        receipt.State.Quantity.ShouldBe(15m);
        receipt.State.Value.ShouldBe(180m);                                // 15 × 12
        receipt.State.AverageCost.ShouldBe(12m);
        (issue.State.Value + receipt.TotalCost - receipt.TrueUp).ShouldBe(receipt.State.Value);
    }

    [Fact]
    public void A_receipt_that_only_partly_covers_a_shortfall_keeps_the_old_average()
    {
        var s = new ValuationState(-10, -100, 10, 10);
        var r = WeightedAverage.Receive(s, 4, 11m);
        r.TrueUp.ShouldBe(4m);
        r.State.Quantity.ShouldBe(-6m);
        r.State.Value.ShouldBe(-60m);
        r.State.AverageCost.ShouldBe(10m);
    }

    [Fact]
    public void An_explicit_cost_is_used_for_returns_at_original_cost()
    {
        var s = WeightedAverage.Receive(Empty, 10, 10m).State;
        s = WeightedAverage.Receive(s, 10, 14m).State;                    // avg 12
        var ret = WeightedAverage.Issue(s, 5, false, explicitUnitCost: 14m).Value;
        ret.TotalCost.ShouldBe(70m);
        ret.State.Value.ShouldBe(170m);
        ret.State.AverageCost.ShouldBe(11.333333m);
    }

    [Fact]
    public void A_transfer_out_and_back_in_at_the_issue_cost_leaves_the_average_unchanged()
    {
        var s = WeightedAverage.Receive(Empty, 7, 13.37m).State;
        s = WeightedAverage.Receive(s, 5, 9.11m).State;
        var avg = s.AverageCost;
        var issue = WeightedAverage.Issue(s, 4, false).Value;
        var back = WeightedAverage.Receive(issue.State, 4, issue.UnitCost);
        back.State.Quantity.ShouldBe(s.Quantity);
        Math.Abs(back.State.Value - s.Value).ShouldBeLessThanOrEqualTo(0.0001m);
        Math.Abs(back.State.AverageCost - avg).ShouldBeLessThanOrEqualTo(0.000001m);
    }
}

public class FifoTests
{
    private static readonly ValuationState Empty = new(0, 0, 0, 0);

    [Fact]
    public void Issues_consume_the_oldest_layers_first()
    {
        var l1 = Guid.NewGuid();
        var l2 = Guid.NewGuid();
        var state = Fifo.Receive(Fifo.Receive(Empty, 10, 5m).State, 10, 7m).State; // value 120
        var issue = Fifo.Issue(state, [new LayerSlice(l1, 10, 5m), new LayerSlice(l2, 10, 7m)], 12).Value;
        issue.Takes.Select(t => (t.LayerId, t.Quantity)).ShouldBe([(l1, 10m), (l2, 2m)]);
        issue.Outcome.TotalCost.ShouldBe(64m);
        issue.Outcome.State.Quantity.ShouldBe(8m);
        issue.Outcome.State.Value.ShouldBe(56m);
    }

    [Fact]
    public void Fifo_never_goes_below_zero()
    {
        var state = Fifo.Receive(Empty, 3, 5m).State;
        Fifo.Issue(state, [new LayerSlice(Guid.NewGuid(), 3, 5m)], 4).Error.Code.ShouldBe("inventory.insufficient-stock");
    }

    [Fact]
    public void Emptying_the_item_zeroes_the_value()
    {
        var state = Fifo.Receive(Fifo.Receive(Empty, 1, 1.00005m).State, 2, 2.00005m).State;
        var issue = Fifo.Issue(state, [new LayerSlice(Guid.NewGuid(), 1, 1.00005m), new LayerSlice(Guid.NewGuid(), 2, 2.00005m)], 3).Value;
        issue.Outcome.State.Value.ShouldBe(0m);
        issue.Outcome.TotalCost.ShouldBe(state.Value);
    }
}

public class CountAndLockTests
{
    [Fact]
    public void Variance_uses_snapshot_plus_movements_after_the_snapshot()
    {
        // Snapshot 100, 7 sold after the snapshot (current 93), 95 counted → +2.
        var since = CountMath.MovementsSinceSnapshot(100, 93);
        since.ShouldBe(-7m);
        CountMath.Variance(95, 100, since).ShouldBe(2m);
    }

    [Fact]
    public void Count_document_ignores_uncounted_lines()
    {
        var count = new StockCount(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "CNT-1", new DateOnly(2026, 11, 27), null);
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        count.TakeSnapshot(DateTime.UtcNow,
        [
            new StockCountLine { ProductId = p1, SnapshotQuantity = 10, BaseUomId = Guid.NewGuid() },
            new StockCountLine { ProductId = p2, SnapshotQuantity = 5, BaseUomId = Guid.NewGuid() },
        ]);
        count.EnterCount(p1, null, null, null, Guid.NewGuid(), 8);
        count.CalculateVariances(count.Lines.ToDictionary(l => l.LineId, l => l.SnapshotQuantity));
        count.Lines.Single(l => l.ProductId == p1).Variance.ShouldBe(-2m);
        count.Lines.Single(l => l.ProductId == p2).Variance.ShouldBe(0m);
        count.Status.ShouldBe(CountStatus.VarianceCalculated);
    }

    [Fact]
    public void Lock_order_is_deterministic_by_product_variant_warehouse_batch()
    {
        var p = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        var w = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        var keys = new[]
        {
            new StockKey(p[1], Guid.Empty, w[0], Guid.Empty),
            new StockKey(p[0], Guid.Empty, w[1], Guid.Empty),
            new StockKey(p[0], Guid.Empty, w[0], Guid.NewGuid()),
            new StockKey(p[0], Guid.Empty, w[0], Guid.Empty),
        };
        var once = LockOrder.Sort(keys, k => k);
        var shuffled = LockOrder.Sort(keys.Reverse(), k => k);
        once.ShouldBe(shuffled);
        once[0].ShouldBe(new StockKey(p[0], Guid.Empty, w[0], Guid.Empty));
        once[^1].ShouldBe(keys[0]);
    }

    [Fact]
    public void Transfer_receipts_cannot_exceed_what_is_in_transit_and_close_writes_off_the_rest()
    {
        var line = new StockDocumentLine { ProductId = Guid.NewGuid(), UomId = Guid.NewGuid(), Quantity = 10 };
        var transfer = new StockTransfer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "TRF-1", new DateOnly(2026, 11, 27), null, [line]);
        transfer.Approve();
        var id = transfer.Lines[0].LineId;
        transfer.MarkIssued(new Dictionary<Guid, (decimal, decimal, decimal)> { [id] = (10, 4m, 40m) });
        Should.Throw<DomainException>(() => transfer.RecordReceipt(new Dictionary<Guid, (decimal, IReadOnlyList<string>)> { [id] = (11, []) }));
        transfer.RecordReceipt(new Dictionary<Guid, (decimal, IReadOnlyList<string>)> { [id] = (6, []) });
        transfer.Status.ShouldBe(TransferStatus.PartiallyReceived);
        var lost = transfer.CloseWithDiscrepancy();
        lost.Single().BaseQuantity.ShouldBe(4m);
        transfer.Status.ShouldBe(TransferStatus.Completed);
    }

    [Fact]
    public void Reservations_track_consumption_and_release()
    {
        var r = new StockReservation(Guid.NewGuid(), "SalesOrder", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), 10);
        r.Consume(4).ShouldBe(4m);
        r.Status.ShouldBe(ReservationStatus.PartiallyConsumed);
        r.Release(null).ShouldBe(6m);
        r.Open.ShouldBe(0m);
        r.Status.ShouldBe(ReservationStatus.Consumed);
        r.Consume(1).ShouldBe(0m);
    }
}
