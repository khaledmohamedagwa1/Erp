using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Manufacturing.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing.Application;

public sealed record ProductionComponentDto(Guid LineId, Guid ProductId, Guid UomId, string IssueMethod, decimal QuantityPerUnit, decimal RequiredQuantity,
    decimal IssuedQuantity, decimal IssuedCost, decimal OpenReservedBase, Guid? VariantId, decimal? StandardUnitCost, IReadOnlyList<Guid> Alternatives,
    Guid? SubstituteForLineId);

public sealed record ProductionSubstitutionDto(Guid OriginalLineId, Guid SubstituteLineId, Guid OriginalProductId, Guid SubstituteProductId, decimal BaseQuantity,
    DateOnly Date, bool Automatic);

public sealed record ProductionOperationDto(int Sequence, string Name, Guid WorkCenterId, decimal PlannedHours, decimal ReportedLaborHours, decimal ReportedMachineHours, decimal ReportedCost);

public sealed record ProductionTransactionDto(Guid TransactionId, string Kind, DateOnly Date, decimal Quantity, decimal WipAmount, Guid? JournalId, string? Reference,
    Guid? WorkCenterId);

public sealed record ProductionOrderDto(Guid Id, string Number, Guid ProductId, Guid LegalEntityId, Guid ComponentWarehouseId, Guid OutputWarehouseId,
    decimal PlannedQuantity, decimal CompletedQuantity, decimal ScrappedQuantity, DateOnly StartDate, DateOnly DueDate, Guid? BomVersionId, Guid? RoutingId,
    string Status, decimal WipBalance, decimal MaterialCost, decimal LaborCost, decimal MachineCost, decimal OverheadCost, decimal ReceivedCost,
    decimal VarianceAmount, string? SourceReference, IReadOnlyList<ProductionComponentDto> Components, IReadOnlyList<ProductionOperationDto> Operations,
    IReadOnlyList<ProductionTransactionDto> Transactions, string OrderType, Guid? VariantId, Guid? ReworkOfOrderId, string CostingMethod, Guid? CostVersionId,
    decimal StandardUnitCost, decimal StandardMaterialUnitCost, decimal StandardConversionUnitCost, decimal MaterialPriceVariance, decimal MaterialUsageVariance,
    decimal ConversionVariance, decimal OtherVariance, IReadOnlyList<ProductionSubstitutionDto> Substitutions);

internal static class ProductionMapping
{
    public static ProductionOrderDto ToDto(ProductionOrder o) => new(o.Id, o.Number, o.ProductId, o.LegalEntityId, o.ComponentWarehouseId, o.OutputWarehouseId,
        o.PlannedQuantity, o.CompletedQuantity, o.ScrappedQuantity, o.StartDate, o.DueDate, o.BomVersionId, o.RoutingId, o.Status.ToString(), o.WipBalance,
        o.MaterialCost, o.LaborCost, o.MachineCost, o.OverheadCost, o.ReceivedCost, o.VarianceAmount, o.SourceReference,
        o.Components.Select(c => new ProductionComponentDto(c.LineId, c.ProductId, c.UomId, c.IssueMethod.ToString(), c.QuantityPerUnit, c.RequiredQuantity,
            c.IssuedQuantity, c.IssuedCost, c.OpenReservedBase, c.VariantId, c.StandardUnitCost, c.Alternatives, c.SubstituteForLineId)).ToList(),
        o.Operations.Select(p => new ProductionOperationDto(p.Sequence, p.Name, p.WorkCenterId, p.PlannedHours, p.ReportedLaborHours, p.ReportedMachineHours, p.ReportedCost)).ToList(),
        o.Transactions.Select(t => new ProductionTransactionDto(t.TransactionId, t.Kind.ToString(), t.Date, t.Quantity, t.WipAmount, t.JournalId, t.Reference, t.WorkCenterId)).ToList(),
        o.OrderType.ToString(), o.VariantId, o.ReworkOfOrderId, o.CostingMethod.ToString(), o.CostVersionId, o.StandardUnitCost, o.StandardMaterialUnitCost,
        o.StandardConversionUnitCost, o.MaterialPriceVariance, o.MaterialUsageVariance, o.ConversionVariance, o.OtherVariance,
        o.Substitutions.Select(s => new ProductionSubstitutionDto(s.OriginalLineId, s.SubstituteLineId, s.OriginalProductId, s.SubstituteProductId, s.BaseQuantity,
            s.Date, s.Automatic)).ToList());
}

public static class ProductionDocTypes
{
    public const string Order = "manufacturing.production-order";
    public const string MaterialIssue = "manufacturing.material-issue";
    public const string MaterialReturn = "manufacturing.material-return";
    public const string Backflush = "manufacturing.backflush";
    public const string OperationReport = "manufacturing.operation-report";
    public const string Receipt = "manufacturing.finished-goods-receipt";
    public const string Close = "manufacturing.close";
    public const string ReservationDemand = "ProductionOrder";
}

/// <summary>
/// Moves value into and out of WIP. Material movements go through Inventory (which also posts Dr WIP / Cr Inventory and the
/// reverse, with WorkInProgress as counter role); conversion cost and close variance are journalled here.
/// </summary>
public sealed class ProductionPoster(IInventoryPostingService inventory, IJournalPostingService journals, ManufacturingSupport support)
{
    public sealed record MaterialLine(ProductionComponent Component, decimal Quantity, string? BatchNumber, IReadOnlyList<string>? Serials);

    /// <summary>Issues (positive quantities) or returns (<paramref name="isReturn"/>) components and records them on the order.</summary>
    public async Task<Result> MaterialAsync(ProductionOrder order, ProductionTransactionKind kind, IReadOnlyList<MaterialLine> lines, DateOnly date, CancellationToken ct)
    {
        if (lines.Count == 0)
        {
            return Result.Success();
        }

        var isReturn = kind == ProductionTransactionKind.MaterialReturn;
        var sourceId = SequentialGuid.New();
        var docType = kind switch
        {
            ProductionTransactionKind.MaterialReturn => ProductionDocTypes.MaterialReturn,
            ProductionTransactionKind.Backflush => ProductionDocTypes.Backflush,
            _ => ProductionDocTypes.MaterialIssue,
        };
        var request = lines.Select(l => new InventoryPostingLine(
            l.Component.LineId,
            l.Component.ProductId,
            l.Component.VariantId,
            order.ComponentWarehouseId,
            l.Component.UomId,
            isReturn ? l.Quantity : -l.Quantity,
            isReturn ? InventoryMovementTypes.ProductionReturn : InventoryMovementTypes.ProductionIssue,
            UnitCost: isReturn ? ReturnUnitCost(l.Component) : null,
            Batch: l.BatchNumber is null ? null : new BatchInput(l.BatchNumber),
            Serials: l.Serials,
            ConsumeReservation: isReturn ? null : new ReservationRef(ProductionDocTypes.ReservationDemand, order.Id, l.Component.LineId),
            CounterRole: PostingRoles.WorkInProgress)).ToList();

        var posted = await inventory.PostAsync(new InventoryPostingRequest(order.LegalEntityId, date, ManufacturingSupport.SourceModule, docType, sourceId,
            order.Number, request, PostJournal: true, JournalDescription: $"{order.Number} {kind}"), ct);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        foreach (var line in lines)
        {
            var result = posted.Value.Lines.Single(r => r.LineId == line.Component.LineId);
            // Inventory signs costs like quantities: an issue comes back negative (WIP grows by its absolute value), a return
            // comes back positive (WIP shrinks). The WIP delta is therefore always the negated stock cost.
            var cost = -result.TotalCost;
            order.RecordMaterial(SequentialGuid.New(), kind, line.Component.LineId, isReturn ? -line.Quantity : line.Quantity, cost, date, posted.Value.JournalId);
        }

        return Result.Success();
    }

    /// <summary>Backflushes Backflush components up to the output reached so far.</summary>
    public Task<Result> BackflushAsync(ProductionOrder order, decimal outputDone, DateOnly date, CancellationToken ct) =>
        MaterialAsync(order, ProductionTransactionKind.Backflush,
            order.BackflushDue(outputDone).Select(x => new MaterialLine(x.Component, x.Quantity, null, null)).ToList(), date, ct);

    public async Task<Result<Guid?>> JournalAsync(ProductionOrder order, string docType, Guid sourceId, string purpose, DateOnly date, string description,
        IReadOnlyList<JournalLineRequest> lines, CancellationToken ct)
    {
        if (lines.Count == 0)
        {
            return (Guid?)null;
        }

        var currency = await support.CurrencyAsync(order.LegalEntityId, ct);
        var posted = await journals.PostAsync(new JournalRequest(order.LegalEntityId, date, ManufacturingSupport.SourceModule, docType, sourceId, purpose,
            description, currency, 1m, lines), ct);
        return posted.IsFailure ? posted.Error : posted.Value.JournalId;
    }

    private static decimal ReturnUnitCost(ProductionComponent component)
    {
        var baseIssued = component.IssuedQuantity * component.UomFactor;
        return baseIssued <= 0 ? 0 : MfgRounding.Cost(component.IssuedCost / baseIssued);
    }
}

// ---------- Create / plan / release / cancel ----------
public sealed record CreateProductionOrderCommand(Guid ProductId, decimal Quantity, Guid ComponentWarehouseId, Guid? OutputWarehouseId, DateOnly? StartDate,
    DateOnly? DueDate, Guid? VariantId = null) : ICommand<ProductionOrderDto>;

public sealed class CreateProductionOrderValidator : AbstractValidator<CreateProductionOrderCommand>
{
    public CreateProductionOrderValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.ComponentWarehouseId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}

public sealed class CreateProductionOrderHandler(IManufacturingDbContext db, ManufacturingSupport support, IDocumentNumberGenerator numbers)
    : ICommandHandler<CreateProductionOrderCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(CreateProductionOrderCommand command, CancellationToken cancellationToken)
    {
        var created = await CreateAsync(db, support, numbers, command.ProductId, command.Quantity, command.ComponentWarehouseId,
            command.OutputWarehouseId ?? command.ComponentWarehouseId, command.StartDate ?? support.Today, command.DueDate, null, cancellationToken, command.VariantId);
        if (created.IsFailure)
        {
            return created.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(created.Value);
    }

    internal static async Task<Result<ProductionOrder>> CreateAsync(IManufacturingDbContext db, ManufacturingSupport support, IDocumentNumberGenerator numbers,
        Guid productId, decimal quantity, Guid componentWarehouseId, Guid outputWarehouseId, DateOnly start, DateOnly? due, string? source, CancellationToken ct,
        Guid? variantId = null, ProductionOrderType orderType = ProductionOrderType.Standard, Guid? reworkOfOrderId = null)
    {
        var products = await support.StockableProductsAsync([productId], ct);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var warehouses = await support.ProductionWarehousesAsync(componentWarehouseId, outputWarehouseId, ct);
        if (warehouses.IsFailure)
        {
            return warehouses.Error;
        }

        if (variantId is { } variant && await support.VariantAttributesAsync(productId, variant, ct) is null)
        {
            return MfgErrors.VariantInvalid;
        }

        var number = await numbers.NextAsync(new NumberScope(support.CompanyId, ProductionDocTypes.Order, "MO", FiscalYear: start.Year), ct);
        var order = ProductionOrder.Create(support.CompanyId, number, productId, warehouses.Value.LegalEntityId, componentWarehouseId, outputWarehouseId,
            quantity, start, due ?? start, source, variantId, orderType, reworkOfOrderId);
        db.ProductionOrders.Add(order);
        return order;
    }
}

public sealed record PlanProductionOrderCommand(Guid OrderId, Guid? BomVersionId, Guid? RoutingId) : ICommand<ProductionOrderDto>;

public sealed class PlanProductionOrderHandler(IManufacturingDbContext db, ManufacturingSupport support, CostingPolicy costing) : ICommandHandler<PlanProductionOrderCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(PlanProductionOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        var versions = await db.BomVersions.Where(v => v.ProductId == order.ProductId).ToListAsync(cancellationToken);
        var version = command.BomVersionId is { } versionId
            ? versions.FirstOrDefault(v => v.Id == versionId && v.IsUsable)
            : BomCalculator.Effective(versions, order.StartDate);
        if (version is null)
        {
            return ManufacturingErrors.NoEffectiveBom;
        }

        Routing? routing = null;
        if (command.RoutingId is { } routingId)
        {
            routing = await db.Routings.FirstOrDefaultAsync(r => r.Id == routingId && r.ProductId == order.ProductId && r.IsActive, cancellationToken);
            if (routing is null)
            {
                return MfgErrors.RoutingNotFound;
            }
        }
        else
        {
            routing = await db.Routings.Where(r => r.ProductId == order.ProductId && r.IsActive).OrderBy(r => r.Code).FirstOrDefaultAsync(cancellationToken);
        }

        var products = await support.StockableProductsAsync(version.Lines.Select(l => l.ComponentProductId), cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var factors = new Dictionary<Guid, decimal>();
        foreach (var line in version.Lines)
        {
            factors[line.LineId] = ManufacturingSupport.Factor(products.Value[line.ComponentProductId], line.UomId) ?? throw new DomainException(MfgErrors.UomInvalid);
        }

        IReadOnlyDictionary<string, string>? attributes = null;
        if (order.VariantId is { } variantId)
        {
            attributes = await support.VariantAttributesAsync(order.ProductId, variantId, cancellationToken);
            if (attributes is null)
            {
                return MfgErrors.VariantInvalid;
            }
        }

        order.Plan(version, factors, routing, attributes);
        if (await costing.MethodForAsync(order.ProductId, cancellationToken) == ManufacturingCostingMethod.Standard)
        {
            var active = await costing.ActiveVersionAsync(cancellationToken);
            var standard = active?.Item(order.ProductId);
            if (active is null || standard is null)
            {
                return MfgErrors.NoStandardCost;
            }

            order.UseStandardCost(active.Id, standard, active.Items.ToDictionary(i => i.ProductId, i => i.UnitCost));
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

public sealed record ReleaseProductionOrderCommand(Guid OrderId) : ICommand<ProductionOrderDto>;

/// <summary>Releases the order and reserves the available part of each component in the component warehouse.</summary>
public sealed class ReleaseProductionOrderHandler(IManufacturingDbContext db, IStockQueries stock, IReservationService reservations, ManufacturingSupport support)
    : ICommandHandler<ReleaseProductionOrderCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(ReleaseProductionOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        order.Release();
        var levels = await stock.GetStockAsync(order.Components.SelectMany(c => c.Alternatives.Append(c.ProductId)).Distinct().ToList(), order.ComponentWarehouseId, cancellationToken);
        var available = levels.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Available));
        var substituted = await Substitution.AutoSubstituteAsync(order, available, support, db, cancellationToken);
        if (substituted.IsFailure)
        {
            return substituted.Error;
        }

        foreach (var component in order.Components.ToList())
        {
            var want = MfgRounding.Quantity(component.RequiredQuantity * component.UomFactor);
            var free = available.GetValueOrDefault(component.ProductId);
            var reserve = Math.Min(want, free);
            if (reserve <= 0)
            {
                continue;
            }

            var reserved = await reservations.ReserveAsync(new ReservationRequest(
                new ReservationRef(ProductionDocTypes.ReservationDemand, order.Id, component.LineId), component.ProductId, component.VariantId, order.ComponentWarehouseId, reserve), cancellationToken);
            if (reserved.IsFailure)
            {
                return reserved.Error;
            }

            available[component.ProductId] = free - reserve;
            order.SetReserved(component.LineId, reserve);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

public sealed record CancelProductionOrderCommand(Guid OrderId) : ICommand<ProductionOrderDto>;

public sealed class CancelProductionOrderHandler(IManufacturingDbContext db, IReservationService reservations) : ICommandHandler<CancelProductionOrderCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(CancelProductionOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        var reserved = order.Components.Where(c => c.OpenReservedBase > 0).ToList();
        order.Cancel();
        foreach (var component in reserved)
        {
            await reservations.ReleaseAsync(new ReservationRef(ProductionDocTypes.ReservationDemand, order.Id, component.LineId), null, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

// ---------- Material issue / return ----------
public sealed record MaterialLineInput(Guid LineId, decimal Quantity, string? BatchNumber, IReadOnlyList<string>? Serials);

public sealed record PostMaterialCommand(Guid OrderId, bool IsReturn, DateOnly? Date, IReadOnlyList<MaterialLineInput> Lines) : ICommand<ProductionOrderDto>;

public sealed class PostMaterialValidator : AbstractValidator<PostMaterialCommand>
{
    public PostMaterialValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThan(0));
    }
}

public sealed class PostMaterialHandler(IManufacturingDbContext db, ProductionPoster poster, ManufacturingSupport support) : ICommandHandler<PostMaterialCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(PostMaterialCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        if (command.Lines.Select(l => l.LineId).Distinct().Count() != command.Lines.Count)
        {
            return MfgErrors.DuplicateLines;
        }

        var lines = command.Lines.Select(l => new ProductionPoster.MaterialLine(order.Component(l.LineId), l.Quantity, l.BatchNumber, l.Serials)).ToList();
        if (command.IsReturn && lines.Any(l => l.Quantity > l.Component.IssuedQuantity))
        {
            return Error.Validation("manufacturing.return-exceeds-issued", "You cannot return more than was issued.");
        }

        var posted = await poster.MaterialAsync(order, command.IsReturn ? ProductionTransactionKind.MaterialReturn : ProductionTransactionKind.MaterialIssue,
            lines, command.Date ?? support.Today, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

// ---------- Operation reporting ----------
public sealed record ReportOperationCommand(Guid OrderId, int Sequence, decimal LaborHours, decimal MachineHours, DateOnly? Date) : ICommand<ProductionOrderDto>;

public sealed class ReportOperationValidator : AbstractValidator<ReportOperationCommand>
{
    public ReportOperationValidator()
    {
        RuleFor(x => x.LaborHours).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MachineHours).GreaterThanOrEqualTo(0);
        RuleFor(x => x).Must(x => x.LaborHours + x.MachineHours > 0).WithName("hours").WithMessage("Report labour or machine hours.");
    }
}

/// <summary>Hours × work-centre rates: Dr WorkInProgress / Cr AppliedLabor, AppliedMachine, AppliedOverhead.</summary>
public sealed class ReportOperationHandler(IManufacturingDbContext db, ProductionPoster poster, ManufacturingSupport support) : ICommandHandler<ReportOperationCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(ReportOperationCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        var operation = order.Operations.FirstOrDefault(o => o.Sequence == command.Sequence);
        if (operation is null)
        {
            return Error.NotFound("manufacturing.operation-not-found", "The operation is not part of this production order.");
        }

        if (!order.IsOpenForWork)
        {
            return ManufacturingErrors.InvalidTransition;
        }

        var workCenter = await db.WorkCenters.AsNoTracking().FirstOrDefaultAsync(w => w.Id == operation.WorkCenterId, cancellationToken);
        if (workCenter is null)
        {
            return MfgErrors.WorkCenterNotFound;
        }

        var (labor, machine, overhead) = workCenter.Cost(command.LaborHours, command.MachineHours);
        var total = labor + machine + overhead;
        var date = command.Date ?? support.Today;
        var sourceId = SequentialGuid.New();
        var lines = new List<JournalLineRequest>();
        if (total > 0)
        {
            lines.Add(new JournalLineRequest(PostingRoles.WorkInProgress, null, total, 0, Description: $"{order.Number} op {operation.Sequence}"));
            AddCredit(lines, PostingRoles.AppliedLabor, labor);
            AddCredit(lines, PostingRoles.AppliedMachine, machine);
            AddCredit(lines, PostingRoles.AppliedOverhead, overhead);
        }

        var journal = await poster.JournalAsync(order, ProductionDocTypes.OperationReport, sourceId, "operation", date,
            $"{order.Number} operation {operation.Sequence} {workCenter.Code}", lines, cancellationToken);
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        order.RecordOperation(sourceId, command.Sequence, command.LaborHours, command.MachineHours, labor, machine, overhead, date, journal.Value);
        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }

    private static void AddCredit(List<JournalLineRequest> lines, string role, decimal amount)
    {
        if (amount > 0)
        {
            lines.Add(new JournalLineRequest(role, null, 0, amount));
        }
    }
}

// ---------- Finished goods receipt / scrap ----------
public sealed record ReceiveFinishedGoodsCommand(Guid OrderId, decimal Quantity, DateOnly? Date, string? BatchNumber, DateOnly? ExpiryDate, IReadOnlyList<string>? Serials)
    : ICommand<ProductionOrderDto>;

public sealed class ReceiveFinishedGoodsValidator : AbstractValidator<ReceiveFinishedGoodsCommand>
{
    public ReceiveFinishedGoodsValidator() => RuleFor(x => x.Quantity).GreaterThan(0);
}

/// <summary>
/// Backflushes Backflush components up to the new output, then receives finished goods at WIP-to-relieve / quantity:
/// Dr Inventory (finished goods) / Cr WorkInProgress.
/// </summary>
public sealed class ReceiveFinishedGoodsHandler(IManufacturingDbContext db, ProductionPoster poster, ManufacturingSupport support, IInventoryPostingService inventory)
    : ICommandHandler<ReceiveFinishedGoodsCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(ReceiveFinishedGoodsCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        if (!order.IsOpenForWork)
        {
            return ManufacturingErrors.InvalidTransition;
        }

        var date = command.Date ?? support.Today;
        var backflush = await poster.BackflushAsync(order, order.CompletedQuantity + order.ScrappedQuantity + command.Quantity, date, cancellationToken);
        if (backflush.IsFailure)
        {
            return backflush.Error;
        }

        var products = await support.ProductsAsync([order.ProductId], cancellationToken);
        if (!products.TryGetValue(order.ProductId, out var product))
        {
            return MfgErrors.ProductNotFound;
        }

        var unitCost = order.ReceiptUnitCost(command.Quantity);
        var sourceId = SequentialGuid.New();
        var posted = await inventory.PostAsync(new InventoryPostingRequest(order.LegalEntityId, date, ManufacturingSupport.SourceModule, ProductionDocTypes.Receipt,
            sourceId, order.Number,
            [new InventoryPostingLine(order.Id, order.ProductId, order.VariantId, order.OutputWarehouseId, product.BaseUomId, command.Quantity,
                InventoryMovementTypes.ProductionReceipt, unitCost,
                command.BatchNumber is null ? null : new BatchInput(command.BatchNumber, date, command.ExpiryDate), command.Serials,
                CounterRole: PostingRoles.WorkInProgress)],
            PostJournal: true, JournalDescription: $"{order.Number} finished goods"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        order.RecordReceipt(sourceId, command.Quantity, posted.Value.Lines[0].TotalCost, date, posted.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

public sealed record ScrapCommand(Guid OrderId, decimal Quantity, DateOnly? Date, string? Reason) : ICommand<ProductionOrderDto>;

public sealed class ScrapValidator : AbstractValidator<ScrapCommand>
{
    public ScrapValidator() => RuleFor(x => x.Quantity).GreaterThan(0);
}

public sealed class ScrapHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<ScrapCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(ScrapCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        order.RecordScrap(SequentialGuid.New(), command.Quantity, command.Date ?? support.Today, command.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }
}

// ---------- Close ----------
public sealed record CloseProductionOrderCommand(Guid OrderId, DateOnly? Date, bool AbnormalScrap) : ICommand<ProductionOrderDto>;

/// <summary>
/// Backflushes the remaining Backflush components, releases open reservations, and posts the residual WIP to
/// ProductionVariance (or AbnormalScrap) so the order's WIP ends at zero.
/// </summary>
public sealed class CloseProductionOrderHandler(IManufacturingDbContext db, ProductionPoster poster, ManufacturingSupport support, IReservationService reservations)
    : ICommandHandler<CloseProductionOrderCommand, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(CloseProductionOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);
        if (order is null)
        {
            return MfgErrors.OrderNotFound;
        }

        if (order.Status is not (ProductionOrderStatus.Completed or ProductionOrderStatus.PartiallyCompleted or ProductionOrderStatus.InProgress))
        {
            return ManufacturingErrors.InvalidTransition;
        }

        var date = command.Date ?? support.Today;
        var backflush = await poster.BackflushAsync(order, order.CompletedQuantity + order.ScrappedQuantity, date, cancellationToken);
        if (backflush.IsFailure)
        {
            return backflush.Error;
        }

        foreach (var component in order.Components.Where(c => c.OpenReservedBase > 0))
        {
            await reservations.ReleaseAsync(new ReservationRef(ProductionDocTypes.ReservationDemand, order.Id, component.LineId), null, cancellationToken);
        }

        Guid? journalId = null;
        var residual = order.WipBalance;
        StandardVariances? variances = null;
        if (order.CostingMethod == ManufacturingCostingMethod.Standard && !command.AbnormalScrap)
        {
            variances = StandardCosting.Split(order);
            var lines = new List<JournalLineRequest>();
            AddVariance(lines, PostingRoles.PurchasePriceVariance, variances.MaterialPrice, "material price");
            AddVariance(lines, PostingRoles.MaterialUsageVariance, variances.MaterialUsage, "material usage");
            AddVariance(lines, PostingRoles.ConversionCostVariance, variances.Conversion, "conversion");
            AddVariance(lines, PostingRoles.ProductionVariance, variances.Other, "other");
            if (residual > 0)
            {
                lines.Add(new JournalLineRequest(PostingRoles.WorkInProgress, null, 0, residual));
            }
            else if (residual < 0)
            {
                lines.Add(new JournalLineRequest(PostingRoles.WorkInProgress, null, -residual, 0));
            }

            var journal = await poster.JournalAsync(order, ProductionDocTypes.Close, order.Id, "variance", date, $"{order.Number} close (standard)", lines, cancellationToken);
            if (journal.IsFailure)
            {
                return journal.Error;
            }

            journalId = journal.Value;
            residual = 0;
        }

        if (residual != 0)
        {
            var role = command.AbnormalScrap ? PostingRoles.AbnormalScrap : PostingRoles.ProductionVariance;
            var amount = Math.Abs(residual);
            IReadOnlyList<JournalLineRequest> lines = residual > 0
                ? [new(role, null, amount, 0), new(PostingRoles.WorkInProgress, null, 0, amount)]
                : [new(PostingRoles.WorkInProgress, null, amount, 0), new(role, null, 0, amount)];
            var journal = await poster.JournalAsync(order, ProductionDocTypes.Close, order.Id, "variance", date, $"{order.Number} close", lines, cancellationToken);
            if (journal.IsFailure)
            {
                return journal.Error;
            }

            journalId = journal.Value;
        }

        order.Close(SequentialGuid.New(), date, journalId, variances);
        await db.SaveChangesAsync(cancellationToken);
        return ProductionMapping.ToDto(order);
    }

    /// <summary>A positive variance is unfavourable (debit), a negative one favourable (credit).</summary>
    private static void AddVariance(List<JournalLineRequest> lines, string role, decimal amount, string description)
    {
        if (amount > 0)
        {
            lines.Add(new JournalLineRequest(role, null, amount, 0, Description: description));
        }
        else if (amount < 0)
        {
            lines.Add(new JournalLineRequest(role, null, 0, -amount, Description: description));
        }
    }
}

// ---------- Queries ----------
public sealed record GetProductionOrderQuery(Guid OrderId) : IQuery<ProductionOrderDto>;

public sealed class GetProductionOrderHandler(IManufacturingDbContext db) : IQueryHandler<GetProductionOrderQuery, ProductionOrderDto>
{
    public async Task<Result<ProductionOrderDto>> Handle(GetProductionOrderQuery query, CancellationToken cancellationToken)
    {
        var order = await db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == query.OrderId, cancellationToken);
        return order is null ? MfgErrors.OrderNotFound : ProductionMapping.ToDto(order);
    }
}

public sealed record ProductionOrderSummaryDto(Guid Id, string Number, Guid ProductId, decimal PlannedQuantity, decimal CompletedQuantity, string Status,
    DateOnly StartDate, DateOnly DueDate, decimal WipBalance);

public sealed record ListProductionOrdersQuery(PageRequest Page, string? Status, Guid? ProductId) : IQuery<PagedResult<ProductionOrderSummaryDto>>;

public sealed class ListProductionOrdersHandler(IManufacturingDbContext db) : IQueryHandler<ListProductionOrdersQuery, PagedResult<ProductionOrderSummaryDto>>
{
    public async Task<Result<PagedResult<ProductionOrderSummaryDto>>> Handle(ListProductionOrdersQuery query, CancellationToken cancellationToken)
    {
        var source = db.ProductionOrders.AsNoTracking();
        if (query.ProductId is { } productId)
        {
            source = source.Where(o => o.ProductId == productId);
        }

        if (query.Status is not null && Enum.TryParse<ProductionOrderStatus>(query.Status, true, out var status))
        {
            source = source.Where(o => o.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(o => o.Number).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(o => new ProductionOrderSummaryDto(o.Id, o.Number, o.ProductId, o.PlannedQuantity, o.CompletedQuantity, o.Status.ToString(), o.StartDate, o.DueDate, o.WipBalance))
            .ToListAsync(cancellationToken);
        return new PagedResult<ProductionOrderSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}
