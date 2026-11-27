using System.Security.Cryptography;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Pos.Contracts;
using Erp.Modules.Pos.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Pos.Application;

public interface IPosDbContext
{
    DbSet<PosTerminal> Terminals { get; }

    DbSet<CashierShift> Shifts { get; }

    DbSet<PosReceipt> Receipts { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Row locks on the shared transaction (implemented in infrastructure with SQL on the existing row).</summary>
public interface IPosLocks
{
    /// <summary>
    /// Update-locks an existing shift row until the transaction ends. Receipts and the close take it, so a shift cannot close
    /// while a receipt is being posted into it. Receipts on different terminals lock different rows and never wait on each other.
    /// </summary>
    Task LockShiftAsync(Guid shiftId, CancellationToken cancellationToken);
}

public static class PosAppErrors
{
    public static readonly Error TerminalNotFound = Error.NotFound("pos.terminal-not-found", "The POS terminal was not found.");
    public static readonly Error ShiftNotFound = Error.NotFound("pos.shift-not-found", "The cashier shift was not found.");
    public static readonly Error ReceiptNotFound = Error.NotFound("pos.receipt-not-found", "The POS receipt was not found.");
    public static readonly Error TerminalInactive = Error.Conflict("pos.terminal-inactive", "The POS terminal is inactive.");
    public static readonly Error TerminalCodeTaken = Error.Conflict("pos.terminal-code-taken", "Another terminal already uses this code.");
    public static readonly Error TerminalHasOpenShift = Error.Conflict("pos.terminal-has-open-shift", "Close the terminal's open shift first.");
    public static readonly Error ShiftAlreadyOpen = Error.Conflict("pos.shift-already-open", "The terminal already has a shift that is not closed.");
    public static readonly Error NoOpenShift = Error.Conflict("pos.no-open-shift", "The terminal has no open shift; open one before selling.");
    public static readonly Error ShiftNotClosed = Error.Conflict("pos.shift-not-closed", "The Z report is only available after the shift is closed.");
    public static readonly Error WarehouseInvalid = Error.Validation("pos.warehouse-invalid", "The warehouse must be an active physical warehouse of this company.");
    public static readonly Error PriceListInvalid = Error.Validation("pos.price-list-invalid", "The price list was not found or is not valid today.");
    public static readonly Error CurrencyUnsupported = Error.Validation("pos.currency-unsupported", "POS sells in the legal entity's functional currency; the price list or account uses another currency.");
    public static readonly Error CashAccountInvalid = Error.Validation("pos.cash-account-invalid", "The cash account must be an active cash account of the warehouse's legal entity in its functional currency.");
    public static readonly Error PaymentAccountInvalid = Error.Validation("pos.payment-account-invalid", "The card/wallet account must be an active cash or bank account of the warehouse's legal entity in its functional currency.");
    public static readonly Error CustomerNotFound = Error.NotFound("pos.customer-not-found", "The customer was not found or is inactive.");
    public static readonly Error ProductInvalid = Error.Validation("pos.product-invalid", "A product was not found or is inactive.");
    public static readonly Error UomInvalid = Error.Validation("pos.uom-invalid", "The unit is not defined for the product.");
    public static readonly Error PriceNotFound = Error.Validation("pos.price-not-found", "The terminal's price list has no price for a line.");
    public static readonly Error DiscountForbidden = Error.Forbidden("pos.discount-forbidden", "You may not give discounts.");
    public static readonly Error DiscountExceedsLimit = Error.Validation("pos.discount-exceeds-limit", "The discount exceeds the terminal's maximum discount.");
    public static readonly Error DeviceKeyInvalid = Error.Forbidden("pos.device-key-invalid", "The terminal is registered to a device; the X-Pos-Device-Key header is missing or wrong.");
    public static readonly Error NotASale = Error.Validation("pos.not-a-sale", "Only sale receipts can be refunded.");
    public static readonly Error RefundTerminalMismatch = Error.Validation("pos.refund-terminal-mismatch", "Refunds must be given at a terminal of the same legal entity and currency.");
    public static readonly Error UnknownPaymentMethod = Error.Validation("pos.invalid-payment-method", "The payment method must be Cash, Card, Wallet or Other.");
    public static readonly Error PriceVersionStale = Error.Conflict("pos.price-version-stale", "Prices changed since the terminal's catalog snapshot; review the repriced cart and resubmit.");
}

public static class PosDevice
{
    public const string HeaderName = "X-Pos-Device-Key";

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>True when the terminal is not bound to a device, or the presented key matches (constant-time comparison).</summary>
    public static bool Accepts(PosTerminal terminal, string? presentedKey)
    {
        if (terminal.DeviceKeyHash is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(presentedKey)), Encoding.ASCII.GetBytes(terminal.DeviceKeyHash));
    }
}

public static class PosPaymentMethods
{
    public static Result<PaymentMethod> Parse(string? method) =>
        Enum.TryParse<PaymentMethod>(method, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(method, out _)
            ? parsed
            : PosAppErrors.UnknownPaymentMethod;
}

/// <summary>Where a payment method posts: a specific GL account (from the Finance cash/bank account) or a posting role.</summary>
public sealed record PaymentTarget(string? Role, Guid? AccountId);

public sealed class PosAccounts(ICashBankAccountQueries accounts)
{
    public async Task<IReadOnlyDictionary<PaymentMethod, PaymentTarget>> ResolveAsync(PosTerminal terminal, CancellationToken ct) => new Dictionary<PaymentMethod, PaymentTarget>
    {
        [PaymentMethod.Cash] = await TargetAsync(terminal.CashAccountId, PostingRoles.CashAccount, ct),
        [PaymentMethod.Card] = terminal.CardAccountId is { } card ? await TargetAsync(card, PostingRoles.BankAccount, ct) : new PaymentTarget(PostingRoles.BankAccount, null),
        [PaymentMethod.Wallet] = terminal.WalletAccountId is { } wallet ? await TargetAsync(wallet, PostingRoles.BankAccount, ct) : new PaymentTarget(PostingRoles.BankAccount, null),
        [PaymentMethod.Other] = new PaymentTarget(PostingRoles.BankAccount, null),
    };

    private async Task<PaymentTarget> TargetAsync(Guid cashBankAccountId, string fallbackRole, CancellationToken ct)
    {
        var account = await accounts.GetAsync(cashBankAccountId, ct);
        if (account?.GlAccountId is { } gl)
        {
            return new PaymentTarget(null, gl);
        }

        return new PaymentTarget(account?.Kind == "Bank" ? PostingRoles.BankAccount : account?.Kind == "Cash" ? PostingRoles.CashAccount : fallbackRole, null);
    }
}

/// <summary>Builds journal lines in the terminal currency (= functional currency, rate 1).</summary>
public sealed class PosJournal
{
    private readonly List<JournalLineRequest> _lines = [];

    public IReadOnlyList<JournalLineRequest> Lines => _lines;

    public PosJournal Add(string? role, Guid? accountId, decimal debit, decimal credit, PostingContext? context = null, string? description = null)
    {
        if (debit == 0 && credit == 0)
        {
            return this;
        }

        _lines.Add(new JournalLineRequest(accountId is null ? role : null, accountId, debit, credit, context, Description: description));
        return this;
    }

    /// <summary>
    /// Sale: Dr each payment method net of change / Cr revenue per posting group / Cr output tax per tax code / rounding to
    /// SalesDiscount. Refund: the mirror image with SalesReturns instead of revenue.
    /// </summary>
    public static PosJournal ForReceipt(PosReceipt receipt, IReadOnlyDictionary<PaymentMethod, PaymentTarget> targets)
    {
        var sale = receipt.Kind == ReceiptKind.Sale;
        var journal = new PosJournal();
        foreach (var method in Enum.GetValues<PaymentMethod>())
        {
            var amount = receipt.NetPaid(method);
            var target = targets[method];
            journal.Add(target.Role, target.AccountId, sale ? amount : 0, sale ? 0 : amount, description: $"{method} {receipt.Number}");
        }

        foreach (var group in receipt.Lines.GroupBy(l => (Role: sale ? (l.IsService ? PostingRoles.ServiceRevenue : PostingRoles.SalesRevenue) : PostingRoles.SalesReturns, l.PostingGroup)))
        {
            var amount = group.Sum(l => l.NetAmount);
            journal.Add(group.Key.Role, null, sale ? 0 : amount, sale ? amount : 0, new PostingContext(ProductPostingGroup: group.Key.PostingGroup));
        }

        foreach (var tax in receipt.Lines.SelectMany(l => l.Taxes).GroupBy(t => t.TaxCodeId))
        {
            var amount = tax.Sum(t => t.Amount);
            journal.Add(PostingRoles.OutputTax, null, sale ? 0 : amount, sale ? amount : 0, new PostingContext(TaxCodeId: tax.Key), tax.First().Code);
        }

        // Cash rounding: rounded up (more collected) is a credit on a sale; rounded down a debit. Mirrored for refunds.
        var rounding = receipt.RoundingAdjustment;
        journal.Add(PostingRoles.SalesDiscount, null, sale ? Math.Max(0, -rounding) : Math.Max(0, rounding), sale ? Math.Max(0, rounding) : Math.Max(0, -rounding),
            description: "Cash rounding");
        return journal;
    }
}

public sealed record PosLineInput(Guid ProductId, Guid? VariantId, Guid? UomId, decimal Quantity, decimal DiscountPercent = 0, string? BatchNumber = null,
    IReadOnlyList<string>? Serials = null);

public sealed record PosPaymentInput(string Method, decimal Amount, string? Reference = null);

public sealed record PricedCart(IReadOnlyList<PosReceiptLine> Lines, bool TaxInclusive)
{
    public decimal Gross => Lines.Sum(l => l.GrossAmount);
}

/// <summary>
/// Server-side pricing: unit prices always from the terminal's price list (the client's prices are never trusted), discount within
/// the permission and the terminal maximum, taxes from the tax engine by the product's tax category.
/// </summary>
public sealed class PosPricing(
    ICatalogQueries catalog,
    IPriceCalculator prices,
    ITaxCalculator taxes,
    ICurrentUser user,
    IPermissionService permissions)
{
    public async Task<Result<PricedCart>> PriceAsync(PosTerminal terminal, DateOnly date, IReadOnlyList<PosLineInput> inputs, int decimals, CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return PosErrors.NoLines;
        }

        var products = (await catalog.GetProductsAsync(inputs.Select(i => i.ProductId).Distinct().ToList(), ct)).ToDictionary(p => p.Id);
        var granted = user.UserId is { } userId ? await permissions.GetPermissionsAsync(userId, ct) : null;
        bool? inclusive = null;
        var lines = new List<PosReceiptLine>();
        foreach (var input in inputs)
        {
            if (!products.TryGetValue(input.ProductId, out var product) || !product.IsActive)
            {
                return PosAppErrors.ProductInvalid;
            }

            var uomId = input.UomId ?? product.BaseUomId;
            var factor = uomId == product.BaseUomId ? 1m : product.Units.FirstOrDefault(u => u.UomId == uomId)?.FactorToBase;
            if (factor is not > 0)
            {
                return PosAppErrors.UomInvalid;
            }

            if (input.Quantity <= 0)
            {
                return PosErrors.InvalidQuantity;
            }

            if (input.DiscountPercent is < 0 or > 100)
            {
                return PosErrors.InvalidDiscount;
            }

            if (input.DiscountPercent > 0 && granted is not null && !granted.Contains(PosPermissions.Discount))
            {
                return PosAppErrors.DiscountForbidden;
            }

            if (input.DiscountPercent > terminal.MaxDiscountPercent)
            {
                return PosAppErrors.DiscountExceedsLimit;
            }

            var quote = await prices.GetPriceAsync(terminal.PriceListId, product.Id, input.VariantId, uomId, input.Quantity, date, ct);
            if (quote is null)
            {
                return PosAppErrors.PriceNotFound;
            }

            if (!string.Equals(quote.Currency, terminal.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return PosAppErrors.CurrencyUnsupported;
            }

            inclusive ??= quote.TaxInclusive;
            (decimal LineAmount, decimal Discount, decimal AfterDiscount) amounts;
            try
            {
                amounts = PosMath.LineAmounts(input.Quantity, quote.UnitPrice, input.DiscountPercent, decimals);
            }
            catch (DomainException ex)
            {
                return ex.Error;
            }

            var tax = await taxes.CalculateForCategoryAsync(product.TaxCategory, TaxDirection.Sales, amounts.AfterDiscount, quote.TaxInclusive, terminal.Currency, date, ct);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            var line = new PosReceiptLine(product.Id, input.VariantId, product.Sku, product.NameEn, uomId, input.Quantity,
                Math.Round(input.Quantity * factor.Value, 6, MidpointRounding.AwayFromZero), quote.UnitPrice, input.DiscountPercent,
                product.IsStockable, product.Type == "Service", product.PostingGroup, null);
            line.SetAmounts(amounts.LineAmount, amounts.Discount, tax.Value.Net,
                tax.Value.Lines.Where(l => l.Kind != TaxKindCode.Withholding).Select(l => new PosLineTax(l.TaxCodeId, l.Code, l.Rate, l.Base, l.Amount)));
            line.SetStockDetail(input.BatchNumber, input.Serials);
            lines.Add(line);
        }

        return new PricedCart(lines, inclusive ?? false);
    }
}

// ============================ DTOs ============================

public sealed record TerminalDto(
    Guid Id,
    string Code,
    string Name,
    Guid LegalEntityId,
    Guid? BranchId,
    Guid WarehouseId,
    Guid PriceListId,
    string Currency,
    Guid CashAccountId,
    Guid? CardAccountId,
    Guid? WalletAccountId,
    decimal MaxDiscountPercent,
    string StalePricePolicy,
    Guid DefaultCustomerId,
    bool DeviceRegistered,
    DateTime? DeviceRegisteredAtUtc,
    bool IsActive,
    string Version);

public sealed record DeviceRegistrationDto(Guid TerminalId, string DeviceKey, string HeaderName);

public sealed record CashMovementDto(Guid Id, string Kind, decimal Amount, string Reason, Guid? UserId, DateTime AtUtc);

public sealed record ShiftCountDto(string Method, decimal Expected, decimal Counted, decimal Variance);

public sealed record ShiftDto(
    Guid Id,
    string Number,
    Guid TerminalId,
    Guid CashierUserId,
    string Status,
    DateOnly BusinessDate,
    string Currency,
    decimal OpeningFloat,
    DateTime OpenedAtUtc,
    DateTime? ClosingStartedAtUtc,
    DateTime? ClosedAtUtc,
    decimal CashVariance,
    Guid? VarianceJournalId,
    IReadOnlyList<CashMovementDto> CashMovements,
    IReadOnlyList<ShiftCountDto> Counts);

public sealed record MethodTotalDto(string Method, decimal Sales, decimal Refunds, decimal Expected, decimal? Counted, decimal? Variance);

/// <summary>X report (any time, running) or Z report (final, after close) of a shift.</summary>
public sealed record ShiftReportDto(
    string ReportType,
    Guid ShiftId,
    string ShiftNumber,
    Guid TerminalId,
    string TerminalCode,
    string Status,
    DateOnly BusinessDate,
    string Currency,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    int SalesCount,
    int RefundCount,
    decimal GrossSales,
    decimal GrossRefunds,
    decimal NetSales,
    decimal TaxTotal,
    decimal DiscountTotal,
    decimal RoundingTotal,
    decimal OpeningFloat,
    decimal PayIns,
    decimal PayOuts,
    IReadOnlyList<MethodTotalDto> Methods,
    decimal CashVariance,
    Guid? VarianceJournalId,
    DateTime GeneratedAtUtc);

public sealed record ReceiptLineTaxDto(Guid TaxCodeId, string Code, decimal Rate, decimal Base, decimal Amount);

public sealed record ReceiptLineDto(
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
    IReadOnlyList<ReceiptLineTaxDto> Taxes,
    decimal CostAmount,
    decimal RefundedQuantity,
    Guid? OriginalLineId,
    string? BatchNumber,
    IReadOnlyList<string> Serials);

public sealed record ReceiptPaymentDto(string Method, decimal Amount, string? Reference);

public sealed record ReceiptDto(
    Guid Id,
    string Number,
    string Kind,
    string Status,
    Guid TerminalId,
    Guid ShiftId,
    Guid ClientReceiptId,
    Guid CustomerId,
    string Currency,
    DateOnly BusinessDate,
    string? PriceVersion,
    bool TaxInclusive,
    decimal NetTotal,
    decimal TaxTotal,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal RoundingAdjustment,
    decimal AmountDue,
    decimal Tendered,
    decimal Change,
    Guid? OriginalReceiptId,
    Guid? JournalId,
    DateTime CreatedAtUtc,
    IReadOnlyList<ReceiptLineDto> Lines,
    IReadOnlyList<ReceiptPaymentDto> Payments,
    bool Replayed = false);

/// <summary>The cart as the server prices it now (returned with 409 when the client's price version is stale).</summary>
public sealed record RepricedCartDto(string PriceVersion, bool TaxInclusive, decimal NetTotal, decimal TaxTotal, decimal GrossTotal, IReadOnlyList<ReceiptLineDto> Lines);

public sealed record ReceiptOutcome(ReceiptDto? Receipt, RepricedCartDto? Repriced);

public sealed record SnapshotProductDto(Guid Id, string Sku, string NameEn, string NameAr, string Type, bool IsStockable, Guid BaseUomId,
    IReadOnlyList<ProductUnitInfo> Units, string Tracking, string TaxCategory);

public sealed record CatalogSnapshotDto(
    Guid TerminalId,
    Guid PriceListId,
    string Currency,
    bool TaxInclusive,
    string PriceVersion,
    decimal CashRoundingIncrement,
    int MinorUnits,
    decimal MaxDiscountPercent,
    DateOnly Date,
    DateTime GeneratedAtUtc,
    IReadOnlyList<SnapshotProductDto> Products,
    IReadOnlyList<PriceListSnapshotItem> Prices,
    IReadOnlyList<BarcodeInfo> Barcodes);

public static class PosMapping
{
    public static string Version(IPosDbContext db, object entity) =>
        Convert.ToBase64String((byte[]?)db.Entry(entity).Property("RowVersion").CurrentValue ?? []);

    public static bool VersionMatches(IPosDbContext db, object entity, byte[] expected)
    {
        var current = (byte[]?)db.Entry(entity).Property("RowVersion").CurrentValue ?? [];
        if (!current.AsSpan().SequenceEqual(expected))
        {
            return false;
        }

        db.Entry(entity).Property("RowVersion").OriginalValue = expected;
        return true;
    }

    public static TerminalDto ToDto(IPosDbContext db, PosTerminal t) => new(t.Id, t.Code, t.Name, t.LegalEntityId, t.BranchId, t.WarehouseId, t.PriceListId,
        t.Currency, t.CashAccountId, t.CardAccountId, t.WalletAccountId, t.MaxDiscountPercent, t.StalePricePolicy, t.DefaultCustomerId, t.DeviceKeyHash is not null,
        t.DeviceRegisteredAtUtc, t.IsActive, Version(db, t));

    public static ShiftDto ToDto(CashierShift s) => new(s.Id, s.Number, s.TerminalId, s.CashierUserId, s.Status.ToString(), s.BusinessDate, s.Currency,
        s.OpeningFloat, s.OpenedAtUtc, s.ClosingStartedAtUtc, s.ClosedAtUtc, s.CashVariance, s.VarianceJournalId,
        s.CashMovements.Select(m => new CashMovementDto(m.Uid, m.Kind.ToString(), m.Amount, m.Reason, m.UserId, m.AtUtc)).ToList(),
        s.Counts.Select(c => new ShiftCountDto(c.Method.ToString(), c.Expected, c.Counted, c.Variance)).ToList());

    public static ReceiptLineDto ToDto(PosReceiptLine l) => new(l.Uid, l.ProductId, l.VariantId, l.Sku, l.Description, l.UomId, l.Quantity, l.BaseQuantity,
        l.UnitPrice, l.DiscountPercent, l.LineAmount, l.DiscountAmount, l.NetAmount, l.TaxAmount, l.GrossAmount,
        l.Taxes.Select(t => new ReceiptLineTaxDto(t.TaxCodeId, t.Code, t.Rate, t.Base, t.Amount)).ToList(), l.CostAmount, l.RefundedQuantity,
        l.OriginalLineUid, l.BatchNumber, l.SerialList);

    public static ReceiptDto ToDto(PosReceipt r, bool replayed = false) => new(r.Id, r.Number, r.Kind.ToString(), r.Status.ToString(), r.TerminalId, r.ShiftId,
        r.ClientReceiptId, r.CustomerId, r.Currency, r.BusinessDate, r.PriceVersion, r.TaxInclusive, r.NetTotal, r.TaxTotal, r.GrossTotal, r.DiscountTotal,
        r.RoundingAdjustment, r.AmountDue, r.Tendered, r.Change, r.OriginalReceiptId, r.JournalId, r.CreatedAtUtc, r.Lines.Select(ToDto).ToList(),
        r.Payments.Select(p => new ReceiptPaymentDto(p.Method.ToString(), p.Amount, p.Reference)).ToList(), replayed);
}

internal static class DuplicateKey
{
    /// <summary>
    /// True for a unique-index violation (SQL Server 2601/2627). Read by reflection because the application layer must not
    /// reference the SQL client.
    /// </summary>
    public static bool Is(DbUpdateException exception)
    {
        for (Exception? e = exception.InnerException; e is not null; e = e.InnerException)
        {
            if (e.GetType().GetProperty("Number")?.GetValue(e) is int number && number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }
}
