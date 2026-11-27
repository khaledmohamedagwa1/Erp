namespace Erp.Modules.Configuration.Domain;

// Policy defaults follow the recommendations in docs/business/03-open-questions-and-missing-requirements.md.
// They are configurable precisely because several of them still await stakeholder confirmation.

public sealed record InventoryPolicy
{
    /// <summary>OQ-INV-4: off by default.</summary>
    public bool AllowNegativeStock { get; init; }

    /// <summary>OQ-INV-2: WeightedAverage or FIFO; product categories may override.</summary>
    public string DefaultCostingMethod { get; init; } = "WeightedAverage";

    /// <summary>OQ-INV-1: LegalEntity or Warehouse.</summary>
    public string ValuationArea { get; init; } = "LegalEntity";

    /// <summary>OQ-INV-3: WithinOpenPeriod or NotAllowed.</summary>
    public string BackdatedPostings { get; init; } = "WithinOpenPeriod";

    public bool EnableBatchTracking { get; init; } = true;

    public bool EnableSerialTracking { get; init; } = true;

    public bool EnableExpiryTracking { get; init; } = true;

    public bool EnableReservations { get; init; } = true;
}

public sealed record SalesPolicy
{
    /// <summary>OQ-SAL-1: AtInvoice or AtDelivery — decides when stock leaves and COGS is posted.</summary>
    public string StockIssueTiming { get; init; } = "AtInvoice";

    public bool EnableCreditLimits { get; init; } = true;

    /// <summary>MR-8: Warn, RequireApproval or Block.</summary>
    public string CreditLimitAction { get; init; } = "RequireApproval";

    public bool AllowCreatorToApprove { get; init; }
}

public sealed record ProcurementPolicy
{
    /// <summary>Receipt-before-invoice (GRNI) vs invoice-before-receipt.</summary>
    public bool ReceiptBeforeInvoice { get; init; } = true;

    public decimal PriceTolerancePercent { get; init; } = 2m;

    public decimal QuantityTolerancePercent { get; init; }
}

public static class PolicySections
{
    public const string Inventory = "inventory";
    public const string Sales = "sales";
    public const string Procurement = "procurement";

    public static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [Inventory] = typeof(InventoryPolicy),
        [Sales] = typeof(SalesPolicy),
        [Procurement] = typeof(ProcurementPolicy),
    };

    public static object Default(string section) => Activator.CreateInstance(Types[section])!;
}
