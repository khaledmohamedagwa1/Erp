using System.Globalization;
using Erp.Application.Reporting;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static Erp.Modules.Reporting.Application.Columns;

namespace Erp.Modules.Reporting.Application;

/// <summary>Manufacturing reports from production orders (Manufacturing.Contracts <see cref="IManufacturingReportQueries"/>).</summary>
public sealed class ManufacturingReports(IServiceProvider services, ReportLookups lookups) : IReportArea
{
    private const string Source = "Production orders, released or later (IManufacturingReportQueries.GetProductionFactsAsync)";
    private static readonly string[] Filters = ["from", "to", "warehouseId", "productId", "categoryId"];

    public string Area => ReportAreas.Manufacturing;

    public bool IsAvailable => services.GetService<IManufacturingReportQueries>() is not null;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        Def("production-summary", "Production summary", "ملخص الإنتاج", "Planned, completed and scrapped quantities and accumulated costs per production order.", Filters),
        Def("material-consumption", "Material consumption vs standard", "استهلاك المواد مقابل المعياري", "Issued component quantities and cost against the BOM standard for the output produced.", Filters),
        Def("variance", "Production variance", "انحرافات الإنتاج", "Costs charged to WIP against the cost of goods received, scrap and the variance posted at close.", Filters),
        Def("wip", "Work in progress valuation", "تقييم الإنتاج تحت التشغيل", "Production orders still carrying value in work in progress (current, no date filter).", ["warehouseId", "productId", "categoryId"]),
        Def("scrap", "Scrap report", "تقرير الهالك", "Scrapped output quantity and its cost per production order.", Filters),
    ];

    public async Task<Result<ReportOutput>> RunAsync(string key, ReportQuery q, CancellationToken ct)
    {
        if (key is not ("production-summary" or "material-consumption" or "variance" or "wip" or "scrap"))
        {
            return ReportingErrors.UnknownReport;
        }

        var facts = await services.GetRequiredService<IManufacturingReportQueries>()
            .GetProductionFactsAsync(new ProductionReportFilter(q.From, q.To, q.WarehouseId, q.ProductIds, OpenWipOnly: key == "wip"), ct);
        var products = await lookups.ProductsAsync(facts.Select(f => f.ProductId).Concat(facts.SelectMany(f => f.Components.Select(c => c.ProductId))), ct);
        var table = key switch
        {
            "production-summary" => Summary(facts, products, q),
            "material-consumption" => Consumption(facts, products, q),
            "variance" => Variance(facts, products, q),
            "wip" => Wip(facts, products, q),
            _ => Scrap(facts, products, q),
        };
        return new ReportOutput(table);
    }

    private static ReportDefinition Def(string key, string en, string ar, string description, IReadOnlyList<string> filters) =>
        new(ReportAreas.Manufacturing, key, en, ar, description, Source, ReportingPermissions.ManufacturingRead, "manufacturing", filters);

    private static decimal CostIn(ProductionFact f) => f.MaterialCost + f.LaborCost + f.MachineCost + f.OverheadCost;

    private static ReportTable Summary(IReadOnlyList<ProductionFact> facts, IReadOnlyDictionary<Guid, Catalog.Contracts.ProductReportInfo> products, ReportQuery q)
    {
        ReportColumn[] columns =
        [
            Text("order", "Order", "أمر الإنتاج"), Sku, Product, Text("status", "Status", "الحالة"), Date("startDate", "Start", "البدء"),
            Quantity("planned", "Planned", "المخطط"), Quantity("completed", "Completed", "المكتمل"), Quantity("scrapped", "Scrapped", "الهالك"),
            Percent("yield", "Yield %", "نسبة الإنتاجية %"),
            Money("material", "Material", "المواد"), Money("labor", "Labor", "العمالة"), Money("machine", "Machine", "الآلات"), Money("overhead", "Overhead", "التكاليف غير المباشرة"),
            Money("received", "Cost of goods received", "تكلفة المنتج التام المستلم"),
        ];
        var rows = facts.Select(f => new object?[]
        {
            f.Number, products.Sku(f.ProductId), products.Name(f.ProductId, q.Arabic), f.Status, f.StartDate,
            f.PlannedQuantity, f.CompletedQuantity, f.ScrappedQuantity, ReportMath.Percent(f.CompletedQuantity, f.CompletedQuantity + f.ScrappedQuantity),
            f.MaterialCost, f.LaborCost, f.MachineCost, f.OverheadCost, f.ReceivedCost,
        }).ToList();
        var totals = Totals(columns.Length, rows, 5, 6, 7, 9, 10, 11, 12, 13);
        totals[8] = ReportMath.Percent((decimal)totals[6]!, (decimal)totals[6]! + (decimal)totals[7]!);
        return new ReportTable("manufacturing/production-summary", "Production summary", "ملخص الإنتاج", columns, rows, totals, Period(q));
    }

    private static ReportTable Consumption(IReadOnlyList<ProductionFact> facts, IReadOnlyDictionary<Guid, Catalog.Contracts.ProductReportInfo> products, ReportQuery q)
    {
        ReportColumn[] columns =
        [
            Text("order", "Order", "أمر الإنتاج"), Text("componentSku", "Component SKU", "رمز المكون"), Text("component", "Component", "المكون"),
            Quantity("required", "Required", "المطلوب"), Quantity("standard", "Standard for output", "المعياري للإنتاج الفعلي"), Quantity("actual", "Actual issued", "المنصرف الفعلي"),
            Quantity("quantityVariance", "Quantity variance", "انحراف الكمية"), Percent("variancePercent", "Variance %", "نسبة الانحراف %"),
            Money("actualCost", "Actual cost", "التكلفة الفعلية"), Money("standardCost", "Standard cost", "التكلفة المعيارية"), Money("costVariance", "Cost variance", "انحراف التكلفة"),
        ];
        var rows = facts.SelectMany(f => f.Components.Select(c =>
        {
            var unitCost = c.IssuedBaseQuantity == 0 ? 0m : c.IssuedCost / c.IssuedBaseQuantity;
            var standardCost = ReportMath.Money(c.StandardBaseQuantity * unitCost);
            var quantityVariance = c.IssuedBaseQuantity - c.StandardBaseQuantity;
            return new object?[]
            {
                f.Number, products.Sku(c.ProductId), products.Name(c.ProductId, q.Arabic), c.RequiredBaseQuantity, c.StandardBaseQuantity, c.IssuedBaseQuantity,
                quantityVariance, ReportMath.Percent(quantityVariance, c.StandardBaseQuantity), c.IssuedCost, standardCost, c.IssuedCost - standardCost,
            };
        })).ToList();
        return new ReportTable("manufacturing/material-consumption", "Material consumption vs standard", "استهلاك المواد مقابل المعياري", columns, rows,
            Totals(columns.Length, rows, 8, 9, 10), Period(q));
    }

    private static ReportTable Variance(IReadOnlyList<ProductionFact> facts, IReadOnlyDictionary<Guid, Catalog.Contracts.ProductReportInfo> products, ReportQuery q)
    {
        ReportColumn[] columns =
        [
            Text("order", "Order", "أمر الإنتاج"), Sku, Product, Text("status", "Status", "الحالة"),
            Money("costIn", "Charged to WIP", "المحمل على الإنتاج تحت التشغيل"), Money("received", "Goods received", "المنتج التام المستلم"),
            Money("scrapCost", "Scrap", "الهالك"), Money("wip", "Remaining WIP", "الرصيد المتبقي"), Money("variance", "Variance posted", "الانحراف المرحل"),
            Money("costPerUnit", "Actual cost per unit", "التكلفة الفعلية للوحدة"),
        ];
        var rows = facts.Select(f => new object?[]
        {
            f.Number, products.Sku(f.ProductId), products.Name(f.ProductId, q.Arabic), f.Status, CostIn(f), f.ReceivedCost, f.ScrapCost, f.WipBalance, f.VarianceAmount,
            f.CompletedQuantity == 0 ? 0m : Math.Round((f.ReceivedCost + f.VarianceAmount) / f.CompletedQuantity, 4),
        }).ToList();
        return new ReportTable("manufacturing/variance", "Production variance", "انحرافات الإنتاج", columns, rows, Totals(columns.Length, rows, 4, 5, 6, 7, 8), Period(q));
    }

    private static ReportTable Wip(IReadOnlyList<ProductionFact> facts, IReadOnlyDictionary<Guid, Catalog.Contracts.ProductReportInfo> products, ReportQuery q)
    {
        ReportColumn[] columns =
        [
            Text("order", "Order", "أمر الإنتاج"), Sku, Product, Text("status", "Status", "الحالة"), Date("startDate", "Start", "البدء"),
            Quantity("open", "Open quantity", "الكمية المفتوحة"),
            Money("material", "Material", "المواد"), Money("conversion", "Labor, machine & overhead", "العمالة والآلات وغير المباشرة"),
            Money("received", "Goods received", "المنتج التام المستلم"), Money("wip", "WIP balance", "رصيد الإنتاج تحت التشغيل"),
        ];
        var rows = facts.Select(f => new object?[]
        {
            f.Number, products.Sku(f.ProductId), products.Name(f.ProductId, q.Arabic), f.Status, f.StartDate,
            Math.Max(0, f.PlannedQuantity - f.CompletedQuantity - f.ScrappedQuantity), f.MaterialCost, f.LaborCost + f.MachineCost + f.OverheadCost, f.ReceivedCost, f.WipBalance,
        }).ToList();
        return new ReportTable("manufacturing/wip", "Work in progress valuation", "تقييم الإنتاج تحت التشغيل", columns, rows, Totals(columns.Length, rows, 6, 7, 8, 9),
            [new("As of", "حتى تاريخ", q.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);
    }

    private static ReportTable Scrap(IReadOnlyList<ProductionFact> facts, IReadOnlyDictionary<Guid, Catalog.Contracts.ProductReportInfo> products, ReportQuery q)
    {
        ReportColumn[] columns =
        [
            Text("order", "Order", "أمر الإنتاج"), Sku, Product, Date("startDate", "Start", "البدء"),
            Quantity("planned", "Planned", "المخطط"), Quantity("completed", "Completed", "المكتمل"), Quantity("scrapped", "Scrapped", "الهالك"),
            Percent("scrapPercent", "Scrap %", "نسبة الهالك %"), Money("scrapCost", "Scrap cost", "تكلفة الهالك"),
        ];
        var rows = facts.Where(f => f.ScrappedQuantity != 0 || f.ScrapCost != 0).Select(f => new object?[]
        {
            f.Number, products.Sku(f.ProductId), products.Name(f.ProductId, q.Arabic), f.StartDate, f.PlannedQuantity, f.CompletedQuantity, f.ScrappedQuantity,
            ReportMath.Percent(f.ScrappedQuantity, f.CompletedQuantity + f.ScrappedQuantity), f.ScrapCost,
        }).ToList();
        return new ReportTable("manufacturing/scrap", "Scrap report", "تقرير الهالك", columns, rows, Totals(columns.Length, rows, 4, 5, 6, 8), Period(q));
    }
}
