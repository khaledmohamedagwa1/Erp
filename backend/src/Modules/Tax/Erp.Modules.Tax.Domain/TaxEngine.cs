namespace Erp.Modules.Tax.Domain;

public sealed record TaxComponentInput(Guid TaxCodeId, string Code, TaxKind Kind, decimal RatePercent, bool CompoundsOnPrevious, bool Recoverable);

public sealed record TaxComputedLine(Guid TaxCodeId, string Code, TaxKind Kind, decimal RatePercent, decimal Base, decimal Amount, bool Recoverable);

public sealed record TaxComputation(decimal Net, decimal TotalTax, decimal Gross, decimal TotalWithholding, IReadOnlyList<TaxComputedLine> Lines);

/// <summary>
/// Pure tax arithmetic (no I/O). Rules:
/// <list type="bullet">
/// <item>Each tax line is rounded to the currency's minor units, half away from zero (per-line rounding, MR-6).</item>
/// <item>A compound component's base = net + all earlier non-withholding taxes.</item>
/// <item>Withholding components are computed on the net and reported, but never added to gross.</item>
/// <item>Tax-inclusive amounts are back-calculated; any rounding remainder goes to the net so Net + TotalTax == Gross exactly.</item>
/// </list>
/// </summary>
public static class TaxEngine
{
    public static TaxComputation Compute(decimal amount, bool taxInclusive, IReadOnlyList<TaxComponentInput> components, int minorUnits)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (minorUnits is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(minorUnits));
        }

        if (!taxInclusive)
        {
            return Exclusive(Round(amount, minorUnits), components, minorUnits);
        }

        var gross = Round(amount, minorUnits);
        var multiplier = 1m + TaxPerUnitOfNet(components);
        var estimatedNet = Round(gross / multiplier, minorUnits);
        var computed = Exclusive(estimatedNet, components, minorUnits);
        var remainder = gross - computed.Gross;
        return computed with { Net = computed.Net + remainder, Gross = gross };
    }

    private static TaxComputation Exclusive(decimal net, IReadOnlyList<TaxComponentInput> components, int minorUnits)
    {
        var lines = new List<TaxComputedLine>(components.Count);
        decimal additive = 0;
        decimal withholding = 0;
        foreach (var c in components)
        {
            if (c.Kind == TaxKind.Withholding)
            {
                var wht = Round(net * c.RatePercent / 100m, minorUnits);
                withholding += wht;
                lines.Add(new TaxComputedLine(c.TaxCodeId, c.Code, c.Kind, c.RatePercent, net, wht, c.Recoverable));
                continue;
            }

            var taxBase = c.CompoundsOnPrevious ? net + additive : net;
            var tax = Round(taxBase * c.RatePercent / 100m, minorUnits);
            additive += tax;
            lines.Add(new TaxComputedLine(c.TaxCodeId, c.Code, c.Kind, c.RatePercent, taxBase, tax, c.Recoverable));
        }

        return new TaxComputation(net, additive, net + additive, withholding, lines);
    }

    /// <summary>Unrounded tax per 1 unit of net, honouring compounding (used to back-calculate inclusive prices).</summary>
    private static decimal TaxPerUnitOfNet(IReadOnlyList<TaxComponentInput> components)
    {
        decimal additive = 0;
        foreach (var c in components.Where(c => c.Kind != TaxKind.Withholding))
        {
            var taxBase = c.CompoundsOnPrevious ? 1m + additive : 1m;
            additive += taxBase * c.RatePercent / 100m;
        }

        return additive;
    }

    public static decimal Round(decimal value, int minorUnits) => Math.Round(value, minorUnits, MidpointRounding.AwayFromZero);
}
