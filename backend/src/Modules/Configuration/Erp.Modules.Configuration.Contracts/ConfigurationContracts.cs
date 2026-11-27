namespace Erp.Modules.Configuration.Contracts;

/// <summary>Called when a company is created. Runs inside the caller's transaction.</summary>
public interface IConfigurationProvisioning
{
    Task InitializeCompanyAsync(Guid companyId, string businessPreset, CancellationToken cancellationToken = default);
}

public static class ModuleCodes
{
    public const string Organization = "organization";
    public const string Identity = "identity";
    public const string Configuration = "configuration";
    public const string Accounting = "accounting";
    public const string Inventory = "inventory";
    public const string Sales = "sales";
    public const string Procurement = "procurement";
    public const string Pos = "pos";
    public const string Manufacturing = "manufacturing";
    public const string FixedAssets = "fixed-assets";
    public const string Commissions = "commissions";
    public const string MultiCurrency = "multi-currency";
}

public static class BusinessPresets
{
    public const string Retail = "retail";
    public const string Wholesale = "wholesale";
    public const string Trading = "trading";
    public const string Distribution = "distribution";
    public const string Manufacturing = "manufacturing";
    public const string Services = "services";

    public static readonly IReadOnlyCollection<string> All = [Retail, Wholesale, Trading, Distribution, Manufacturing, Services];
}

public static class ConfigurationPermissions
{
    public const string Read = "configuration.read";
    public const string ModuleManage = "configuration.module.manage";
    public const string PolicyManage = "configuration.policy.manage";
}

/// <summary>
/// Read-only copies of the business policy sections for other modules (same JSON shape as the Configuration domain records).
/// Defaults match the decisions log, so a company without a stored section behaves as decided.
/// </summary>
public sealed record InventoryPolicySettings
{
    public const string Section = "inventory";

    public bool AllowNegativeStock { get; init; }

    public string DefaultCostingMethod { get; init; } = "WeightedAverage";

    public string ValuationArea { get; init; } = "LegalEntity";

    public string BackdatedPostings { get; init; } = "WithinOpenPeriod";

    public bool EnableBatchTracking { get; init; } = true;

    public bool EnableSerialTracking { get; init; } = true;

    public bool EnableExpiryTracking { get; init; } = true;

    public bool EnableReservations { get; init; } = true;

    public bool AutoRecostBackdatedPostings { get; init; }
}

public sealed record SalesPolicySettings
{
    public const string Section = "sales";

    public string StockIssueTiming { get; init; } = "AtInvoice";

    public bool EnableCreditLimits { get; init; } = true;

    public string CreditLimitAction { get; init; } = "RequireApproval";

    public bool AllowCreatorToApprove { get; init; }
}

public sealed record ProcurementPolicySettings
{
    public const string Section = "procurement";

    public bool ReceiptBeforeInvoice { get; init; } = true;

    public decimal PriceTolerancePercent { get; init; } = 2m;

    public decimal QuantityTolerancePercent { get; init; }

    public string ReceiptValuationRate { get; init; } = "ReceiptDate";
}

/// <summary>Reads a company's business policies (any company of the current tenant).</summary>
public interface IPolicyReader
{
    Task<InventoryPolicySettings> GetInventoryPolicyAsync(Guid companyId, CancellationToken cancellationToken = default);

    Task<SalesPolicySettings> GetSalesPolicyAsync(Guid companyId, CancellationToken cancellationToken = default);

    Task<ProcurementPolicySettings> GetProcurementPolicyAsync(Guid companyId, CancellationToken cancellationToken = default);
}
