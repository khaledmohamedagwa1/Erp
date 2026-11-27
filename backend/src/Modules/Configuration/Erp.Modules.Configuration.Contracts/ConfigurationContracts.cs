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
