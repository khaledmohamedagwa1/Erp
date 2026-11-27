using Erp.Modules.Catalog.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class UomConversionTests
{
    private static (Product Product, Guid Piece, Guid Box, Guid Carton) PlateWithUnits()
    {
        Guid piece = Guid.NewGuid(), box = Guid.NewGuid(), carton = Guid.NewGuid();
        var product = Product.Create(Guid.NewGuid(), "SKU-2051", "Ceramic Plate", "طبق", ProductType.Stockable, piece, Guid.NewGuid());
        product.AddUnit(box, 24);
        product.AddUnit(carton, 288);
        return (product, piece, box, carton);
    }

    [Fact]
    public void One_carton_is_twelve_boxes_and_288_pieces()
    {
        var (p, piece, box, carton) = PlateWithUnits();
        p.Convert(1, carton, box).ShouldBe(12m);
        p.Convert(1, carton, piece).ShouldBe(288m);
        p.Convert(48, piece, box).ShouldBe(2m);
        p.Convert(30, piece, carton).ShouldBe(0.104167m);
    }

    [Fact]
    public void Conversion_is_deterministic_and_rounds_half_away_from_zero()
    {
        UomConversion.Convert(1, 1, 3).ShouldBe(0.333333m);
        UomConversion.Convert(2, 1, 3).ShouldBe(0.666667m);
        UomConversion.Round(0.0000005m).ShouldBe(0.000001m);
        UomConversion.Round(-0.0000005m).ShouldBe(-0.000001m);
        UomConversion.Convert(7, 288, 24).ShouldBe(UomConversion.Convert(7, 288, 24));
    }

    [Fact]
    public void The_base_unit_has_factor_one_and_units_cannot_be_duplicated_or_zero()
    {
        var (p, piece, box, _) = PlateWithUnits();
        p.FactorOf(piece).ShouldBe(1m);
        Should.Throw<DomainException>(() => p.AddUnit(box, 12)).Error.ShouldBe(CatalogErrors.DuplicateUnit);
        Should.Throw<DomainException>(() => p.AddUnit(Guid.NewGuid(), 0)).Error.ShouldBe(CatalogErrors.InvalidFactor);
        Should.Throw<DomainException>(() => p.FactorOf(Guid.NewGuid())).Error.ShouldBe(CatalogErrors.UnknownUnit);
    }

    [Fact]
    public void The_base_unit_is_locked_once_the_product_has_been_used()
    {
        var piece = Guid.NewGuid();
        var product = Product.Create(Guid.NewGuid(), "SKU-1", "Item", "صنف", ProductType.Stockable, piece, Guid.NewGuid());
        var kg = Guid.NewGuid();
        Should.Throw<DomainException>(() => product.ChangeBaseUom(kg, hasBeenUsed: true)).Error.ShouldBe(CatalogErrors.BaseUomLocked);
        product.ChangeBaseUom(kg, hasBeenUsed: false);
        product.BaseUomId.ShouldBe(kg);
        product.FactorOf(kg).ShouldBe(1m);
    }
}

public class ProductRulesTests
{
    [Fact]
    public void Services_cannot_be_tracked_but_stocked_items_can()
    {
        var service = Product.Create(Guid.NewGuid(), "SRV-1", "Delivery", "توصيل", ProductType.Service, Guid.NewGuid(), Guid.NewGuid());
        service.IsStockable.ShouldBeFalse();
        Should.Throw<DomainException>(() => service.SetTracking(TrackingMode.Batch, false)).Error.ShouldBe(CatalogErrors.TrackingNotAllowed);

        var oil = Product.Create(Guid.NewGuid(), "SKU-1001", "Olive Oil", "زيت", ProductType.Stockable, Guid.NewGuid(), Guid.NewGuid());
        oil.SetTracking(TrackingMode.Batch, true);
        oil.Tracking.ShouldBe(TrackingMode.Batch);
        oil.ExpiryTracked.ShouldBeTrue();
    }

    [Fact]
    public void A_barcode_appears_once_per_product_and_must_point_to_a_known_unit()
    {
        var piece = Guid.NewGuid();
        var product = Product.Create(Guid.NewGuid(), "SKU-1", "Item", "صنف", ProductType.Stockable, piece, Guid.NewGuid());
        product.AddBarcode("6221234567890", null, null);
        Should.Throw<DomainException>(() => product.AddBarcode("6221234567890", null, null)).Error.ShouldBe(CatalogErrors.DuplicateBarcode);
        Should.Throw<DomainException>(() => product.AddBarcode("123", Guid.NewGuid(), null)).Error.ShouldBe(CatalogErrors.UnknownUnit);
        Should.Throw<DomainException>(() => product.AddBarcode("12 34", null, null));
    }

    [Fact]
    public void Variant_skus_differ_from_the_product_and_each_other()
    {
        var product = Product.Create(Guid.NewGuid(), "TEE", "T-shirt", "تيشيرت", ProductType.Stockable, Guid.NewGuid(), Guid.NewGuid());
        var red = product.AddVariant("TEE-RED-L", new Dictionary<string, string> { ["Size"] = "L", ["Colour"] = "Red" });
        red.Attributes.ShouldBe("{\"Colour\":\"Red\",\"Size\":\"L\"}");
        Should.Throw<DomainException>(() => product.AddVariant("TEE", new Dictionary<string, string> { ["Size"] = "M" }));
        Should.Throw<DomainException>(() => product.AddVariant("tee-red-l", new Dictionary<string, string> { ["Size"] = "M" }));
    }
}

public class CategoryHierarchyTests
{
    [Fact]
    public void A_category_cannot_become_its_own_ancestor()
    {
        var company = Guid.NewGuid();
        var food = ProductCategory.Create(company, "FOOD", "Food", "أغذية", CostingMethod.WeightedAverage, null);
        var oils = ProductCategory.Create(company, "OILS", "Oils", "زيوت", CostingMethod.WeightedAverage, null);
        var olive = ProductCategory.Create(company, "OLIVE", "Olive oils", "زيوت زيتون", CostingMethod.FIFO, null);
        var parents = new Dictionary<Guid, Guid?> { [food.Id] = null, [oils.Id] = food.Id, [olive.Id] = oils.Id };
        oils.MoveTo(food.Id, parents);
        olive.MoveTo(oils.Id, parents);

        Should.Throw<DomainException>(() => food.MoveTo(olive.Id, parents)).Error.ShouldBe(CatalogErrors.CategoryCycle);
        Should.Throw<DomainException>(() => food.MoveTo(food.Id, parents)).Error.ShouldBe(CatalogErrors.CategoryCycle);
        food.MoveTo(null, parents);
        food.PostingGroup.ShouldBe("GOODS");
    }
}

public class PriceListTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Piece = Guid.NewGuid();

    private static PriceList NewList() => PriceList.Create(Guid.NewGuid(), "RETAIL", "Retail", "egp", true, null, null);

    [Fact]
    public void Overlapping_prices_for_the_same_key_are_rejected()
    {
        var list = NewList();
        list.Currency.ShouldBe("EGP");
        list.AddItem(Product, null, Piece, 0, 100, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        Should.Throw<DomainException>(() => list.AddItem(Product, null, Piece, 0, 110, new DateOnly(2026, 6, 30), null))
            .Error.ShouldBe(CatalogErrors.PriceOverlap);
        Should.Throw<DomainException>(() => list.AddItem(Product, null, Piece, 0, 110, null, new DateOnly(2026, 1, 1)))
            .Error.ShouldBe(CatalogErrors.PriceOverlap);
    }

    [Fact]
    public void Adjacent_periods_and_different_tiers_are_allowed()
    {
        var list = NewList();
        list.AddItem(Product, null, Piece, 0, 100, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        list.AddItem(Product, null, Piece, 0, 105, new DateOnly(2026, 7, 1), null);
        list.AddItem(Product, null, Piece, 10, 95, null, null);
        list.AddItem(Product, Guid.NewGuid(), Piece, 0, 120, null, null);
        list.Items.Count.ShouldBe(4);
    }

    [Fact]
    public void Invalid_prices_and_date_ranges_are_rejected()
    {
        var list = NewList();
        Should.Throw<DomainException>(() => list.AddItem(Product, null, Piece, 0, -1, null, null)).Error.ShouldBe(CatalogErrors.PriceNegative);
        Should.Throw<DomainException>(() => list.AddItem(Product, null, Piece, -1, 1, null, null)).Error.ShouldBe(CatalogErrors.MinQuantityNegative);
        Should.Throw<DomainException>(() => list.AddItem(Product, null, Piece, 0, 1, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)))
            .Error.ShouldBe(CatalogErrors.InvalidDateRange);
    }

    [Fact]
    public void Replacing_a_products_prices_validates_the_whole_set()
    {
        var list = NewList();
        list.AddItem(Product, null, Piece, 0, 100, null, null);
        Should.Throw<DomainException>(() => list.ReplaceItemsForProduct(Product,
        [
            new PriceLine(null, Piece, 0, 90, null, null),
            new PriceLine(null, Piece, 0, 80, new DateOnly(2026, 1, 1), null),
        ]));
        list.Items.Single().Price.ShouldBe(100m);

        list.ReplaceItemsForProduct(Product, [new PriceLine(null, Piece, 0, 90, null, null), new PriceLine(null, Piece, 12, 80, null, null)]);
        list.Items.Select(i => i.Price).Order().ShouldBe([80m, 90m]);
    }
}
