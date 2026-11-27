using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Manufacturing.Domain;

public enum ProductionOrderStatus
{
    Draft = 1,
    Planned = 2,
    Released = 3,
    InProgress = 4,
    PartiallyCompleted = 5,
    Completed = 6,
    Closed = 7,
    Cancelled = 8,
}

public enum ProductionOrderType
{
    Standard = 1,

    /// <summary>Reworks defective units of a product: the defective item itself is the main component; always actual costing.</summary>
    Rework = 2,
}

/// <summary>A component substitution recorded on the order (manual or automatic at release).</summary>
public sealed class ProductionSubstitution
{
    public Guid OriginalLineId { get; set; }

    public Guid SubstituteLineId { get; set; }

    public Guid OriginalProductId { get; set; }

    public Guid SubstituteProductId { get; set; }

    /// <summary>Base units moved from the original component to the substitute.</summary>
    public decimal BaseQuantity { get; set; }

    public DateOnly Date { get; set; }

    public bool Automatic { get; set; }
}

/// <summary>A component requirement snapshotted from the BOM version at planning. Quantities in <see cref="UomId"/> unless noted.</summary>
public sealed class ProductionComponent
{
    public Guid LineId { get; set; }

    public Guid ProductId { get; set; }

    public Guid UomId { get; set; }

    /// <summary>Base units of the component per base unit of the component's UOM.</summary>
    public decimal UomFactor { get; set; } = 1m;

    public IssueMethod IssueMethod { get; set; }

    /// <summary>Component quantity per base unit of output, including scrap and yield.</summary>
    public decimal QuantityPerUnit { get; set; }

    public decimal RequiredQuantity { get; set; }

    /// <summary>Net issued (issues + backflush − returns).</summary>
    public decimal IssuedQuantity { get; set; }

    /// <summary>Net issued cost in base currency (positive).</summary>
    public decimal IssuedCost { get; set; }

    /// <summary>Base quantity reserved at release and not yet consumed or released.</summary>
    public decimal OpenReservedBase { get; set; }

    public Guid? VariantId { get; set; }

    /// <summary>Standard cost per base unit of the component (from the active cost version) for standard-costed orders.</summary>
    public decimal? StandardUnitCost { get; set; }

    /// <summary>Alternative components in priority order (from the BOM line).</summary>
    public List<Guid> Alternatives { get; set; } = [];

    /// <summary>Set on a substitute line: the line it replaces (partly).</summary>
    public Guid? SubstituteForLineId { get; set; }

    public decimal RemainingQuantity => Math.Max(0, RequiredQuantity - IssuedQuantity);
}

/// <summary>A routing operation snapshotted at planning, with reported actuals.</summary>
public sealed class ProductionOperation
{
    public int Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid WorkCenterId { get; set; }

    public decimal PlannedHours { get; set; }

    public decimal ReportedLaborHours { get; set; }

    public decimal ReportedMachineHours { get; set; }

    public decimal ReportedCost { get; set; }
}

public enum ProductionTransactionKind
{
    MaterialIssue = 1,
    MaterialReturn = 2,
    Backflush = 3,
    OperationReport = 4,
    FinishedGoodsReceipt = 5,
    Scrap = 6,
    Variance = 7,
}

/// <summary>Append-only log of everything that moved value in or out of WIP. <see cref="WipAmount"/> is + into WIP, − out of WIP.</summary>
public sealed class ProductionTransaction
{
    public Guid TransactionId { get; set; }

    public ProductionTransactionKind Kind { get; set; }

    public DateOnly Date { get; set; }

    public decimal Quantity { get; set; }

    public decimal WipAmount { get; set; }

    public Guid? JournalId { get; set; }

    public string? Reference { get; set; }

    /// <summary>Operation reports: the work center and the cost split (used by period-end absorption clearing).</summary>
    public Guid? WorkCenterId { get; set; }

    public decimal LaborAmount { get; set; }

    public decimal MachineAmount { get; set; }

    public decimal OverheadAmount { get; set; }
}

[Audited]
public sealed class ProductionOrder : AggregateRoot, ICompanyScoped
{
    private ProductionOrder()
    {
    }

    private ProductionOrder(Guid id, Guid companyId, string number)
        : base(id)
    {
        CompanyId = companyId;
        Number = number;
        Status = ProductionOrderStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public Guid ProductId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid ComponentWarehouseId { get; private set; }

    public Guid OutputWarehouseId { get; private set; }

    /// <summary>Planned output in the product's base unit.</summary>
    public decimal PlannedQuantity { get; private set; }

    public decimal CompletedQuantity { get; private set; }

    public decimal ScrappedQuantity { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public Guid? BomVersionId { get; private set; }

    public Guid? RoutingId { get; private set; }

    public ProductionOrderStatus Status { get; private set; }

    /// <summary>Value currently in work in progress for this order; equals the order's WIP journal movements.</summary>
    public decimal WipBalance { get; private set; }

    public decimal MaterialCost { get; private set; }

    public decimal LaborCost { get; private set; }

    public decimal MachineCost { get; private set; }

    public decimal OverheadCost { get; private set; }

    public decimal ReceivedCost { get; private set; }

    public decimal VarianceAmount { get; private set; }

    public string? SourceReference { get; private set; }

    public ProductionOrderType OrderType { get; private set; } = ProductionOrderType.Standard;

    /// <summary>Output variant (null for products without variants).</summary>
    public Guid? VariantId { get; private set; }

    /// <summary>For rework orders: the order whose output is reworked (optional).</summary>
    public Guid? ReworkOfOrderId { get; private set; }

    public ManufacturingCostingMethod CostingMethod { get; private set; } = ManufacturingCostingMethod.Actual;

    public Guid? CostVersionId { get; private set; }

    public decimal StandardMaterialUnitCost { get; private set; }

    public decimal StandardLaborUnitCost { get; private set; }

    public decimal StandardMachineUnitCost { get; private set; }

    public decimal StandardOverheadUnitCost { get; private set; }

    public decimal StandardConversionUnitCost => MfgRounding.Cost(StandardLaborUnitCost + StandardMachineUnitCost + StandardOverheadUnitCost);

    public decimal StandardUnitCost => MfgRounding.Cost(StandardMaterialUnitCost + StandardConversionUnitCost);

    public decimal MaterialPriceVariance { get; private set; }

    public decimal MaterialUsageVariance { get; private set; }

    public decimal ConversionVariance { get; private set; }

    public decimal OtherVariance { get; private set; }

    public List<ProductionSubstitution> Substitutions { get; private set; } = [];

    public List<ProductionComponent> Components { get; private set; } = [];

    public List<ProductionOperation> Operations { get; private set; } = [];

    public List<ProductionTransaction> Transactions { get; private set; } = [];

    public decimal OpenQuantity => Math.Max(0, PlannedQuantity - CompletedQuantity - ScrappedQuantity);

    public bool IsOpenForWork => Status is ProductionOrderStatus.Released or ProductionOrderStatus.InProgress or ProductionOrderStatus.PartiallyCompleted or ProductionOrderStatus.Completed;

    public static ProductionOrder Create(Guid companyId, string number, Guid productId, Guid legalEntityId, Guid componentWarehouseId, Guid outputWarehouseId,
        decimal quantity, DateOnly startDate, DateOnly dueDate, string? sourceReference = null, Guid? variantId = null,
        ProductionOrderType orderType = ProductionOrderType.Standard, Guid? reworkOfOrderId = null)
    {
        if (quantity <= 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.quantity-invalid", "The planned quantity must be positive."));
        }

        if (dueDate < startDate)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.dates-invalid", "The due date cannot be before the start date."));
        }

        return new ProductionOrder(SequentialGuid.New(), companyId, number)
        {
            ProductId = productId,
            LegalEntityId = legalEntityId,
            ComponentWarehouseId = componentWarehouseId,
            OutputWarehouseId = outputWarehouseId,
            PlannedQuantity = quantity,
            StartDate = startDate,
            DueDate = dueDate,
            SourceReference = sourceReference,
            VariantId = variantId,
            OrderType = orderType,
            ReworkOfOrderId = reworkOfOrderId,
        };
    }

    /// <summary>Snapshots the BOM version and routing: later changes to them do not affect this order.</summary>
    public void Plan(BomVersion version, IReadOnlyDictionary<Guid, decimal> uomFactors, Routing? routing, IReadOnlyDictionary<string, string>? variantAttributes = null)
    {
        Require(ProductionOrderStatus.Draft);
        if (OrderType != ProductionOrderType.Standard)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        BomVersionId = version.Id;
        SetRequirements(BomCalculator.Explode(version, PlannedQuantity, variantAttributes), uomFactors, routing);
    }

    /// <summary>Plans a rework order: the defective product itself (1 : 1) plus optional extra components and the routing's operations.</summary>
    public void PlanRework(IReadOnlyList<ComponentRequirement> components, IReadOnlyDictionary<Guid, decimal> uomFactors, Routing? routing)
    {
        Require(ProductionOrderStatus.Draft);
        if (OrderType != ProductionOrderType.Rework)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        SetRequirements(components, uomFactors, routing);
    }

    /// <summary>Switches a planned order to standard costing with the standards of the active cost version.</summary>
    public void UseStandardCost(Guid costVersionId, StandardCostItem product, IReadOnlyDictionary<Guid, decimal> componentStandards)
    {
        Require(ProductionOrderStatus.Planned);
        if (OrderType != ProductionOrderType.Standard)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.rework-actual-only", "Rework orders are always costed at actual."));
        }

        CostingMethod = ManufacturingCostingMethod.Standard;
        CostVersionId = costVersionId;
        StandardMaterialUnitCost = product.Material;
        StandardLaborUnitCost = product.Labor;
        StandardMachineUnitCost = product.Machine;
        StandardOverheadUnitCost = product.Overhead;
        foreach (var component in Components)
        {
            component.StandardUnitCost = componentStandards.TryGetValue(component.ProductId, out var std) ? std : null;
        }
    }

    private void SetRequirements(IReadOnlyList<ComponentRequirement> requirements, IReadOnlyDictionary<Guid, decimal> uomFactors, Routing? routing)
    {
        RoutingId = routing?.Id;
        Components = requirements.Select(r => new ProductionComponent
        {
            LineId = r.LineId,
            ProductId = r.ProductId,
            VariantId = r.VariantId,
            UomId = r.UomId,
            UomFactor = uomFactors.GetValueOrDefault(r.LineId, 1m),
            IssueMethod = r.IssueMethod,
            QuantityPerUnit = r.QuantityPerUnit,
            RequiredQuantity = r.Quantity,
            Alternatives = r.Alternatives.ToList(),
        }).ToList();
        Operations = routing?.Operations.Select(o => new ProductionOperation
        {
            Sequence = o.Sequence,
            Name = o.Name,
            WorkCenterId = o.WorkCenterId,
            PlannedHours = o.PlannedHours(PlannedQuantity),
        }).ToList() ?? [];
        Status = ProductionOrderStatus.Planned;
    }

    public void Release()
    {
        Require(ProductionOrderStatus.Planned);
        Status = ProductionOrderStatus.Released;
    }

    public void SetReserved(Guid lineId, decimal baseQuantity) => Component(lineId).OpenReservedBase = baseQuantity;

    /// <summary>
    /// Moves <paramref name="baseQuantity"/> (base units) of a component's remaining requirement to an alternative component, 1 : 1
    /// in base units. The substitute gets its own line (in the alternative's base unit) carrying the moved share of the standard
    /// quantity, so standard usage and variances stay correct. Returns the substitute line.
    /// </summary>
    public ProductionComponent Substitute(Guid lineId, Guid alternativeProductId, Guid alternativeBaseUomId, decimal baseQuantity, DateOnly date, bool automatic,
        decimal? alternativeStandardCost)
    {
        if (Status is not (ProductionOrderStatus.Planned or ProductionOrderStatus.Released or ProductionOrderStatus.InProgress or ProductionOrderStatus.PartiallyCompleted))
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        var original = Component(lineId);
        if (!original.Alternatives.Contains(alternativeProductId))
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.not-an-alternative", "The product is not an alternative of this component in the BOM."));
        }

        var remainingBase = MfgRounding.Quantity(original.RemainingQuantity * original.UomFactor);
        if (baseQuantity <= 0 || baseQuantity > remainingBase)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.substitution-quantity-invalid",
                "The quantity must be positive and at most the component's remaining requirement."));
        }

        var fraction = original.RequiredQuantity <= 0 ? 0 : baseQuantity / (original.RequiredQuantity * original.UomFactor);
        var movedPerUnitBase = original.QuantityPerUnit * original.UomFactor * fraction;
        original.RequiredQuantity = MfgRounding.Quantity(original.RequiredQuantity - (baseQuantity / original.UomFactor));
        original.QuantityPerUnit -= movedPerUnitBase / original.UomFactor;

        var existing = Components.FirstOrDefault(c => c.SubstituteForLineId == lineId && c.ProductId == alternativeProductId);
        if (existing is null)
        {
            existing = new ProductionComponent
            {
                LineId = SequentialGuid.New(),
                ProductId = alternativeProductId,
                UomId = alternativeBaseUomId,
                UomFactor = 1m,
                IssueMethod = original.IssueMethod,
                SubstituteForLineId = lineId,
                StandardUnitCost = CostingMethod == ManufacturingCostingMethod.Standard ? alternativeStandardCost : null,
            };
            Components.Add(existing);
        }

        existing.QuantityPerUnit += movedPerUnitBase;
        existing.RequiredQuantity = MfgRounding.Quantity(existing.RequiredQuantity + baseQuantity);
        Substitutions.Add(new ProductionSubstitution
        {
            OriginalLineId = lineId,
            SubstituteLineId = existing.LineId,
            OriginalProductId = original.ProductId,
            SubstituteProductId = alternativeProductId,
            BaseQuantity = baseQuantity,
            Date = date,
            Automatic = automatic,
        });
        return existing;
    }

    public ProductionComponent Component(Guid lineId) =>
        Components.FirstOrDefault(c => c.LineId == lineId)
        ?? throw new DomainException(Error.NotFound("manufacturing.component-not-found", "The component is not part of this production order."));

    /// <summary>Records an issue (positive quantity) or a return (negative quantity) of a component at the given cost.</summary>
    public void RecordMaterial(Guid transactionId, ProductionTransactionKind kind, Guid lineId, decimal quantity, decimal cost, DateOnly date, Guid? journalId)
    {
        EnsureWorkAllowed();
        var component = Component(lineId);
        if (quantity < 0 && component.IssuedQuantity + quantity < 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.return-exceeds-issued", "You cannot return more than was issued."));
        }

        component.IssuedQuantity = MfgRounding.Quantity(component.IssuedQuantity + quantity);
        component.IssuedCost = MfgRounding.Money(component.IssuedCost + cost);
        if (quantity > 0)
        {
            component.OpenReservedBase = Math.Max(0, MfgRounding.Quantity(component.OpenReservedBase - (quantity * component.UomFactor)));
        }

        MaterialCost = MfgRounding.Money(MaterialCost + cost);
        AddTransaction(transactionId, kind, date, quantity, cost, journalId, component.ProductId.ToString());
        MarkInProgress();
    }

    public void RecordOperation(Guid transactionId, int sequence, decimal laborHours, decimal machineHours, decimal labor, decimal machine, decimal overhead, DateOnly date, Guid? journalId)
    {
        EnsureWorkAllowed();
        var operation = Operations.FirstOrDefault(o => o.Sequence == sequence)
            ?? throw new DomainException(Error.NotFound("manufacturing.operation-not-found", "The operation is not part of this production order."));
        operation.ReportedLaborHours = MfgRounding.Quantity(operation.ReportedLaborHours + laborHours);
        operation.ReportedMachineHours = MfgRounding.Quantity(operation.ReportedMachineHours + machineHours);
        var total = labor + machine + overhead;
        operation.ReportedCost = MfgRounding.Money(operation.ReportedCost + total);
        LaborCost = MfgRounding.Money(LaborCost + labor);
        MachineCost = MfgRounding.Money(MachineCost + machine);
        OverheadCost = MfgRounding.Money(OverheadCost + overhead);
        AddTransaction(transactionId, ProductionTransactionKind.OperationReport, date, laborHours + machineHours, total, journalId, $"op {sequence}");
        var transaction = Transactions[^1];
        transaction.WorkCenterId = operation.WorkCenterId;
        transaction.LaborAmount = labor;
        transaction.MachineAmount = machine;
        transaction.OverheadAmount = overhead;
        MarkInProgress();
    }

    /// <summary>
    /// Unit cost for receiving <paramref name="quantity"/>: the WIP still to relieve spread over the output still to come.
    /// The receipt that completes the order relieves all remaining WIP.
    /// </summary>
    public decimal ReceiptUnitCost(decimal quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.quantity-invalid", "The quantity must be positive."));
        }

        if (CostingMethod == ManufacturingCostingMethod.Standard)
        {
            return StandardUnitCost;
        }

        var spread = Math.Max(OpenQuantity, quantity);
        return WipBalance <= 0 ? 0 : MfgRounding.Cost(WipBalance / spread);
    }

    /// <summary>Backflush quantities (component UOM) due when the output reaches <paramref name="outputDone"/> (completed + scrapped).</summary>
    public IReadOnlyList<(ProductionComponent Component, decimal Quantity)> BackflushDue(decimal outputDone) =>
        Components.Where(c => c.IssueMethod == IssueMethod.Backflush)
            .Select(c => (c, MfgRounding.Quantity(Math.Min(c.RequiredQuantity, c.QuantityPerUnit * outputDone) - c.IssuedQuantity)))
            .Where(x => x.Item2 > 0)
            .ToList();

    public void RecordReceipt(Guid transactionId, decimal quantity, decimal cost, DateOnly date, Guid? journalId)
    {
        EnsureWorkAllowed();
        CompletedQuantity = MfgRounding.Quantity(CompletedQuantity + quantity);
        ReceivedCost = MfgRounding.Money(ReceivedCost + cost);
        AddTransaction(transactionId, ProductionTransactionKind.FinishedGoodsReceipt, date, quantity, -cost, journalId, null);
        Status = CompletedQuantity + ScrappedQuantity >= PlannedQuantity ? ProductionOrderStatus.Completed : ProductionOrderStatus.PartiallyCompleted;
    }

    /// <summary>Normal scrap: the scrapped units carry no cost of their own, so remaining WIP is spread over good output.</summary>
    public void RecordScrap(Guid transactionId, decimal quantity, DateOnly date, string? reason)
    {
        EnsureWorkAllowed();
        if (quantity <= 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.quantity-invalid", "The quantity must be positive."));
        }

        ScrappedQuantity = MfgRounding.Quantity(ScrappedQuantity + quantity);
        AddTransaction(transactionId, ProductionTransactionKind.Scrap, date, quantity, 0, null, reason);
        MarkInProgress();
        if (CompletedQuantity + ScrappedQuantity >= PlannedQuantity && CompletedQuantity > 0)
        {
            Status = ProductionOrderStatus.Completed;
        }
    }

    /// <summary>Closes the order: the residual WIP (already journalled by the caller) is recorded as variance; WIP ends at zero.</summary>
    public void Close(Guid transactionId, DateOnly date, Guid? journalId, StandardVariances? variances = null)
    {
        if (Status is not (ProductionOrderStatus.Completed or ProductionOrderStatus.PartiallyCompleted or ProductionOrderStatus.InProgress))
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        if (variances is not null)
        {
            MaterialPriceVariance = variances.MaterialPrice;
            MaterialUsageVariance = variances.MaterialUsage;
            ConversionVariance = variances.Conversion;
            OtherVariance = variances.Other;
        }

        if (WipBalance != 0)
        {
            VarianceAmount = MfgRounding.Money(VarianceAmount + WipBalance);
            AddTransaction(transactionId, ProductionTransactionKind.Variance, date, 0, -WipBalance, journalId, null);
        }

        foreach (var component in Components)
        {
            component.OpenReservedBase = 0;
        }

        Status = ProductionOrderStatus.Closed;
    }

    public void Cancel()
    {
        if (Status is not (ProductionOrderStatus.Draft or ProductionOrderStatus.Planned or ProductionOrderStatus.Released) || Transactions.Count > 0)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        foreach (var component in Components)
        {
            component.OpenReservedBase = 0;
        }

        Status = ProductionOrderStatus.Cancelled;
    }

    private void AddTransaction(Guid id, ProductionTransactionKind kind, DateOnly date, decimal quantity, decimal wipAmount, Guid? journalId, string? reference)
    {
        WipBalance = MfgRounding.Money(WipBalance + wipAmount);
        Transactions.Add(new ProductionTransaction
        {
            TransactionId = id,
            Kind = kind,
            Date = date,
            Quantity = quantity,
            WipAmount = wipAmount,
            JournalId = journalId,
            Reference = reference,
        });
    }

    private void EnsureWorkAllowed()
    {
        if (!IsOpenForWork)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }
    }

    private void MarkInProgress()
    {
        if (Status == ProductionOrderStatus.Released)
        {
            Status = ProductionOrderStatus.InProgress;
        }
    }

    private void Require(ProductionOrderStatus status)
    {
        if (Status != status)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }
    }
}
