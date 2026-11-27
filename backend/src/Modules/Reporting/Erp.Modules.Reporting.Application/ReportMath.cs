using System.Globalization;

namespace Erp.Modules.Reporting.Application;

/// <summary>Pure calculations shared by the report builders (kept separate so they can be unit tested).</summary>
public static class ReportMath
{
    /// <summary>Upper bounds (days, inclusive) of the stock-aging buckets; the last bucket is open-ended.</summary>
    public static readonly int[] AgingBounds = [30, 60, 90, 180];

    public static readonly string[] AgingLabels = ["0-30", "31-60", "61-90", "91-180", "181+"];

    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Quantity(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    /// <summary>Profit as a percentage of revenue (2 decimals); 0 when there is no revenue.</summary>
    public static decimal Margin(decimal revenue, decimal profit) => revenue == 0 ? 0 : Math.Round(profit / revenue * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Part as a percentage of a whole (2 decimals); 0 when the whole is 0.</summary>
    public static decimal Percent(decimal part, decimal whole) => whole == 0 ? 0 : Math.Round(part / whole * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Period label for grouping: <c>day</c> → yyyy-MM-dd, <c>month</c> → yyyy-MM, <c>year</c> → yyyy.</summary>
    public static string Period(DateOnly date, string groupBy) => groupBy switch
    {
        "day" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "year" => date.ToString("yyyy", CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Ages on-hand stock by receipt date assuming first-in-first-out: the quantity on hand is taken from the newest inbound
    /// movements backwards. Inbound movements must be ordered newest first. Quantity not explained by inbound movements
    /// (e.g. data before the ledger started) is placed in the oldest bucket. Returns one quantity per bucket of
    /// <see cref="AgingBounds"/> plus the open-ended last bucket.
    /// </summary>
    public static decimal[] AgeStock(decimal onHand, IEnumerable<(DateOnly Date, decimal Quantity)> inboundNewestFirst, DateOnly asOf)
    {
        var buckets = new decimal[AgingBounds.Length + 1];
        if (onHand <= 0)
        {
            return buckets;
        }

        var remaining = onHand;
        foreach (var (date, quantity) in inboundNewestFirst)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (quantity <= 0)
            {
                continue;
            }

            var take = Math.Min(remaining, quantity);
            buckets[BucketOf(asOf.DayNumber - date.DayNumber)] += take;
            remaining -= take;
        }

        if (remaining > 0)
        {
            buckets[^1] += remaining;
        }

        return buckets;
    }

    public static int BucketOf(int ageDays)
    {
        for (var i = 0; i < AgingBounds.Length; i++)
        {
            if (ageDays <= AgingBounds[i])
            {
                return i;
            }
        }

        return AgingBounds.Length;
    }
}
