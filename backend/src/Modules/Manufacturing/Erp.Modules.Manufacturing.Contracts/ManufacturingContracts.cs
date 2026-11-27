namespace Erp.Modules.Manufacturing.Contracts;

public static class ManufacturingPermissions
{
    public const string Read = "manufacturing.read";
    public const string SetupManage = "manufacturing.setup.manage";
    public const string OrderManage = "manufacturing.order.manage";
    public const string OrderPost = "manufacturing.order.post";
    public const string PlanningRun = "manufacturing.planning.run";
    public const string CostingManage = "manufacturing.costing.manage";
}

/// <summary>Open production supply (output not yet received) and demand (components not yet issued) of one product, base units.</summary>
public sealed record ProductionSupplyDemand(Guid ProductId, decimal OpenSupply, decimal OpenDemand);

/// <summary>Read access to production orders for other modules (e.g. sales availability, procurement).</summary>
public interface IManufacturingQueries
{
    Task<IReadOnlyList<ProductionSupplyDemand>> GetOpenSupplyDemandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);
}

/// <summary>A gross requirement for MRP (base units of the product).</summary>
public sealed record MrpDemand(Guid ProductId, decimal Quantity, DateOnly DueDate, string SourceType, string SourceReference);

/// <summary>A scheduled receipt for MRP (base units of the product).</summary>
public sealed record MrpSupply(Guid ProductId, decimal Quantity, DateOnly DueDate, string SourceType, string SourceReference);

/// <summary>
/// Pluggable MRP demand. Manufacturing supplies production-order component demand itself; Sales is expected to register
/// confirmed sales-order demand (until then a no-op source is registered). Implementations read the active company's data.
/// </summary>
public interface IMrpDemandSource
{
    string Name { get; }

    Task<IReadOnlyList<MrpDemand>> GetDemandAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pluggable MRP supply. Manufacturing supplies open production-order output itself; Procurement is expected to register
/// open purchase-order lines (until then a no-op source is registered).
/// </summary>
public interface IMrpSupplySource
{
    string Name { get; }

    Task<IReadOnlyList<MrpSupply>> GetSupplyAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default);
}
