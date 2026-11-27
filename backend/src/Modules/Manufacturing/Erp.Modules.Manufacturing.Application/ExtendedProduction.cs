using Erp.Application.Messaging;
using Erp.Application.Abstractions;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Manufacturing.Domain;
using Erp.Modules.Procurement.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing.Application;

// ---------- Component substitution ----------

/// <summary>Moves short components to their BOM alternatives (in priority order) and keeps reservations in step.</summary>
public static class Substitution
{
    /// <summary>
    /// At release: for each original component whose requirement exceeds the available stock in the component warehouse, moves
    /// the shortfall to alternatives with available stock, first alternative first. <paramref name="available"/> holds base
    /// quantities available per product and is not changed (reservations are made afterwards per line).
    /// </summary>
    public static async Task<Result> AutoSubstituteAsync(ProductionOrder order, IReadOnlyDictionary<Guid, decimal> available, ManufacturingSupport support,
        IManufacturingDbContext db, CancellationToken ct)
    {
        var candidates = order.Components.Where(c => c.SubstituteForLineId is null && c.Alternatives.Count > 0).ToList();
        if (candidates.Count == 0)
        {
            return Result.Success();
        }

        var free = new Dictionary<Guid, decimal>(available);
        foreach (var component in order.Components.Where(c => c.Alternatives.Count == 0 || c.SubstituteForLineId is not null))
        {
            free[component.ProductId] = free.GetValueOrDefault(component.ProductId) - (component.RemainingQuantity * component.UomFactor);
        }

        var alternatives = await support.ProductsAsync(candidates.SelectMany(c => c.Alternatives), ct);
        var standards = await StandardsAsync(order, db, ct);
        foreach (var component in candidates)
        {
            var want = MfgRounding.Quantity(component.RemainingQuantity * component.UomFactor);
            var own = Math.Max(0, free.GetValueOrDefault(component.ProductId));
            free[component.ProductId] = own - Math.Min(own, want);
            var shortfall = want - Math.Min(own, want);
            foreach (var alternative in component.Alternatives)
            {
                if (shortfall <= 0)
                {
                    break;
                }

                var altFree = Math.Max(0, free.GetValueOrDefault(alternative));
                var take = MfgRounding.Quantity(Math.Min(shortfall, altFree));
                if (take <= 0 || !alternatives.TryGetValue(alternative, out var product) || !product.IsActive || !product.IsStockable)
                {
                    continue;
                }

                order.Substitute(component.LineId, alternative, product.BaseUomId, take, support.Today, automatic: true, standards.GetValueOrDefault(alternative));
                free[alternative] = altFree - take;
                shortfall -= take;
            }
        }

        return Result.Success();
    }

    public static async Task<IReadOnlyDictionary<Guid, decimal?>> StandardsAsync(ProductionOrder order, IManufacturingDbContext db, CancellationToken ct)
    {
        if (order.CostVersionId is not { } versionId)
        {
            return new Dictionary<Guid, decimal?>();
        }

        var version = await db.CostVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
        return version?.Items.ToDictionary(i => i.ProductId, i => (decimal?)i.UnitCost) ?? new Dictionary<Guid, decimal?>();
    }
}

public sealed record SubstituteComponentCommand(Guid OrderId, Guid LineId, Guid AlternativeProductId, decimal BaseQuantity) : ICommand<ProductionOrderDto>;

public sealed class SubstituteComponentValidator : AbstractValidator<SubstituteComponentCommand>
{
    public SubstituteComponentValidator()
    {
        RuleFor(x => x.AlternativeProductId).NotEmpty();
        RuleFor(x => x.BaseQuantity).GreaterThan(0);
    }
}

/// <summary>
/// Manual substitution of part of a component's remaining requirement by one of its BOM alternatives. On a released order the
/// original's reservation is reduced and the alternative reserved (as far as stock is available).
/// </summary>
public sealed class SubstituteComponentHandler(IManufacturingDbContext db, ManufacturingSupport support, IStockQueries stock, IReservationService reservations)
    : ICommandHandler<SubstituteComponentCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(SubstituteComponentCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        var products = await support.StockableProductsAsync([command.AlternativeProductId], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var original = order.Component(command.LineId);
        var standards = await Substitution.StandardsAsync(order, db, cancellationToken);
        var substitute = order.Substitute(command.LineId, command.AlternativeProductId, products.Value[command.AlternativeProductId].BaseUomId, command.BaseQuantity,
            support.Today, automatic: false, standards.GetValueOrDefault(command.AlternativeProductId));

        if (order.Status != ProductionOrderStatus.Planned)
        {
            var release = Math.Min(original.OpenReservedBase, command.BaseQuantity);
            if (release > 0)
            {
                var released = await reservations.ReleaseAsync(new ReservationRef(ProductionDocTypes.ReservationDemand, order.Id, original.LineId), release, cancellationToken);
                if (released.IsFailure)
                {
                    return released.Error;
                }

                original.OpenReservedBase = MfgRounding.Quantity(original.OpenReservedBase - release);
            }

            var levels = await stock.GetStockAsync([command.AlternativeProductId], order.ComponentWarehouseId, cancellationToken);
            var reserve = MfgRounding.Quantity(Math.Min(command.BaseQuantity, levels.Sum(l => l.Available)));
            if (reserve > 0)
            {
                var reserved = await reservations.ReserveAsync(new ReservationRequest(new ReservationRef(ProductionDocTypes.ReservationDemand, order.Id, substitute.LineId),
                    command.AlternativeProductId, null, order.ComponentWarehouseId, reserve), cancellationToken);
                if (reserved.IsFailure)
                {
                    return reserved.Error;
                }

                substitute.OpenReservedBase = MfgRounding.Quantity(substitute.OpenReservedBase + reserve);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

// ---------- Rework orders ----------
public sealed record ReworkComponentInput(Guid ProductId, decimal Quantity, Guid UomId, string? IssueMethod);

public sealed record CreateReworkOrderCommand(Guid ProductId, Guid? VariantId, decimal Quantity, Guid ComponentWarehouseId, Guid? OutputWarehouseId, Guid? RoutingId,
    DateOnly? StartDate, DateOnly? DueDate, Guid? ReworkOfOrderId, IReadOnlyList<ReworkComponentInput>? ExtraComponents) : ICommand<ProductionOrderDto>;

public sealed class CreateReworkOrderValidator : AbstractValidator<CreateReworkOrderCommand>
{
    public CreateReworkOrderValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.ComponentWarehouseId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleForEach(x => x.ExtraComponents).ChildRules(c =>
        {
            c.RuleFor(x => x.Quantity).GreaterThan(0);
            c.RuleFor(x => x.IssueMethod).Must(m => m is null || Enum.TryParse<IssueMethod>(m, true, out _)).WithMessage("IssueMethod must be Manual or Backflush.");
        });
    }
}

/// <summary>
/// Creates a planned rework order: the defective units of the product are issued (from the component warehouse, e.g. a
/// quarantine store) as the main component, optional extra components and the given routing's operations are added, and the
/// reworked units are received back at actual cost.
/// </summary>
public sealed class CreateReworkOrderHandler(IManufacturingDbContext db, ManufacturingSupport support, IDocumentNumberGenerator numbers)
    : ICommandHandler<CreateReworkOrderCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(CreateReworkOrderCommand command, CancellationToken cancellationToken)
    {
        if (command.ReworkOfOrderId is { } sourceId && !await db.ProductionOrders.AnyAsync(o => o.Id == sourceId && o.ProductId == command.ProductId, cancellationToken))
        {
            return MfgErrors.OrderNotFound;
        }

        var extras = command.ExtraComponents ?? [];
        if (extras.Any(e => e.ProductId == command.ProductId) || extras.Select(e => e.ProductId).Distinct().Count() != extras.Count)
        {
            return MfgErrors.DuplicateLines;
        }

        var products = await support.StockableProductsAsync(extras.Select(e => e.ProductId).Append(command.ProductId), cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        if (extras.Any(e => ManufacturingSupport.Factor(products.Value[e.ProductId], e.UomId) is null))
        {
            return MfgErrors.UomInvalid;
        }

        Routing? routing = null;
        if (command.RoutingId is { } routingId)
        {
            routing = await db.Routings.FirstOrDefaultAsync(r => r.Id == routingId && r.ProductId == command.ProductId && r.IsActive, cancellationToken);
            if (routing is null)
            {
                return MfgErrors.RoutingNotFound;
            }
        }

        var start = command.StartDate ?? support.Today;
        var created = await CreateProductionOrderHandler.CreateAsync(db, support, numbers, command.ProductId, command.Quantity, command.ComponentWarehouseId,
            command.OutputWarehouseId ?? command.ComponentWarehouseId, start, command.DueDate, "Rework", cancellationToken, command.VariantId, ProductionOrderType.Rework,
            command.ReworkOfOrderId);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var product = products.Value[command.ProductId];
        var requirements = new List<ComponentRequirement>
        {
            new(SequentialGuid.New(), command.ProductId, product.BaseUomId, IssueMethod.Manual, 1m, command.Quantity) { VariantId = command.VariantId },
        };
        var factors = new Dictionary<Guid, decimal> { [requirements[0].LineId] = 1m };
        foreach (var extra in extras)
        {
            var line = new ComponentRequirement(SequentialGuid.New(), extra.ProductId, extra.UomId,
                extra.IssueMethod is null ? IssueMethod.Manual : Enum.Parse<IssueMethod>(extra.IssueMethod, true), extra.Quantity / command.Quantity, extra.Quantity);
            requirements.Add(line);
            factors[line.LineId] = ManufacturingSupport.Factor(products.Value[extra.ProductId], extra.UomId)!.Value;
        }

        created.Value.PlanRework(requirements, factors, routing);
        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(created.Value);
    }
}

// ---------- Capacity ----------
public sealed record CapacityDayDto(DateOnly Date, decimal Capacity, decimal Load, bool Overloaded);

public sealed record WorkCenterLoadDto(Guid WorkCenterId, string Code, decimal HoursPerDay, IReadOnlyList<string> WorkingDays, int OverloadedDays,
    IReadOnlyList<CapacityDayDto> Days);

public sealed record CapacityProposalDto(string SourceType, Guid SourceId, string Reference, DateOnly DueDate, DateOnly ProposedFinish, bool Late, bool Unschedulable);

public sealed record CapacityLoadDto(DateOnly From, DateOnly To, IReadOnlyList<WorkCenterLoadDto> WorkCenters, IReadOnlyList<CapacityProposalDto> Proposals);

public sealed record GetCapacityLoadQuery(DateOnly? From, DateOnly? To, Guid? MrpRunId) : IQuery<CapacityLoadDto>;

public sealed class GetCapacityLoadValidator : AbstractValidator<GetCapacityLoadQuery>
{
    public GetCapacityLoadValidator() =>
        RuleFor(x => x).Must(x => x.From is null || x.To is null || (x.To >= x.From && x.To.Value.DayNumber - x.From.Value.DayNumber <= 366))
            .WithName("to").WithMessage("The range must be at most one year and end after it starts.");
}

/// <summary>
/// Work-center load from the remaining hours of open production orders (and, optionally, the open production suggestions of an
/// MRP run) against daily capacity, with a finite schedule that flags orders whose hours do not fit before their due date and
/// proposes the date they would finish.
/// </summary>
public sealed class GetCapacityLoadHandler(IManufacturingDbContext db, ManufacturingSupport support) : IQueryHandler<GetCapacityLoadQuery, CapacityLoadDto>
{
    private static readonly ProductionOrderStatus[] Open =
    [
        ProductionOrderStatus.Planned, ProductionOrderStatus.Released, ProductionOrderStatus.InProgress, ProductionOrderStatus.PartiallyCompleted,
    ];

    public async Task<Result<CapacityLoadDto>> Handle(GetCapacityLoadQuery query, CancellationToken cancellationToken)
    {
        var from = query.From ?? support.Today;
        var to = query.To ?? from.AddDays(27);
        var workCenters = await db.WorkCenters.AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.Code).ToListAsync(cancellationToken);
        var loads = new List<CapacityLoad>();
        foreach (var order in await db.ProductionOrders.AsNoTracking().Where(o => Open.Contains(o.Status)).ToListAsync(cancellationToken))
        {
            loads.AddRange(order.Operations.Select(op => new CapacityLoad("ProductionOrder", order.Id, order.Number, op.WorkCenterId,
                Math.Max(0, op.PlannedHours - Math.Max(op.ReportedLaborHours, op.ReportedMachineHours)), order.StartDate, order.DueDate)));
        }

        if (query.MrpRunId is { } runId)
        {
            var run = await db.MrpRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (run is null)
            {
                return MfgErrors.RunNotFound;
            }

            var routings = (await db.Routings.AsNoTracking().Where(r => r.IsActive).ToListAsync(cancellationToken))
                .GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Code, StringComparer.Ordinal).First());
            foreach (var suggestion in run.Suggestions.Where(s => s.Status == SuggestionStatus.Open && s.Action == SuggestionAction.Produce))
            {
                if (!routings.TryGetValue(suggestion.ProductId, out var routing))
                {
                    continue;
                }

                var start = suggestion.OrderByDate < run.RunDate ? run.RunDate : suggestion.OrderByDate;
                loads.AddRange(routing.Operations.Select(op => new CapacityLoad("PlannedProduction", suggestion.SuggestionId, $"{run.Number}/{routing.Code}",
                    op.WorkCenterId, op.PlannedHours(suggestion.Quantity), start, suggestion.DueDate < start ? start : suggestion.DueDate)));
            }
        }

        var result = CapacityPlanner.Plan(workCenters.Select(w => new CapacityWorkCenter(w.Id, w.Code, w.CapacityHoursPerDay, w.WorkingDaysMask)).ToList(), loads, from, to);
        var byCenter = result.Buckets.GroupBy(b => b.WorkCenterId).ToDictionary(g => g.Key, g => g.ToList());
        return new CapacityLoadDto(from, to,
            workCenters.Select(w =>
            {
                var days = byCenter.GetValueOrDefault(w.Id) ?? [];
                return new WorkCenterLoadDto(w.Id, w.Code, w.CapacityHoursPerDay, WorkingDays.ToNames(w.WorkingDaysMask), days.Count(d => d.Overloaded),
                    days.Select(d => new CapacityDayDto(d.Date, d.Capacity, d.Load, d.Overloaded)).ToList());
            }).ToList(),
            result.Proposals.Select(p => new CapacityProposalDto(p.SourceType, p.SourceId, p.Reference, p.Due, p.ProposedFinish, p.Late, p.Unschedulable)).ToList());
    }
}

// ---------- MRP purchase suggestions → draft purchase orders ----------
public sealed record ConvertPurchaseSuggestionsCommand(Guid RunId, IReadOnlyList<Guid> SuggestionIds, Guid WarehouseId, Guid? DefaultSupplierId) : ICommand<MrpRunDto>;

public sealed class ConvertPurchaseSuggestionsValidator : AbstractValidator<ConvertPurchaseSuggestionsCommand>
{
    public ConvertPurchaseSuggestionsValidator()
    {
        RuleFor(x => x.SuggestionIds).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
    }
}

/// <summary>
/// Converts purchase suggestions into Draft purchase orders through Procurement — one per supplier (the item's preferred
/// supplier, else the default supplier given), lines in the product's base unit at the last known cost in the warehouse (a buyer
/// completes prices and sends the order).
/// </summary>
public sealed class ConvertPurchaseSuggestionsHandler(
    IManufacturingDbContext db,
    ManufacturingSupport support,
    IStockQueries stock,
    IPurchaseOrderDraftService purchasing) : ICommandHandler<ConvertPurchaseSuggestionsCommand, MrpRunDto>
{
    public async Task<Result<MrpRunDto>> Handle(ConvertPurchaseSuggestionsCommand command, CancellationToken cancellationToken)
    {
        var run = await db.MrpRuns.FirstOrDefaultAsync(r => r.Id == command.RunId, cancellationToken);
        if (run is null)
        {
            return MfgErrors.RunNotFound;
        }

        var warehouse = await support.WarehouseAsync(command.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error;
        }

        var suggestions = command.SuggestionIds.Distinct().Select(run.Suggestion).ToList();
        if (suggestions.Any(s => s.Status != SuggestionStatus.Open || s.Action != SuggestionAction.Purchase))
        {
            run.MarkPurchased(suggestions.First(s => s.Status != SuggestionStatus.Open || s.Action != SuggestionAction.Purchase).SuggestionId, Guid.Empty); // throws the reason
        }

        var productIds = suggestions.Select(s => s.ProductId).Distinct().ToList();
        var preferred = await db.PlanningParameters.AsNoTracking().Where(p => productIds.Contains(p.ProductId) && p.PreferredSupplierId != null)
            .ToDictionaryAsync(p => p.ProductId, p => p.PreferredSupplierId!.Value, cancellationToken);
        var products = await support.ProductsAsync(productIds, cancellationToken);
        var bySupplier = new Dictionary<Guid, List<MrpSuggestion>>();
        foreach (var suggestion in suggestions)
        {
            var supplier = preferred.TryGetValue(suggestion.ProductId, out var s) ? s : command.DefaultSupplierId;
            if (supplier is null)
            {
                return MfgErrors.SupplierRequired;
            }

            if (!bySupplier.TryGetValue(supplier.Value, out var list))
            {
                bySupplier[supplier.Value] = list = [];
            }

            list.Add(suggestion);
        }

        foreach (var (supplierId, list) in bySupplier.OrderBy(kv => kv.Key))
        {
            var lines = new List<DraftPurchaseOrderLine>();
            foreach (var suggestion in list.OrderBy(s => s.DueDate))
            {
                if (!products.TryGetValue(suggestion.ProductId, out var product))
                {
                    return MfgErrors.ProductNotFound;
                }

                var cost = await stock.GetItemCostAsync(suggestion.ProductId, null, command.WarehouseId, cancellationToken);
                decimal? price = cost is null ? null : cost.LastCost > 0 ? cost.LastCost : cost.AverageCost;
                lines.Add(new DraftPurchaseOrderLine(suggestion.ProductId, null, product.BaseUomId, suggestion.Quantity, price, suggestion.DueDate));
            }

            var created = await purchasing.CreateDraftAsync(new DraftPurchaseOrderRequest(warehouse.Value.LegalEntityId, command.WarehouseId, supplierId, support.Today,
                list.Min(s => s.DueDate), $"MRP {run.Number}", lines), cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            foreach (var suggestion in list)
            {
                run.MarkPurchased(suggestion.SuggestionId, created.Value.PurchaseOrderId);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return PlanningMapping.ToDto(run);
    }
}
