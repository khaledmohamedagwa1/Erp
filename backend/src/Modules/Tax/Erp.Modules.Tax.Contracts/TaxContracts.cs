using Erp.SharedKernel.Results;

namespace Erp.Modules.Tax.Contracts;

public enum TaxDirection
{
    Sales = 1,
    Purchase = 2,
}

public enum TaxKindCode
{
    /// <summary>Tax charged to customers (liability).</summary>
    Output = 1,

    /// <summary>Tax paid to suppliers (recoverable asset when <c>Recoverable</c>).</summary>
    Input = 2,

    /// <summary>Withheld at payment/receipt. Reported on a line but never added to the gross amount.</summary>
    Withholding = 3,
}

public enum ExchangeRateKind
{
    Spot = 1,
    Average = 2,
    Closing = 3,
}

public sealed record TaxLineResult(Guid TaxCodeId, string Code, TaxKindCode Kind, decimal Rate, decimal Base, decimal Amount, bool Recoverable);

/// <summary>
/// Result of a tax calculation. <c>Net + TotalTax == Gross</c> always holds exactly; withholding lines are listed
/// but excluded from <c>TotalTax</c>/<c>Gross</c> (they reduce the amount paid, not the invoice total).
/// </summary>
public sealed record TaxResult(decimal Net, decimal TotalTax, decimal Gross, decimal TotalWithholding, IReadOnlyList<TaxLineResult> Lines);

public sealed record CurrencyInfo(string Code, string NameEn, string NameAr, int MinorUnits, decimal CashRoundingIncrement);

/// <summary>Calculates taxes for documents of other modules. Requires the active company context.</summary>
public interface ITaxCalculator
{
    Task<Result<TaxResult>> CalculateAsync(Guid taxGroupId, decimal amount, bool taxInclusive, string currency, DateOnly date, CancellationToken cancellationToken = default);

    Task<Result<TaxResult>> CalculateForCategoryAsync(string taxCategoryCode, TaxDirection direction, decimal amount, bool taxInclusive, string currency, DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Tax group mapped to a product tax category for the given direction, or null when unmapped.</summary>
    Task<Guid?> ResolveTaxGroupAsync(string taxCategoryCode, TaxDirection direction, CancellationToken cancellationToken = default);
}

public interface IExchangeRateProvider
{
    /// <summary>
    /// Units of <paramref name="to"/> per 1 unit of <paramref name="from"/>, using the latest rate effective on or before
    /// <paramref name="date"/>. Same currency → 1. Falls back to the inverse pair (rounded to 8 decimals). Null when none exists.
    /// </summary>
    Task<decimal?> GetRateAsync(string from, string to, DateOnly date, ExchangeRateKind kind = ExchangeRateKind.Spot, CancellationToken cancellationToken = default);
}

public interface ICurrencyQueries
{
    Task<CurrencyInfo?> GetAsync(string code, CancellationToken cancellationToken = default);
}

public static class TaxPermissions
{
    public const string Read = "tax.read";
    public const string Manage = "tax.manage";
}
