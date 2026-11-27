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

    public List<ProductionComponent> Components { get; private set; } = [];

    public List<ProductionOperation> Operations { get; private set; } = [];

    public List<ProductionTransaction> Transactions { get; private set; } = [];

    public decimal OpenQuantity => Math.Max(0, PlannedQuantity - CompletedQuantity - ScrappedQuantity);

    public bool IsOpenForWork => Status is ProductionOrderStatus.Released or ProductionOrderStatus.InProgress or ProductionOrderStatus.PartiallyCompleted or ProductionOrderStatus.Completed;

    public static ProductionOrder Create(Guid companyId, string number, Guid productId, Guid legalEntityId, Guid componentWarehouseId, Guid outputWarehouseId,
        decimal quantity, DateOnly startDate, DateOnly dueDate, string? sourceReference = null)
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
        };
    }

    /// <summary>Snapshots the BOM version and routing: later changes to them do not affect this order.</summary>
    public void Plan(BomVersion version, IReadOnlyDictionary<Guid, decimal> uomFactors, Routing? routing)
    {
        Require(ProductionOrderStatus.Draft);
        BomVersionId = version.Id;
        RoutingId = routing?.Id;
        Components = BomCalculator.Explode(version, PlannedQuantity).Select(r => new ProductionComponent
        {
            LineId = r.LineId,
            ProductId = r.ProductId,
            UomId = r.UomId,
            UomFactor = uomFactors.GetValueOrDefault(r.LineId, 1m),
            IssueMethod = r.IssueMethod,
            QuantityPerUnit = r.QuantityPerUnit,
            RequiredQuantity = r.Quantity,
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
    public void Close(Guid transactionId, DateOnly date, Guid? journalId)
    {
        if (Status is not (ProductionOrderStatus.Completed or ProductionOrderStatus.PartiallyCompleted or ProductionOrderStatus.InProgress))
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
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
