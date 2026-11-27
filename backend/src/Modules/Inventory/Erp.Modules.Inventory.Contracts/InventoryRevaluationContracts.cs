using Erp.SharedKernel.Results;

namespace Erp.Modules.Inventory.Contracts;

/// <summary>
/// Value-only revaluation of everything on hand of one product (or variant) in one legal entity to a new unit cost, e.g. when a
/// new standard cost is activated. (SourceDocType, SourceDocId, ProductId) is idempotent. When <see cref="PostJournal"/> is
/// true one journal per legal entity is posted: Dr Inventory / Cr <see cref="GainRole"/> for an increase, Dr
/// <see cref="LossRole"/> / Cr Inventory for a decrease. Must run inside the caller's transaction.
/// </summary>
public sealed record InventoryRevaluationRequest(
    Guid LegalEntityId,
    Guid ProductId,
    Guid? VariantId,
    decimal NewUnitCost,
    DateOnly Date,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string GainRole,
    string LossRole,
    bool PostJournal = true,
    string? Description = null);

/// <summary>Per valuation area: quantity on hand, value before and the change applied (positive = increase).</summary>
public sealed record RevaluedArea(Guid ValuationAreaId, decimal Quantity, decimal ValueBefore, decimal Change);

public sealed record InventoryRevaluationResult(IReadOnlyList<RevaluedArea> Areas, decimal TotalChange, Guid? JournalId, bool AlreadyPosted);

public interface IInventoryRevaluationService
{
    Task<Result<InventoryRevaluationResult>> RevalueAsync(InventoryRevaluationRequest request, CancellationToken cancellationToken = default);
}
