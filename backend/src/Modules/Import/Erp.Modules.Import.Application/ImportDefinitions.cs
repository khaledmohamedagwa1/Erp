using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Import.Domain;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Import.Application;

internal static class Col
{
    public static ImportColumn Code(string description, string example, params string[] aliases) =>
        new("code", "Code", "الرمز", true, description, example, ["كود", .. aliases]);

    public static readonly ImportColumn NameEn = new("nameEn", "Name (English)", "الاسم بالإنجليزية", true, "Name in English.", "Main warehouse shelf", "Name", "English name", "الاسم الإنجليزي");

    public static readonly ImportColumn NameAr = new("nameAr", "Name (Arabic)", "الاسم بالعربية", true, "Name in Arabic.", "رف المستودع الرئيسي", "Arabic name", "الاسم العربي", "الاسم");

    public static readonly ImportColumn Currency = new("currency", "Currency", "العملة", false, "ISO currency code; blank = the default currency.", "EGP");

    public static readonly ImportColumn TaxNumber = new("taxRegistrationNo", "Tax registration no", "الرقم الضريبي", false, "Tax registration number.", "123-456-789",
        "Tax number", "VAT number", "TRN", "رقم التسجيل الضريبي");

    public static readonly ImportColumn PaymentTerms = new("paymentTerms", "Payment terms code", "شروط الدفع", false, "Code of existing payment terms.", "NET30",
        "Payment terms", "Terms", "رمز شروط الدفع");

    public static readonly ImportColumn PostingGroup = new("postingGroup", "Posting group", "مجموعة الترحيل", false, "Party posting group (blank = default).", "DOMESTIC");

    public static readonly ImportColumn Description = new("description", "Description", "البيان", false, "Free text.", "Migrated from the old system", "Notes", "ملاحظات", "الوصف");

    public static readonly ImportColumn DocumentNumber = new("documentNumber", "Document number", "رقم المستند", true, "Invoice/bill number in the old system (unique per party).",
        "INV-2025-0412", "Invoice number", "Bill number", "Reference", "رقم الفاتورة", "المرجع");

    public static readonly ImportColumn DocumentDate = new("documentDate", "Document date", "تاريخ المستند", true, "Original document date (yyyy-MM-dd).", "2025-11-15",
        "Invoice date", "Bill date", "Date", "تاريخ الفاتورة", "التاريخ");

    public static readonly ImportColumn DueDate = new("dueDate", "Due date", "تاريخ الاستحقاق", false, "Due date for aging (blank = document date).", "2025-12-15");

    public static readonly ImportColumn ExchangeRate = new("exchangeRate", "Exchange rate", "سعر الصرف", false, "Functional currency units per one document currency unit (blank = recorded rate).", "1");

    public static readonly ImportColumn OpenAmount = new("amount", "Open amount", "الرصيد المفتوح", true,
        "Amount still open, in the document currency. Negative = open credit (credit note / advance).", "1500.00", "Amount", "Balance", "المبلغ", "الرصيد");

    public static readonly ImportColumn Sku = new("sku", "SKU", "رمز الصنف", true, "Product code (SKU).", "SHELF-01", "Product code", "Item code", "Product", "كود الصنف", "الصنف");
}

// ============================ Chart of accounts ============================
public sealed record AccountRow(string Code, string NameEn, string NameAr, string? Type, string ParentCode, bool Posting, bool AllowManual, string? CashFlow);

public sealed class ChartOfAccountsImport(IAccountingImportService accounting) : ImportDefinition<AccountRow>
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.ChartOfAccounts, "Chart of accounts", "دليل الحسابات", AccountingPermissions.AccountManage,
        false, false,
        [
            Col.Code("Account code (unique).", "1115", "Account code", "رمز الحساب", "رقم الحساب"),
            Col.NameEn,
            Col.NameAr,
            new("parentCode", "Parent code", "الحساب الأب", true, "Code of the parent group account (e.g. a template group such as 11).", "11",
                "Parent", "Parent account", "رمز الحساب الأب", "الحساب الرئيسي"),
            new("type", "Type", "النوع", false, "Asset, Liability, Equity, Revenue or Expense (blank = parent's type).", "Asset", "Account type", "نوع الحساب"),
            new("posting", "Posting", "حساب ترحيل", false, "yes = posting (leaf) account, no = group account. Default yes.", "yes", "Is posting", "Posting account", "قابل للترحيل"),
            new("allowManualPosting", "Allow manual posting", "يسمح بالقيود اليدوية", false, "Default yes.", "yes", "Manual posting"),
            new("cashFlowCategory", "Cash-flow category", "تصنيف التدفق النقدي", false, "Operating, Investing, Financing, Cash or None.", "Cash", "Cash flow"),
        ]);

    protected override AccountRow? Parse(ImportRowValues v, ImportContext context) =>
        new(v.RequiredText("code", 20), v.RequiredText("nameEn", 200), v.RequiredText("nameAr", 200), v.Text("type", 20), v.RequiredText("parentCode", 20),
            v.Bool("posting", true), v.Bool("allowManualPosting", true), v.Text("cashFlowCategory", 20));

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<AccountRow>> rows, ImportContext context, CancellationToken ct)
    {
        var r = rows[0].Data;
        return await accounting.CreateAccountAsync(new AccountImportRequest(r.Code, r.NameEn, r.NameAr, r.Type, r.ParentCode, r.Posting, r.AllowManual, r.CashFlow), ct);
    }
}

// ============================ Products ============================
public sealed record ProductRow(ProductImportRequest Request);

public sealed class ProductsImport(ICatalogImportService catalog) : ImportDefinition<ProductRow>
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.Products, "Products", "الأصناف", CatalogPermissions.ProductManage, false, false,
        [
            Col.Sku,
            Col.NameEn,
            Col.NameAr,
            new("type", "Type", "النوع", false, "Stockable, Consumable, Service, Bundle or RawMaterial. Default Stockable.", "Stockable", "Product type", "نوع الصنف"),
            new("category", "Category code", "رمز الفئة", true, "Code of an existing product category.", "GENERAL", "Category", "الفئة"),
            new("baseUom", "Base unit", "الوحدة الأساسية", true, "Code of an existing unit of measure.", "PCS", "Unit", "UoM", "Base UoM", "الوحدة"),
            new("brand", "Brand code", "العلامة التجارية", false, "Code of an existing brand.", "", "Brand"),
            new("tracking", "Tracking", "التتبع", false, "None, Batch or Serial. Default None.", "None", "Tracking mode"),
            new("expiryTracked", "Expiry tracked", "تتبع الصلاحية", false, "yes/no. Default no.", "no", "Expiry"),
            new("taxCategory", "Tax category", "الفئة الضريبية", false, "Default STANDARD.", "STANDARD"),
            new("barcode", "Barcode", "الباركود", false, "Barcode of the base unit.", "6221234567890", "EAN", "UPC"),
            new("priceList", "Price list code", "رمز قائمة الأسعار", false, "Existing price list for the price below.", "RETAIL", "Price list", "قائمة الأسعار"),
            new("price", "Price", "السعر", false, "Price per base unit in that price list.", "250.00", "Unit price", "سعر البيع"),
        ]);

    protected override ProductRow? Parse(ImportRowValues v, ImportContext context)
    {
        var price = v.Decimal("price");
        if (price is < 0)
        {
            v.AddError("price", "import.negative-price", "The price cannot be negative.");
        }

        return new ProductRow(new ProductImportRequest(v.RequiredText("sku", 40), v.RequiredText("nameEn", 200), v.RequiredText("nameAr", 200),
            v.Text("type", 20) ?? "Stockable", v.RequiredText("category", 30), v.RequiredText("baseUom", 20), v.Text("brand", 30), v.Text("tracking", 10),
            v.Bool("expiryTracked", false), v.Text("taxCategory", 30), v.Text("barcode", 64), v.Text("priceList", 30), price));
    }

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<ProductRow>> rows, ImportContext context, CancellationToken ct) =>
        await catalog.CreateProductAsync(rows[0].Data.Request, ct);
}

// ============================ Customers / suppliers ============================
public sealed record CustomerRow(CustomerImportRequest Request);

public sealed class CustomersImport(IPartyImportService parties, IOrganizationQueries organization) : ImportDefinition<CustomerRow>
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.Customers, "Customers", "العملاء", PartiesPermissions.CustomerManage, false, false,
        [
            Col.Code("Customer code (unique).", "C-0001", "Customer code", "رمز العميل"),
            Col.NameEn,
            Col.NameAr,
            new("kind", "Kind", "النوع", false, "Individual or Business. Default Business.", "Business", "Customer kind", "نوع العميل"),
            Col.TaxNumber,
            Col.Currency,
            new("customerGroup", "Customer group code", "مجموعة العملاء", false, "Code of an existing customer group.", "", "Customer group", "Group", "المجموعة"),
            Col.PaymentTerms,
            Col.PostingGroup,
            new("creditLimit", "Credit limit", "حد الائتمان", false, "Credit limit in the customer currency.", "50000", "Limit"),
            new("creditAction", "Credit action", "إجراء الائتمان", false, "Warn, RequireApproval or Block when the limit is exceeded.", "Warn"),
        ]);

    protected override CustomerRow? Parse(ImportRowValues v, ImportContext context)
    {
        var limit = v.Decimal("creditLimit");
        if (limit is < 0)
        {
            v.AddError("creditLimit", "import.negative-amount", "The credit limit cannot be negative.");
        }

        return new CustomerRow(new CustomerImportRequest(v.RequiredText("code", 30), v.Text("kind", 20) ?? "Business", v.RequiredText("nameEn", 200),
            v.RequiredText("nameAr", 200), v.Text("taxRegistrationNo", 50), v.Text("currency", 3) ?? string.Empty, v.Text("customerGroup", 30),
            v.Text("paymentTerms", 30), v.Text("postingGroup", 30), limit, v.Text("creditAction", 30)));
    }

    protected override async Task<(IReadOnlyList<ImportRow<CustomerRow>> Rows, IReadOnlyList<ImportRowError> Errors)> ResolveAsync(
        IReadOnlyList<ImportRow<CustomerRow>> rows, ImportContext context, CancellationToken cancellationToken)
    {
        var currency = await DefaultCurrency.OfCompanyAsync(organization, context.CompanyId, cancellationToken);
        return (rows.Select(r => r.Data.Request.Currency.Length > 0 ? r : r with { Data = new CustomerRow(r.Data.Request with { Currency = currency }) }).ToList(), []);
    }

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<CustomerRow>> rows, ImportContext context, CancellationToken ct) =>
        await parties.CreateCustomerAsync(rows[0].Data.Request, ct);
}

public sealed record SupplierRow(SupplierImportRequest Request);

public sealed class SuppliersImport(IPartyImportService parties, IOrganizationQueries organization) : ImportDefinition<SupplierRow>
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.Suppliers, "Suppliers", "الموردون", PartiesPermissions.SupplierManage, false, false,
        [
            Col.Code("Supplier code (unique).", "S-0001", "Supplier code", "رمز المورد"),
            Col.NameEn,
            Col.NameAr,
            Col.TaxNumber,
            Col.Currency,
            Col.PaymentTerms,
            Col.PostingGroup,
        ]);

    protected override SupplierRow? Parse(ImportRowValues v, ImportContext context) =>
        new(new SupplierImportRequest(v.RequiredText("code", 30), v.RequiredText("nameEn", 200), v.RequiredText("nameAr", 200), v.Text("taxRegistrationNo", 50),
            v.Text("currency", 3) ?? string.Empty, v.Text("paymentTerms", 30), v.Text("postingGroup", 30)));

    protected override async Task<(IReadOnlyList<ImportRow<SupplierRow>> Rows, IReadOnlyList<ImportRowError> Errors)> ResolveAsync(
        IReadOnlyList<ImportRow<SupplierRow>> rows, ImportContext context, CancellationToken cancellationToken)
    {
        var currency = await DefaultCurrency.OfCompanyAsync(organization, context.CompanyId, cancellationToken);
        return (rows.Select(r => r.Data.Request.Currency.Length > 0 ? r : r with { Data = new SupplierRow(r.Data.Request with { Currency = currency }) }).ToList(), []);
    }

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<SupplierRow>> rows, ImportContext context, CancellationToken ct) =>
        await parties.CreateSupplierAsync(rows[0].Data.Request, ct);
}

internal static class DefaultCurrency
{
    public static async Task<string> OfCompanyAsync(IOrganizationQueries organization, Guid companyId, CancellationToken ct) =>
        (await organization.GetCompaniesAsync([companyId], ct)).FirstOrDefault()?.BaseCurrency ?? string.Empty;
}

// ============================ Opening stock ============================
public sealed record StockRow(string WarehouseCode, string Sku, string? UomCode, decimal Quantity, decimal UnitCost, string? Batch, DateOnly? ManufactureDate,
    DateOnly? ExpiryDate, IReadOnlyList<string>? Serials, Guid WarehouseId = default, Guid ProductId = default, Guid UomId = default);

public sealed class OpeningStockImport(IInventoryImportService inventory, IOrganizationLookups organization, ICatalogImportService catalogImport, ICatalogQueries catalog)
    : ImportDefinition<StockRow>
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.OpeningStock, "Opening stock", "أرصدة المخزون الافتتاحية", InventoryPermissions.DocumentPost,
        false, true,
        [
            new("warehouse", "Warehouse code", "رمز المستودع", true, "Code of the warehouse. One opening-stock document is posted per warehouse.", "MAIN", "Warehouse", "المستودع"),
            Col.Sku,
            new("uom", "Unit", "الوحدة", false, "Unit of the quantity and cost (blank = base unit).", "PCS", "UoM", "Unit code"),
            new("quantity", "Quantity", "الكمية", true, "Quantity on hand (positive).", "40", "Qty"),
            new("unitCost", "Unit cost", "تكلفة الوحدة", true, "Cost per unit in functional currency.", "120.50", "Cost", "التكلفة"),
            new("batch", "Batch number", "رقم التشغيلة", false, "Required for batch-tracked products.", "", "Batch", "Lot", "التشغيلة"),
            new("manufactureDate", "Manufacture date", "تاريخ الإنتاج", false, "yyyy-MM-dd.", ""),
            new("expiryDate", "Expiry date", "تاريخ الانتهاء", false, "Required for expiry-tracked products (yyyy-MM-dd).", "", "Expiry", "تاريخ الصلاحية"),
            new("serials", "Serial numbers", "الأرقام التسلسلية", false, "Serial-tracked products: one serial per unit, separated by ';'.", "", "Serials"),
        ]);

    protected override StockRow? Parse(ImportRowValues v, ImportContext context)
    {
        var quantity = v.RequiredDecimal("quantity");
        if (quantity <= 0 && !v.HasErrors)
        {
            v.AddError("quantity", "import.quantity-not-positive", "The quantity must be positive.");
        }

        var cost = v.RequiredDecimal("unitCost");
        if (cost < 0)
        {
            v.AddError("unitCost", "import.negative-amount", "The unit cost cannot be negative.");
        }

        var serials = v.Text("serials", 4000)?.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new StockRow(v.RequiredText("warehouse", 30), v.RequiredText("sku", 40), v.Text("uom", 20), quantity, cost, v.Text("batch", 60),
            v.Date("manufactureDate"), v.Date("expiryDate"), serials is { Length: > 0 } ? serials : null);
    }

    protected override async Task<(IReadOnlyList<ImportRow<StockRow>> Rows, IReadOnlyList<ImportRowError> Errors)> ResolveAsync(
        IReadOnlyList<ImportRow<StockRow>> rows, ImportContext context, CancellationToken cancellationToken)
    {
        var errors = new List<ImportRowError>();
        var warehouses = new Dictionary<string, WarehouseSummary?>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in rows.Select(r => r.Data.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            warehouses[code] = await organization.FindWarehouseByCodeAsync(code, cancellationToken);
        }

        var productIds = await catalogImport.FindProductIdsBySkuAsync(rows.Select(r => r.Data.Sku).ToList(), cancellationToken);
        var products = (await catalog.GetProductsAsync(productIds.Values.ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var resolved = new List<ImportRow<StockRow>>();
        foreach (var row in rows)
        {
            var d = row.Data;
            var warehouse = warehouses[d.WarehouseCode];
            if (warehouse is not { IsActive: true })
            {
                errors.Add(new ImportRowError(row.Row, "Warehouse code", "import.warehouse-not-found", $"Warehouse '{d.WarehouseCode}' was not found or is inactive."));
                continue;
            }

            if (!productIds.TryGetValue(d.Sku, out var productId) || !products.TryGetValue(productId, out var product))
            {
                errors.Add(new ImportRowError(row.Row, "SKU", "import.product-not-found", $"Product '{d.Sku}' was not found."));
                continue;
            }

            var unit = d.UomCode is null ? product.Units.FirstOrDefault(u => u.UomId == product.BaseUomId)
                : product.Units.FirstOrDefault(u => string.Equals(u.Code, d.UomCode, StringComparison.OrdinalIgnoreCase));
            if (unit is null && d.UomCode is null)
            {
                resolved.Add(row with { Data = d with { WarehouseId = warehouse.Id, ProductId = product.Id, UomId = product.BaseUomId } });
                continue;
            }

            if (unit is null)
            {
                errors.Add(new ImportRowError(row.Row, "Unit", "import.unit-not-found", $"Unit '{d.UomCode}' is not a unit of product '{d.Sku}'."));
                continue;
            }

            resolved.Add(row with { Data = d with { WarehouseId = warehouse.Id, ProductId = product.Id, UomId = unit.UomId } });
        }

        return (resolved, errors);
    }

    protected override IEnumerable<IReadOnlyList<ImportRow<StockRow>>> Group(IReadOnlyList<ImportRow<StockRow>> rows) =>
        rows.GroupBy(r => r.Data.WarehouseId).Select(g => (IReadOnlyList<ImportRow<StockRow>>)g.ToList());

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<StockRow>> rows, ImportContext context, CancellationToken ct) =>
        await inventory.PostOpeningStockAsync(rows[0].Data.WarehouseId, context.OpeningDate!.Value,
            rows.Select(r => new OpeningStockImportLine(r.Data.ProductId, r.Data.UomId, r.Data.Quantity, r.Data.UnitCost, r.Data.Batch, r.Data.ManufactureDate,
                r.Data.ExpiryDate, r.Data.Serials)).ToList(), ct);
}

// ============================ Open receivables / payables ============================
public sealed record OpenItemRow(string PartyCode, string DocumentNumber, DateOnly DocumentDate, DateOnly DueDate, string? Currency, decimal? ExchangeRate,
    decimal Amount, string? Description, Guid PartyId = default);

public abstract class OpenItemsImport(IFinanceImportService finance, IPartyImportService parties) : ImportDefinition<OpenItemRow>
{
    protected abstract PartyKind Party { get; }

    protected static IReadOnlyList<ImportColumn> ColumnsFor(ImportColumn party) =>
        [party, Col.DocumentNumber, Col.DocumentDate, Col.DueDate, Col.Currency, Col.ExchangeRate, Col.OpenAmount, Col.Description];

    protected override OpenItemRow? Parse(ImportRowValues v, ImportContext context)
    {
        var date = v.RequiredDate("documentDate");
        var due = v.Date("dueDate") ?? date;
        var amount = v.RequiredDecimal("amount");
        if (amount == 0 && !v.HasErrors)
        {
            v.AddError("amount", "import.zero-amount", "The open amount cannot be zero.");
        }

        var rate = v.Decimal("exchangeRate");
        if (rate is <= 0)
        {
            v.AddError("exchangeRate", "import.invalid-rate", "The exchange rate must be positive.");
        }

        return new OpenItemRow(v.RequiredText("party", 30), v.RequiredText("documentNumber", 50), date, due, v.Text("currency", 3), rate, amount, v.Text("description", 500));
    }

    protected override IReadOnlyList<ImportRowError> CheckFile(IReadOnlyList<ImportRow<OpenItemRow>> rows, ImportContext context, bool complete) =>
        rows.GroupBy(r => (r.Data.PartyCode.ToUpperInvariant(), r.Data.DocumentNumber.ToUpperInvariant())).Where(g => g.Count() > 1)
            .SelectMany(g => g.Skip(1)).Select(r => new ImportRowError(r.Row, "Document number", "import.duplicate-document",
                $"Document '{r.Data.DocumentNumber}' of '{r.Data.PartyCode}' appears more than once in the file.")).ToList();

    protected override async Task<(IReadOnlyList<ImportRow<OpenItemRow>> Rows, IReadOnlyList<ImportRowError> Errors)> ResolveAsync(
        IReadOnlyList<ImportRow<OpenItemRow>> rows, ImportContext context, CancellationToken cancellationToken)
    {
        var codes = rows.Select(r => r.Data.PartyCode).ToList();
        var ids = Party == PartyKind.Customer
            ? await parties.FindCustomerIdsByCodeAsync(codes, cancellationToken)
            : await parties.FindSupplierIdsByCodeAsync(codes, cancellationToken);
        var errors = new List<ImportRowError>();
        var resolved = new List<ImportRow<OpenItemRow>>();
        foreach (var row in rows)
        {
            if (ids.TryGetValue(row.Data.PartyCode, out var id))
            {
                resolved.Add(row with { Data = row.Data with { PartyId = id } });
            }
            else
            {
                errors.Add(new ImportRowError(row.Row, Info.Columns[0].Header, "import.party-not-found",
                    $"{(Party == PartyKind.Customer ? "Customer" : "Supplier")} '{row.Data.PartyCode}' was not found."));
            }
        }

        return (resolved, errors);
    }

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<OpenItemRow>> rows, ImportContext context, CancellationToken ct)
    {
        var r = rows[0].Data;
        return await finance.CreateOpeningItemAsync(new OpeningItemImportRequest(Party, r.PartyId, context.LegalEntityId!.Value, r.DocumentNumber, r.DocumentDate,
            r.DueDate, r.Currency, r.ExchangeRate, r.Amount, r.Description), ct);
    }
}

public sealed class OpenReceivablesImport(IFinanceImportService finance, IPartyImportService parties) : OpenItemsImport(finance, parties)
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.OpenReceivables, "Open receivables", "أرصدة العملاء المفتوحة",
        FinanceOpeningPermissions.OpeningBalanceManage, true, false,
        ColumnsFor(new ImportColumn("party", "Customer code", "رمز العميل", true, "Code of an existing customer.", "C-0001", "Customer", "العميل")));

    protected override PartyKind Party => PartyKind.Customer;
}

public sealed class OpenPayablesImport(IFinanceImportService finance, IPartyImportService parties) : OpenItemsImport(finance, parties)
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.OpenPayables, "Open payables", "أرصدة الموردين المفتوحة",
        FinanceOpeningPermissions.OpeningBalanceManage, true, false,
        ColumnsFor(new ImportColumn("party", "Supplier code", "رمز المورد", true, "Code of an existing supplier.", "S-0001", "Supplier", "المورد")));

    protected override PartyKind Party => PartyKind.Supplier;
}

// ============================ GL opening balances ============================
public sealed record GlRow(string AccountCode, decimal Debit, decimal Credit, string? Description);

/// <summary>All rows become one balanced opening journal. Control accounts are refused: open them with the sub-ledger imports.</summary>
public sealed class GlOpeningBalancesImport(IAccountingImportService accounting) : ImportDefinition<GlRow>
{
    public override ImportDefinitionInfo Info { get; } = new(ImportType.GlOpeningBalances, "GL opening balances", "الأرصدة الافتتاحية للحسابات",
        AccountingPermissions.OpeningBalanceManage, true, true,
        [
            new("account", "Account code", "رمز الحساب", true, "Posting account (not a receivable/payable/inventory control account).", "1110", "Account", "الحساب", "رقم الحساب"),
            new("debit", "Debit", "مدين", false, "Debit balance.", "25000"),
            new("credit", "Credit", "دائن", false, "Credit balance.", ""),
            Col.Description,
        ]);

    protected override bool DiagnosePerRow => false;

    protected override GlRow? Parse(ImportRowValues v, ImportContext context)
    {
        var debit = v.Decimal("debit") ?? 0;
        var credit = v.Decimal("credit") ?? 0;
        if (!v.HasErrors && (debit < 0 || credit < 0 || (debit > 0) == (credit > 0)))
        {
            v.AddError("debit", "import.one-sided", "Give a positive debit or a positive credit, not both.");
        }

        return new GlRow(v.RequiredText("account", 20), debit, credit, v.Text("description", 500));
    }

    protected override IReadOnlyList<ImportRowError> CheckFile(IReadOnlyList<ImportRow<GlRow>> rows, ImportContext context, bool complete)
    {
        if (!complete)
        {
            return []; // totals are meaningless until every line parses
        }

        var debit = rows.Sum(r => r.Data.Debit);
        var credit = rows.Sum(r => r.Data.Credit);
        return debit == credit ? [] :
            [new ImportRowError(0, null, "import.unbalanced", $"Total debit ({debit:N2}) must equal total credit ({credit:N2}); difference {debit - credit:N2}.")];
    }

    protected override IEnumerable<IReadOnlyList<ImportRow<GlRow>>> Group(IReadOnlyList<ImportRow<GlRow>> rows) => rows.Count == 0 ? [] : [rows];

    protected override async Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<GlRow>> rows, ImportContext context, CancellationToken ct) =>
        await accounting.PostOpeningBalancesAsync(new OpeningBalanceImportRequest(context.LegalEntityId!.Value, context.OpeningDate!.Value, "Imported opening balances",
            rows.Select(r => new OpeningBalanceImportLine(r.Data.AccountCode, r.Data.Debit, r.Data.Credit, r.Data.Description)).ToList()), ct);
}
