using Erp.Application.Abstractions;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Sales.Application;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class SalesPricingTests
{
    private static readonly Guid Pcs = Guid.NewGuid();
    private static readonly Guid Box = Guid.NewGuid();
    private static readonly Guid PriceList = Guid.NewGuid();
    private static readonly Guid Vat = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 1);

    private static readonly ProductInfo Plate = new(Guid.NewGuid(), "PLATE", "Plate", "طبق", "Stockable", true, Pcs, [new ProductUnitInfo(Box, "BOX", 12m)],
        "None", false, "WeightedAverage", "GOODS", "standard", true);

    private static readonly ProductInfo Repair = new(Guid.NewGuid(), "REPAIR", "Repair", "صيانة", "Service", false, Pcs, [], "None", false, "WeightedAverage", "SERVICES", "standard", true);

    private sealed class Catalog : ICatalogQueries, IPriceCalculator
    {
        public bool Inclusive { get; init; }

        public Task<IReadOnlyList<ProductInfo>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProductInfo>>(new[] { Plate, Repair }.Where(p => productIds.Contains(p.Id)).ToList());

        public Task<PriceQuote?> GetPriceAsync(Guid priceListId, Guid productId, Guid? variantId, Guid uomId, decimal quantity, DateOnly date, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriceQuote?>(productId == Plate.Id ? new PriceQuote(uomId == Box ? 1140m : 114m, "EGP", Inclusive, Guid.NewGuid(), false) : null);
    }

    /// <summary>Standard 14 % VAT, exclusive or inclusive (same rounding rule as the tax engine: tax = gross − round(gross / 1.14)).</summary>
    private sealed class Vat14 : ITaxCalculator, ICurrencyQueries
    {
        public Task<Result<TaxResult>> CalculateAsync(Guid taxGroupId, decimal amount, bool taxInclusive, string currency, DateOnly date, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TaxResult>> CalculateForCategoryAsync(string taxCategoryCode, TaxDirection direction, decimal amount, bool taxInclusive, string currency, DateOnly date,
            CancellationToken cancellationToken = default)
        {
            var net = taxInclusive ? Math.Round(amount / 1.14m, 2, MidpointRounding.AwayFromZero) : amount;
            var tax = taxInclusive ? amount - net : Math.Round(amount * 0.14m, 2, MidpointRounding.AwayFromZero);
            return Task.FromResult(Result.Success(new TaxResult(net, tax, net + tax, 0, [new TaxLineResult(Vat, "VAT14-OUT", TaxKindCode.Output, 14m, net, tax, false)])));
        }

        public Task<Guid?> ResolveTaxGroupAsync(string taxCategoryCode, TaxDirection direction, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task<CurrencyInfo?> GetAsync(string code, CancellationToken cancellationToken = default) => Task.FromResult<CurrencyInfo?>(new CurrencyInfo("EGP", "Pound", "جنيه", 2, 0));
    }

    private sealed class User(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;

        public Guid? SessionId => null;

        public bool IsAuthenticated => id is not null;
    }

    private sealed class Permissions(params string[] granted) : IPermissionService
    {
        public Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(granted.ToHashSet());

        public Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default) => Task.FromResult(granted.Contains(permission));
    }

    private static SalesPricing Pricing(bool inclusive = false, params string[] permissions)
    {
        var catalog = new Catalog { Inclusive = inclusive };
        var tax = new Vat14();
        return new SalesPricing(catalog, catalog, tax, tax, new User(Guid.NewGuid()), new Permissions(permissions));
    }

    private static Task<Result<PricedLines>> Price(SalesPricing pricing, params LineInput[] lines) =>
        pricing.PriceAsync(new PricingContext(PriceList, "EGP", Today, false), lines, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Exclusive_price_list_adds_tax_on_the_discounted_amount()
    {
        var result = await Price(Pricing(false, SalesPermissions.Discount), new LineInput(Plate.Id, null, Pcs, 10, null, 10));
        var line = result.Value.Lines.Single();
        line.UnitPrice.ShouldBe(114m);
        line.LineAmount.ShouldBe(1140m);
        line.DiscountAmount.ShouldBe(114m);
        line.NetAmount.ShouldBe(1026m);
        line.TaxAmount.ShouldBe(143.64m);
        line.GrossAmount.ShouldBe(1169.64m);
        line.Taxes.Single().Code.ShouldBe("VAT14-OUT");
        result.Value.TaxInclusive.ShouldBeFalse();
    }

    [Fact]
    public async Task Inclusive_price_list_extracts_tax_from_the_price()
    {
        var result = await Price(Pricing(true), new LineInput(Plate.Id, null, Pcs, 3, null, 0));
        var line = result.Value.Lines.Single();
        line.GrossAmount.ShouldBe(342m);
        line.NetAmount.ShouldBe(300m);
        line.TaxAmount.ShouldBe(42m);
        result.Value.TaxInclusive.ShouldBeTrue();
    }

    [Fact]
    public async Task Units_convert_to_the_base_quantity()
    {
        var line = (await Price(Pricing(), new LineInput(Plate.Id, null, Box, 2, null, 0))).Value.Lines.Single();
        line.BaseQuantity.ShouldBe(24m);
        line.LineAmount.ShouldBe(2280m);
        line.IsStockable.ShouldBeTrue();
        line.PostingGroup.ShouldBe("GOODS");
    }

    [Fact]
    public async Task Discounts_and_manual_prices_need_their_permissions()
    {
        (await Price(Pricing(), new LineInput(Plate.Id, null, Pcs, 1, null, 5))).Error.Code.ShouldBe("sales.discount-forbidden");
        (await Price(Pricing(), new LineInput(Plate.Id, null, Pcs, 1, 50m, 0))).Error.Code.ShouldBe("sales.price-override-forbidden");
        (await Price(Pricing(false, SalesPermissions.PriceOverride), new LineInput(Plate.Id, null, Pcs, 1, 50m, 0))).Value.Lines[0].UnitPrice.ShouldBe(50m);

        // Prices and discounts agreed on a source document are not re-checked.
        (await Price(Pricing(), new LineInput(Plate.Id, null, Pcs, 1, 99m, 20, TrustedUnitPrice: true))).Value.Lines[0].NetAmount.ShouldBe(79.2m);
    }

    [Fact]
    public async Task Services_without_a_price_need_a_manual_price()
    {
        (await Price(Pricing(), new LineInput(Repair.Id, null, Pcs, 1, null, 0))).Error.Code.ShouldBe("sales.price-not-found");
        var line = (await Price(Pricing(false, SalesPermissions.PriceOverride), new LineInput(Repair.Id, null, Pcs, 2, 250m, 0))).Value.Lines.Single();
        line.IsService.ShouldBeTrue();
        line.IsStockable.ShouldBeFalse();
        line.GrossAmount.ShouldBe(570m);
    }

    [Fact]
    public async Task Repricing_keeps_line_identity()
    {
        var pricing = Pricing();
        var first = (await Price(pricing, new LineInput(Plate.Id, null, Pcs, 4, null, 0))).Value.Lines;
        var again = (await pricing.RepriceAsync(new PricingContext(PriceList, "EGP", Today, false), first, TestContext.Current.CancellationToken)).Value.Lines;
        again.Single().Uid.ShouldBe(first.Single().Uid);
        again.Single().GrossAmount.ShouldBe(first.Single().GrossAmount);
    }
}

public class SalesDomainTests
{
    private static readonly DateOnly D = new(2026, 6, 1);

    private static SalesLine Line(decimal qty = 10, decimal price = 100m, bool stock = true)
    {
        var line = new SalesLine(Guid.NewGuid(), null, "P", "Product", Guid.NewGuid(), qty, qty, price, 0, stock, !stock, "GOODS", null);
        var net = qty * price;
        line.SetAmounts(net, 0, net, net * 0.14m, net * 1.14m, null, [new LineTax(Guid.NewGuid(), "VAT", 14m, net, net * 0.14m)]);
        return line;
    }

    private static SalesOrder Order(params SalesLine[] lines) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "DOMESTIC", "EGP", 1m, null, false, D, null, null, null, null, lines.Length == 0 ? [Line()] : lines);

    private static SalesInvoice Invoice(params SalesLine[] lines) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "DOMESTIC", "EGP", 1m, null, false, D, null, null, null, null, null, lines.Length == 0 ? [Line()] : lines);

    [Fact]
    public void Line_amounts_round_half_away_from_zero_and_validate()
    {
        SalesMath.LineAmounts(3, 0.335m, 0, 2).LineAmount.ShouldBe(1.01m);
        SalesMath.LineAmounts(1, 10.05m, 50, 2).Discount.ShouldBe(5.03m);
        Should.Throw<DomainException>(() => SalesMath.LineAmounts(0, 1, 0, 2)).Error.Code.ShouldBe("sales.invalid-quantity");
        Should.Throw<DomainException>(() => SalesMath.LineAmounts(1, -1, 0, 2)).Error.Code.ShouldBe("sales.invalid-price");
        Should.Throw<DomainException>(() => SalesMath.LineAmounts(1, 1, 101, 2)).Error.Code.ShouldBe("sales.invalid-discount");
        SalesMath.Prorate(100m, 1, 3, 2).ShouldBe(33.33m);
        SalesMath.Prorate(100m, 3, 3, 2).ShouldBe(100m);
    }

    [Theory]
    [InlineData(true, 1000, false, "Block", 900, CreditDecision.Ok)]
    [InlineData(true, 1000, false, "Block", 1000.01, CreditDecision.Block)]
    [InlineData(true, 1000, false, "Warn", 5000, CreditDecision.Warn)]
    [InlineData(true, 1000, false, "RequireApproval", 5000, CreditDecision.RequireApproval)]
    [InlineData(true, 0, false, "Block", 1_000_000, CreditDecision.Ok)]
    [InlineData(false, 1000, false, "Block", 5000, CreditDecision.Ok)]
    [InlineData(false, 0, true, "Warn", 0, CreditDecision.OnHold)]
    public void Credit_decisions(bool enabled, double limit, bool onHold, string action, double exposure, CreditDecision expected) =>
        CreditCheck.Evaluate(enabled, (decimal)limit, onHold, action, (decimal)exposure).ShouldBe(expected);

    [Fact]
    public void Quotation_lifecycle_with_revision_expiry_and_conversion()
    {
        var q = new SalesQuotation(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "DOMESTIC", "EGP", 1m, null, false, D, D.AddDays(10), null, null, [Line()]);
        q.Number.ShouldStartWith("D-");
        q.Send("QUO-1");
        q.Revise();
        q.Revision.ShouldBe(2);
        q.Send("QUO-2");
        q.Number.ShouldBe("QUO-1");
        Should.Throw<DomainException>(() => q.Accept(D.AddDays(11))).Error.Code.ShouldBe("sales.invalid-state-transition");
        q.Status.ShouldBe(QuotationStatus.Expired);

        var fresh = new SalesQuotation(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "DOMESTIC", "EGP", 1m, null, false, D, D.AddDays(10), null, null, [Line()]);
        Should.Throw<DomainException>(() => fresh.Accept(D));
        fresh.Send("QUO-3");
        fresh.Accept(D);
        fresh.MarkConverted(Guid.NewGuid());
        fresh.Status.ShouldBe(QuotationStatus.Converted);
    }

    [Fact]
    public void Order_status_follows_deliveries_and_invoices()
    {
        var a = Line(10);
        var b = Line(5);
        var order = Order(a, b);
        Should.Throw<DomainException>(() => order.RegisterDelivery(a.Uid, 1));
        order.Confirm("SO-1", stockReserved: false);
        order.RegisterDelivery(a.Uid, 4);
        order.Status.ShouldBe(SalesOrderStatus.PartiallyDelivered);
        Should.Throw<DomainException>(() => order.RegisterDelivery(a.Uid, 7)).Error.Code.ShouldBe("sales.delivery-exceeds-ordered");
        order.RegisterDelivery(a.Uid, 6);
        order.RegisterDelivery(b.Uid, 5);
        order.Status.ShouldBe(SalesOrderStatus.FullyDelivered);
        order.RegisterInvoice(a.Uid, 10);
        order.RegisterInvoice(b.Uid, 5);
        order.Status.ShouldBe(SalesOrderStatus.Closed);

        // A reversal after closing reopens the order.
        order.RegisterInvoice(b.Uid, -5);
        order.RegisterDelivery(b.Uid, -5);
        order.Status.ShouldBe(SalesOrderStatus.PartiallyDelivered);
        Should.Throw<DomainException>(order.Cancel);
    }

    [Fact]
    public void Order_approval_cancel_and_short_close()
    {
        var order = Order();
        order.SubmitForApproval(Guid.NewGuid(), forCredit: true);
        order.Status.ShouldBe(SalesOrderStatus.PendingApproval);
        order.RejectApproval();
        order.Status.ShouldBe(SalesOrderStatus.Draft);
        order.CreditApproved.ShouldBeFalse();
        order.Cancel();
        order.Status.ShouldBe(SalesOrderStatus.Cancelled);

        var open = Order();
        Should.Throw<DomainException>(open.ShortClose);
        open.Confirm("SO-2", true);
        open.RegisterDelivery(open.Lines[0].Uid, 3);
        open.ShortClose();
        open.Status.ShouldBe(SalesOrderStatus.Closed);
        open.RegisterInvoice(open.Lines[0].Uid, 3);
        open.Status.ShouldBe(SalesOrderStatus.Closed);
    }

    [Fact]
    public void Invoice_lifecycle_settlement_and_cancellation()
    {
        var invoice = Invoice();
        Should.Throw<DomainException>(() => invoice.MarkPosted("INV-1", Guid.NewGuid(), false));
        invoice.Approve();
        invoice.MarkPosted("INV-1", Guid.NewGuid(), issuedStock: true);
        invoice.Status.ShouldBe(SalesInvoiceStatus.Posted);
        invoice.OpenAmount.ShouldBe(1140m);
        invoice.Lines[0].InvoicedQuantity.ShouldBe(10m);

        invoice.ApplySettlement("PartiallyPaid", 140m);
        invoice.Status.ShouldBe(SalesInvoiceStatus.PartiallyPaid);
        Should.Throw<DomainException>(() => invoice.Cancel(D)).Error.Code.ShouldBe("sales.invalid-state-transition");
        invoice.ApplySettlement("Open", 1140m);
        invoice.Cancel(D);
        invoice.Status.ShouldBe(SalesInvoiceStatus.Cancelled);
        Should.Throw<DomainException>(invoice.Discard);
    }

    [Fact]
    public void Returns_are_limited_to_invoiced_minus_returned()
    {
        var invoice = Invoice(Line(10));
        invoice.Approve();
        invoice.MarkPosted("INV-2", Guid.NewGuid(), true);
        var uid = invoice.Lines[0].Uid;
        invoice.RegisterReturn(uid, 4);
        invoice.Lines[0].OpenToReturn.ShouldBe(6m);
        Should.Throw<DomainException>(() => invoice.RegisterReturn(uid, 7)).Error.Code.ShouldBe("sales.return-exceeds-invoiced");
        invoice.RegisterReturn(uid, 6);
        invoice.Lines[0].OpenToReturn.ShouldBe(0m);
        Should.Throw<DomainException>(() => invoice.Cancel(D)).Error.Code.ShouldBe("sales.invoice-has-returns");
    }

    [Fact]
    public void Return_lines_credit_the_invoiced_share_of_net_and_tax()
    {
        var source = Line(3, 33.33m);
        source.RecordCost(60m, Guid.NewGuid(), "B1");
        var line = SalesReturnLines.From(source, 1, restock: true, null);
        line.NetAmount.ShouldBe(33.33m);
        line.TaxAmount.ShouldBe(4.67m);
        line.GrossAmount.ShouldBe(38m);
        line.CostAmount.ShouldBe(20m);
        line.CostMovementId.ShouldBe(source.CostMovementId);
        line.BatchNumber.ShouldBe("B1");
        line.SourceLineUid.ShouldBe(source.Uid);
        line.Restock.ShouldBeTrue();
    }

    [Fact]
    public void Delivery_reverses_only_before_invoicing()
    {
        var order = Order(Line(5));
        order.Confirm("SO-3", false);
        var dn = new DeliveryNote(order, D, null, null, [Line(5)]);
        dn.Pick(new Dictionary<Guid, (string?, IReadOnlyList<string>?)> { [dn.Lines[0].Uid] = ("B1", ["S1", "S2"]) });
        dn.Lines[0].SerialList.ShouldBe(["S1", "S2"]);
        dn.MarkPosted("DN-1", null);
        dn.RegisterInvoice(dn.Lines[0].Uid, 2);
        Should.Throw<DomainException>(dn.Reverse);
        dn.RegisterInvoice(dn.Lines[0].Uid, -2);
        dn.Reverse();
        dn.Status.ShouldBe(DeliveryStatus.Reversed);
    }
}
