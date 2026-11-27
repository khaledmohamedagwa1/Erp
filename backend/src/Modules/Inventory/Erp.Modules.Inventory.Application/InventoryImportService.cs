using Erp.Application.Messaging;
using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Application;

/// <summary>Runs the regular opening-stock document commands (create → submit → approve → post) for the Import module.</summary>
public sealed class InventoryImportService(IDispatcher dispatcher) : IInventoryImportService
{
    public async Task<Result<Guid>> PostOpeningStockAsync(Guid warehouseId, DateOnly date, IReadOnlyList<OpeningStockImportLine> lines, CancellationToken cancellationToken = default)
    {
        var created = await dispatcher.Send(new CreateOpeningStockCommand(warehouseId, date,
            lines.Select(l => new StockLineInput(null, l.ProductId, null, l.UomId, l.Quantity, l.UnitCost, l.BatchNumber, l.ManufactureDate, l.ExpiryDate, l.Serials)).ToList()),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        foreach (var action in new[] { DocumentAction.Submit, DocumentAction.Approve })
        {
            var step = await dispatcher.Send(new OpeningStockActionCommand(created.Value.Id, action), cancellationToken);
            if (step.IsFailure)
            {
                return step.Error;
            }
        }

        var posted = await dispatcher.Send(new PostOpeningStockCommand(created.Value.Id), cancellationToken);
        return posted.IsFailure ? posted.Error : created.Value.Id;
    }
}
