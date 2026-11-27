namespace Erp.Modules.Manufacturing.Contracts;

/// <summary>
/// Filter for production facts. An order is included when its start date or any of its WIP transactions falls in the range;
/// with <see cref="OpenWipOnly"/> the dates are ignored and only orders still carrying WIP value are returned.
/// </summary>
public sealed record ProductionReportFilter(
    DateOnly From,
    DateOnly To,
    Guid? WarehouseId = null,
    IReadOnlyCollection<Guid>? ProductIds = null,
    bool OpenWipOnly = false);

/// <summary>A component of a production order. Quantities are the component's base unit; cost is functional currency.</summary>
public sealed record ProductionComponentFact(
    Guid ProductId,
    decimal RequiredBaseQuantity,
    decimal StandardBaseQuantity,
    decimal IssuedBaseQuantity,
    decimal IssuedCost);

/// <summary>
/// One production order (released or later, not cancelled) with its quantities (output base unit) and accumulated costs.
/// <see cref="StandardBaseQuantity"/> on a component is the BOM quantity for the output actually produced (completed + scrapped).
/// </summary>
public sealed record ProductionFact(
    Guid OrderId,
    string Number,
    string Status,
    Guid ProductId,
    Guid ComponentWarehouseId,
    Guid OutputWarehouseId,
    DateOnly StartDate,
    DateOnly DueDate,
    DateOnly? LastActivityDate,
    decimal PlannedQuantity,
    decimal CompletedQuantity,
    decimal ScrappedQuantity,
    decimal MaterialCost,
    decimal LaborCost,
    decimal MachineCost,
    decimal OverheadCost,
    decimal ReceivedCost,
    decimal ScrapCost,
    decimal VarianceAmount,
    decimal WipBalance,
    IReadOnlyList<ProductionComponentFact> Components);

/// <summary>Read access to production orders for reporting. Scoped to the active company.</summary>
public interface IManufacturingReportQueries
{
    Task<IReadOnlyList<ProductionFact>> GetProductionFactsAsync(ProductionReportFilter filter, CancellationToken cancellationToken = default);
}
