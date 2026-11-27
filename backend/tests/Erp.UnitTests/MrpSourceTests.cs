using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Manufacturing.Application;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Results;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class MrpSourceTests
{
    private sealed class FakeSales(IReadOnlyList<OpenSalesDemand> demand) : ISalesQueries
    {
        public Task<IReadOnlyList<OpenSalesDemand>> GetOpenDemandAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpenSalesDemand>>(demand.Where(d => productIds.Contains(d.ProductId)).ToList());

        public Task<IReadOnlyList<OpenSalesDemand>> GetAllOpenDemandAsync(Guid? warehouseId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(demand);

        public Task<decimal> GetUninvoicedOrderExposureAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.FromResult(0m);
    }

    private sealed class FakeReservations(IReadOnlyList<ReservationInfo> reservations) : IReservationService
    {
        public Task<IReadOnlyList<ReservationInfo>> GetAsync(string demandType, Guid demandId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReservationInfo>>(reservations.Where(r => r.Reference.DemandType == demandType && r.Reference.DemandId == demandId).ToList());

        public Task<Result<ReservationInfo>> ReserveAsync(ReservationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ReservationInfo>> ReleaseAsync(ReservationRef reference, decimal? baseQuantity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Sales_demand_excludes_the_reserved_part_and_respects_the_horizon()
    {
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();
        var reservedLine = Guid.NewGuid();
        var openLine = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var demand = new List<OpenSalesDemand>
        {
            new(order, "SO-1", reservedLine, product, null, warehouse, new DateOnly(2026, 4, 10), 30m),
            new(order, "SO-1", openLine, product, null, warehouse, new DateOnly(2026, 4, 12), 20m),
            new(order, "SO-1", Guid.NewGuid(), product, null, warehouse, new DateOnly(2026, 9, 1), 99m), // beyond horizon
        };
        var reservations = new List<ReservationInfo>
        {
            new(Guid.NewGuid(), new ReservationRef(SalesDocumentTypes.Order, order, reservedLine), product, null, warehouse, 25m, 0m, 0m, 25m, "Active"),
        };

        var source = new SalesOrderMrpSource(new FakeSales(demand), new FakeReservations(reservations));
        var result = await source.GetDemandAsync(Guid.NewGuid(), new DateOnly(2026, 5, 31), TestContext.Current.CancellationToken);

        // 30 open − 25 reserved = 5 still to plan; the unreserved line counts fully; the line after the horizon is ignored.
        result.Select(d => (d.Quantity, d.DueDate)).ShouldBe([(5m, new DateOnly(2026, 4, 10)), (20m, new DateOnly(2026, 4, 12))]);
    }

    [Fact]
    public async Task Fully_reserved_lines_produce_no_demand()
    {
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();
        var line = Guid.NewGuid();
        var source = new SalesOrderMrpSource(
            new FakeSales([new(order, "SO-2", line, product, null, Guid.NewGuid(), new DateOnly(2026, 4, 10), 10m)]),
            new FakeReservations([new(Guid.NewGuid(), new ReservationRef(SalesDocumentTypes.Order, order, line), product, null, Guid.NewGuid(), 10m, 0m, 0m, 10m, "Active")]));
        (await source.GetDemandAsync(Guid.NewGuid(), new DateOnly(2026, 5, 31), TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
