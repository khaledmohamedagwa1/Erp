using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Manufacturing.Domain;

/// <summary>How finished goods of a production order are valued.</summary>
public enum ManufacturingCostingMethod
{
    /// <summary>Received at the actual cost accumulated in WIP; the residual at close is one production variance.</summary>
    Actual = 1,

    /// <summary>Received at the active standard cost; the difference to actual is split into variances at close.</summary>
    Standard = 2,
}

/// <summary>Company-wide manufacturing settings (one row per company; defaults apply until saved).</summary>
[Audited]
public sealed class ManufacturingSettings : AggregateRoot, ICompanyScoped
{
    private ManufacturingSettings()
    {
    }

    private ManufacturingSettings(Guid id, Guid companyId)
        : base(id)
    {
        CompanyId = companyId;
        DefaultCostingMethod = ManufacturingCostingMethod.Actual;
    }

    public Guid CompanyId { get; private set; }

    public ManufacturingCostingMethod DefaultCostingMethod { get; private set; }

    public static ManufacturingSettings Create(Guid companyId) => new(SequentialGuid.New(), companyId);

    public void Update(ManufacturingCostingMethod method) => DefaultCostingMethod = method;
}

public enum CostVersionStatus
{
    Draft = 1,
    Approved = 2,
    Active = 3,
    Superseded = 4,
}

public enum StandardCostSource
{
    /// <summary>Entered by a user; a roll-up keeps it unless asked to overwrite.</summary>
    Manual = 1,

    /// <summary>Calculated from BOM and routing.</summary>
    RollUp = 2,

    /// <summary>Taken from the current inventory cost of a purchased item during roll-up.</summary>
    CurrentCost = 3,
}

/// <summary>Standard cost of one product per base unit, split by element. Stored as JSON on the cost version.</summary>
public sealed class StandardCostItem
{
    public Guid ProductId { get; set; }

    public decimal Material { get; set; }

    public decimal Labor { get; set; }

    public decimal Machine { get; set; }

    public decimal Overhead { get; set; }

    public StandardCostSource Source { get; set; }

    public string? Note { get; set; }

    public decimal Conversion => MfgRounding.Cost(Labor + Machine + Overhead);

    public decimal UnitCost => MfgRounding.Cost(Material + Labor + Machine + Overhead);
}

/// <summary>
/// A set of standard costs: Draft (editable, rolled up) → Approved → Active (one per company; activating revalues stock of
/// standard-costed products and supersedes the previous active version) → Superseded.
/// </summary>
[Audited]
public sealed class CostVersion : AggregateRoot, ICompanyScoped
{
    private CostVersion()
    {
    }

    private CostVersion(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        Status = CostVersionStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public DateOnly EffectiveFrom { get; private set; }

    public CostVersionStatus Status { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateOnly? ActivatedOn { get; private set; }

    public List<StandardCostItem> Items { get; private set; } = [];

    public static CostVersion Create(Guid companyId, string code, string name, DateOnly effectiveFrom)
    {
        var version = new CostVersion(SequentialGuid.New(), companyId, Guard.NotEmpty(code, "manufacturing.code-required", "Code").ToUpperInvariant());
        version.Rename(name, effectiveFrom);
        return version;
    }

    public void Rename(string name, DateOnly effectiveFrom)
    {
        RequireDraft();
        Name = Guard.NotEmpty(name, "manufacturing.name-required", "Name");
        EffectiveFrom = effectiveFrom;
    }

    public StandardCostItem? Item(Guid productId) => Items.FirstOrDefault(i => i.ProductId == productId);

    /// <summary>Adds or replaces items by product.</summary>
    public void SetItems(IEnumerable<StandardCostItem> items)
    {
        RequireDraft();
        foreach (var item in items)
        {
            if (item.ProductId == Guid.Empty || item.Material < 0 || item.Labor < 0 || item.Machine < 0 || item.Overhead < 0)
            {
                throw new DomainException(ManufacturingErrors.Invalid("manufacturing.standard-cost-invalid", "Standard costs need a product and non-negative elements."));
            }

            item.Material = MfgRounding.Cost(item.Material);
            item.Labor = MfgRounding.Cost(item.Labor);
            item.Machine = MfgRounding.Cost(item.Machine);
            item.Overhead = MfgRounding.Cost(item.Overhead);
            Items.RemoveAll(i => i.ProductId == item.ProductId);
            Items.Add(item);
        }
    }

    public void RemoveItem(Guid productId)
    {
        RequireDraft();
        Items.RemoveAll(i => i.ProductId == productId);
    }

    public void Approve(Guid? userId)
    {
        RequireDraft();
        if (Items.Count == 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.cost-version-empty", "A cost version needs at least one standard cost."));
        }

        Status = CostVersionStatus.Approved;
        ApprovedBy = userId;
    }

    public void Reopen()
    {
        if (Status != CostVersionStatus.Approved)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        Status = CostVersionStatus.Draft;
        ApprovedBy = null;
    }

    /// <summary>Activates the version on <paramref name="today"/> (not before its effective date) and supersedes the previous active one.</summary>
    public void Activate(CostVersion? previous, DateOnly today)
    {
        if (Status != CostVersionStatus.Approved)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        if (EffectiveFrom > today)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.cost-version-not-effective",
                "The cost version can only be activated on or after its effective date."));
        }

        if (previous is not null && previous.Id != Id)
        {
            previous.Status = CostVersionStatus.Superseded;
        }

        Status = CostVersionStatus.Active;
        ActivatedOn = today;
    }

    private void RequireDraft()
    {
        if (Status != CostVersionStatus.Draft)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }
    }
}

public enum CostPoolKind
{
    Labor = 1,
    Machine = 2,
    Overhead = 3,
}

/// <summary>
/// Groups work centers whose applied labour, machine or overhead cost is compared with actual spending on the given expense
/// accounts at period end. An expense account belongs to at most one pool.
/// </summary>
[Audited]
public sealed class CostPool : AggregateRoot, ICompanyScoped
{
    private CostPool()
    {
    }

    private CostPool(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public CostPoolKind Kind { get; private set; }

    public List<Guid> WorkCenterIds { get; private set; } = [];

    public List<Guid> ExpenseAccountIds { get; private set; } = [];

    public bool IsActive { get; private set; }

    public static CostPool Create(Guid companyId, string code, string name, CostPoolKind kind, IEnumerable<Guid> workCenters, IEnumerable<Guid> accounts)
    {
        var pool = new CostPool(SequentialGuid.New(), companyId, Guard.NotEmpty(code, "manufacturing.code-required", "Code").ToUpperInvariant());
        pool.Update(name, kind, workCenters, accounts);
        return pool;
    }

    public void Update(string name, CostPoolKind kind, IEnumerable<Guid> workCenters, IEnumerable<Guid> accounts)
    {
        var wc = workCenters.Distinct().ToList();
        var acc = accounts.Distinct().ToList();
        if (wc.Count == 0 || acc.Count == 0 || wc.Contains(Guid.Empty) || acc.Contains(Guid.Empty))
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.cost-pool-invalid", "A cost pool needs at least one work center and one expense account."));
        }

        Name = Guard.NotEmpty(name, "manufacturing.name-required", "Name");
        Kind = kind;
        WorkCenterIds = wc;
        ExpenseAccountIds = acc;
    }

    public void Deactivate() => IsActive = false;
}

/// <summary>Applied versus actual of one pool for one absorption run. Stored as JSON on the run.</summary>
public sealed class AbsorptionLine
{
    public Guid PoolId { get; set; }

    public string PoolCode { get; set; } = string.Empty;

    public CostPoolKind Kind { get; set; }

    /// <summary>Cost absorbed into WIP at work-center rates in the period (credit balance of the applied account).</summary>
    public decimal Applied { get; set; }

    /// <summary>Net debits on the pool's expense accounts in the period.</summary>
    public decimal Actual { get; set; }

    /// <summary>Actual − applied: positive = under-absorbed (expense), negative = over-absorbed.</summary>
    public decimal Variance => MfgRounding.Money(Actual - Applied);

    public List<AbsorptionAccountAmount> Accounts { get; set; } = [];
}

public sealed class AbsorptionAccountAmount
{
    public Guid AccountId { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>A posted period-end clearing of applied production cost against actual spending (one per legal entity and period).</summary>
[Audited]
public sealed class AbsorptionRun : AggregateRoot, ICompanyScoped
{
    private AbsorptionRun()
    {
    }

    private AbsorptionRun(Guid id, Guid companyId, Guid legalEntityId, DateOnly from, DateOnly to)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        PeriodFrom = from;
        PeriodTo = to;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public Guid? JournalId { get; private set; }

    public List<AbsorptionLine> Lines { get; private set; } = [];

    public decimal TotalVariance => Lines.Sum(l => l.Variance);

    public static AbsorptionRun Create(Guid companyId, Guid legalEntityId, DateOnly from, DateOnly to, IEnumerable<AbsorptionLine> lines)
    {
        if (to < from)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.dates-invalid", "The period end cannot be before its start."));
        }

        return new AbsorptionRun(SequentialGuid.New(), companyId, legalEntityId, from, to) { Lines = lines.ToList() };
    }

    public void Posted(Guid? journalId) => JournalId = journalId;
}

/// <summary>Variances of a standard-costed production order at close, base currency. Their sum equals the residual WIP.</summary>
public sealed record StandardVariances(decimal MaterialPrice, decimal MaterialUsage, decimal Conversion, decimal Other)
{
    public decimal Total => MaterialPrice + MaterialUsage + Conversion + Other;
}

/// <summary>Pure standard-cost arithmetic.</summary>
public static class StandardCosting
{
    /// <summary>
    /// Splits the residual WIP of a standard-costed order:
    /// <list type="bullet">
    /// <item>material price = Σ (actual issued cost − actual base quantity × component standard), for components with a standard;</item>
    /// <item>material usage = Σ (actual base quantity − standard base quantity for the good output) × component standard (components
    /// without a standard use their actual average issue cost);</item>
    /// <item>conversion = actual applied conversion − standard conversion × good output;</item>
    /// <item>other = what remains (material standard of the product differing from the sum of component standards, rounding).</item>
    /// </list>
    /// </summary>
    public static StandardVariances Split(ProductionOrder order)
    {
        var good = order.CompletedQuantity;
        decimal price = 0, usage = 0;
        foreach (var c in order.Components)
        {
            var actualBase = c.IssuedQuantity * c.UomFactor;
            var standardBase = c.QuantityPerUnit * c.UomFactor * good;
            var unit = c.StandardUnitCost ?? (actualBase > 0 ? c.IssuedCost / actualBase : 0);
            if (c.StandardUnitCost is { } std)
            {
                price += c.IssuedCost - (actualBase * std);
            }

            usage += (actualBase - standardBase) * unit;
        }

        price = MfgRounding.Money(price);
        usage = MfgRounding.Money(usage);
        var conversion = MfgRounding.Money(order.LaborCost + order.MachineCost + order.OverheadCost - (order.StandardConversionUnitCost * good));
        var other = MfgRounding.Money(order.WipBalance - price - usage - conversion);
        return new StandardVariances(price, usage, conversion, other);
    }
}
