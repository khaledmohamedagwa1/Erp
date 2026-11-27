using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Catalog.Domain;

public static partial class CatalogRules
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_.-]{0,39}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public static string Code(string? value, string field)
    {
        var code = Guard.NotEmpty(value, "catalog.code-required", field).ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainException(Error.Validation("catalog.code-invalid", $"{field} must be 1–40 characters: letters, digits, '.', '-' or '_'."));
        }

        return code;
    }

    public static string Name(string? value, string field) =>
        Guard.MaxLength(Guard.NotEmpty(value, "catalog.name-required", field), 200, "catalog.name-too-long", field);

    public static string Currency(string? value)
    {
        var currency = Guard.NotEmpty(value, "catalog.currency-required", "Currency").ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(currency))
        {
            throw new DomainException(Error.Validation("catalog.currency-invalid", "Currency must be a 3-letter ISO 4217 code."));
        }

        return currency;
    }

    public static string Barcode(string? value)
    {
        var barcode = Guard.MaxLength(Guard.NotEmpty(value, "catalog.barcode-required", "Barcode"), 64, "catalog.barcode-too-long", "Barcode");
        if (barcode.Any(char.IsWhiteSpace))
        {
            throw new DomainException(Error.Validation("catalog.barcode-invalid", "Barcode must not contain spaces."));
        }

        return barcode;
    }
}

public static class CatalogErrors
{
    public static readonly Error CategoryCycle = Error.Validation("catalog.category-cycle", "A category cannot be placed under itself or one of its children.");
    public static readonly Error DuplicateUnit = Error.Conflict("catalog.unit-duplicate", "The product already has this unit.");
    public static readonly Error InvalidFactor = Error.Validation("catalog.unit-factor-invalid", "The conversion factor must be greater than zero.");
    public static readonly Error UnknownUnit = Error.Validation("catalog.unit-unknown", "The product has no such unit.");
    public static readonly Error BaseUomLocked = Error.Conflict("catalog.base-uom-locked", "The base unit cannot change after the product has been used.");
    public static readonly Error DuplicateBarcode = Error.Conflict("catalog.barcode-taken", "This barcode is already assigned.");
    public static readonly Error TrackingNotAllowed = Error.Validation("catalog.tracking-not-allowed", "Only stocked products can be batch-, serial- or expiry-tracked.");
    public static readonly Error PriceOverlap = Error.Conflict("catalog.price-overlap", "Another price for the same product, unit and minimum quantity overlaps these dates.");
    public static readonly Error PriceNegative = Error.Validation("catalog.price-negative", "Price must not be negative.");
    public static readonly Error MinQuantityNegative = Error.Validation("catalog.min-quantity-negative", "Minimum quantity must not be negative.");
    public static readonly Error InvalidDateRange = Error.Validation("catalog.date-range-invalid", "Valid-from must be on or before valid-to.");
    public static readonly Error SkuTaken = Error.Conflict("catalog.sku-taken", "This SKU is already used.");
    public static readonly Error UnknownVariant = Error.Validation("catalog.variant-unknown", "The product has no such variant.");
}

public enum UomClass
{
    Count = 1,
    Mass = 2,
    Volume = 3,
    Length = 4,
    Time = 5,
}

public enum ProductType
{
    Stockable = 1,
    Consumable = 2,
    Service = 3,
    Bundle = 4,
    RawMaterial = 5,
    SemiFinished = 6,
    FinishedGood = 7,
    Manufactured = 8,
}

public enum TrackingMode
{
    None = 0,
    Batch = 1,
    Serial = 2,
}

public enum CostingMethod
{
    WeightedAverage = 1,
    FIFO = 2,
}

/// <summary>Deterministic quantity conversion: always through the base unit, rounded half away from zero to 6 decimals.</summary>
public static class UomConversion
{
    public const int Decimals = 6;

    public static decimal ToBase(decimal quantity, decimal factorToBase) => Round(quantity * factorToBase);

    public static decimal FromBase(decimal baseQuantity, decimal factorToBase) => Round(baseQuantity / factorToBase);

    public static decimal Convert(decimal quantity, decimal fromFactor, decimal toFactor) => Round(quantity * fromFactor / toFactor);

    public static decimal Round(decimal value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}

public sealed class UnitOfMeasure : AggregateRoot, ICompanyScoped
{
    private UnitOfMeasure()
    {
    }

    private UnitOfMeasure(Guid id, Guid companyId, string code, string nameEn, string nameAr, UomClass uomClass)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
        Class = uomClass;
    }

    /// <summary>Common units offered when a company starts using the catalog.</summary>
    public static IReadOnlyList<(string Code, string En, string Ar, UomClass Class)> Standard { get; } =
    [
        ("PCS", "Piece", "قطعة", UomClass.Count),
        ("BOX", "Box", "علبة", UomClass.Count),
        ("CTN", "Carton", "كرتونة", UomClass.Count),
        ("SET", "Set", "طقم", UomClass.Count),
        ("KG", "Kilogram", "كيلوجرام", UomClass.Mass),
        ("G", "Gram", "جرام", UomClass.Mass),
        ("L", "Litre", "لتر", UomClass.Volume),
        ("ML", "Millilitre", "ملليلتر", UomClass.Volume),
        ("M", "Metre", "متر", UomClass.Length),
        ("CM", "Centimetre", "سنتيمتر", UomClass.Length),
        ("HR", "Hour", "ساعة", UomClass.Time),
    ];

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public UomClass Class { get; private set; }

    public static UnitOfMeasure Create(Guid companyId, string code, string nameEn, string nameAr, UomClass uomClass) =>
        new(SequentialGuid.New(), companyId, CatalogRules.Code(code, "Unit code"), CatalogRules.Name(nameEn, "English name"), CatalogRules.Name(nameAr, "Arabic name"), uomClass);
}

[Audited]
public sealed class ProductCategory : AggregateRoot, ICompanyScoped
{
    private ProductCategory()
    {
    }

    private ProductCategory(Guid id, Guid companyId, string code, string nameEn, string nameAr, CostingMethod costingMethod, string postingGroup)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
        DefaultCostingMethod = costingMethod;
        PostingGroup = postingGroup;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public Guid? ParentId { get; private set; }

    /// <summary>Products inherit this unless they override it (decisions log OQ-INV-2).</summary>
    public CostingMethod DefaultCostingMethod { get; private set; }

    /// <summary>Product posting group used by accounting rules to find inventory, COGS and revenue accounts.</summary>
    public string PostingGroup { get; private set; } = string.Empty;

    public static ProductCategory Create(Guid companyId, string code, string nameEn, string nameAr, CostingMethod costingMethod, string? postingGroup) =>
        new(SequentialGuid.New(), companyId, CatalogRules.Code(code, "Category code"), CatalogRules.Name(nameEn, "English name"),
            CatalogRules.Name(nameAr, "Arabic name"), costingMethod, CatalogRules.Code(string.IsNullOrWhiteSpace(postingGroup) ? "GOODS" : postingGroup, "Posting group"));

    public void Update(string nameEn, string nameAr, CostingMethod costingMethod, string postingGroup)
    {
        NameEn = CatalogRules.Name(nameEn, "English name");
        NameAr = CatalogRules.Name(nameAr, "Arabic name");
        DefaultCostingMethod = costingMethod;
        PostingGroup = CatalogRules.Code(postingGroup, "Posting group");
    }

    /// <summary>Moves the category. <paramref name="parentsById"/> maps every category of the company to its parent.</summary>
    public void MoveTo(Guid? parentId, IReadOnlyDictionary<Guid, Guid?> parentsById)
    {
        var current = parentId;
        var steps = 0;
        while (current is { } id)
        {
            if (id == Id || ++steps > 1000)
            {
                throw new DomainException(CatalogErrors.CategoryCycle);
            }

            current = parentsById.TryGetValue(id, out var next) ? next : null;
        }

        ParentId = parentId;
    }
}

public sealed class Brand : AggregateRoot, ICompanyScoped
{
    private Brand()
    {
    }

    private Brand(Guid id, Guid companyId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static Brand Create(Guid companyId, string code, string name) =>
        new(SequentialGuid.New(), companyId, CatalogRules.Code(code, "Brand code"), CatalogRules.Name(name, "Brand name"));
}

[Audited]
public sealed class Product : AggregateRoot, ICompanyScoped
{
    private readonly List<ProductUnit> _units = [];
    private readonly List<ProductBarcode> _barcodes = [];
    private readonly List<ProductVariant> _variants = [];

    private Product()
    {
    }

    private Product(Guid id, Guid companyId, string sku, string nameEn, string nameAr, ProductType type, Guid baseUomId, Guid categoryId)
        : base(id)
    {
        CompanyId = companyId;
        Sku = sku;
        NameEn = nameEn;
        NameAr = nameAr;
        Type = type;
        BaseUomId = baseUomId;
        CategoryId = categoryId;
        IsActive = true;
        TaxCategory = "STANDARD";
    }

    public Guid CompanyId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public ProductType Type { get; private set; }

    public Guid BaseUomId { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    public TrackingMode Tracking { get; private set; }

    public bool ExpiryTracked { get; private set; }

    /// <summary>Resolved to tax codes by the Tax module (e.g. STANDARD, EXEMPT, ZERO).</summary>
    public string TaxCategory { get; private set; } = string.Empty;

    public CostingMethod? CostingMethodOverride { get; private set; }

    public string? PostingGroupOverride { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<ProductUnit> Units => _units.AsReadOnly();

    public IReadOnlyCollection<ProductBarcode> Barcodes => _barcodes.AsReadOnly();

    public IReadOnlyCollection<ProductVariant> Variants => _variants.AsReadOnly();

    /// <summary>Whether the product is held in stock (has inventory movements and valuation).</summary>
    public bool IsStockable => IsStockableType(Type);

    public static bool IsStockableType(ProductType type) =>
        type is ProductType.Stockable or ProductType.RawMaterial or ProductType.SemiFinished or ProductType.FinishedGood or ProductType.Manufactured;

    public static Product Create(Guid companyId, string sku, string nameEn, string nameAr, ProductType type, Guid baseUomId, Guid categoryId)
    {
        var product = new Product(SequentialGuid.New(), companyId, CatalogRules.Code(sku, "SKU"), CatalogRules.Name(nameEn, "English name"),
            CatalogRules.Name(nameAr, "Arabic name"), type, baseUomId, categoryId);
        product._units.Add(new ProductUnit(product.Id, baseUomId, 1m));
        return product;
    }

    public void Update(string nameEn, string nameAr, Guid categoryId, Guid? brandId, string taxCategory, CostingMethod? costingOverride, string? postingGroupOverride)
    {
        NameEn = CatalogRules.Name(nameEn, "English name");
        NameAr = CatalogRules.Name(nameAr, "Arabic name");
        CategoryId = categoryId;
        BrandId = brandId;
        TaxCategory = CatalogRules.Code(taxCategory, "Tax category");
        CostingMethodOverride = costingOverride;
        PostingGroupOverride = string.IsNullOrWhiteSpace(postingGroupOverride) ? null : CatalogRules.Code(postingGroupOverride, "Posting group");
    }

    public void SetTracking(TrackingMode tracking, bool expiryTracked)
    {
        if (!IsStockable && (tracking != TrackingMode.None || expiryTracked))
        {
            throw new DomainException(CatalogErrors.TrackingNotAllowed);
        }

        Tracking = tracking;
        ExpiryTracked = expiryTracked;
    }

    /// <summary>The base unit is locked once stock or documents reference the product (all ledger quantities are in it).</summary>
    public void ChangeBaseUom(Guid uomId, bool hasBeenUsed)
    {
        if (uomId == BaseUomId)
        {
            return;
        }

        if (hasBeenUsed)
        {
            throw new DomainException(CatalogErrors.BaseUomLocked);
        }

        if (_units.Count > 1 || _barcodes.Any(b => b.UomId is not null))
        {
            throw new DomainException(Error.Conflict("catalog.base-uom-has-units", "Remove the other units before changing the base unit; their factors refer to it."));
        }

        _units.Clear();
        BaseUomId = uomId;
        _units.Add(new ProductUnit(Id, uomId, 1m));
    }

    public ProductUnit AddUnit(Guid uomId, decimal factorToBase)
    {
        if (factorToBase <= 0)
        {
            throw new DomainException(CatalogErrors.InvalidFactor);
        }

        if (_units.Any(u => u.UomId == uomId))
        {
            throw new DomainException(CatalogErrors.DuplicateUnit);
        }

        var unit = new ProductUnit(Id, uomId, UomConversion.Round(factorToBase));
        _units.Add(unit);
        return unit;
    }

    public decimal FactorOf(Guid uomId) =>
        _units.FirstOrDefault(u => u.UomId == uomId)?.FactorToBase ?? throw new DomainException(CatalogErrors.UnknownUnit);

    public decimal Convert(decimal quantity, Guid fromUomId, Guid toUomId) =>
        UomConversion.Convert(quantity, FactorOf(fromUomId), FactorOf(toUomId));

    /// <summary>Adds a barcode. Uniqueness across the company is checked by the caller and enforced by a unique index.</summary>
    public ProductBarcode AddBarcode(string barcode, Guid? uomId, Guid? variantId)
    {
        var code = CatalogRules.Barcode(barcode);
        if (_barcodes.Any(b => b.Barcode == code))
        {
            throw new DomainException(CatalogErrors.DuplicateBarcode);
        }

        if (uomId is { } u && _units.All(x => x.UomId != u))
        {
            throw new DomainException(CatalogErrors.UnknownUnit);
        }

        if (variantId is { } v && _variants.All(x => x.Id != v))
        {
            throw new DomainException(CatalogErrors.UnknownVariant);
        }

        var entry = new ProductBarcode(SequentialGuid.New(), Id, CompanyId, code, uomId, variantId);
        _barcodes.Add(entry);
        return entry;
    }

    public ProductVariant AddVariant(string sku, IReadOnlyDictionary<string, string> attributes)
    {
        var code = CatalogRules.Code(sku, "Variant SKU");
        if (code == Sku || _variants.Any(v => v.Sku == code))
        {
            throw new DomainException(CatalogErrors.SkuTaken);
        }

        var variant = new ProductVariant(SequentialGuid.New(), Id, CompanyId, code, attributes);
        _variants.Add(variant);
        return variant;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}

public sealed class ProductUnit : ITenantScoped
{
    private ProductUnit()
    {
    }

    internal ProductUnit(Guid productId, Guid uomId, decimal factorToBase)
    {
        ProductId = productId;
        UomId = uomId;
        FactorToBase = factorToBase;
    }

    public Guid ProductId { get; private set; }

    public Guid UomId { get; private set; }

    /// <summary>How many base units one of this unit holds (carton = 288 pieces).</summary>
    public decimal FactorToBase { get; private set; }
}

public sealed class ProductBarcode : ICompanyScoped
{
    private ProductBarcode()
    {
    }

    internal ProductBarcode(Guid id, Guid productId, Guid companyId, string barcode, Guid? uomId, Guid? variantId)
    {
        Id = id;
        ProductId = productId;
        CompanyId = companyId;
        Barcode = barcode;
        UomId = uomId;
        VariantId = variantId;
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string Barcode { get; private set; } = string.Empty;

    /// <summary>Scanning this barcode selects this unit (e.g. the carton barcode). Null = base unit.</summary>
    public Guid? UomId { get; private set; }

    public Guid? VariantId { get; private set; }
}

public sealed class ProductVariant : ICompanyScoped
{
    private ProductVariant()
    {
    }

    internal ProductVariant(Guid id, Guid productId, Guid companyId, string sku, IReadOnlyDictionary<string, string> attributes)
    {
        Id = id;
        ProductId = productId;
        CompanyId = companyId;
        Sku = sku;
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            sorted[key.Trim()] = value.Trim();
        }

        Attributes = JsonSerializer.Serialize(sorted);
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    /// <summary>Attribute values as a JSON object, keys sorted (e.g. {"Colour":"Red","Size":"L"}).</summary>
    public string Attributes { get; private set; } = "{}";

    public IReadOnlyDictionary<string, string> AttributeValues() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Attributes) ?? new Dictionary<string, string>();
}

[Audited]
public sealed class PriceList : AggregateRoot, ICompanyScoped
{
    private readonly List<PriceListItem> _items = [];

    private PriceList()
    {
    }

    private PriceList(Guid id, Guid companyId, string code, string name, string currency, bool taxInclusive, DateOnly? validFrom, DateOnly? validTo)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Name = name;
        Currency = currency;
        TaxInclusive = taxInclusive;
        ValidFrom = validFrom;
        ValidTo = validTo;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    public bool TaxInclusive { get; private set; }

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<PriceListItem> Items => _items.AsReadOnly();

    public static PriceList Create(Guid companyId, string code, string name, string currency, bool taxInclusive, DateOnly? validFrom, DateOnly? validTo)
    {
        EnsureRange(validFrom, validTo);
        return new PriceList(SequentialGuid.New(), companyId, CatalogRules.Code(code, "Price list code"), CatalogRules.Name(name, "Price list name"),
            CatalogRules.Currency(currency), taxInclusive, validFrom, validTo);
    }

    public bool IsValidOn(DateOnly date) => IsActive && (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || ValidTo >= date);

    public PriceListItem AddItem(Guid productId, Guid? variantId, Guid uomId, decimal minQuantity, decimal price, DateOnly? validFrom, DateOnly? validTo)
    {
        var item = PriceListItem.Create(Id, productId, variantId, uomId, minQuantity, price, validFrom, validTo);
        if (_items.Any(existing => existing.Overlaps(item)))
        {
            throw new DomainException(CatalogErrors.PriceOverlap);
        }

        _items.Add(item);
        return item;
    }

    /// <summary>Replaces all prices of one product (e.g. after an import). Validates the new set as a whole.</summary>
    public void ReplaceItemsForProduct(Guid productId, IEnumerable<PriceLine> lines)
    {
        var replacement = lines.Select(i => PriceListItem.Create(Id, productId, i.VariantId, i.UomId, i.MinQuantity, i.Price, i.ValidFrom, i.ValidTo)).ToList();
        for (var i = 0; i < replacement.Count; i++)
        {
            for (var j = i + 1; j < replacement.Count; j++)
            {
                if (replacement[i].Overlaps(replacement[j]))
                {
                    throw new DomainException(CatalogErrors.PriceOverlap);
                }
            }
        }

        _items.RemoveAll(x => x.ProductId == productId);
        _items.AddRange(replacement);
    }

    internal static void EnsureRange(DateOnly? from, DateOnly? to)
    {
        if (from is not null && to is not null && from > to)
        {
            throw new DomainException(CatalogErrors.InvalidDateRange);
        }
    }
}

public sealed record PriceLine(Guid? VariantId, Guid UomId, decimal MinQuantity, decimal Price, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed class PriceListItem : ITenantScoped
{
    private PriceListItem()
    {
    }

    private PriceListItem(Guid id, Guid priceListId, Guid productId, Guid? variantId, Guid uomId, decimal minQuantity, decimal price, DateOnly? validFrom, DateOnly? validTo)
    {
        Id = id;
        PriceListId = priceListId;
        ProductId = productId;
        VariantId = variantId;
        UomId = uomId;
        MinQuantity = minQuantity;
        Price = price;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public Guid Id { get; private set; }

    public Guid PriceListId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid? VariantId { get; private set; }

    public Guid UomId { get; private set; }

    /// <summary>Volume tier: applies from this quantity (in the item's unit) upwards.</summary>
    public decimal MinQuantity { get; private set; }

    public decimal Price { get; private set; }

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public bool IsValidOn(DateOnly date) => (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || ValidTo >= date);

    /// <summary>Same product/variant/unit/minimum quantity and intersecting validity periods (open ends are infinite).</summary>
    public bool Overlaps(PriceListItem other) =>
        ProductId == other.ProductId && VariantId == other.VariantId && UomId == other.UomId && MinQuantity == other.MinQuantity &&
        (ValidFrom ?? DateOnly.MinValue) <= (other.ValidTo ?? DateOnly.MaxValue) &&
        (other.ValidFrom ?? DateOnly.MinValue) <= (ValidTo ?? DateOnly.MaxValue);

    internal static PriceListItem Create(Guid priceListId, Guid productId, Guid? variantId, Guid uomId, decimal minQuantity, decimal price, DateOnly? validFrom, DateOnly? validTo)
    {
        if (price < 0)
        {
            throw new DomainException(CatalogErrors.PriceNegative);
        }

        if (minQuantity < 0)
        {
            throw new DomainException(CatalogErrors.MinQuantityNegative);
        }

        PriceList.EnsureRange(validFrom, validTo);
        return new PriceListItem(SequentialGuid.New(), priceListId, productId, variantId, uomId, UomConversion.Round(minQuantity),
            Math.Round(price, 4, MidpointRounding.AwayFromZero), validFrom, validTo);
    }
}
