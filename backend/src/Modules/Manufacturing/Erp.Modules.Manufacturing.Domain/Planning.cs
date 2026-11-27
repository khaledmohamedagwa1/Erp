using Erp.SharedKernel.Domain;

namespace Erp.Modules.Manufacturing.Domain;

public enum LotSizeRule
{
    LotForLot = 1,

    /// <summary>Order at least <see cref="ItemPlanningParameters.LotSize"/>.</summary>
    FixedMinimum = 2,

    /// <summary>Order in multiples of <see cref="ItemPlanningParameters.LotSize"/>.</summary>
    Multiple = 3,
}

public enum ProcurementType
{
    Make = 1,
    Buy = 2,
}

/// <summary>MRP settings of one product. Products without parameters plan lot-for-lot with zero lead time and no safety stock.</summary>
[Audited]
public sealed class ItemPlanningParameters : AggregateRoot, ICompanyScoped
{
    private ItemPlanningParameters()
    {
    }

    private ItemPlanningParameters(Guid id, Guid companyId, Guid productId)
        : base(id)
    {
        CompanyId = companyId;
        ProductId = productId;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public decimal SafetyStock { get; private set; }

    public int LeadTimeDays { get; private set; }

    public LotSizeRule LotSizeRule { get; private set; } = LotSizeRule.LotForLot;

    public decimal LotSize { get; private set; }

    /// <summary>Null: make when the product has an effective BOM, otherwise buy.</summary>
    public ProcurementType? Procurement { get; private set; }

    /// <summary>Null: the company default (<see cref="ManufacturingSettings.DefaultCostingMethod"/>).</summary>
    public ManufacturingCostingMethod? CostingMethod { get; private set; }

    /// <summary>Supplier for purchase orders created from MRP purchase suggestions.</summary>
    public Guid? PreferredSupplierId { get; private set; }

    public static ItemPlanningParameters Create(Guid companyId, Guid productId) => new(SequentialGuid.New(), companyId, productId);

    public void Update(decimal safetyStock, int leadTimeDays, LotSizeRule rule, decimal lotSize, ProcurementType? procurement,
        ManufacturingCostingMethod? costingMethod = null, Guid? preferredSupplierId = null)
    {
        if (safetyStock < 0 || leadTimeDays < 0 || lotSize < 0 || (rule != LotSizeRule.LotForLot && lotSize <= 0))
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.planning-invalid",
                "Safety stock and lead time cannot be negative; minimum and multiple lot rules need a positive lot size."));
        }

        SafetyStock = safetyStock;
        LeadTimeDays = leadTimeDays;
        LotSizeRule = rule;
        LotSize = lotSize;
        Procurement = procurement;
        CostingMethod = costingMethod;
        PreferredSupplierId = preferredSupplierId;
    }
}

public enum SuggestionAction
{
    Purchase = 1,
    Produce = 2,
}

public enum SuggestionStatus
{
    Open = 1,
    Converted = 2,
}

public sealed class PeggingEntry
{
    public string SourceType { get; set; } = string.Empty;

    public string SourceReference { get; set; } = string.Empty;

    public DateOnly DueDate { get; set; }

    public decimal Quantity { get; set; }
}

public sealed class MrpSuggestion
{
    public Guid SuggestionId { get; set; }

    public Guid ProductId { get; set; }

    public SuggestionAction Action { get; set; }

    /// <summary>Base units.</summary>
    public decimal Quantity { get; set; }

    public DateOnly DueDate { get; set; }

    public DateOnly OrderByDate { get; set; }

    /// <summary>The order-by date is before the run date: the lead time cannot be met.</summary>
    public bool IsLate { get; set; }

    public SuggestionStatus Status { get; set; } = SuggestionStatus.Open;

    public Guid? ProductionOrderId { get; set; }

    /// <summary>Set when a purchase suggestion was converted into a draft purchase order.</summary>
    public Guid? PurchaseOrderId { get; set; }

    public List<PeggingEntry> Pegging { get; set; } = [];
}

public sealed class MrpShortage
{
    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public DateOnly NeededBy { get; set; }

    public string Reason { get; set; } = string.Empty;
}

/// <summary>Result of one MRP run. Suggestions are proposals until converted.</summary>
public sealed class MrpRun : AggregateRoot, ICompanyScoped
{
    private MrpRun()
    {
    }

    private MrpRun(Guid id, Guid companyId, string number, DateOnly runDate, int horizonDays)
        : base(id)
    {
        CompanyId = companyId;
        Number = number;
        RunDate = runDate;
        HorizonDays = horizonDays;
    }

    public Guid CompanyId { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public DateOnly RunDate { get; private set; }

    public int HorizonDays { get; private set; }

    public List<MrpSuggestion> Suggestions { get; private set; } = [];

    public List<MrpShortage> Shortages { get; private set; } = [];

    public static MrpRun Create(Guid companyId, string number, DateOnly runDate, int horizonDays, MrpResult result) =>
        new(SequentialGuid.New(), companyId, number, runDate, horizonDays)
        {
            Suggestions = result.Suggestions.ToList(),
            Shortages = result.Shortages.ToList(),
        };

    public MrpSuggestion Suggestion(Guid id) =>
        Suggestions.FirstOrDefault(s => s.SuggestionId == id)
        ?? throw new DomainException(SharedKernel.Results.Error.NotFound("manufacturing.suggestion-not-found", "The suggestion is not part of this MRP run."));

    public void MarkConverted(Guid suggestionId, Guid productionOrderId)
    {
        var suggestion = Suggestion(suggestionId);
        if (suggestion.Status != SuggestionStatus.Open || suggestion.Action != SuggestionAction.Produce)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.suggestion-not-convertible",
                "Only open production suggestions can be converted here; convert purchase suggestions with convert-purchases."));
        }

        suggestion.Status = SuggestionStatus.Converted;
        suggestion.ProductionOrderId = productionOrderId;
    }

    public void MarkPurchased(Guid suggestionId, Guid purchaseOrderId)
    {
        var suggestion = Suggestion(suggestionId);
        if (suggestion.Status != SuggestionStatus.Open || suggestion.Action != SuggestionAction.Purchase)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.suggestion-not-convertible",
                "Only open purchase suggestions can be converted into purchase orders."));
        }

        suggestion.Status = SuggestionStatus.Converted;
        suggestion.PurchaseOrderId = purchaseOrderId;
    }
}

// ---------------- MRP engine (pure) ----------------

public sealed record MrpItem(
    Guid ProductId,
    decimal Available,
    decimal SafetyStock,
    int LeadTimeDays,
    LotSizeRule LotSizeRule,
    decimal LotSize,
    ProcurementType Procurement);

public sealed record MrpRequirement(Guid ProductId, decimal Quantity, DateOnly DueDate, string SourceType, string SourceReference);

public sealed record MrpReceipt(Guid ProductId, decimal Quantity, DateOnly DueDate);

/// <summary>Explodes a planned production quantity of a product, as of a date, into component base quantities; null when no BOM.</summary>
public delegate IReadOnlyList<(Guid ComponentId, decimal BaseQuantity)>? MrpExplosion(Guid productId, decimal quantity, DateOnly date);

public sealed record MrpInput(
    DateOnly RunDate,
    DateOnly HorizonEnd,
    IReadOnlyDictionary<Guid, MrpItem> Items,
    IReadOnlyList<MrpRequirement> Demand,
    IReadOnlyList<MrpReceipt> Supply,
    IReadOnlyDictionary<Guid, int> LowLevelCodes,
    MrpExplosion Explode);

public sealed record MrpResult(IReadOnlyList<MrpSuggestion> Suggestions, IReadOnlyList<MrpShortage> Shortages);

/// <summary>
/// Basic MRP (infinite capacity): products are netted in low-level-code order; for each product, requirements and scheduled
/// receipts are processed by date; whenever projected stock would fall below safety stock, a planned order is created for the
/// lot-sized shortage, due on the requirement date and ordered lead-time days earlier. Planned production explodes into
/// dependent demand for its components on the order-by date.
/// </summary>
public static class MrpEngine
{
    public static decimal LotSize(decimal need, LotSizeRule rule, decimal lotSize) => rule switch
    {
        LotSizeRule.FixedMinimum => Math.Max(need, lotSize),
        LotSizeRule.Multiple => Math.Ceiling(need / lotSize) * lotSize,
        _ => need,
    };

    public static MrpResult Run(MrpInput input)
    {
        var demand = input.Demand.Where(d => d.Quantity > 0 && d.DueDate <= input.HorizonEnd)
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var supply = input.Supply.Where(s => s.Quantity > 0 && s.DueDate <= input.HorizonEnd)
            .GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var suggestions = new List<MrpSuggestion>();
        var shortages = new List<MrpShortage>();

        var products = input.Items.Keys.Union(demand.Keys).OrderBy(p => input.LowLevelCodes.GetValueOrDefault(p)).ThenBy(p => p).ToList();
        var processed = new HashSet<Guid>();
        var queue = new List<Guid>(products);
        while (queue.Count > 0)
        {
            // Re-sort each time: dependent demand can add products discovered through explosion.
            queue = queue.Where(p => !processed.Contains(p)).Distinct()
                .OrderBy(p => input.LowLevelCodes.GetValueOrDefault(p)).ThenBy(p => p).ToList();
            if (queue.Count == 0)
            {
                break;
            }

            var product = queue[0];
            queue.RemoveAt(0);
            processed.Add(product);
            var item = input.Items.GetValueOrDefault(product) ?? new MrpItem(product, 0, 0, 0, LotSizeRule.LotForLot, 0, ProcurementType.Buy);
            var events = (demand.GetValueOrDefault(product) ?? []).Select(d => (d.DueDate, Order: 1, Demand: (MrpRequirement?)d, Receipt: (MrpReceipt?)null))
                .Concat((supply.GetValueOrDefault(product) ?? []).Select(s => (s.DueDate, Order: 0, Demand: (MrpRequirement?)null, Receipt: (MrpReceipt?)s)))
                .OrderBy(e => e.DueDate).ThenBy(e => e.Order).ToList();

            var projected = item.Available;
            if (projected < item.SafetyStock)
            {
                Plan(item, item.SafetyStock - projected, input.RunDate, [new PeggingEntry { SourceType = "SafetyStock", SourceReference = "safety stock", DueDate = input.RunDate, Quantity = item.SafetyStock - projected }]);
            }

            foreach (var e in events)
            {
                if (e.Receipt is { } receipt)
                {
                    projected += receipt.Quantity;
                    continue;
                }

                var requirement = e.Demand!;
                projected -= requirement.Quantity;
                if (projected < item.SafetyStock)
                {
                    var need = item.SafetyStock - projected;
                    Plan(item, need, requirement.DueDate,
                        [new PeggingEntry { SourceType = requirement.SourceType, SourceReference = requirement.SourceReference, DueDate = requirement.DueDate, Quantity = Math.Min(need, requirement.Quantity) }]);
                }
            }

            void Plan(MrpItem planned, decimal need, DateOnly due, List<PeggingEntry> pegging)
            {
                var quantity = MfgRounding.Quantity(LotSize(need, planned.LotSizeRule, planned.LotSize));
                var orderBy = due.AddDays(-planned.LeadTimeDays);
                var explosion = planned.Procurement == ProcurementType.Make ? input.Explode(planned.ProductId, quantity, orderBy) : null;
                var action = planned.Procurement == ProcurementType.Make && explosion is not null ? SuggestionAction.Produce : SuggestionAction.Purchase;
                var suggestion = new MrpSuggestion
                {
                    SuggestionId = SequentialGuid.New(),
                    ProductId = planned.ProductId,
                    Action = action,
                    Quantity = quantity,
                    DueDate = due,
                    OrderByDate = orderBy,
                    IsLate = orderBy < input.RunDate,
                    Pegging = pegging,
                };
                suggestions.Add(suggestion);
                projected += quantity;

                if (planned.Procurement == ProcurementType.Make && explosion is null)
                {
                    shortages.Add(new MrpShortage { ProductId = planned.ProductId, Quantity = quantity, NeededBy = due, Reason = "No effective BOM for a make item; planned as purchase." });
                }

                if (suggestion.IsLate)
                {
                    shortages.Add(new MrpShortage { ProductId = planned.ProductId, Quantity = quantity, NeededBy = due, Reason = "The lead time cannot be met before the need date." });
                }

                foreach (var (componentId, baseQuantity) in explosion ?? [])
                {
                    if (!demand.TryGetValue(componentId, out var list))
                    {
                        list = [];
                        demand[componentId] = list;
                    }

                    list.Add(new MrpRequirement(componentId, baseQuantity, orderBy < input.RunDate ? input.RunDate : orderBy, "PlannedProduction", suggestion.SuggestionId.ToString()));
                    if (!processed.Contains(componentId))
                    {
                        queue.Add(componentId);
                    }
                }
            }
        }

        return new MrpResult(suggestions, shortages);
    }
}
