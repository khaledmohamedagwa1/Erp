using Erp.SharedKernel.Results;

namespace Erp.Modules.Procurement.Contracts;

/// <summary>One line of a draft purchase order requested by another module. Quantity is in <see cref="UomId"/>; a null price means 0 (to be completed by a buyer).</summary>
public sealed record DraftPurchaseOrderLine(Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal? UnitPrice, DateOnly? ExpectedDate);

/// <summary>A draft purchase order created on behalf of another module (e.g. MRP purchase suggestions).</summary>
public sealed record DraftPurchaseOrderRequest(
    Guid LegalEntityId,
    Guid WarehouseId,
    Guid SupplierId,
    DateOnly Date,
    DateOnly? ExpectedDate,
    string? Notes,
    IReadOnlyList<DraftPurchaseOrderLine> Lines);

public sealed record DraftPurchaseOrderResult(Guid PurchaseOrderId, string Number, string Status);

/// <summary>Creates purchase orders in Draft through Procurement's regular create command (same validation, numbering and tax). Runs in the caller's transaction.</summary>
public interface IPurchaseOrderDraftService
{
    Task<Result<DraftPurchaseOrderResult>> CreateDraftAsync(DraftPurchaseOrderRequest request, CancellationToken cancellationToken = default);
}
