using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Contracts;

/// <summary>One opening-stock line of a data import. <see cref="UnitCost"/> is per <see cref="UomId"/> in functional currency.</summary>
public sealed record OpeningStockImportLine(
    Guid ProductId,
    Guid UomId,
    decimal Quantity,
    decimal UnitCost,
    string? BatchNumber,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    IReadOnlyList<string>? Serials);

/// <summary>Command-style inventory operations for the Import module (same rules as manual entry, caller's transaction).</summary>
public interface IInventoryImportService
{
    /// <summary>Creates, approves and posts one opening-stock document for the warehouse (stock + journal against opening balance equity). Returns the document id.</summary>
    Task<Result<Guid>> PostOpeningStockAsync(Guid warehouseId, DateOnly date, IReadOnlyList<OpeningStockImportLine> lines, CancellationToken cancellationToken = default);
}
