using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Pos.Domain;

public static class PosErrors
{
    public static Error InvalidTransition(string document, string from, string action) =>
        Error.Conflict("pos.invalid-state-transition", $"A {document} in status {from} cannot be {action}.");

    public static readonly Error NoLines = Error.Validation("pos.lines-required", "At least one line is required.");
    public static readonly Error InvalidQuantity = Error.Validation("pos.invalid-quantity", "Quantities must be greater than zero.");
    public static readonly Error InvalidDiscount = Error.Validation("pos.invalid-discount", "Discounts must be between 0 and 100 %.");
    public static readonly Error InvalidAmount = Error.Validation("pos.invalid-amount", "Amounts must be greater than zero.");
    public static readonly Error InvalidFloat = Error.Validation("pos.invalid-float", "The opening float cannot be negative.");
    public static readonly Error InvalidCount = Error.Validation("pos.invalid-count", "Counted amounts cannot be negative.");
    public static readonly Error NoPayments = Error.Validation("pos.payments-required", "At least one payment is required.");
    public static readonly Error InvalidPaymentMethod = Error.Validation("pos.invalid-payment-method", "The payment method must be Cash, Card, Wallet or Other.");
    public static readonly Error Underpaid = Error.Validation("pos.underpaid", "The payments do not cover the amount due.");
    public static readonly Error OverpaidNonCash = Error.Validation("pos.overpaid-non-cash", "Only cash can be over-tendered; non-cash payments exceed the amount due.");
    public static readonly Error RefundPaymentMismatch = Error.Validation("pos.refund-payment-mismatch", "Refund payments must equal the amount refunded exactly.");
    public static readonly Error RefundExceedsSold = Error.Validation("pos.refund-exceeds-sold", "The refunded quantity exceeds what was sold and not yet refunded.");
    public static readonly Error LineNotFound = Error.NotFound("pos.line-not-found", "A referenced receipt line was not found.");
    public static readonly Error InvalidCode = Error.Validation("pos.invalid-code", "The terminal code is required (at most 12 letters, digits or dashes).");
    public static readonly Error InvalidMaxDiscount = Error.Validation("pos.invalid-max-discount", "The maximum discount must be between 0 and 100 %.");
    public static readonly Error InvalidStalePricePolicy = Error.Validation("pos.invalid-stale-price-policy", "The stale price policy must be Reject or AcceptServerPrices.");
    public static readonly Error InvalidMovementKind = Error.Validation("pos.invalid-movement-kind", "The cash movement kind must be PayIn or PayOut.");
    public static readonly Error InvalidOfflineRange = Error.Validation("pos.invalid-offline-range", "An offline number range must hold between 1 and 10000 numbers.");
}

public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    Wallet = 3,
    Other = 4,
}

public sealed record PaymentInput(PaymentMethod Method, decimal Amount);

/// <summary>Result of settling a receipt: what was tendered, the change (cash only) and the net cash kept in the drawer.</summary>
public sealed record Settlement(decimal AmountDue, decimal Tendered, decimal Change, decimal CashNet, decimal RoundingAdjustment);

/// <summary>Deterministic POS arithmetic: rounding, cash rounding, payment settlement and proration.</summary>
public static class PosMath
{
    public static decimal Round(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    /// <summary>Rounds to the currency's cash increment (e.g. 0.05), half away from zero. An increment of 0 means no cash rounding.</summary>
    public static decimal CashRound(decimal amount, decimal increment) =>
        increment <= 0 ? amount : Math.Round(amount / increment, 0, MidpointRounding.AwayFromZero) * increment;

    /// <summary>Line amounts before tax: gross line amount, discount, and the amount the tax engine receives.</summary>
    public static (decimal LineAmount, decimal Discount, decimal AfterDiscount) LineAmounts(decimal quantity, decimal unitPrice, decimal discountPercent, int decimals)
    {
        if (quantity <= 0)
        {
            throw new DomainException(PosErrors.InvalidQuantity);
        }

        if (discountPercent is < 0 or > 100)
        {
            throw new DomainException(PosErrors.InvalidDiscount);
        }

        var lineAmount = Round(quantity * unitPrice, decimals);
        var discount = Round(lineAmount * discountPercent / 100m, decimals);
        return (lineAmount, discount, lineAmount - discount);
    }

    /// <summary>Share of <paramref name="amount"/> for <paramref name="part"/> of <paramref name="whole"/>; the part that completes the whole gets the remainder.</summary>
    public static decimal Prorate(decimal amount, decimal alreadyTaken, decimal part, decimal partsTakenBefore, decimal whole, int decimals) =>
        whole == 0 ? 0 : partsTakenBefore + part >= whole ? amount - alreadyTaken : Round(amount * part / whole, decimals);

    /// <summary>
    /// Settles a sale. Cash rounding applies only when every payment is cash (a card pays the exact amount). Non-cash payments may
    /// never exceed the amount due; only cash produces change. Fails with a domain error when the payments are invalid.
    /// </summary>
    public static Settlement SettleSale(decimal gross, IReadOnlyList<PaymentInput> payments, decimal cashIncrement, int decimals)
    {
        Validate(payments);
        var allCash = payments.All(p => p.Method == PaymentMethod.Cash);
        var due = allCash ? Round(CashRound(gross, cashIncrement), decimals) : gross;
        var nonCash = payments.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount);
        var cash = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        if (nonCash > due)
        {
            throw new DomainException(PosErrors.OverpaidNonCash);
        }

        var tendered = nonCash + cash;
        if (tendered < due)
        {
            throw new DomainException(PosErrors.Underpaid);
        }

        var change = tendered - due;
        return new Settlement(due, tendered, change, cash - change, due - gross);
    }

    /// <summary>Settles a refund: payments out must equal the amount due exactly (cash rounding when all cash). No payments = all cash.</summary>
    public static (Settlement Settlement, IReadOnlyList<PaymentInput> Payments) SettleRefund(decimal gross, IReadOnlyList<PaymentInput>? payments, decimal cashIncrement, int decimals)
    {
        if (payments is null || payments.Count == 0)
        {
            var due = Round(CashRound(gross, cashIncrement), decimals);
            return (new Settlement(due, due, 0, due, due - gross), due > 0 ? [new PaymentInput(PaymentMethod.Cash, due)] : []);
        }

        Validate(payments);
        var allCash = payments.All(p => p.Method == PaymentMethod.Cash);
        var amountDue = allCash ? Round(CashRound(gross, cashIncrement), decimals) : gross;
        var total = payments.Sum(p => p.Amount);
        if (total != amountDue)
        {
            throw new DomainException(PosErrors.RefundPaymentMismatch);
        }

        var cash = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        return (new Settlement(amountDue, total, 0, cash, amountDue - gross), payments);
    }

    private static void Validate(IReadOnlyList<PaymentInput> payments)
    {
        if (payments.Count == 0)
        {
            throw new DomainException(PosErrors.NoPayments);
        }

        foreach (var payment in payments)
        {
            if (!Enum.IsDefined(payment.Method))
            {
                throw new DomainException(PosErrors.InvalidPaymentMethod);
            }

            if (payment.Amount <= 0)
            {
                throw new DomainException(PosErrors.InvalidAmount);
            }
        }
    }
}

public static class StalePricePolicies
{
    public const string Reject = "Reject";
    public const string AcceptServerPrices = "AcceptServerPrices";

    public static bool IsValid(string value) => value is Reject or AcceptServerPrices;
}

/// <summary>A till: where it sells from (warehouse → legal entity), at which prices, and into which cash/bank accounts.</summary>
[Audited]
public sealed class PosTerminal : AggregateRoot, IBranchScoped, IWarehouseScoped
{
    private PosTerminal()
    {
    }

    public PosTerminal(Guid companyId, Guid legalEntityId, Guid? branchId, Guid warehouseId, string code, string name, Guid priceListId, string currency,
        Guid cashAccountId, Guid? cardAccountId, Guid? walletAccountId, decimal maxDiscountPercent, string stalePricePolicy, Guid defaultCustomerId)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        Code = ValidCode(code);
        IsActive = true;
        Update(legalEntityId, branchId, warehouseId, name, priceListId, currency, cashAccountId, cardAccountId, walletAccountId, maxDiscountPercent, stalePricePolicy,
            defaultCustomerId);
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public Guid PriceListId { get; private set; }

    /// <summary>The terminal's selling currency (= the legal entity's functional currency and the price list currency).</summary>
    public string Currency { get; private set; } = string.Empty;

    /// <summary>Finance cash account (drawer) receiving cash payments.</summary>
    public Guid CashAccountId { get; private set; }

    /// <summary>Finance bank account used as card clearing; the BankAccount role when not set.</summary>
    public Guid? CardAccountId { get; private set; }

    public Guid? WalletAccountId { get; private set; }

    public decimal MaxDiscountPercent { get; private set; }

    /// <summary>Customer recorded on receipts that name none (the company's walk-in customer unless configured).</summary>
    public Guid DefaultCustomerId { get; private set; }

    public string StalePricePolicy { get; private set; } = StalePricePolicies.Reject;

    public string ReceiptPrefix => "POS";

    [Sensitive]
    public string? DeviceKeyHash { get; private set; }

    public DateTime? DeviceRegisteredAtUtc { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Highest offline receipt number handed to the terminal so far (numbers 1..this are valid offline numbers).</summary>
    public long OfflineAllocatedTo { get; private set; }

    /// <summary>Prefix of receipt numbers the terminal creates while offline (followed by the number from its allocated ranges).</summary>
    public string OfflinePrefix => $"OFF-{Code}-";

    /// <summary>Reserves the next block of offline receipt numbers for the device (it numbers receipts itself while offline).</summary>
    public (long From, long To) AllocateOfflineRange(int size)
    {
        if (size is < 1 or > 10000)
        {
            throw new DomainException(PosErrors.InvalidOfflineRange);
        }

        var from = OfflineAllocatedTo + 1;
        OfflineAllocatedTo += size;
        return (from, OfflineAllocatedTo);
    }

    /// <summary>True when <paramref name="number"/> is an offline number from one of the terminal's allocated ranges.</summary>
    public bool IsAllocatedOfflineNumber(string? number) =>
        number is not null && number.StartsWith(OfflinePrefix, StringComparison.Ordinal) && long.TryParse(number[OfflinePrefix.Length..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= OfflineAllocatedTo;

    public void Update(Guid legalEntityId, Guid? branchId, Guid warehouseId, string name, Guid priceListId, string currency, Guid cashAccountId,
        Guid? cardAccountId, Guid? walletAccountId, decimal maxDiscountPercent, string stalePricePolicy, Guid defaultCustomerId)
    {
        if (maxDiscountPercent is < 0 or > 100)
        {
            throw new DomainException(PosErrors.InvalidMaxDiscount);
        }

        if (!StalePricePolicies.IsValid(stalePricePolicy))
        {
            throw new DomainException(PosErrors.InvalidStalePricePolicy);
        }

        LegalEntityId = legalEntityId;
        BranchId = branchId;
        WarehouseId = warehouseId;
        Name = Guard.NotEmpty(name, "pos.name-required", "name").Trim();
        PriceListId = priceListId;
        Currency = currency.ToUpperInvariant();
        CashAccountId = cashAccountId;
        CardAccountId = cardAccountId;
        WalletAccountId = walletAccountId;
        MaxDiscountPercent = maxDiscountPercent;
        StalePricePolicy = stalePricePolicy;
        DefaultCustomerId = defaultCustomerId;
    }

    public void RegisterDevice(string keyHash, DateTime atUtc)
    {
        DeviceKeyHash = keyHash;
        DeviceRegisteredAtUtc = atUtc;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private static string ValidCode(string code)
    {
        var trimmed = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (trimmed.Length is 0 or > 12 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException(PosErrors.InvalidCode);
        }

        return trimmed;
    }
}

public enum ShiftStatus
{
    Open = 1,
    Closing = 2,
    Closed = 3,
}

public enum CashMovementKind
{
    PayIn = 1,
    PayOut = 2,
}

public sealed class ShiftCashMovement
{
    private ShiftCashMovement()
    {
    }

    public ShiftCashMovement(CashMovementKind kind, decimal amount, string reason, Guid? userId, DateTime atUtc)
    {
        Uid = SequentialGuid.New();
        Kind = kind;
        Amount = amount;
        Reason = reason;
        UserId = userId;
        AtUtc = atUtc;
    }

    public Guid Uid { get; private set; }

    public CashMovementKind Kind { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid? UserId { get; private set; }

    public DateTime AtUtc { get; private set; }
}

/// <summary>Expected (system) vs. counted (blind count) amount per payment method at close.</summary>
public sealed class ShiftCount
{
    private ShiftCount()
    {
    }

    public ShiftCount(PaymentMethod method, decimal expected, decimal counted)
    {
        Method = method;
        Expected = expected;
        Counted = counted;
        Variance = counted - expected;
    }

    public PaymentMethod Method { get; private set; }

    public decimal Expected { get; private set; }

    public decimal Counted { get; private set; }

    /// <summary>Counted − expected: negative is a shortage, positive an overage.</summary>
    public decimal Variance { get; private set; }
}

/// <summary>
/// A cashier's session on a terminal: Open → Closing → Closed, one open (non-closed) shift per terminal. Receipts are only
/// taken while Open. Sales totals are not kept on the shift (they are summed from receipts), so selling never updates it.
/// </summary>
[Audited]
public sealed class CashierShift : AggregateRoot, ICompanyScoped
{
    private CashierShift()
    {
    }

    public CashierShift(Guid companyId, Guid terminalId, Guid legalEntityId, Guid cashierUserId, decimal openingFloat, DateOnly businessDate, string currency, DateTime openedAtUtc)
        : base(SequentialGuid.New())
    {
        if (openingFloat < 0)
        {
            throw new DomainException(PosErrors.InvalidFloat);
        }

        CompanyId = companyId;
        TerminalId = terminalId;
        LegalEntityId = legalEntityId;
        CashierUserId = cashierUserId;
        OpeningFloat = openingFloat;
        BusinessDate = businessDate;
        Currency = currency;
        OpenedAtUtc = openedAtUtc;
        Status = ShiftStatus.Open;
        Number = "D-" + Id.ToString("N").ToUpperInvariant();
    }

    public Guid CompanyId { get; private set; }

    public Guid TerminalId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public Guid CashierUserId { get; private set; }

    public ShiftStatus Status { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal OpeningFloat { get; private set; }

    public DateTime OpenedAtUtc { get; private set; }

    public DateTime? ClosingStartedAtUtc { get; private set; }

    public DateTime? ClosedAtUtc { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public decimal CashVariance { get; private set; }

    public Guid? VarianceJournalId { get; private set; }

    public List<ShiftCashMovement> CashMovements { get; private set; } = [];

    public List<ShiftCount> Counts { get; private set; } = [];

    public decimal PayIns => CashMovements.Where(m => m.Kind == CashMovementKind.PayIn).Sum(m => m.Amount);

    public decimal PayOuts => CashMovements.Where(m => m.Kind == CashMovementKind.PayOut).Sum(m => m.Amount);

    public void AssignNumber(string number) => Number = number;

    public void AddCashMovement(CashMovementKind kind, decimal amount, string reason, Guid? userId, DateTime atUtc)
    {
        if (Status == ShiftStatus.Closed)
        {
            throw new DomainException(PosErrors.InvalidTransition("shift", Status.ToString(), "given cash movements"));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new DomainException(PosErrors.InvalidMovementKind);
        }

        if (amount <= 0)
        {
            throw new DomainException(PosErrors.InvalidAmount);
        }

        CashMovements.Add(new ShiftCashMovement(kind, amount, Guard.NotEmpty(reason, "pos.reason-required", "reason").Trim(), userId, atUtc));
    }

    public void StartClosing(DateTime atUtc)
    {
        if (Status != ShiftStatus.Open)
        {
            throw new DomainException(PosErrors.InvalidTransition("shift", Status.ToString(), "put into closing"));
        }

        Status = ShiftStatus.Closing;
        ClosingStartedAtUtc = atUtc;
    }

    /// <summary>
    /// Records the blind counts against the expected amounts (methods not counted count as 0) and closes the shift.
    /// Returns the cash variance (counted − expected).
    /// </summary>
    public decimal Close(IReadOnlyDictionary<PaymentMethod, decimal> expected, IReadOnlyDictionary<PaymentMethod, decimal> counted, Guid? userId, DateTime atUtc)
    {
        if (Status == ShiftStatus.Closed)
        {
            throw new DomainException(PosErrors.InvalidTransition("shift", Status.ToString(), "closed"));
        }

        if (counted.Values.Any(v => v < 0))
        {
            throw new DomainException(PosErrors.InvalidCount);
        }

        Counts = expected.Keys.Union(counted.Keys).Append(PaymentMethod.Cash).Distinct().OrderBy(m => m)
            .Select(m => new ShiftCount(m, expected.GetValueOrDefault(m), counted.GetValueOrDefault(m))).ToList();
        CashVariance = Counts.First(c => c.Method == PaymentMethod.Cash).Variance;
        Status = ShiftStatus.Closed;
        ClosingStartedAtUtc ??= atUtc;
        ClosedAtUtc = atUtc;
        ClosedByUserId = userId;
        return CashVariance;
    }

    public void RecordVarianceJournal(Guid journalId) => VarianceJournalId = journalId;
}

public enum ReceiptKind
{
    Sale = 1,
    Refund = 2,
}

public enum ReceiptStatus
{
    Completed = 1,
    PartiallyRefunded = 2,
    Refunded = 3,
}

public sealed class PosLineTax
{
    private PosLineTax()
    {
    }

    public PosLineTax(Guid taxCodeId, string code, decimal rate, decimal baseAmount, decimal amount)
    {
        TaxCodeId = taxCodeId;
        Code = code;
        Rate = rate;
        Base = baseAmount;
        Amount = amount;
    }

    public Guid TaxCodeId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public decimal Rate { get; private set; }

    public decimal Base { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>Tax already given back by refunds (sale lines only).</summary>
    public decimal RefundedAmount { get; private set; }

    public void AddRefunded(decimal amount) => RefundedAmount += amount;
}

/// <summary>One receipt line. Amounts are in the terminal currency; refunds reference the sale line in <see cref="OriginalLineUid"/>.</summary>
public sealed class PosReceiptLine
{
    private PosReceiptLine()
    {
    }

    public PosReceiptLine(Guid productId, Guid? variantId, string sku, string description, Guid uomId, decimal quantity, decimal baseQuantity,
        decimal unitPrice, decimal discountPercent, bool isStockable, bool isService, string postingGroup, Guid? originalLineUid)
    {
        if (quantity <= 0 || baseQuantity <= 0)
        {
            throw new DomainException(PosErrors.InvalidQuantity);
        }

        Uid = SequentialGuid.New();
        ProductId = productId;
        VariantId = variantId;
        Sku = sku;
        Description = description;
        UomId = uomId;
        Quantity = quantity;
        BaseQuantity = baseQuantity;
        UnitPrice = unitPrice;
        DiscountPercent = discountPercent;
        IsStockable = isStockable;
        IsService = isService;
        PostingGroup = postingGroup;
        OriginalLineUid = originalLineUid;
    }

    public Guid Uid { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? VariantId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public Guid UomId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercent { get; private set; }

    public decimal LineAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal GrossAmount { get; private set; }

    public bool IsStockable { get; private set; }

    public bool IsService { get; private set; }

    public string PostingGroup { get; private set; } = string.Empty;

    public List<PosLineTax> Taxes { get; private set; } = [];

    public string? BatchNumber { get; private set; }

    public string? Serials { get; private set; }

    public decimal CostAmount { get; private set; }

    public Guid? CostMovementId { get; private set; }

    public Guid? OriginalLineUid { get; private set; }

    public decimal RefundedQuantity { get; private set; }

    public decimal RefundedNetAmount { get; private set; }

    public decimal RefundableQuantity => Quantity - RefundedQuantity;

    public IReadOnlyList<string> SerialList => string.IsNullOrEmpty(Serials) ? [] : Serials.Split(',');

    public void SetAmounts(decimal lineAmount, decimal discountAmount, decimal net, IEnumerable<PosLineTax> taxes)
    {
        Taxes = taxes.ToList();
        LineAmount = lineAmount;
        DiscountAmount = discountAmount;
        NetAmount = net;
        TaxAmount = Taxes.Sum(t => t.Amount);
        GrossAmount = net + TaxAmount;
    }

    public void SetStockDetail(string? batchNumber, IEnumerable<string>? serials)
    {
        BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();
        var list = serials?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        Serials = list is { Count: > 0 } ? string.Join(',', list) : null;
    }

    public void RecordCost(decimal costAmount, Guid? movementId, string? batchNumber)
    {
        CostAmount = costAmount;
        CostMovementId = movementId;
        if (batchNumber is not null)
        {
            BatchNumber = batchNumber;
        }
    }

    /// <summary>
    /// Builds the refund line for <paramref name="quantity"/> of this sale line: amounts prorated from the sale, the refund that
    /// completes the line takes the remainder (so a fully refunded line nets to exactly zero). Updates the refunded totals.
    /// </summary>
    public PosReceiptLine Refund(decimal quantity, int decimals)
    {
        if (quantity <= 0)
        {
            throw new DomainException(PosErrors.InvalidQuantity);
        }

        if (quantity > RefundableQuantity)
        {
            throw new DomainException(PosErrors.RefundExceedsSold);
        }

        var baseQuantity = Math.Round(BaseQuantity * quantity / Quantity, 6, MidpointRounding.AwayFromZero);
        var refund = new PosReceiptLine(ProductId, VariantId, Sku, Description, UomId, quantity, baseQuantity, UnitPrice, DiscountPercent,
            IsStockable, IsService, PostingGroup, Uid);
        var net = PosMath.Prorate(NetAmount, RefundedNetAmount, quantity, RefundedQuantity, Quantity, decimals);
        var taxes = Taxes.Select(t =>
        {
            var amount = PosMath.Prorate(t.Amount, t.RefundedAmount, quantity, RefundedQuantity, Quantity, decimals);
            t.AddRefunded(amount);
            return new PosLineTax(t.TaxCodeId, t.Code, t.Rate, PosMath.Prorate(t.Base, 0, quantity, 0, Quantity, decimals), amount);
        }).ToList();
        refund.SetAmounts(PosMath.Prorate(LineAmount, 0, quantity, 0, Quantity, decimals), PosMath.Prorate(DiscountAmount, 0, quantity, 0, Quantity, decimals), net, taxes);
        refund.Batch(BatchNumber);
        RefundedQuantity += quantity;
        RefundedNetAmount += net;
        return refund;
    }

    private void Batch(string? batchNumber) => BatchNumber = batchNumber;
}

public sealed class PosPayment
{
    private PosPayment()
    {
    }

    public PosPayment(PaymentMethod method, decimal amount, string? reference, string? foreignCurrency = null, decimal? foreignAmount = null, decimal? exchangeRate = null)
    {
        Method = method;
        Amount = amount;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        ForeignCurrency = foreignCurrency;
        ForeignAmount = foreignAmount;
        ExchangeRate = exchangeRate;
    }

    public PaymentMethod Method { get; private set; }

    /// <summary>Tendered amount in the terminal currency (for cash, before change). Foreign cash: converted at the day rate.</summary>
    public decimal Amount { get; private set; }

    public string? Reference { get; private set; }

    /// <summary>Foreign cash tendered: its currency, amount and the rate used (terminal-currency units per unit).</summary>
    public string? ForeignCurrency { get; private set; }

    public decimal? ForeignAmount { get; private set; }

    public decimal? ExchangeRate { get; private set; }
}

/// <summary>
/// A POS sale or refund. Unique per terminal on <see cref="ClientReceiptId"/> (the client-generated id that makes resubmission
/// idempotent). Posted atomically when created: stock, the receipt journal, the number. No receivable is created.
/// </summary>
[Audited]
public sealed class PosReceipt : AggregateRoot, IBranchScoped, IWarehouseScoped
{
    private PosReceipt()
    {
    }

    public PosReceipt(Guid companyId, Guid legalEntityId, Guid? branchId, Guid terminalId, Guid shiftId, Guid warehouseId, Guid clientReceiptId,
        ReceiptKind kind, Guid customerId, string currency, Guid priceListId, string? priceVersion, bool taxInclusive, DateOnly businessDate,
        Guid? cashierUserId, DateTime createdAtUtc, Guid? originalReceiptId)
        : base(SequentialGuid.New())
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        BranchId = branchId;
        TerminalId = terminalId;
        ShiftId = shiftId;
        WarehouseId = warehouseId;
        ClientReceiptId = clientReceiptId;
        Kind = kind;
        CustomerId = customerId;
        Currency = currency;
        PriceListId = priceListId;
        PriceVersion = priceVersion;
        TaxInclusive = taxInclusive;
        BusinessDate = businessDate;
        CashierUserId = cashierUserId;
        CreatedAtUtc = createdAtUtc;
        OriginalReceiptId = originalReceiptId;
        Status = ReceiptStatus.Completed;
        Number = "D-" + Id.ToString("N").ToUpperInvariant();
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid TerminalId { get; private set; }

    public Guid ShiftId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid ClientReceiptId { get; private set; }

    public ReceiptKind Kind { get; private set; }

    public ReceiptStatus Status { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public Guid CustomerId { get; private set; }

    /// <summary>Employee credited with the sale (commissions); refunds inherit the sale's salesperson.</summary>
    public Guid? SalespersonId { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public Guid PriceListId { get; private set; }

    public string? PriceVersion { get; private set; }

    public bool TaxInclusive { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public Guid? CashierUserId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public Guid? OriginalReceiptId { get; private set; }

    public decimal NetTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal GrossTotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    /// <summary>Amount due − gross (cash rounding).</summary>
    public decimal RoundingAdjustment { get; private set; }

    public decimal AmountDue { get; private set; }

    public decimal Tendered { get; private set; }

    public decimal Change { get; private set; }

    /// <summary>Cash kept in (sale) or paid out of (refund) the drawer.</summary>
    public decimal CashNet { get; private set; }

    public Guid? JournalId { get; private set; }

    /// <summary>Created on the device while it was offline and synchronised later (numbered from the terminal's offline range).</summary>
    public bool IsOffline { get; private set; }

    public DateTime? OfflineCreatedAtUtc { get; private set; }

    public List<PosReceiptLine> Lines { get; private set; } = [];

    public List<PosPayment> Payments { get; private set; } = [];

    public void MarkOffline(DateTime createdOnDeviceUtc)
    {
        IsOffline = true;
        OfflineCreatedAtUtc = createdOnDeviceUtc;
    }

    public PosReceiptLine Line(Guid uid) => Lines.FirstOrDefault(l => l.Uid == uid) ?? throw new DomainException(PosErrors.LineNotFound);

    public void AssignSalesperson(Guid? salespersonId) => SalespersonId = salespersonId;

    public void SetLines(IReadOnlyList<PosReceiptLine> lines)
    {
        if (lines.Count == 0)
        {
            throw new DomainException(PosErrors.NoLines);
        }

        Lines = lines.ToList();
        NetTotal = Lines.Sum(l => l.NetAmount);
        TaxTotal = Lines.Sum(l => l.TaxAmount);
        GrossTotal = Lines.Sum(l => l.GrossAmount);
        DiscountTotal = Lines.Sum(l => l.DiscountAmount);
    }

    public void Settle(Settlement settlement, IEnumerable<PosPayment> payments)
    {
        AmountDue = settlement.AmountDue;
        Tendered = settlement.Tendered;
        Change = settlement.Change;
        CashNet = settlement.CashNet;
        RoundingAdjustment = settlement.RoundingAdjustment;
        Payments = payments.ToList();
    }

    public void Complete(string number, Guid? journalId)
    {
        Number = number;
        JournalId = journalId;
    }

    /// <summary>Sale status after a refund: Refunded when every line is fully refunded.</summary>
    public void UpdateRefundStatus()
    {
        if (Kind != ReceiptKind.Sale)
        {
            return;
        }

        Status = Lines.All(l => l.RefundableQuantity == 0) ? ReceiptStatus.Refunded
            : Lines.Any(l => l.RefundedQuantity > 0) ? ReceiptStatus.PartiallyRefunded : ReceiptStatus.Completed;
    }

    /// <summary>Amount paid with a method net of change (cash) — what the method's account received (sale) or paid (refund).</summary>
    public decimal NetPaid(PaymentMethod method) =>
        method == PaymentMethod.Cash ? CashNet : Payments.Where(p => p.Method == method).Sum(p => p.Amount);
}
