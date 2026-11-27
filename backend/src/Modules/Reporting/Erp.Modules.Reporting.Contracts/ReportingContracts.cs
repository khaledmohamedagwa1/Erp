namespace Erp.Modules.Reporting.Contracts;

public static class ReportingPermissions
{
    public const string SalesRead = "reporting.sales.read";
    public const string PurchasingRead = "reporting.purchasing.read";
    public const string InventoryRead = "reporting.inventory.read";
    public const string ManufacturingRead = "reporting.manufacturing.read";
    public const string TaxRead = "reporting.tax.read";
    public const string DocumentPrint = "reporting.document.print";
    public const string ViewManage = "reporting.view.manage";
    public const string DashboardRead = "reporting.dashboard.read";
    public const string DashboardManage = "reporting.dashboard.manage";
    public const string ScheduleManage = "reporting.schedule.manage";
    public const string ReadModelRebuild = "reporting.read-model.rebuild";
}

/// <summary>Report areas used in <c>/api/v1/reports/{area}/{report}</c>.</summary>
public static class ReportAreas
{
    public const string Sales = "sales";
    public const string Purchasing = "purchasing";
    public const string Inventory = "inventory";
    public const string Manufacturing = "manufacturing";
    public const string Financial = "financial";
    public const string Tax = "tax";
    public const string People = "people";
}
