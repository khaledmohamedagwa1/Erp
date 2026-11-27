using Erp.Modules.Inventory.Application;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.Modules.Pos.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class ReturnAllocationTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    [Fact]
    public void Whole_units_are_split_by_largest_remainder()
    {
        var split = ReturnAllocation.Allocate(5, [new(A, 6), new(B, 4)])!;
        split.ShouldBe([(A, 3m), (B, 2m)]);

        var thirds = ReturnAllocation.Allocate(2, [new(A, 1), new(B, 1), new(C, 1)])!;
        thirds.Sum(t => t.Quantity).ShouldBe(2m);
        thirds.ShouldAllBe(t => t.Quantity == 1m);
    }

    [Fact]
    public void Fractional_quantities_are_prorated_with_the_remainder_on_the_last_source()
    {
        var split = ReturnAllocation.Allocate(1.5m, [new(A, 1), new(B, 2)])!;
        split.Sum(t => t.Quantity).ShouldBe(1.5m);
        split[0].Quantity.ShouldBe(0.5m);
    }

    [Fact]
    public void A_return_larger_than_what_can_come_back_is_refused()
    {
        ReturnAllocation.Allocate(11, [new(A, 6), new(B, 4)]).ShouldBeNull();
        ReturnAllocation.Allocate(3, [new(A, 3), new(B, 0)]).ShouldBe([(A, 3m)]);
    }
}

public class BinPickingTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Bins_are_picked_in_order_then_stock_outside_any_bin()
    {
        BinPicking.Pick(7, [new(A, 3), new(B, 2)], 5, null).ShouldBe([(A, 3m), (B, 2m), (null, 2m)]);
    }

    [Fact]
    public void A_requested_bin_must_hold_the_whole_quantity()
    {
        BinPicking.Pick(2, [new(A, 3), new(B, 2)], 5, B).ShouldBe([(B, 2m)]);
        BinPicking.Pick(3, [new(A, 3), new(B, 2)], 5, B).ShouldBeNull();
    }
}

public class CostLayerAdjustmentTests
{
    [Fact]
    public void Only_the_share_still_in_the_layer_is_reversed()
    {
        var adjustment = new CostLayerAdjustment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 20m);
        adjustment.StillInStock(10).ShouldBe(20m);
        adjustment.StillInStock(6).ShouldBe(12m);
        adjustment.StillInStock(0).ShouldBe(0m);
        adjustment.StillInStock(15).ShouldBe(20m); // never more than was applied
    }
}

public class RecostReplayTests
{
    private static long _sequence;

    private static InventoryMovement Movement(string date, string type, decimal quantity, decimal total, Guid? reversalOf = null, string docType = "Doc") =>
        new(Guid.Empty, Guid.Empty, Guid.Empty, ++_sequence, Guid.Empty, null, Guid.Empty, null, null, type, quantity, Guid.Empty, quantity,
            quantity == 0 ? 0 : Math.Abs(total / quantity), total, DateOnly.Parse(date), "test", docType, Guid.NewGuid(), Guid.NewGuid(), null, reversalOf, null,
            DateTime.UtcNow, null);

    [Fact]
    public void An_issue_posted_before_a_back_dated_receipt_is_recosted_at_the_date_order_average()
    {
        var receipt1 = Movement("2026-06-01", InventoryMovementTypes.PurchaseReceipt, 10, 100m);
        var issue = Movement("2026-06-10", InventoryMovementTypes.SalesIssue, -5, -50m);
        var receipt2 = Movement("2026-06-05", InventoryMovementTypes.PurchaseReceipt, 10, 160m);

        var corrections = RecostEngine.Replay([receipt1, issue, receipt2], new DateOnly(2026, 6, 1));
        var correction = corrections.ShouldHaveSingleItem();
        correction.MovementId.ShouldBe(issue.Id);
        correction.RecordedCost.ShouldBe(50m);
        correction.RecalculatedCost.ShouldBe(65m);
    }

    [Fact]
    public void Issues_before_the_window_reversed_issues_and_transfers_keep_their_cost()
    {
        var receipt1 = Movement("2026-06-01", InventoryMovementTypes.PurchaseReceipt, 10, 100m);
        var early = Movement("2026-06-02", InventoryMovementTypes.SalesIssue, -2, -20m);
        var reversed = Movement("2026-06-10", InventoryMovementTypes.SalesIssue, -1, -10m);
        var reversal = Movement("2026-06-11", InventoryMovementTypes.SalesIssue, 1, 10m, reversalOf: reversed.Id);
        var transfer = Movement("2026-06-10", InventoryMovementTypes.TransferOut, -1, -10m);
        var receipt2 = Movement("2026-06-05", InventoryMovementTypes.PurchaseReceipt, 10, 160m);

        RecostEngine.Replay([receipt1, early, reversed, reversal, transfer, receipt2], new DateOnly(2026, 6, 3)).ShouldBeEmpty();
    }

    [Fact]
    public void Earlier_recost_corrections_count_as_part_of_the_recorded_cost()
    {
        var receipt1 = Movement("2026-06-01", InventoryMovementTypes.PurchaseReceipt, 10, 100m);
        var issue = Movement("2026-06-10", InventoryMovementTypes.SalesIssue, -5, -50m);
        var receipt2 = Movement("2026-06-05", InventoryMovementTypes.PurchaseReceipt, 10, 160m);
        var correction = new InventoryMovement(Guid.Empty, Guid.Empty, Guid.Empty, ++_sequence, Guid.Empty, null, Guid.Empty, null, null,
            InventoryMovementTypes.CostAdjustment, 0, Guid.Empty, 0, 0, -15m, new DateOnly(2026, 6, 20), "inventory", RecostEngine.SourceDocType, Guid.NewGuid(), issue.Id,
            null, null, null, DateTime.UtcNow, null, issue.Id);

        RecostEngine.Replay([receipt1, issue, receipt2, correction], new DateOnly(2026, 6, 1)).ShouldBeEmpty();
    }
}

public class TransferCarriedCostTests
{
    [Fact]
    public void The_receipt_that_empties_a_line_carries_the_exact_remainder()
    {
        var transfer = new StockTransfer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "TRF-1", new DateOnly(2026, 3, 1), null,
            [new StockDocumentLine { ProductId = Guid.NewGuid(), UomId = Guid.NewGuid(), Quantity = 3 }]);
        transfer.Approve();
        var line = transfer.Lines[0];
        transfer.MarkIssued(new Dictionary<Guid, (decimal, decimal, decimal)> { [line.LineId] = (3m, 33.333333m, 100m) });

        var carried = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            var cost = transfer.CarriedCost(line, 1m);
            carried.Add(cost);
            transfer.RecordReceipt(new Dictionary<Guid, (decimal, IReadOnlyList<string>)> { [line.LineId] = (1m, []) }, new Dictionary<Guid, decimal> { [line.LineId] = cost });
        }

        carried.ShouldBe([33.33m, 33.33m, 33.34m]);
        carried.Sum().ShouldBe(100m);
        transfer.Status.ShouldBe(TransferStatus.Completed);
    }

    [Fact]
    public void Only_direct_transfers_complete_in_one_step()
    {
        var line = new StockDocumentLine { ProductId = Guid.NewGuid(), UomId = Guid.NewGuid(), Quantity = 2 };
        var transfer = new StockTransfer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "TRF-2", new DateOnly(2026, 3, 1), null, [line]);
        transfer.Approve();
        Should.Throw<DomainException>(() => transfer.MarkMovedDirectly(new Dictionary<Guid, (decimal, decimal, decimal)> { [line.LineId] = (2m, 5m, 10m) }));
    }
}

public class SerialCountTests
{
    [Fact]
    public void Serials_found_and_missing_become_count_lines()
    {
        var product = Guid.NewGuid();
        var count = new StockCount(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "CNT-1", new DateOnly(2026, 4, 1), null);
        count.TakeSnapshot(DateTime.UtcNow,
        [
            new StockCountLine { ProductId = product, Serial = "SN-1", SnapshotQuantity = 1 },
            new StockCountLine { ProductId = product, Serial = "SN-2", SnapshotQuantity = 1 },
        ]);
        count.EnterSerialCount(product, null, Guid.NewGuid(), ["sn-1", "SN-7"]);
        count.Lines.ToDictionary(l => l.Serial!, l => l.CountedQuantity).ShouldBe(new Dictionary<string, decimal?> { ["SN-1"] = 1, ["SN-2"] = 0, ["SN-7"] = 1 }, ignoreOrder: true);
    }
}

public class PosOfflineRangeTests
{
    [Fact]
    public void Offline_numbers_are_valid_only_inside_allocated_ranges()
    {
        var terminal = new PosTerminal(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "T01", "Till", Guid.NewGuid(), "EGP", Guid.NewGuid(), null, null, 0, "Reject", Guid.NewGuid());
        terminal.AllocateOfflineRange(10).ShouldBe((1L, 10L));
        terminal.AllocateOfflineRange(5).ShouldBe((11L, 15L));
        terminal.IsAllocatedOfflineNumber("OFF-T01-15").ShouldBeTrue();
        terminal.IsAllocatedOfflineNumber("OFF-T01-16").ShouldBeFalse();
        terminal.IsAllocatedOfflineNumber("OFF-T02-1").ShouldBeFalse();
        terminal.IsAllocatedOfflineNumber("OFF-T01--1").ShouldBeFalse();
        terminal.IsAllocatedOfflineNumber(null).ShouldBeFalse();
        Should.Throw<DomainException>(() => terminal.AllocateOfflineRange(0)).Error.Code.ShouldBe("pos.invalid-offline-range");
    }
}
