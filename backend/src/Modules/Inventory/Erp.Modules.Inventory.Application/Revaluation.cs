using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Application;

/// <summary>
/// Revalues the stock on hand of one product in one legal entity to a new unit cost (contract
/// <see cref="IInventoryRevaluationService"/>). Each valuation area is locked, its value set to quantity × new cost (FIFO
/// layers still open take the new unit cost), and a value-only <c>CostAdjustment</c> movement records the change.
/// </summary>
public sealed class InventoryRevaluationService(
    IInventoryDbContext db,
    IInventoryStore store,
    IUnitOfWork unitOfWork,
    IOrganizationQueries organization,
    ICatalogQueries catalog,
    IPeriodGuard periods,
    IJournalPostingService journals,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IClock clock) : IInventoryRevaluationService
{
    public const string JournalPurpose = "inventory-revaluation";

    public async Task<Result<InventoryRevaluationResult>> RevalueAsync(InventoryRevaluationRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return InventoryErrors.TransactionRequired;
        }

        if (request.NewUnitCost < 0)
        {
            return Error.Validation("inventory.cost-invalid", "The new unit cost cannot be negative.");
        }

        var legalEntity = await organization.GetLegalEntityAsync(request.LegalEntityId, cancellationToken);
        if (legalEntity is null || (tenant.CompanyId is { } company && legalEntity.CompanyId != company))
        {
            return Error.NotFound("inventory.legal-entity-not-found", "The legal entity was not found.");
        }

        tenant.SetCompany(legalEntity.CompanyId);
        var done = await db.Movements.AsNoTracking().Where(m =>
                m.SourceDocType == request.SourceDocType && m.SourceDocId == request.SourceDocId && m.ProductId == request.ProductId &&
                m.LegalEntityId == request.LegalEntityId && m.MovementType == InventoryMovementTypes.CostAdjustment && m.ReversalOfId == null)
            .ToListAsync(cancellationToken);
        if (done.Count > 0)
        {
            return new InventoryRevaluationResult(done.Select(m => new RevaluedArea(m.ValuationAreaId, 0, 0, m.TotalCost)).ToList(), done.Sum(m => m.TotalCost), null, true);
        }

        var open = await periods.EnsureOpenAsync(request.LegalEntityId, request.Date, cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        var variantKey = Keys.Of(request.VariantId);
        // The areas of this legal entity that ever held the product, with the warehouse of the latest movement there.
        var lastMovements = await db.Movements.AsNoTracking()
            .Where(m => m.ProductId == request.ProductId && m.LegalEntityId == request.LegalEntityId && m.VariantId == request.VariantId)
            .GroupBy(m => m.ValuationAreaId)
            .Select(g => g.OrderByDescending(m => m.PostingSequence).First())
            .ToListAsync(cancellationToken);
        var product = (await catalog.GetProductsAsync([request.ProductId], cancellationToken)).FirstOrDefault();
        if (product is null)
        {
            return Error.NotFound("inventory.product-not-found", "The product was not found.");
        }

        var warehouses = (await organization.GetWarehousesAsync(lastMovements.Select(m => m.WarehouseId).Distinct().ToList(), cancellationToken)).ToDictionary(w => w.Id);
        var areas = new List<RevaluedArea>();
        var journalLines = new List<JournalLineRequest>();
        foreach (var last in lastMovements.OrderBy(m => m.ValuationAreaId))
        {
            await store.LockValuationAsync(legalEntity.CompanyId, last.ValuationAreaId, request.ProductId, variantKey, CostingMethods.WeightedAverage, cancellationToken);
            var valuation = await db.Valuations.FirstAsync(v => v.ValuationAreaId == last.ValuationAreaId && v.ProductId == request.ProductId && v.VariantKey == variantKey, cancellationToken);
            var state = valuation.ToState();
            if (state.Quantity <= 0)
            {
                continue;
            }

            var target = InventoryRounding.Money(state.Quantity * request.NewUnitCost);
            var change = target - state.Value;
            if (valuation.CostingMethod == CostingMethods.Fifo)
            {
                var layers = await db.CostLayers.Where(l => l.ValuationAreaId == last.ValuationAreaId && l.ProductId == request.ProductId &&
                    l.VariantKey == variantKey && l.RemainingQuantity > 0).ToListAsync(cancellationToken);
                foreach (var layer in layers)
                {
                    layer.UnitCost = request.NewUnitCost;
                }
            }

            areas.Add(new RevaluedArea(last.ValuationAreaId, state.Quantity, state.Value, change));
            if (change == 0)
            {
                continue;
            }

            valuation.Apply(new ValuationState(state.Quantity, target, InventoryRounding.Cost(request.NewUnitCost), state.LastCost));
            db.Movements.Add(new InventoryMovement(
                legalEntity.CompanyId, request.LegalEntityId, last.ValuationAreaId, valuation.NextSequence(), request.ProductId, request.VariantId, last.WarehouseId,
                null, null, InventoryMovementTypes.CostAdjustment, 0, product.BaseUomId, 0, 0, change, request.Date,
                request.SourceModule, request.SourceDocType, request.SourceDocId, request.ProductId, null, null,
                request.PostJournal ? JournalPurpose : null, clock.UtcNow, currentUser.UserId));

            if (warehouses.TryGetValue(last.WarehouseId, out var warehouse))
            {
                var stockRole = InventoryPostingService.StockRole(warehouse);
                var stockContext = new PostingContext(ProductPostingGroup: product.PostingGroup, WarehouseId: warehouse.Id);
                var counterContext = new PostingContext(ProductPostingGroup: product.PostingGroup);
                var amount = Math.Abs(change);
                if (change > 0)
                {
                    journalLines.Add(new JournalLineRequest(stockRole, null, amount, 0, stockContext, Description: product.Sku));
                    journalLines.Add(new JournalLineRequest(request.GainRole, null, 0, amount, counterContext, Description: product.Sku));
                }
                else
                {
                    journalLines.Add(new JournalLineRequest(request.LossRole, null, amount, 0, counterContext, Description: product.Sku));
                    journalLines.Add(new JournalLineRequest(stockRole, null, 0, amount, stockContext, Description: product.Sku));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        Guid? journalId = null;
        if (request.PostJournal && journalLines.Count > 0)
        {
            // One journal per (source, product): the purpose carries the product so several products of one source never collide.
            var posted = await journals.PostAsync(new JournalRequest(request.LegalEntityId, request.Date, request.SourceModule, request.SourceDocType,
                request.SourceDocId, $"reval:{product.Sku}", request.Description ?? $"Revaluation {product.Sku}", legalEntity.FunctionalCurrency, 1m,
                journalLines), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            journalId = posted.Value.JournalId;
        }

        return new InventoryRevaluationResult(areas, areas.Sum(a => a.Change), journalId, false);
    }
}
