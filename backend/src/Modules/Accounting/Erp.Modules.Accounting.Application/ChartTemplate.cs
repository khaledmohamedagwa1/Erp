using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;

namespace Erp.Modules.Accounting.Application;

public sealed record TemplateAccount(string Code, string? Parent, string NameEn, string NameAr, AccountType Type, bool Posting,
    ControlSubledger Control = ControlSubledger.None);

/// <summary>
/// Generic IFRS-style chart in English and Arabic (OQ-ACC-1) plus a default posting rule for every role.
/// Jurisdiction-specific charts are data and can be added later.
/// </summary>
public static class GenericChartTemplate
{
    public static readonly IReadOnlyList<TemplateAccount> Accounts =
    [
        new("1", null, "Assets", "الأصول", AccountType.Asset, false),
        new("11", "1", "Current assets", "الأصول المتداولة", AccountType.Asset, false),
        new("1110", "11", "Cash on hand", "النقدية بالصندوق", AccountType.Asset, true),
        new("1120", "11", "Banks – current accounts", "البنوك – حسابات جارية", AccountType.Asset, true),
        new("1130", "11", "Internal transfers clearing", "حساب وسيط التحويلات الداخلية", AccountType.Asset, true),
        new("1210", "11", "Trade receivables", "العملاء – ذمم مدينة", AccountType.Asset, true, ControlSubledger.Receivables),
        new("1220", "11", "Withholding tax receivable", "ضريبة الخصم المستحقة لنا", AccountType.Asset, true),
        new("1310", "11", "Inventory", "المخزون", AccountType.Asset, true, ControlSubledger.Inventory),
        new("1320", "11", "Inventory in transit", "مخزون بالطريق", AccountType.Asset, true, ControlSubledger.Inventory),
        new("1350", "11", "Work in progress", "إنتاج تحت التشغيل", AccountType.Asset, true, ControlSubledger.Wip),
        new("1360", "11", "Goods invoiced not received", "بضاعة مفوترة لم تستلم", AccountType.Asset, true, ControlSubledger.Gini),
        new("1410", "11", "Input VAT recoverable", "ضريبة القيمة المضافة – مدخلات", AccountType.Asset, true),
        new("1420", "11", "Supplier advances", "دفعات مقدمة للموردين", AccountType.Asset, true, ControlSubledger.Payables),
        new("12", "1", "Non-current assets", "الأصول غير المتداولة", AccountType.Asset, false),
        new("1510", "12", "Property, plant and equipment", "الممتلكات والآلات والمعدات", AccountType.Asset, true),
        new("1590", "12", "Accumulated depreciation", "مجمع الإهلاك", AccountType.Asset, true),

        new("2", null, "Liabilities", "الالتزامات", AccountType.Liability, false),
        new("21", "2", "Current liabilities", "الالتزامات المتداولة", AccountType.Liability, false),
        new("2110", "21", "Trade payables", "الموردون – ذمم دائنة", AccountType.Liability, true, ControlSubledger.Payables),
        new("2150", "21", "Goods received not invoiced", "بضاعة مستلمة غير مفوترة", AccountType.Liability, true, ControlSubledger.Grni),
        new("2160", "21", "Customer advances", "دفعات مقدمة من العملاء", AccountType.Liability, true, ControlSubledger.Receivables),
        new("2310", "21", "Output VAT payable", "ضريبة القيمة المضافة – مخرجات", AccountType.Liability, true),
        new("2320", "21", "Withholding tax payable", "ضريبة الخصم والإضافة المستحقة", AccountType.Liability, true),
        new("2410", "21", "Accrued expenses", "مصروفات مستحقة", AccountType.Liability, true),
        new("2420", "21", "Landed cost clearing", "حساب وسيط تكاليف الاستيراد", AccountType.Liability, true),

        new("3", null, "Equity", "حقوق الملكية", AccountType.Equity, false),
        new("3100", "3", "Share capital", "رأس المال", AccountType.Equity, true),
        new("3200", "3", "Retained earnings", "الأرباح المحتجزة", AccountType.Equity, true),
        new("3300", "3", "Opening balance equity", "أرصدة افتتاحية", AccountType.Equity, true),

        new("4", null, "Revenue", "الإيرادات", AccountType.Revenue, false),
        new("4110", "4", "Sales revenue – goods", "إيرادات مبيعات البضاعة", AccountType.Revenue, true),
        new("4120", "4", "Sales returns", "مردودات المبيعات", AccountType.Revenue, true),
        new("4130", "4", "Sales discounts", "خصم المبيعات", AccountType.Revenue, true),
        new("4150", "4", "Service revenue", "إيرادات الخدمات", AccountType.Revenue, true),
        new("4910", "4", "Inventory adjustment gain", "أرباح تسوية المخزون", AccountType.Revenue, true),
        new("4920", "4", "Realized foreign exchange gain", "أرباح فروق عملة محققة", AccountType.Revenue, true),
        new("4930", "4", "Unrealized foreign exchange gain", "أرباح فروق عملة غير محققة", AccountType.Revenue, true),
        new("4940", "4", "Gain or loss on disposal of assets", "أرباح وخسائر بيع الأصول", AccountType.Revenue, true),

        new("5", null, "Expenses", "المصروفات", AccountType.Expense, false),
        new("5110", "5", "Cost of goods sold", "تكلفة البضاعة المباعة", AccountType.Expense, true),
        new("5120", "5", "Purchase price variance", "فروق أسعار الشراء", AccountType.Expense, true),
        new("5130", "5", "Inventory cost true-up", "تسوية تكلفة المخزون", AccountType.Expense, true),
        new("5140", "5", "Production variance", "انحرافات الإنتاج", AccountType.Expense, true),
        new("5150", "5", "Abnormal scrap", "هالك غير عادي", AccountType.Expense, true),
        new("5160", "5", "Non-recoverable input tax", "ضريبة مدخلات غير قابلة للخصم", AccountType.Expense, true),
        new("5210", "5", "Inventory adjustment loss", "خسائر تسوية المخزون", AccountType.Expense, true),
        new("5310", "5", "Rent expense", "مصروف الإيجار", AccountType.Expense, true),
        new("5320", "5", "Salaries and wages", "الرواتب والأجور", AccountType.Expense, true),
        new("5410", "5", "Bank charges", "مصروفات بنكية", AccountType.Expense, true),
        new("5420", "5", "Cash over and short", "عجز وزيادة النقدية", AccountType.Expense, true),
        new("5510", "5", "Depreciation expense", "مصروف الإهلاك", AccountType.Expense, true),
        new("5610", "5", "Realized foreign exchange loss", "خسائر فروق عملة محققة", AccountType.Expense, true),
        new("5620", "5", "Unrealized foreign exchange loss", "خسائر فروق عملة غير محققة", AccountType.Expense, true),
        new("5630", "5", "Foreign exchange rounding differences", "فروق تقريب العملات", AccountType.Expense, true),
        new("5710", "5", "Applied labour (production)", "عمالة محملة على الإنتاج", AccountType.Expense, true),
        new("5720", "5", "Applied machine cost (production)", "تكلفة آلات محملة على الإنتاج", AccountType.Expense, true),
        new("5730", "5", "Applied overhead (production)", "تكاليف غير مباشرة محملة على الإنتاج", AccountType.Expense, true),
    ];

    /// <summary>
    /// Cash-flow categories of the template's balance-sheet posting accounts. Accounts not listed are working-capital items
    /// (operating). Accumulated depreciation is operating on purpose: in the indirect method its increase is the depreciation add-back.
    /// Opening balance equity is financing (owners' funding of the opening position).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CashFlowCategory> CashFlowCategories = new Dictionary<string, CashFlowCategory>(StringComparer.Ordinal)
    {
        ["1110"] = CashFlowCategory.Cash,
        ["1120"] = CashFlowCategory.Cash,
        ["1510"] = CashFlowCategory.Investing,
        ["3100"] = CashFlowCategory.Financing,
        ["3200"] = CashFlowCategory.Financing,
        ["3300"] = CashFlowCategory.Financing,
    };

    public static CashFlowCategory CategoryFor(TemplateAccount account) =>
        account.Type is AccountType.Revenue or AccountType.Expense || !account.Posting
            ? CashFlowCategory.None
            : CashFlowCategories.GetValueOrDefault(account.Code, CashFlowCategory.Operating);

    public static readonly IReadOnlyDictionary<string, string> RoleAccounts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PostingRoles.CustomerReceivable] = "1210",
        [PostingRoles.CustomerAdvance] = "2160",
        [PostingRoles.SupplierPayable] = "2110",
        [PostingRoles.SupplierAdvance] = "1420",
        [PostingRoles.SalesRevenue] = "4110",
        [PostingRoles.SalesReturns] = "4120",
        [PostingRoles.SalesDiscount] = "4130",
        [PostingRoles.ServiceRevenue] = "4150",
        [PostingRoles.OutputTax] = "2310",
        [PostingRoles.InputTax] = "1410",
        [PostingRoles.NonRecoverableInputTax] = "5160",
        [PostingRoles.WithholdingTaxPayable] = "2320",
        [PostingRoles.WithholdingTaxReceivable] = "1220",
        [PostingRoles.Inventory] = "1310",
        [PostingRoles.CostOfGoodsSold] = "5110",
        [PostingRoles.InventoryAdjustmentLoss] = "5210",
        [PostingRoles.InventoryAdjustmentGain] = "4910",
        [PostingRoles.InventoryInTransit] = "1320",
        [PostingRoles.GoodsReceivedNotInvoiced] = "2150",
        [PostingRoles.GoodsInvoicedNotReceived] = "1360",
        [PostingRoles.PurchasePriceVariance] = "5120",
        [PostingRoles.LandedCostClearing] = "2420",
        [PostingRoles.OpeningBalanceEquity] = "3300",
        [PostingRoles.CostTrueUp] = "5130",
        [PostingRoles.WorkInProgress] = "1350",
        [PostingRoles.AppliedLabor] = "5710",
        [PostingRoles.AppliedMachine] = "5720",
        [PostingRoles.AppliedOverhead] = "5730",
        [PostingRoles.ProductionVariance] = "5140",
        [PostingRoles.AbnormalScrap] = "5150",
        [PostingRoles.CashAccount] = "1110",
        [PostingRoles.BankAccount] = "1120",
        [PostingRoles.BankCharges] = "5410",
        [PostingRoles.CashOverShort] = "5420",
        [PostingRoles.InternalTransferClearing] = "1130",
        [PostingRoles.RealizedFxGain] = "4920",
        [PostingRoles.RealizedFxLoss] = "5610",
        [PostingRoles.UnrealizedFxGain] = "4930",
        [PostingRoles.UnrealizedFxLoss] = "5620",
        [PostingRoles.FxRoundingDifference] = "5630",
        [PostingRoles.FixedAssetCost] = "1510",
        [PostingRoles.AccumulatedDepreciation] = "1590",
        [PostingRoles.DepreciationExpense] = "5510",
        [PostingRoles.AssetDisposalGainLoss] = "4940",
        [PostingRoles.RetainedEarnings] = "3200",
    };
}
