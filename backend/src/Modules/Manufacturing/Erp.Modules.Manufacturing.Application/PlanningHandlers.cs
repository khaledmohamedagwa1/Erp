using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Manufacturing.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing.Application;

public sealed record PeggingDto(string SourceType, string SourceReference, DateOnly DueDate, decimal Quantity);

public sealed record MrpSuggestionDto(Guid SuggestionId, Guid ProductId, string Action, decimal Quantity, DateOnly DueDate, DateOnly OrderByDate, bool IsLate,
    string Status, Guid? ProductionOrderId, IReadOnlyList<PeggingDto> Pegging, Guid? PurchaseOrderId);

public sealed record MrpShortageDto(Guid ProductId, decimal Quantity, DateOnly NeededBy, string Reason);

public sealed record MrpRunDto(Guid Id, string Number, DateOnly RunDate, int HorizonDays, IReadOnlyList<string> Sources,
    IReadOnlyList<MrpSuggestionDto> Suggestions, IReadOnlyList<MrpShortageDto> Shortages);

internal static class PlanningMapping
{
    public static MrpRunDto ToDto(MrpRun run, IReadOnlyList<string>? sources = null) => new(run.Id, run.Number, run.RunDate, run.HorizonDays, sources ?? [],
        run.Suggestions.Select(s => new MrpSuggestionDto(s.SuggestionId, s.ProductId, s.Action.ToString(), s.Quantity, s.DueDate, s.OrderByDate, s.IsLate,
            s.Status.ToString(), s.ProductionOrderId, s.Pegging.Select(p => new PeggingDto(p.SourceType, p.SourceReference, p.DueDate, p.Quantity)).ToList(),
            s.PurchaseOrderId)).ToList(),
        run.Shortages.Select(s => new MrpShortageDto(s.ProductId, s.Quantity, s.NeededBy, s.Reason)).ToList());
}

// ---------- Built-in sources ----------

/// <summary>
/// Manufacturing's own MRP inputs: open production orders supply their remaining output (due on the due date) and demand
/// their remaining components (due on the start date). Component demand already covered by an open reservation is left out,
/// because reserved stock is already excluded from available stock.
/// </summary>
public sealed class ProductionOrderMrpSource(IManufacturingDbContext db) : IMrpDemandSource, IMrpSupplySource
{
    private static readonly ProductionOrderStatus[] Open =
    [
        ProductionOrderStatus.Planned, ProductionOrderStatus.Released, ProductionOrderStatus.InProgress, ProductionOrderStatus.PartiallyCompleted,
    ];

    public string Name => "production-orders";

    public async Task<IReadOnlyList<MrpDemand>> GetDemandAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default)
    {
        var orders = await db.ProductionOrders.AsNoTracking().Where(o => Open.Contains(o.Status)).ToListAsync(cancellationToken);
        return orders.SelectMany(o => o.Components.Select(c => new MrpDemand(c.ProductId,
                MfgRounding.Quantity(Math.Max(0, (c.RemainingQuantity * c.UomFactor) - c.OpenReservedBase)), o.StartDate, "ProductionOrder", o.Number)))
            .Where(d => d.Quantity > 0).ToList();
    }

    public async Task<IReadOnlyList<MrpSupply>> GetSupplyAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default)
    {
        var orders = await db.ProductionOrders.AsNoTracking().Where(o => Open.Contains(o.Status)).ToListAsync(cancellationToken);
        return orders.Where(o => o.OpenQuantity > 0).Select(o => new MrpSupply(o.ProductId, o.OpenQuantity, o.DueDate, "ProductionOrder", o.Number)).ToList();
    }
}

/// <summary>
/// Confirmed, undelivered sales-order lines as MRP demand, in base units. MRP's available stock is on-hand minus reserved,
/// so the part of a line already reserved in stock is subtracted here (as for production-order components) — otherwise it
/// would be counted twice.
/// </summary>
public sealed class SalesOrderMrpSource(
    Erp.Modules.Sales.Contracts.ISalesQueries sales,
    Erp.Modules.Inventory.Contracts.IReservationService reservations) : IMrpDemandSource
{
    public string Name => "sales-orders";

    public async Task<IReadOnlyList<MrpDemand>> GetDemandAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default)
    {
        var lines = (await sales.GetAllOpenDemandAsync(cancellationToken: cancellationToken))
            .Where(l => l.OpenBaseQuantity > 0 && l.RequiredDate <= horizonEnd).ToList();
        var reservedByLine = new Dictionary<Guid, decimal>();
        foreach (var orderId in lines.Select(l => l.OrderId).Distinct())
        {
            foreach (var reservation in await reservations.GetAsync(Erp.Modules.Sales.Contracts.SalesDocumentTypes.Order, orderId, cancellationToken))
            {
                reservedByLine[reservation.Reference.DemandLineId] = reservedByLine.GetValueOrDefault(reservation.Reference.DemandLineId) + reservation.Open;
            }
        }

        return lines
            .Select(l => new MrpDemand(l.ProductId, MfgRounding.Quantity(Math.Max(0, l.OpenBaseQuantity - reservedByLine.GetValueOrDefault(l.OrderLineId))),
                l.RequiredDate, "SalesOrder", l.OrderNumber))
            .Where(d => d.Quantity > 0)
            .ToList();
    }
}

/// <summary>Open purchase-order lines (approved, sent, partially received) as MRP supply, in base units.</summary>
public sealed class PurchaseOrderMrpSource(Erp.Modules.Procurement.Contracts.IProcurementQueries procurement, IClock clock) : IMrpSupplySource
{
    public string Name => "purchase-orders";

    public async Task<IReadOnlyList<MrpSupply>> GetSupplyAsync(Guid companyId, DateOnly horizonEnd, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var lines = await procurement.GetAllOpenSupplyAsync(cancellationToken: cancellationToken);
        // Lines without an expected date are assumed available today; lines due after the horizon are ignored.
        return lines.Select(l => new MrpSupply(l.ProductId, l.OpenBaseQuantity, l.ExpectedDate ?? today, "PurchaseOrder", l.PurchaseOrderNumber))
            .Where(s => s.Quantity > 0 && s.DueDate <= horizonEnd)
            .ToList();
    }
}

// ---------- Run ----------
public sealed record RunMrpCommand(DateOnly? RunDate, int? HorizonDays) : ICommand<MrpRunDto>;

public sealed class RunMrpValidator : AbstractValidator<RunMrpCommand>
{
    public RunMrpValidator() => RuleFor(x => x.HorizonDays).InclusiveBetween(1, 730).When(x => x.HorizonDays is not null);
}

public sealed class RunMrpHandler(
    IManufacturingDbContext db,
    ManufacturingSupport support,
    IStockQueries stock,
    IEnumerable<IMrpDemandSource> demandSources,
    IEnumerable<IMrpSupplySource> supplySources,
    IDocumentNumberGenerator numbers) : ICommandHandler<RunMrpCommand, MrpRunDto>
{
    public async Task<Result<MrpRunDto>> Handle(RunMrpCommand command, CancellationToken cancellationToken)
    {
        var companyId = support.CompanyId;
        var runDate = command.RunDate ?? support.Today;
        var horizon = command.HorizonDays ?? 56;
        var horizonEnd = runDate.AddDays(horizon);

        var demand = new List<MrpRequirement>();
        foreach (var source in demandSources)
        {
            demand.AddRange((await source.GetDemandAsync(companyId, horizonEnd, cancellationToken))
                .Select(d => new MrpRequirement(d.ProductId, d.Quantity, d.DueDate < runDate ? runDate : d.DueDate, d.SourceType, d.SourceReference)));
        }

        var supply = new List<MrpReceipt>();
        foreach (var source in supplySources)
        {
            supply.AddRange((await source.GetSupplyAsync(companyId, horizonEnd, cancellationToken))
                .Select(s => new MrpReceipt(s.ProductId, s.Quantity, s.DueDate < runDate ? runDate : s.DueDate)));
        }

        var versions = await db.BomVersions.AsNoTracking()
            .Where(v => v.Status == BomVersionStatus.Active || v.Status == BomVersionStatus.Superseded).ToListAsync(cancellationToken);
        var versionsByProduct = versions.GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var graph = BomVersionActionHandler.BuildGraph(versions);
        var parameters = (await db.PlanningParameters.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(p => p.ProductId);

        var universe = demand.Select(d => d.ProductId).Concat(supply.Select(s => s.ProductId)).Concat(parameters.Keys)
            .Concat(graph.Keys).Concat(graph.Values.SelectMany(v => v)).Distinct().ToList();
        var products = await support.ProductsAsync(universe, cancellationToken);
        var stockLevels = universe.Count == 0 ? [] : await stock.GetStockAsync(universe, null, cancellationToken);
        var available = stockLevels.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Available));

        var items = universe.ToDictionary(p => p, p =>
        {
            var param = parameters.GetValueOrDefault(p);
            var procurement = param?.Procurement ?? (versionsByProduct.ContainsKey(p) ? ProcurementType.Make : ProcurementType.Buy);
            return new MrpItem(p, Math.Max(0, available.GetValueOrDefault(p)), param?.SafetyStock ?? 0, param?.LeadTimeDays ?? 0,
                param?.LotSizeRule ?? LotSizeRule.LotForLot, param?.LotSize ?? 0, procurement);
        });

        IReadOnlyList<(Guid, decimal)>? Explode(Guid productId, decimal quantity, DateOnly date)
        {
            var version = BomCalculator.Effective(versionsByProduct.GetValueOrDefault(productId) ?? [], date);
            if (version is null)
            {
                return null;
            }

            return BomCalculator.Explode(version, quantity)
                .Select(r => (r.ProductId, MfgRounding.Quantity(r.Quantity * (products.TryGetValue(r.ProductId, out var p) ? ManufacturingSupport.Factor(p, r.UomId) ?? 1m : 1m))))
                .ToList();
        }

        var result = MrpEngine.Run(new MrpInput(runDate, horizonEnd, items, demand, supply, BomCalculator.LowLevelCodes(graph, universe), Explode));
        var number = await numbers.NextAsync(new Erp.Application.Abstractions.NumberScope(companyId, "manufacturing.mrp-run", "MRP", FiscalYear: runDate.Year), cancellationToken);
        var run = MrpRun.Create(companyId, number, runDate, horizon, result);
        db.MrpRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return PlanningMapping.ToDto(run, demandSources.Select(s => s.Name).Concat(supplySources.Select(s => s.Name)).Distinct().ToList());
    }
}

public sealed record GetMrpRunQuery(Guid RunId) : IQuery<MrpRunDto>;

public sealed class GetMrpRunHandler(IManufacturingDbContext db) : IQueryHandler<GetMrpRunQuery, MrpRunDto>
{
    public async Task<Result<MrpRunDto>> Handle(GetMrpRunQuery query, CancellationToken cancellationToken)
    {
        var run = await db.MrpRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.RunId, cancellationToken);
        return run is null ? MfgErrors.RunNotFound : PlanningMapping.ToDto(run);
    }
}

public sealed record MrpRunSummaryDto(Guid Id, string Number, DateOnly RunDate, int HorizonDays, int Suggestions, int Shortages);

public sealed record ListMrpRunsQuery : IQuery<IReadOnlyList<MrpRunSummaryDto>>;

public sealed class ListMrpRunsHandler(IManufacturingDbContext db) : IQueryHandler<ListMrpRunsQuery, IReadOnlyList<MrpRunSummaryDto>>
{
    public async Task<Result<IReadOnlyList<MrpRunSummaryDto>>> Handle(ListMrpRunsQuery query, CancellationToken cancellationToken) =>
        (await db.MrpRuns.AsNoTracking().OrderByDescending(r => r.Number).Take(50).ToListAsync(cancellationToken))
        .Select(r => new MrpRunSummaryDto(r.Id, r.Number, r.RunDate, r.HorizonDays, r.Suggestions.Count, r.Shortages.Count)).ToList();
}

public sealed record ConvertSuggestionsCommand(Guid RunId, IReadOnlyList<Guid> SuggestionIds, Guid ComponentWarehouseId, Guid? OutputWarehouseId) : ICommand<MrpRunDto>;

public sealed class ConvertSuggestionsValidator : AbstractValidator<ConvertSuggestionsCommand>
{
    public ConvertSuggestionsValidator()
    {
        RuleFor(x => x.SuggestionIds).NotEmpty();
        RuleFor(x => x.ComponentWarehouseId).NotEmpty();
    }
}

/// <summary>Turns production suggestions into Draft production orders (purchase suggestions: <see cref="ConvertPurchaseSuggestionsCommand"/>).</summary>
public sealed class ConvertSuggestionsHandler(IManufacturingDbContext db, ManufacturingSupport support, IDocumentNumberGenerator numbers)
    : ICommandHandler<ConvertSuggestionsCommand, MrpRunDto>
{
    public async Task<Result<MrpRunDto>> Handle(ConvertSuggestionsCommand command, CancellationToken cancellationToken)
    {
        var run = await db.MrpRuns.FirstOrDefaultAsync(r => r.Id == command.RunId, cancellationToken);
        if (run is null)
        {
            return MfgErrors.RunNotFound;
        }

        foreach (var id in command.SuggestionIds.Distinct())
        {
            var suggestion = run.Suggestion(id);
            if (suggestion.Status != SuggestionStatus.Open || suggestion.Action != SuggestionAction.Produce)
            {
                run.MarkConverted(id, Guid.Empty); // throws the domain error explaining why
            }

            var start = suggestion.OrderByDate < run.RunDate ? run.RunDate : suggestion.OrderByDate;
            var order = await CreateProductionOrderHandler.CreateAsync(db, support, numbers, suggestion.ProductId, suggestion.Quantity,
                command.ComponentWarehouseId, command.OutputWarehouseId ?? command.ComponentWarehouseId, start,
                suggestion.DueDate < start ? start : suggestion.DueDate, $"MRP {run.Number}", cancellationToken);
            if (order.IsFailure)
            {
                return order.Error;
            }

            run.MarkConverted(id, order.Value.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        return PlanningMapping.ToDto(run);
    }
}

// ---------- Contract ----------
public sealed class ManufacturingQueries(IManufacturingDbContext db) : IManufacturingQueries
{
    public async Task<IReadOnlyList<ProductionSupplyDemand>> GetOpenSupplyDemandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
    {
        var source = new ProductionOrderMrpSource(db);
        var supply = await source.GetSupplyAsync(Guid.Empty, DateOnly.MaxValue, cancellationToken);
        var demand = await source.GetDemandAsync(Guid.Empty, DateOnly.MaxValue, cancellationToken);
        return productIds.Distinct().Select(p => new ProductionSupplyDemand(p,
            supply.Where(s => s.ProductId == p).Sum(s => s.Quantity), demand.Where(d => d.ProductId == p).Sum(d => d.Quantity))).ToList();
    }
}
