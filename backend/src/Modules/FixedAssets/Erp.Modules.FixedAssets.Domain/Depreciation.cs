using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.FixedAssets.Domain;

public enum DepreciationMethod
{
    StraightLine = 1,

    /// <summary>Annual rate applied monthly to the net book value; switches to straight-line once that gives more.</summary>
    DecliningBalance = 2,

    /// <summary>Depreciation follows recorded usage: (cost − salvage) × units used ÷ total expected units.</summary>
    UnitsOfProduction = 3,
}

public static class AssetErrors
{
    public static readonly Error NotDraft = Error.Conflict("assets.not-draft", "Only draft assets can be changed this way.");
    public static readonly Error NotActive = Error.Conflict("assets.not-active", "The asset is not active.");
    public static readonly Error Disposed = Error.Conflict("assets.disposed", "The asset has been disposed.");
    public static readonly Error CostInvalid = Error.Validation("assets.cost-invalid", "Acquisition cost must be greater than zero.");
    public static readonly Error SalvageInvalid = Error.Validation("assets.salvage-invalid", "Salvage value must be between zero and the cost.");
    public static readonly Error LifeInvalid = Error.Validation("assets.useful-life-invalid", "Useful life must be between 1 and 1200 months.");
    public static readonly Error RateInvalid = Error.Validation("assets.rate-invalid", "Declining-balance rate must be between 0 and 100 percent.");
    public static readonly Error UnitsInvalid = Error.Validation("assets.units-invalid", "Units-of-production assets need total expected units greater than zero.");
    public static readonly Error DatesInvalid = Error.Validation("assets.dates-invalid", "The in-service date cannot be before the acquisition date.");
    public static readonly Error NotUnitsOfProduction = Error.Conflict("assets.not-units-of-production", "Usage can only be recorded for units-of-production assets.");
    public static readonly Error PeriodPosted = Error.Conflict("assets.period-posted", "Depreciation for this period is already posted.");
    public static readonly Error PeriodBeforeService = Error.Validation("assets.period-before-service", "The period is before the asset's first depreciation month.");
    public static readonly Error DepreciationNotUpToDate = Error.Conflict("assets.depreciation-not-up-to-date",
        "Post depreciation for all months before the disposal month first.");
    public static readonly Error ValueBelowSalvage = Error.Validation("assets.value-below-salvage", "The new net book value cannot be below the salvage value.");
    public static readonly Error NoRemainingLife = Error.Validation("assets.no-remaining-life", "No useful life remains; give a new useful life to spread the revalued amount.");
    public static readonly Error CategoryCodeInvalid = Error.Validation("assets.category-code-invalid", "Category code is required (max 30 characters).");
}

/// <summary>Money and calendar helpers. Amounts round to 2 decimals, half away from zero.</summary>
public static class AssetMath
{
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    public static DateOnly MonthEnd(DateOnly period) => MonthStart(period).AddMonths(1).AddDays(-1);

    /// <summary>
    /// Mid-month convention: an asset put in service on or before the 15th depreciates for that whole month;
    /// after the 15th, depreciation starts the next month.
    /// </summary>
    public static DateOnly FirstDepreciationMonth(DateOnly inServiceDate) =>
        inServiceDate.Day <= 15 ? MonthStart(inServiceDate) : MonthStart(inServiceDate).AddMonths(1);
}

public sealed record ScheduleInput(
    DepreciationMethod Method,
    decimal Cost,
    decimal Salvage,
    decimal AccumulatedBefore,
    DateOnly FirstMonth,
    int Months,
    decimal? AnnualRatePercent = null);

/// <summary>Pure depreciation calculations. The last period absorbs rounding so the asset ends exactly at salvage value.</summary>
public static class DepreciationCalculator
{
    public static IReadOnlyList<(DateOnly Period, decimal Amount)> Build(ScheduleInput input)
    {
        var depreciable = input.Cost - input.Salvage - input.AccumulatedBefore;
        if (input.Months <= 0 || depreciable <= 0 || input.Method == DepreciationMethod.UnitsOfProduction)
        {
            return [];
        }

        var result = new List<(DateOnly, decimal)>(input.Months);
        var period = AssetMath.MonthStart(input.FirstMonth);
        if (input.Method == DepreciationMethod.StraightLine)
        {
            var monthly = AssetMath.Money(depreciable / input.Months);
            decimal sum = 0;
            for (var i = 0; i < input.Months; i++)
            {
                var amount = i == input.Months - 1 ? depreciable - sum : Math.Min(monthly, depreciable - sum);
                result.Add((period.AddMonths(i), amount));
                sum += amount;
            }

            return result;
        }

        // Declining balance with switch to straight-line: each month takes the larger of the declining amount and the
        // straight-line amount over the remaining months, never going below salvage.
        var monthlyRate = (input.AnnualRatePercent ?? 0) / 100m / 12m;
        var nbv = input.Cost - input.AccumulatedBefore;
        for (var i = 0; i < input.Months; i++)
        {
            var remainingMonths = input.Months - i;
            var remainingDepreciable = nbv - input.Salvage;
            decimal amount;
            if (i == input.Months - 1)
            {
                amount = remainingDepreciable;
            }
            else
            {
                var declining = AssetMath.Money(nbv * monthlyRate);
                var straight = AssetMath.Money(remainingDepreciable / remainingMonths);
                amount = Math.Min(Math.Max(declining, straight), remainingDepreciable);
            }

            result.Add((period.AddMonths(i), amount));
            nbv -= amount;
        }

        return result;
    }

    /// <summary>Units-of-production amount for <paramref name="units"/>, capped at what is still depreciable.</summary>
    public static decimal UnitsOfProduction(decimal cost, decimal salvage, decimal totalUnits, decimal units, decimal stillDepreciable)
    {
        if (totalUnits <= 0 || units <= 0 || stillDepreciable <= 0)
        {
            return 0;
        }

        return Math.Min(AssetMath.Money((cost - salvage) * units / totalUnits), stillDepreciable);
    }
}
