using Erp.Application.Messaging;
using Erp.Modules.Procurement.Contracts;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Procurement.Application;

/// <summary>Contract implementation: sends the regular <see cref="CreatePurchaseOrderCommand"/> (the order starts in Draft).</summary>
public sealed class PurchaseOrderDraftService(IDispatcher dispatcher) : IPurchaseOrderDraftService
{
    public async Task<Result<DraftPurchaseOrderResult>> CreateDraftAsync(DraftPurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        var created = await dispatcher.Send(new CreatePurchaseOrderCommand(request.LegalEntityId, null, request.WarehouseId, request.SupplierId, request.Date,
            request.ExpectedDate, null, null, null, request.Notes,
            request.Lines.Select(l => new OrderLineInput(l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice ?? 0m, 0m, null, null, l.ExpectedDate)).ToList()),
            cancellationToken);
        return created.IsFailure ? created.Error : new DraftPurchaseOrderResult(created.Value.Id, created.Value.Number, created.Value.Status);
    }
}
