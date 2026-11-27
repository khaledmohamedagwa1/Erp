namespace Erp.Modules.Procurement.Domain;

public static class Money
{
    public static decimal Round(decimal value, int decimals = 2) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static decimal Cost(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    public static decimal Quantity(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

/// <summary>Outcome of matching one invoice line against its receipt and order (three-way match).</summary>
public sealed record MatchResult(bool QuantityException, bool PriceException, decimal PriceVariancePercent)
{
    public bool HasException => QuantityException || PriceException;
}

/// <summary>
/// Three-way match (decisions OQ-PUR-4): the invoiced quantity may exceed what is still invoiceable on the receipt by at most
/// the quantity tolerance, and the invoiced unit price may differ from the order price by at most the price tolerance.
/// </summary>
public static class ThreeWayMatch
{
    public static MatchResult Evaluate(decimal invoicedQuantity, decimal invoiceUnitPrice, decimal invoiceableQuantity, decimal orderUnitPrice,
        decimal quantityTolerancePercent, decimal priceTolerancePercent)
    {
        var quantityLimit = invoiceableQuantity * (1 + (quantityTolerancePercent / 100m));
        var quantityException = invoicedQuantity > Money.Quantity(quantityLimit);

        var variance = orderUnitPrice == 0
            ? (invoiceUnitPrice == 0 ? 0m : 100m)
            : Math.Round((invoiceUnitPrice - orderUnitPrice) / orderUnitPrice * 100m, 4, MidpointRounding.AwayFromZero);
        var priceException = Math.Abs(variance) > priceTolerancePercent;
        return new MatchResult(quantityException, priceException, variance);
    }
}

public enum AllocationBasis
{
    Value = 1,
    Quantity = 2,
}

/// <summary>Splits an amount over weights so the parts sum exactly to the total (residual on the largest part).</summary>
public static class Allocator
{
    public static IReadOnlyList<decimal> Distribute(decimal total, IReadOnlyList<decimal> weights, int decimals = 2)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        var sum = weights.Sum();
        if (sum <= 0)
        {
            throw new ArgumentException("Allocation weights must be positive.", nameof(weights));
        }

        var parts = weights.Select(w => Money.Round(total * w / sum, decimals)).ToArray();
        var residual = total - parts.Sum();
        if (residual != 0)
        {
            var largest = Array.IndexOf(parts, parts.OrderByDescending(Math.Abs).First());
            parts[largest] += residual;
        }

        return parts;
    }

    /// <summary>Landed-cost allocation by value or quantity of the receipt lines.</summary>
    public static IReadOnlyList<decimal> LandedCost(decimal total, IReadOnlyList<(decimal Quantity, decimal Value)> lines, AllocationBasis basis) =>
        Distribute(total, lines.Select(l => basis == AllocationBasis.Value ? l.Value : l.Quantity).ToList());
}

/// <summary>
/// Converts document amounts to the functional currency so the journal balances exactly against the open item, whose base
/// amount Finance computes as round(gross × rate, 2): the rounding residual goes to the largest debit line.
/// </summary>
public static class BaseAmounts
{
    public static decimal[] FitToTotal(IReadOnlyList<decimal> transactionAmounts, decimal rate, decimal baseTotal)
    {
        var parts = transactionAmounts.Select(a => Money.Round(a * rate)).ToArray();
        var residual = baseTotal - parts.Sum();
        if (residual != 0 && parts.Length > 0)
        {
            var largest = Array.IndexOf(parts, parts.OrderByDescending(Math.Abs).First());
            parts[largest] += residual;
        }

        return parts;
    }
}
