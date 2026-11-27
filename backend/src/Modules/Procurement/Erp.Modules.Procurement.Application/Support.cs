using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Procurement.Application;

public interface IProcurementDbContext
{
    DbSet<PurchaseRequest> Requests { get; }

    DbSet<PurchaseQuotation> Quotations { get; }

    DbSet<PurchaseOrder> Orders { get; }

    DbSet<GoodsReceipt> Receipts { get; }

    DbSet<PurchaseInvoice> Invoices { get; }

    DbSet<PurchaseReturn> Returns { get; }

    DbSet<LandedCost> LandedCosts { get; }

    EntityEntry Entry(object entity);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class ProcurementAppErrors
{
    public static readonly Error NotFound = Error.NotFound("procurement.document-not-found", "The document was not found.");
    public static readonly Error LegalEntityNotFound = Error.NotFound("procurement.legal-entity-not-found", "The legal entity was not found in this company.");
    public static readonly Error SupplierNotFound = Error.NotFound("procurement.supplier-not-found", "The supplier was not found or is inactive.");
    public static readonly Error WarehouseNotFound = Error.NotFound("procurement.warehouse-not-found", "The warehouse was not found or does not belong to the legal entity.");
    public static readonly Error ProductNotFound = Error.NotFound("procurement.product-not-found", "A product was not found or is inactive.");
    public static readonly Error UnitNotAllowed = Error.Validation("procurement.unit-not-allowed", "The unit is not defined for the product.");
    public static readonly Error NotStockable = Error.Validation("procurement.not-stockable", "Only stockable products can be received into stock; invoice services as expense lines.");
    public static readonly Error RateMissing = Error.Validation("procurement.rate-missing", "No exchange rate is recorded for the currency on this date.");
    public static readonly Error CurrencyMismatch = Error.Validation("procurement.currency-mismatch", "All lines must be in the document currency.");
    public static readonly Error SupplierMismatch = Error.Validation("procurement.supplier-mismatch", "All referenced documents must belong to the same supplier and legal entity.");
    public static readonly Error OrderNotReceivable = Error.Conflict("procurement.order-not-receivable", "The purchase order is not approved for receiving.");
    public static readonly Error InvalidExpenseAccount = Error.Validation("procurement.invalid-expense-account",
        "Expense lines must use an active posting account of type Expense or Asset that is not a control account.");
    public static readonly Error OutputTaxOnPurchase = Error.Validation("procurement.output-tax-on-purchase", "Purchase lines can only carry input (or withholding) tax.");
    public static readonly Error InvoiceBeforeReceiptDisabled = Error.Validation("procurement.invoice-before-receipt-disabled",
        "Invoicing ordered goods before they are received is disabled by the procurement policy (ReceiptBeforeInvoice).");
    public static readonly Error QuantityExceedsInvoiceable = Error.Conflict("procurement.quantity-exceeds-invoiceable", "The invoiced quantity exceeds what can still be invoiced.");
    public static readonly Error DuplicateSupplierInvoice = Error.Conflict("procurement.duplicate-supplier-invoice", "This supplier invoice number was already entered for the supplier.");
    public static readonly Error PartialInvoicedReceipt = Error.Conflict("procurement.partial-invoiced-receipt",
        "Part of this quantity was invoiced before receipt and part was not. Receive the invoiced quantity in a separate receipt line.");
    public static readonly Error InvoiceGoodsReceived = Error.Conflict("procurement.invoice-goods-received",
        "Goods invoiced before receipt have since been received; reverse the receipt first.");
    public static readonly Error ReceiptNotPosted = Error.Conflict("procurement.receipt-not-posted", "Only posted goods receipts can be used.");
}

public sealed record ProcurementLegalEntity(Guid Id, Guid CompanyId, string FunctionalCurrency);

/// <summary>Resolves the legal entity of the active company, exchange rates and currency precision.</summary>
public sealed class ProcurementScope(ITenantContext tenant, IOrganizationQueries organization, IExchangeRateProvider rates, ICurrencyQueries currencies)
{
    public async Task<Result<ProcurementLegalEntity>> LegalEntityAsync(Guid legalEntityId, CancellationToken ct)
    {
        var le = await organization.GetLegalEntityAsync(legalEntityId, ct);
        if (le is null || (tenant.CompanyId is { } company && company != le.CompanyId))
        {
            return ProcurementAppErrors.LegalEntityNotFound;
        }

        return new ProcurementLegalEntity(le.Id, le.CompanyId, le.FunctionalCurrency);
    }

    /// <summary>Functional units per one unit of <paramref name="currency"/>: functional → 1, explicit wins, else the spot rate.</summary>
    public async Task<Result<decimal>> RateAsync(string currency, string functionalCurrency, DateOnly date, decimal? explicitRate, CancellationToken ct)
    {
        if (string.Equals(currency, functionalCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        if (explicitRate is > 0)
        {
            return explicitRate.Value;
        }

        var rate = await rates.GetRateAsync(currency, functionalCurrency, date, ExchangeRateKind.Spot, ct);
        return rate is > 0 ? rate.Value : ProcurementAppErrors.RateMissing;
    }

    public async Task<int> MinorUnitsAsync(string currency, CancellationToken ct) => (await currencies.GetAsync(currency, ct))?.MinorUnits ?? 2;

    public async Task<Result<WarehouseSummary>> WarehouseAsync(Guid warehouseId, Guid legalEntityId, CancellationToken ct)
    {
        var warehouse = (await organization.GetWarehousesAsync([warehouseId], ct)).FirstOrDefault();
        return warehouse is { IsActive: true } && warehouse.LegalEntityId == legalEntityId && warehouse.Type != "Transit"
            ? warehouse
            : ProcurementAppErrors.WarehouseNotFound;
    }
}

/// <summary>Catalog product data and unit factors.</summary>
public sealed class ProductLookup(ICatalogQueries catalog)
{
    public async Task<Result<IReadOnlyDictionary<Guid, ProductInfo>>> LoadAsync(IEnumerable<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().ToList();
        var products = await catalog.GetProductsAsync(ids, ct);
        if (products.Count != ids.Count || products.Any(p => !p.IsActive))
        {
            return ProcurementAppErrors.ProductNotFound;
        }

        return Result.Success<IReadOnlyDictionary<Guid, ProductInfo>>(products.ToDictionary(p => p.Id));
    }

    public static decimal? Factor(ProductInfo product, Guid uomId) =>
        uomId == product.BaseUomId ? 1m : product.Units.FirstOrDefault(u => u.UomId == uomId)?.FactorToBase;
}

public sealed record LineTax(decimal Net, decimal Recoverable, decimal NonRecoverable, IReadOnlyList<TaxLineResult> Lines);

/// <summary>Purchase-side tax for one line: input tax only; withholding is applied by Finance at payment.</summary>
public sealed class PurchaseTax(ITaxCalculator taxes)
{
    public async Task<Result<LineTax>> CalculateAsync(Guid? taxGroupId, decimal net, string currency, DateOnly date, CancellationToken ct)
    {
        if (taxGroupId is null || net == 0)
        {
            return new LineTax(net, 0, 0, []);
        }

        var result = await taxes.CalculateAsync(taxGroupId.Value, net, false, currency, date, ct);
        if (result.IsFailure)
        {
            return result.Error;
        }

        if (result.Value.Lines.Any(l => l.Kind == TaxKindCode.Output))
        {
            return ProcurementAppErrors.OutputTaxOnPurchase;
        }

        var input = result.Value.Lines.Where(l => l.Kind == TaxKindCode.Input).ToList();
        return new LineTax(result.Value.Net, input.Where(l => l.Recoverable).Sum(l => l.Amount), input.Where(l => !l.Recoverable).Sum(l => l.Amount), input);
    }

    public Task<Guid?> ResolveAsync(string taxCategory, CancellationToken ct) => taxes.ResolveTaxGroupAsync(taxCategory, TaxDirection.Purchase, ct);
}

/// <summary>Accumulates functional-currency journal lines (debits positive, credits negative), skipping zeros.</summary>
public sealed class JournalBuilder
{
    private readonly List<JournalLineRequest> _lines = [];

    public void Add(string role, decimal signedAmount, PostingContext? context = null, string? description = null, string? partyType = null, Guid? partyId = null)
    {
        if (signedAmount == 0)
        {
            return;
        }

        _lines.Add(signedAmount > 0
            ? new JournalLineRequest(role, null, signedAmount, 0, context, null, partyType, partyId, description)
            : new JournalLineRequest(role, null, 0, -signedAmount, context, null, partyType, partyId, description));
    }

    public void AddAccount(Guid accountId, decimal signedAmount, string? description = null)
    {
        if (signedAmount == 0)
        {
            return;
        }

        _lines.Add(signedAmount > 0
            ? new JournalLineRequest(null, accountId, signedAmount, 0, null, null, null, null, description)
            : new JournalLineRequest(null, accountId, 0, -signedAmount, null, null, null, null, description));
    }

    public bool IsEmpty => _lines.Count == 0;

    public IReadOnlyList<JournalLineRequest> Lines => _lines;

    public JournalRequest Build(Guid legalEntityId, DateOnly date, string docType, Guid docId, string purpose, string description, string functionalCurrency) =>
        new(legalEntityId, date, SourceModules.Procurement, docType, docId, purpose, description, functionalCurrency, 1m, _lines);
}

public static class ProcurementPostingContext
{
    public static PostingContext Stock(ProductInfo product, Guid warehouseId) => new(ProductPostingGroup: product.PostingGroup, WarehouseId: warehouseId);

    public static PostingContext Product(ProductInfo product) => new(ProductPostingGroup: product.PostingGroup);

    public static PostingContext Tax(Guid taxCodeId) => new(TaxCodeId: taxCodeId);

    public static PostingContext Party(string postingGroup) => new(PartyPostingGroup: postingGroup);
}
