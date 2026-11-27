using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.Modules.Workflow.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Sales.Application;

public interface ISalesDbContext
{
    DbSet<SalesQuotation> Quotations { get; }

    DbSet<SalesOrder> Orders { get; }

    DbSet<DeliveryNote> Deliveries { get; }

    DbSet<SalesInvoice> Invoices { get; }

    DbSet<SalesReturn> Returns { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class SalesAppErrors
{
    public static readonly Error NotFound = Error.NotFound("sales.document-not-found", "The sales document was not found.");
    public static readonly Error CustomerNotFound = Error.NotFound("sales.customer-not-found", "The customer was not found or is inactive.");
    public static readonly Error WarehouseInvalid = Error.Validation("sales.warehouse-invalid", "The warehouse must be an active physical warehouse of this company.");
    public static readonly Error ProductInvalid = Error.Validation("sales.product-invalid", "A product was not found or is inactive.");
    public static readonly Error UomInvalid = Error.Validation("sales.uom-invalid", "The unit is not defined for the product.");
    public static readonly Error PriceNotFound = Error.Validation("sales.price-not-found", "No price list price was found for a line; a price list is required.");
    public static readonly Error PriceCurrencyMismatch = Error.Validation("sales.price-currency-mismatch", "The price list currency differs from the document currency.");
    public static readonly Error PriceOverrideForbidden = Error.Forbidden("sales.price-override-forbidden", "You may not enter prices manually.");
    public static readonly Error DiscountForbidden = Error.Forbidden("sales.discount-forbidden", "You may not give line discounts.");
    public static readonly Error RateMissing = Error.Validation("sales.rate-missing", "No exchange rate is recorded for the customer's currency on this date.");
    public static readonly Error InventoryDisabled = Error.Validation("sales.inventory-disabled", "Stock items cannot be sold while the inventory module is disabled.");
    public static readonly Error DeliveriesDisabled = Error.Conflict("sales.deliveries-disabled", "Delivery notes are only used when the sales policy issues stock at delivery.");
    public static readonly Error InvoiceRequiresDelivery = Error.Validation("sales.invoice-requires-delivery",
        "With stock issued at delivery, stock items are invoiced from posted delivery notes.");
    public static readonly Error CreditLimitExceeded = Error.Conflict("sales.credit-limit-exceeded", "The customer's credit limit would be exceeded.");
    public static readonly Error CustomerOnHold = Error.Conflict("sales.customer-on-hold", "The customer is on credit hold.");
    public static readonly Error CreditApprovalUnavailable = Error.Conflict("sales.credit-approval-unavailable",
        "The credit limit would be exceeded and needs approval, but no approval workflow is configured. Ask finance to raise the limit.");
    public static readonly Error SourceMismatch = Error.Validation("sales.source-mismatch", "The referenced lines do not belong to the source document.");
}

/// <summary>Where a sales document lives: legal entity and branch derived from the warehouse, plus currency and rate.</summary>
public sealed record SalesSetting(Guid CompanyId, Guid LegalEntityId, Guid? BranchId, Guid WarehouseId, string FunctionalCurrency, CustomerInfo Customer, string Currency, decimal Rate);

public sealed class SalesScope(
    ITenantContext tenant,
    IOrganizationQueries organization,
    IPartyQueries parties,
    IExchangeRateProvider rates)
{
    public Guid CompanyId => tenant.CompanyId ?? throw new InvalidOperationException("Sales requires an active company.");

    public async Task<Result<SalesSetting>> ResolveAsync(Guid customerId, Guid warehouseId, DateOnly date, CancellationToken ct)
    {
        var customer = await parties.GetCustomerAsync(customerId, ct);
        if (customer is not { IsActive: true })
        {
            return SalesAppErrors.CustomerNotFound;
        }

        var warehouse = (await organization.GetWarehousesAsync([warehouseId], ct)).FirstOrDefault();
        if (warehouse is null || !warehouse.IsActive || warehouse.CompanyId != CompanyId || warehouse.Type is "Transit")
        {
            return SalesAppErrors.WarehouseInvalid;
        }

        var le = await organization.GetLegalEntityAsync(warehouse.LegalEntityId, ct);
        if (le is null)
        {
            return SalesAppErrors.WarehouseInvalid;
        }

        var rate = await RateAsync(customer.Currency, le.FunctionalCurrency, date, ct);
        return rate.IsFailure
            ? rate.Error
            : new SalesSetting(CompanyId, le.Id, warehouse.BranchId, warehouse.Id, le.FunctionalCurrency, customer, customer.Currency, rate.Value);
    }

    public async Task<Result<decimal>> RateAsync(string currency, string functionalCurrency, DateOnly date, CancellationToken ct)
    {
        if (string.Equals(currency, functionalCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        var rate = await rates.GetRateAsync(currency, functionalCurrency, date, ExchangeRateKind.Spot, ct);
        return rate is > 0 ? rate.Value : SalesAppErrors.RateMissing;
    }
}

/// <summary>A requested line. <see cref="TrustedUnitPrice"/> is set by the system when the price comes from a source document.</summary>
public sealed record LineInput(
    Guid ProductId,
    Guid? VariantId,
    Guid UomId,
    decimal Quantity,
    decimal? UnitPrice,
    decimal DiscountPercent,
    Guid? SourceLineUid = null,
    Guid? DeliveryLineUid = null,
    bool TrustedUnitPrice = false,
    string? BatchNumber = null,
    IReadOnlyList<string>? Serials = null);

public sealed record PricingContext(Guid? PriceListId, string Currency, DateOnly Date, bool DefaultTaxInclusive);

public sealed record PricedLines(IReadOnlyList<SalesLine> Lines, bool TaxInclusive, IReadOnlyDictionary<Guid, ProductInfo> Products);

/// <summary>
/// Server-side pricing pipeline: unit price from the price list (or a permitted manual price), line discount (permission above 0 %),
/// then tax from the tax engine using the product's tax category. The client never decides final amounts.
/// </summary>
public sealed class SalesPricing(
    ICatalogQueries catalog,
    IPriceCalculator prices,
    ITaxCalculator taxes,
    ICurrencyQueries currencies,
    ICurrentUser user,
    IPermissionService permissions)
{
    public async Task<Result<PricedLines>> PriceAsync(PricingContext context, IReadOnlyList<LineInput> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return SalesErrors.NoLines;
        }

        var products = (await catalog.GetProductsAsync(inputs.Select(i => i.ProductId).Distinct().ToList(), ct)).ToDictionary(p => p.Id);
        var decimals = (await currencies.GetAsync(context.Currency, ct))?.MinorUnits ?? 2;
        var granted = user.UserId is { } userId ? await permissions.GetPermissionsAsync(userId, ct) : null;
        bool Can(string permission) => granted is null || granted.Contains(permission);

        bool? listInclusive = null;
        var priced = new List<(LineInput Input, ProductInfo Product, decimal Factor, decimal UnitPrice)>();
        foreach (var input in inputs)
        {
            if (!products.TryGetValue(input.ProductId, out var product) || !product.IsActive)
            {
                return SalesAppErrors.ProductInvalid;
            }

            var factor = input.UomId == product.BaseUomId ? 1m : product.Units.FirstOrDefault(u => u.UomId == input.UomId)?.FactorToBase;
            if (factor is not > 0)
            {
                return SalesAppErrors.UomInvalid;
            }

            if (input.Quantity <= 0)
            {
                return SalesErrors.InvalidQuantity;
            }

            if (input.DiscountPercent is < 0 or > 100)
            {
                return SalesErrors.InvalidDiscount;
            }

            if (input.DiscountPercent > 0 && !input.TrustedUnitPrice && !Can(SalesPermissions.Discount))
            {
                return SalesAppErrors.DiscountForbidden;
            }

            decimal unitPrice;
            if (input.TrustedUnitPrice || input.UnitPrice is not null)
            {
                if (!input.TrustedUnitPrice && !Can(SalesPermissions.PriceOverride))
                {
                    return SalesAppErrors.PriceOverrideForbidden;
                }

                unitPrice = input.UnitPrice ?? 0m;
            }
            else
            {
                if (context.PriceListId is not { } priceListId)
                {
                    return SalesAppErrors.PriceNotFound;
                }

                var quote = await prices.GetPriceAsync(priceListId, product.Id, input.VariantId, input.UomId, input.Quantity, context.Date, ct);
                if (quote is null)
                {
                    return SalesAppErrors.PriceNotFound;
                }

                if (!string.Equals(quote.Currency, context.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    return SalesAppErrors.PriceCurrencyMismatch;
                }

                listInclusive ??= quote.TaxInclusive;
                unitPrice = quote.UnitPrice;
            }

            priced.Add((input, product, factor.Value, unitPrice));
        }

        var inclusive = listInclusive ?? context.DefaultTaxInclusive;
        var lines = new List<SalesLine>();
        foreach (var (input, product, factor, unitPrice) in priced)
        {
            (decimal lineAmount, decimal discount, decimal afterDiscount) amounts;
            try
            {
                amounts = SalesMath.LineAmounts(input.Quantity, unitPrice, input.DiscountPercent, decimals);
            }
            catch (DomainException ex)
            {
                return ex.Error;
            }

            var tax = await taxes.CalculateForCategoryAsync(product.TaxCategory, TaxDirection.Sales, amounts.afterDiscount, inclusive, context.Currency, context.Date, ct);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            var taxLines = tax.Value.Lines.Where(l => l.Kind != TaxKindCode.Withholding)
                .Select(l => new LineTax(l.TaxCodeId, l.Code, l.Rate, l.Base, l.Amount)).ToList();
            var line = new SalesLine(product.Id, input.VariantId, product.Sku, product.NameEn, input.UomId, input.Quantity,
                Math.Round(input.Quantity * factor, 6, MidpointRounding.AwayFromZero), unitPrice, input.DiscountPercent,
                product.IsStockable, product.Type == "Service", product.PostingGroup, input.SourceLineUid);
            line.SetAmounts(amounts.lineAmount, amounts.discount, tax.Value.Net, tax.Value.TotalTax, tax.Value.Net + tax.Value.TotalTax, null, taxLines);
            line.SetStockDetail(input.BatchNumber, input.Serials);
            if (input.DeliveryLineUid is { } deliveryLine)
            {
                line.SetDeliveryLine(deliveryLine);
            }

            lines.Add(line);
        }

        return new PricedLines(lines, inclusive, products);
    }

    /// <summary>Re-prices existing lines (same products, units, quantities, prices and discounts) — used just before posting.</summary>
    public async Task<Result<PricedLines>> RepriceAsync(PricingContext context, IEnumerable<SalesLine> lines, CancellationToken ct)
    {
        var source = lines.ToList();
        var priced = await PriceAsync(context, source.Select(l => new LineInput(l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice, l.DiscountPercent,
            l.SourceLineUid, l.DeliveryLineUid, TrustedUnitPrice: true, l.BatchNumber, l.SerialList)).ToList(), ct);
        if (priced.IsSuccess)
        {
            for (var i = 0; i < source.Count; i++)
            {
                priced.Value.Lines[i].KeepIdentity(source[i].Uid);
            }
        }

        return priced;
    }
}

public sealed record CreditResult(CreditDecision Decision, decimal Limit, decimal ExposureAfter);

/// <summary>Credit exposure = open receivables − unallocated advances (Finance) + uninvoiced confirmed orders + this document.</summary>
public sealed class SalesCreditService(ICreditExposureQueries exposure, SalesQueries salesQueries, IPolicyReader policies)
{
    public async Task<CreditResult> EvaluateAsync(CustomerInfo customer, Guid companyId, decimal documentGrossBase, Guid? excludeOrderId, CancellationToken ct)
    {
        var policy = await policies.GetSalesPolicyAsync(companyId, ct);
        var finance = await exposure.GetCustomerExposureAsync(customer.Id, ct);
        var orders = await salesQueries.GetUninvoicedOrderExposureAsync(customer.Id, excludeOrderId, ct);
        var after = finance.NetExposureBase + orders + documentGrossBase;
        var action = string.IsNullOrWhiteSpace(customer.CreditAction) ? policy.CreditLimitAction : customer.CreditAction;
        return new CreditResult(CreditCheck.Evaluate(policy.EnableCreditLimits, customer.CreditLimit, customer.OnHold, action, after), customer.CreditLimit, after);
    }
}

public sealed record SubmitOutcome(bool PendingApproval, Guid? InstanceId, bool ForCredit, string? Warning);

/// <summary>Shared submit logic for orders and invoices: credit check, then approval rules.</summary>
public sealed class SalesSubmission(SalesCreditService credit, IApprovalService approvals, ICurrentUser user)
{
    public async Task<Result<SubmitOutcome>> SubmitAsync(SalesDocument document, string approvalType, CustomerInfo customer, Guid? excludeOrderId, CancellationToken ct)
    {
        var grossBase = SalesMath.Round(document.GrossTotal * document.ExchangeRate, 2);
        var check = await credit.EvaluateAsync(customer, document.CompanyId, grossBase, excludeOrderId, ct);
        var maxDiscount = document.Lines.Count == 0 ? 0 : document.Lines.Max(l => l.DiscountPercent);
        string? warning = null;
        switch (check.Decision)
        {
            case CreditDecision.OnHold:
                return SalesAppErrors.CustomerOnHold;
            case CreditDecision.Block:
                return SalesAppErrors.CreditLimitExceeded;
            case CreditDecision.Warn:
                warning = $"Credit limit {check.Limit:0.00} exceeded: exposure would be {check.ExposureAfter:0.00}.";
                break;
            case CreditDecision.RequireApproval:
                var creditDecision = await approvals.SubmitAsync(Request(document, approvalType, grossBase, maxDiscount, creditExceeded: true), ct);
                return creditDecision.Required
                    ? new SubmitOutcome(true, creditDecision.InstanceId, true, null)
                    : SalesAppErrors.CreditApprovalUnavailable;
        }

        var decision = await approvals.SubmitAsync(Request(document, approvalType, grossBase, maxDiscount, creditExceeded: false), ct);
        return new SubmitOutcome(decision.Required, decision.InstanceId, false, warning);
    }

    private ApprovalRequest Request(SalesDocument document, string type, decimal grossBase, decimal maxDiscount, bool creditExceeded) =>
        new(type, document.Id, document.Number, document.CompanyId, grossBase, document.Currency, user.UserId ?? Guid.Empty, document.BranchId,
            new Dictionary<string, decimal> { ["maxDiscountPercent"] = maxDiscount, ["creditExceeded"] = creditExceeded ? 1 : 0, ["documentAmount"] = document.GrossTotal });
}

internal static class SalesNumbers
{
    public static Task<string> NextAsync(IDocumentNumberGenerator numbers, SalesDocument document, string docType, string prefix, CancellationToken ct) =>
        numbers.NextAsync(new NumberScope(document.CompanyId, docType, prefix, LegalEntityId: document.LegalEntityId, FiscalYear: document.DocumentDate.Year), ct);
}

/// <summary>Confirms an order: legal number and stock reservations (what is available, when the inventory policy reserves).</summary>
public sealed class OrderConfirmation(
    IDocumentNumberGenerator numbers,
    IPolicyReader policies,
    IModuleActivationService modules,
    IReservationService reservations,
    IStockQueries stock)
{
    public async Task<Result> ConfirmAsync(SalesOrder order, CancellationToken ct)
    {
        var reserved = false;
        var policy = await policies.GetInventoryPolicyAsync(order.CompanyId, ct);
        if (policy.EnableReservations && await modules.IsEnabledAsync(order.CompanyId, "inventory", ct))
        {
            var stockLines = order.Lines.Where(l => l.IsStockable).ToList();
            var levels = (await stock.GetStockAsync(stockLines.Select(l => l.ProductId).Distinct().ToList(), order.WarehouseId, ct))
                .GroupBy(s => (s.ProductId, s.VariantId)).ToDictionary(g => g.Key, g => g.Sum(s => s.Available));
            foreach (var line in stockLines)
            {
                var available = levels.GetValueOrDefault((line.ProductId, line.VariantId));
                var quantity = Math.Min(available, line.BaseQuantity);
                if (quantity <= 0)
                {
                    continue;
                }

                var result = await reservations.ReserveAsync(new ReservationRequest(
                    new ReservationRef(SalesDocumentTypes.Order, order.Id, line.Uid), line.ProductId, line.VariantId, order.WarehouseId, quantity), ct);
                if (result.IsFailure)
                {
                    return result.Error;
                }

                levels[(line.ProductId, line.VariantId)] = available - quantity;
                reserved = true;
            }
        }

        try
        {
            order.Confirm(await SalesNumbers.NextAsync(numbers, order, SalesDocumentTypes.Order, "SO", ct), reserved);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        return Result.Success();
    }

    /// <summary>Releases what is still reserved for the order (cancel / short-close).</summary>
    public async Task ReleaseAsync(SalesOrder order, CancellationToken ct)
    {
        if (!order.StockReserved)
        {
            return;
        }

        foreach (var reservation in await reservations.GetAsync(SalesDocumentTypes.Order, order.Id, ct))
        {
            if (reservation.Open > 0)
            {
                await reservations.ReleaseAsync(reservation.Reference, null, ct);
            }
        }
    }
}

/// <summary>Builds journal lines in the document currency (the posting engine converts at the document rate).</summary>
public sealed class SalesJournal
{
    private readonly List<JournalLineRequest> _lines = [];

    public IReadOnlyList<JournalLineRequest> Lines => _lines;

    public SalesJournal Add(string role, decimal debit, decimal credit, PostingContext? context = null, Guid? customerId = null, string? description = null,
        decimal? taxableAmount = null)
    {
        if (debit == 0 && credit == 0)
        {
            return this;
        }

        _lines.Add(new JournalLineRequest(role, null, debit, credit, context, null, customerId is null ? null : "Customer", customerId, description,
            TaxableAmount: taxableAmount));
        return this;
    }

    /// <summary>Revenue (or returns) per posting group and output tax per tax code, on the given side.</summary>
    public SalesJournal AddRevenueAndTax(IEnumerable<SalesLine> lines, bool credit, string goodsRole, string serviceRole)
    {
        var materialized = lines.ToList();
        foreach (var group in materialized.GroupBy(l => (Role: l.IsService ? serviceRole : goodsRole, l.PostingGroup)))
        {
            var amount = group.Sum(l => l.NetAmount);
            Add(group.Key.Role, credit ? 0 : amount, credit ? amount : 0, new PostingContext(ProductPostingGroup: group.Key.PostingGroup));
        }

        foreach (var tax in materialized.SelectMany(l => l.Taxes).GroupBy(t => t.TaxCodeId))
        {
            var amount = tax.Sum(t => t.Amount);
            Add(PostingRoles.OutputTax, credit ? 0 : amount, credit ? amount : 0, new PostingContext(TaxCodeId: tax.Key), description: tax.First().Code,
                taxableAmount: tax.Sum(t => t.Base));
        }

        return this;
    }
}

public sealed record SalesLineTaxDto(Guid TaxCodeId, string Code, decimal Rate, decimal Base, decimal Amount);

public sealed record SalesLineDto(
    Guid Id,
    Guid ProductId,
    Guid? VariantId,
    string Sku,
    string Description,
    Guid UomId,
    decimal Quantity,
    decimal BaseQuantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineAmount,
    decimal DiscountAmount,
    decimal NetAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    IReadOnlyList<SalesLineTaxDto> Taxes,
    Guid? SourceLineId,
    Guid? DeliveryLineId,
    decimal DeliveredQuantity,
    decimal InvoicedQuantity,
    decimal ReturnedQuantity,
    decimal CostAmount,
    string? BatchNumber,
    IReadOnlyList<string> Serials,
    bool Restock);

public sealed record SalesDocumentDto(
    Guid Id,
    string DocumentType,
    string Number,
    bool IsNumbered,
    string Status,
    Guid CustomerId,
    Guid LegalEntityId,
    Guid? BranchId,
    Guid WarehouseId,
    string Currency,
    decimal ExchangeRate,
    Guid? PriceListId,
    bool TaxInclusive,
    DateOnly DocumentDate,
    string? Reference,
    string? Notes,
    decimal NetTotal,
    decimal TaxTotal,
    decimal GrossTotal,
    IReadOnlyList<SalesLineDto> Lines,
    DateOnly? ValidUntil = null,
    int? Revision = null,
    Guid? SalesOrderId = null,
    Guid? DeliveryNoteId = null,
    Guid? SalesInvoiceId = null,
    Guid? JournalId = null,
    decimal? OpenAmount = null,
    string? Reason = null,
    Guid? ApprovalInstanceId = null,
    IReadOnlyList<string>? Warnings = null,
    Guid? SalespersonId = null);

public sealed record SalesListResult(IReadOnlyList<SalesDocumentDto> Items, int Page, int PageSize, int TotalCount, decimal NetTotal, decimal TaxTotal, decimal GrossTotal);

public static class SalesMapping
{
    public static SalesLineDto ToDto(SalesLine l) => new(l.Uid, l.ProductId, l.VariantId, l.Sku, l.Description, l.UomId, l.Quantity, l.BaseQuantity, l.UnitPrice,
        l.DiscountPercent, l.LineAmount, l.DiscountAmount, l.NetAmount, l.TaxAmount, l.GrossAmount,
        l.Taxes.Select(t => new SalesLineTaxDto(t.TaxCodeId, t.Code, t.Rate, t.Base, t.Amount)).ToList(), l.SourceLineUid, l.DeliveryLineUid,
        l.DeliveredQuantity, l.InvoicedQuantity, l.ReturnedQuantity, l.CostAmount, l.BatchNumber, l.SerialList, l.Restock);

    private static SalesDocumentDto Base(SalesDocument d, string type, string status, IReadOnlyList<string>? warnings) => new(
        d.Id, type, d.Number, d.IsNumbered, status, d.CustomerId, d.LegalEntityId, d.BranchId, d.WarehouseId, d.Currency, d.ExchangeRate, d.PriceListId,
        d.TaxInclusive, d.DocumentDate, d.Reference, d.Notes, d.NetTotal, d.TaxTotal, d.GrossTotal, d.Lines.Select(ToDto).ToList(), Warnings: warnings,
        SalespersonId: d.SalespersonId);

    public static SalesDocumentDto ToDto(SalesQuotation q) =>
        Base(q, SalesDocumentTypes.Quotation, q.Status.ToString(), null) with { ValidUntil = q.ValidUntil, Revision = q.Revision, SalesOrderId = q.ConvertedOrderId };

    public static SalesDocumentDto ToDto(SalesOrder o, IReadOnlyList<string>? warnings = null) =>
        Base(o, SalesDocumentTypes.Order, o.Status.ToString(), warnings) with { ApprovalInstanceId = o.ApprovalInstanceId };

    public static SalesDocumentDto ToDto(DeliveryNote d) =>
        Base(d, SalesDocumentTypes.Delivery, d.Status.ToString(), null) with { SalesOrderId = d.SalesOrderId, JournalId = d.JournalId };

    public static SalesDocumentDto ToDto(SalesInvoice i, IReadOnlyList<string>? warnings = null) =>
        Base(i, SalesDocumentTypes.Invoice, i.Status.ToString(), warnings) with
        {
            SalesOrderId = i.SalesOrderId, DeliveryNoteId = i.DeliveryNoteId, JournalId = i.JournalId, OpenAmount = i.IsPosted ? i.OpenAmount : null,
            ApprovalInstanceId = i.ApprovalInstanceId,
        };

    public static SalesDocumentDto ToDto(SalesReturn r) =>
        Base(r, SalesDocumentTypes.Return, r.Status.ToString(), null) with { SalesInvoiceId = r.SalesInvoiceId, JournalId = r.JournalId, Reason = r.Reason };
}

/// <summary>Contract implementation for other modules (MRP demand, credit exposure).</summary>
public sealed class SalesQueries(ISalesDbContext db) : ISalesQueries
{
    public Task<IReadOnlyList<OpenSalesDemand>> GetOpenDemandAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default) =>
        LoadOpenDemandAsync(productIds.ToHashSet(), warehouseId, cancellationToken);

    public Task<IReadOnlyList<OpenSalesDemand>> GetAllOpenDemandAsync(Guid? warehouseId = null, CancellationToken cancellationToken = default) =>
        LoadOpenDemandAsync(null, warehouseId, cancellationToken);

    private async Task<IReadOnlyList<OpenSalesDemand>> LoadOpenDemandAsync(HashSet<Guid>? productIds, Guid? warehouseId, CancellationToken cancellationToken)
    {
        var orders = await OpenOrders().Where(o => warehouseId == null || o.WarehouseId == warehouseId).ToListAsync(cancellationToken);
        return orders.SelectMany(o => o.Lines
                .Where(l => l.IsStockable && (productIds is null || productIds.Contains(l.ProductId)) && l.OpenToDeliver > 0)
                .Select(l => new OpenSalesDemand(o.Id, o.Number, l.Uid, l.ProductId, l.VariantId, o.WarehouseId, o.RequestedDate ?? o.DocumentDate, l.ToBase(l.OpenToDeliver))))
            .ToList();
    }

    public Task<decimal> GetUninvoicedOrderExposureAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        GetUninvoicedOrderExposureAsync(customerId, null, cancellationToken);

    public async Task<decimal> GetUninvoicedOrderExposureAsync(Guid customerId, Guid? excludeOrderId, CancellationToken cancellationToken)
    {
        var orders = await OpenOrders().Where(o => o.CustomerId == customerId && o.Id != excludeOrderId).ToListAsync(cancellationToken);
        return orders.Sum(o => SalesMath.Round(o.Lines.Sum(l => SalesMath.Prorate(l.GrossAmount, l.OpenToInvoice, l.Quantity, 2)) * o.ExchangeRate, 2));
    }

    private IQueryable<SalesOrder> OpenOrders() => db.Orders.AsNoTracking()
        .Where(o => o.Status == SalesOrderStatus.Confirmed || o.Status == SalesOrderStatus.PartiallyDelivered || o.Status == SalesOrderStatus.FullyDelivered);
}
