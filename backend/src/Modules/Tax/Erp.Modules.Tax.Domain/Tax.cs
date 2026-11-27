using System.Text.RegularExpressions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Tax.Domain;

public enum TaxKind
{
    Output = 1,
    Input = 2,
    Withholding = 3,
}

public enum TaxDirectionKind
{
    Sales = 1,
    Purchase = 2,
}

public enum RateType
{
    Spot = 1,
    Average = 2,
    Closing = 3,
}

public static partial class TaxRules
{
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,29}$")]
    private static partial Regex CodePattern();

    public static string CurrencyCode(string? value)
    {
        var code = Guard.NotEmpty(value, "tax.currency-required", "Currency").ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(code))
        {
            throw new DomainException(Error.Validation("tax.currency-invalid", "Currency must be a 3-letter ISO 4217 code."));
        }

        return code;
    }

    public static string Code(string? value, string field)
    {
        var code = Guard.NotEmpty(value, "tax.code-required", field).ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainException(Error.Validation("tax.code-invalid", $"{field} must be 1–30 characters: letters, digits, '-' or '_'."));
        }

        return code;
    }

    public static string Name(string? value, string field) =>
        Guard.MaxLength(Guard.NotEmpty(value, "tax.name-required", field), 200, "tax.name-too-long", field);

    public static decimal Percent(decimal value)
    {
        if (value is < 0 or > 100)
        {
            throw new DomainException(Error.Validation("tax.rate-invalid", "Tax rate must be between 0 and 100 percent."));
        }

        return value;
    }
}

[Audited]
public sealed class Currency : AggregateRoot, ITenantScoped
{
    private Currency()
    {
    }

    private Currency(Guid id, string code, string nameEn, string nameAr, int minorUnits, decimal cashRoundingIncrement)
        : base(id)
    {
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
        MinorUnits = minorUnits;
        CashRoundingIncrement = cashRoundingIncrement;
    }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    /// <summary>Decimal places of the smallest unit (EGP 2, KWD 3, JPY 0). Amounts are rounded to this.</summary>
    public int MinorUnits { get; private set; }

    /// <summary>Smallest cash denomination used for POS cash rounding (e.g. 0.05). Equal to one minor unit when no cash rounding applies.</summary>
    public decimal CashRoundingIncrement { get; private set; }

    public static Currency Create(string code, string nameEn, string nameAr, int minorUnits, decimal? cashRoundingIncrement)
    {
        if (minorUnits is < 0 or > 4)
        {
            throw new DomainException(Error.Validation("tax.minor-units-invalid", "Minor units must be between 0 and 4."));
        }

        var oneUnit = 1m / (decimal)Math.Pow(10, minorUnits);
        var increment = cashRoundingIncrement ?? oneUnit;
        if (increment <= 0 || increment < oneUnit)
        {
            throw new DomainException(Error.Validation("tax.cash-rounding-invalid", "Cash rounding must be at least one minor unit."));
        }

        return new Currency(SequentialGuid.New(), TaxRules.CurrencyCode(code), TaxRules.Name(nameEn, "English name"),
            TaxRules.Name(nameAr, "Arabic name"), minorUnits, increment);
    }
}

/// <summary>Rate = units of <see cref="ToCurrency"/> (normally the base currency) per 1 unit of <see cref="FromCurrency"/>.</summary>
[Audited]
public sealed class ExchangeRate : AggregateRoot, ITenantScoped
{
    private ExchangeRate()
    {
    }

    private ExchangeRate(Guid id, string from, string to, RateType type, DateOnly effectiveDate, decimal rate)
        : base(id)
    {
        FromCurrency = from;
        ToCurrency = to;
        Type = type;
        EffectiveDate = effectiveDate;
        Rate = rate;
    }

    public string FromCurrency { get; private set; } = string.Empty;

    public string ToCurrency { get; private set; } = string.Empty;

    public RateType Type { get; private set; }

    public DateOnly EffectiveDate { get; private set; }

    public decimal Rate { get; private set; }

    public static ExchangeRate Create(string from, string to, RateType type, DateOnly effectiveDate, decimal rate)
    {
        var f = TaxRules.CurrencyCode(from);
        var t = TaxRules.CurrencyCode(to);
        if (f == t)
        {
            throw new DomainException(Error.Validation("tax.exchange-rate-same-currency", "From and to currency must differ."));
        }

        return new ExchangeRate(SequentialGuid.New(), f, t, type, effectiveDate, Positive(rate));
    }

    public void ChangeRate(decimal rate) => Rate = Positive(rate);

    private static decimal Positive(decimal rate) =>
        rate > 0 ? Math.Round(rate, 8, MidpointRounding.AwayFromZero)
            : throw new DomainException(Error.Validation("tax.exchange-rate-invalid", "Exchange rate must be greater than zero."));
}

[Audited]
public sealed class TaxCode : AggregateRoot, ICompanyScoped
{
    private readonly List<TaxRate> _rates = [];

    private TaxCode()
    {
    }

    private TaxCode(Guid id, Guid companyId, string code, string nameEn, string nameAr, TaxKind kind, bool recoverable, string? reportingBox, string? jurisdiction)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
        Kind = kind;
        Recoverable = recoverable;
        ReportingBox = reportingBox;
        Jurisdiction = jurisdiction;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public TaxKind Kind { get; private set; }

    /// <summary>Input tax that can be reclaimed. Non-recoverable input tax is added to cost (OQ-PUR-5).</summary>
    public bool Recoverable { get; private set; }

    public string? ReportingBox { get; private set; }

    public string? Jurisdiction { get; private set; }

    public IReadOnlyCollection<TaxRate> Rates => _rates.AsReadOnly();

    public static TaxCode Create(Guid companyId, string code, string nameEn, string nameAr, TaxKind kind, bool recoverable,
        string? reportingBox, string? jurisdiction, decimal ratePercent, DateOnly validFrom)
    {
        var taxCode = new TaxCode(SequentialGuid.New(), companyId, TaxRules.Code(code, "Tax code"), TaxRules.Name(nameEn, "English name"),
            TaxRules.Name(nameAr, "Arabic name"), kind, kind != TaxKind.Output && recoverable, Trim(reportingBox), Trim(jurisdiction));
        taxCode.AddRate(ratePercent, validFrom, null);
        return taxCode;
    }

    /// <summary>
    /// Adds a rate period. Periods may not overlap. As a convenience, an open-ended current rate that starts before
    /// <paramref name="validFrom"/> is closed the day before the new rate starts.
    /// </summary>
    public void AddRate(decimal ratePercent, DateOnly validFrom, DateOnly? validTo)
    {
        if (validTo is not null && validTo < validFrom)
        {
            throw new DomainException(Error.Validation("tax.rate-period-invalid", "Valid-to must not be before valid-from."));
        }

        var openEnded = _rates.FirstOrDefault(r => r.ValidTo is null && r.ValidFrom < validFrom);
        if (openEnded is not null && _rates.All(r => r == openEnded || !Overlaps(r, validFrom, validTo)))
        {
            openEnded.Close(validFrom.AddDays(-1));
        }

        if (_rates.Any(r => Overlaps(r, validFrom, validTo)))
        {
            throw new DomainException(Error.Conflict("tax.rate-period-overlap", "The rate period overlaps an existing rate."));
        }

        _rates.Add(new TaxRate(SequentialGuid.New(), Id, TaxRules.Percent(ratePercent), validFrom, validTo));
    }

    /// <summary>Rate in percent effective on <paramref name="date"/>, or null when no rate covers that date.</summary>
    public decimal? RateOn(DateOnly date) =>
        _rates.FirstOrDefault(r => r.ValidFrom <= date && (r.ValidTo is null || r.ValidTo >= date))?.Rate;

    private static bool Overlaps(TaxRate r, DateOnly from, DateOnly? to) =>
        r.ValidFrom <= (to ?? DateOnly.MaxValue) && from <= (r.ValidTo ?? DateOnly.MaxValue);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TaxRate : ITenantScoped
{
    private TaxRate()
    {
    }

    internal TaxRate(Guid id, Guid taxCodeId, decimal rate, DateOnly validFrom, DateOnly? validTo)
    {
        Id = id;
        TaxCodeId = taxCodeId;
        Rate = rate;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public Guid Id { get; private set; }

    public Guid TaxCodeId { get; private set; }

    /// <summary>Percent, e.g. 14 for 14 %.</summary>
    public decimal Rate { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    internal void Close(DateOnly validTo) => ValidTo = validTo;
}

/// <summary>An ordered set of tax codes applied together (e.g. table tax then VAT on top).</summary>
[Audited]
public sealed class TaxGroup : AggregateRoot, ICompanyScoped
{
    private readonly List<TaxGroupComponent> _components = [];

    private TaxGroup()
    {
    }

    private TaxGroup(Guid id, Guid companyId, string code, string nameEn, string nameAr)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public IReadOnlyCollection<TaxGroupComponent> Components => _components.OrderBy(c => c.Sequence).ToList().AsReadOnly();

    public static TaxGroup Create(Guid companyId, string code, string nameEn, string nameAr, IEnumerable<(Guid TaxCodeId, int Sequence, bool CompoundsOnPrevious)> components)
    {
        var group = new TaxGroup(SequentialGuid.New(), companyId, TaxRules.Code(code, "Tax group code"), TaxRules.Name(nameEn, "English name"), TaxRules.Name(nameAr, "Arabic name"));
        group.SetComponents(components);
        return group;
    }

    public void SetComponents(IEnumerable<(Guid TaxCodeId, int Sequence, bool CompoundsOnPrevious)> components)
    {
        var list = components.ToList();
        if (list.Count == 0)
        {
            throw new DomainException(Error.Validation("tax.group-empty", "A tax group needs at least one tax code."));
        }

        if (list.Select(c => c.Sequence).Distinct().Count() != list.Count || list.Select(c => c.TaxCodeId).Distinct().Count() != list.Count)
        {
            throw new DomainException(Error.Validation("tax.group-duplicate", "Sequences and tax codes must be unique within a group."));
        }

        var first = list.OrderBy(c => c.Sequence).First();
        if (first.CompoundsOnPrevious)
        {
            throw new DomainException(Error.Validation("tax.group-compound-first", "The first component cannot compound on previous components."));
        }

        _components.Clear();
        _components.AddRange(list.Select(c => new TaxGroupComponent(Id, c.TaxCodeId, c.Sequence, c.CompoundsOnPrevious)));
    }
}

public sealed class TaxGroupComponent : ITenantScoped
{
    private TaxGroupComponent()
    {
    }

    internal TaxGroupComponent(Guid taxGroupId, Guid taxCodeId, int sequence, bool compoundsOnPrevious)
    {
        TaxGroupId = taxGroupId;
        TaxCodeId = taxCodeId;
        Sequence = sequence;
        CompoundsOnPrevious = compoundsOnPrevious;
    }

    public Guid TaxGroupId { get; private set; }

    public Guid TaxCodeId { get; private set; }

    public int Sequence { get; private set; }

    /// <summary>The base of this component includes the taxes of all earlier non-withholding components.</summary>
    public bool CompoundsOnPrevious { get; private set; }
}

/// <summary>Maps a product tax category (e.g. STANDARD) and direction to a tax group, so products only carry a category code.</summary>
[Audited]
public sealed class TaxCategoryMapping : AggregateRoot, ICompanyScoped
{
    private TaxCategoryMapping()
    {
    }

    private TaxCategoryMapping(Guid id, Guid companyId, string categoryCode, TaxDirectionKind direction, Guid taxGroupId)
        : base(id)
    {
        CompanyId = companyId;
        CategoryCode = categoryCode;
        Direction = direction;
        TaxGroupId = taxGroupId;
    }

    public Guid CompanyId { get; private set; }

    public string CategoryCode { get; private set; } = string.Empty;

    public TaxDirectionKind Direction { get; private set; }

    public Guid TaxGroupId { get; private set; }

    public static TaxCategoryMapping Create(Guid companyId, string categoryCode, TaxDirectionKind direction, Guid taxGroupId) =>
        new(SequentialGuid.New(), companyId, TaxRules.Code(categoryCode, "Tax category"), direction, taxGroupId);

    public void ChangeGroup(Guid taxGroupId) => TaxGroupId = taxGroupId;
}
