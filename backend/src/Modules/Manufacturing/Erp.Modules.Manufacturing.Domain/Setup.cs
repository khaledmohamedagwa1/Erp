using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Manufacturing.Domain;

public static class MfgRounding
{
    public static decimal Quantity(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Cost(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

public static class ManufacturingErrors
{
    public static Error Invalid(string code, string message) => Error.Validation(code, message);

    public static readonly Error InvalidTransition = Error.Conflict("invalid-state-transition", "This action is not allowed in the document's current status.");
    public static readonly Error CodeTaken = Error.Conflict("manufacturing.code-taken", "The code is already used in this company.");
    public static readonly Error NotFound = Error.NotFound("manufacturing.not-found", "The record was not found.");
    public static readonly Error BomCycle = Error.Conflict("manufacturing.bom-cycle", "Activating this BOM version would create a cycle in the multi-level bill of materials.");
    public static readonly Error VersionOverlap = Error.Conflict("manufacturing.bom-version-overlap", "The effective dates overlap another active version of this BOM.");
    public static readonly Error NoEffectiveBom = Error.Conflict("manufacturing.no-effective-bom", "The product has no active BOM version effective on that date.");
}

public enum OverheadBasis
{
    LaborHours = 1,
    MachineHours = 2,
}

/// <summary>A resource that performs operations. Rates are base currency per hour.</summary>
[Audited]
public sealed class WorkCenter : AggregateRoot, ICompanyScoped
{
    private WorkCenter()
    {
    }

    private WorkCenter(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public decimal CapacityHoursPerDay { get; private set; }

    public decimal LaborRatePerHour { get; private set; }

    public decimal MachineRatePerHour { get; private set; }

    public decimal OverheadRatePerHour { get; private set; }

    public OverheadBasis OverheadBasis { get; private set; }

    /// <summary>Working days for capacity planning (<see cref="WorkingDays"/> bit mask; all days by default).</summary>
    public int WorkingDaysMask { get; private set; } = WorkingDays.All;

    public bool IsActive { get; private set; }

    public static WorkCenter Create(Guid companyId, string code, string nameEn, string nameAr, decimal capacity, decimal labor, decimal machine, decimal overhead, OverheadBasis basis)
    {
        var wc = new WorkCenter(SequentialGuid.New(), companyId, Guard.NotEmpty(code, "manufacturing.code-required", "Code").ToUpperInvariant());
        wc.Update(nameEn, nameAr, capacity, labor, machine, overhead, basis);
        return wc;
    }

    public void Update(string nameEn, string nameAr, decimal capacity, decimal labor, decimal machine, decimal overhead, OverheadBasis basis)
    {
        if (capacity < 0 || labor < 0 || machine < 0 || overhead < 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.rate-negative", "Capacity and rates cannot be negative."));
        }

        NameEn = Guard.NotEmpty(nameEn, "manufacturing.name-required", "English name");
        NameAr = Guard.NotEmpty(nameAr, "manufacturing.name-required", "Arabic name");
        CapacityHoursPerDay = capacity;
        LaborRatePerHour = labor;
        MachineRatePerHour = machine;
        OverheadRatePerHour = overhead;
        OverheadBasis = basis;
    }

    public void SetWorkingDays(int mask)
    {
        if (mask is < 0 or > WorkingDays.All)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.working-days-invalid", "Invalid working days."));
        }

        WorkingDaysMask = mask;
    }

    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Standard conversion cost per planned hour, unrounded: an operation is assumed to occupy labour and machine for its planned
    /// hours; overhead follows the work center's basis (both bases equal the planned hours).
    /// </summary>
    public (decimal Labor, decimal Machine, decimal Overhead) StandardCost(decimal plannedHours) =>
        (plannedHours * LaborRatePerHour, plannedHours * MachineRatePerHour, plannedHours * OverheadRatePerHour);

    /// <summary>Conversion cost of reported hours, each component rounded to cents.</summary>
    public (decimal Labor, decimal Machine, decimal Overhead) Cost(decimal laborHours, decimal machineHours) =>
        (MfgRounding.Money(laborHours * LaborRatePerHour),
         MfgRounding.Money(machineHours * MachineRatePerHour),
         MfgRounding.Money((OverheadBasis == OverheadBasis.MachineHours ? machineHours : laborHours) * OverheadRatePerHour));
}

/// <summary>One step of a routing. Stored as JSON on the routing.</summary>
public sealed class RoutingOperation
{
    public int Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid WorkCenterId { get; set; }

    public decimal SetupHours { get; set; }

    public decimal RunHoursPerUnit { get; set; }

    public decimal PlannedHours(decimal quantity) => MfgRounding.Quantity(SetupHours + (RunHoursPerUnit * quantity));
}

[Audited]
public sealed class Routing : AggregateRoot, ICompanyScoped
{
    private Routing()
    {
    }

    private Routing(Guid id, Guid companyId, Guid productId, string code)
        : base(id)
    {
        CompanyId = companyId;
        ProductId = productId;
        Code = code;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public List<RoutingOperation> Operations { get; private set; } = [];

    public static Routing Create(Guid companyId, Guid productId, string code, string name, IEnumerable<RoutingOperation> operations)
    {
        var routing = new Routing(SequentialGuid.New(), companyId, productId, Guard.NotEmpty(code, "manufacturing.code-required", "Code").ToUpperInvariant());
        routing.Update(name, operations);
        return routing;
    }

    public void Update(string name, IEnumerable<RoutingOperation> operations)
    {
        var list = operations.OrderBy(o => o.Sequence).ToList();
        if (list.Count == 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.routing-empty", "A routing needs at least one operation."));
        }

        if (list.Any(o => o.Sequence <= 0 || o.SetupHours < 0 || o.RunHoursPerUnit < 0 || o.WorkCenterId == Guid.Empty || string.IsNullOrWhiteSpace(o.Name)))
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.routing-invalid", "Operations need a positive sequence, a name, a work center and non-negative hours."));
        }

        if (list.Select(o => o.Sequence).Distinct().Count() != list.Count)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.routing-duplicate-sequence", "Operation sequences must be unique."));
        }

        Name = Guard.NotEmpty(name, "manufacturing.name-required", "Name");
        Operations = list;
    }

    public void Deactivate() => IsActive = false;
}

/// <summary>Bill of materials header: one output product, many versions.</summary>
[Audited]
public sealed class BillOfMaterials : AggregateRoot, ICompanyScoped
{
    private BillOfMaterials()
    {
    }

    private BillOfMaterials(Guid id, Guid companyId, Guid productId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        ProductId = productId;
        Code = code;
        Name = name;
    }

    public Guid CompanyId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static BillOfMaterials Create(Guid companyId, Guid productId, string code, string name) =>
        new(SequentialGuid.New(), companyId, productId, Guard.NotEmpty(code, "manufacturing.code-required", "Code").ToUpperInvariant(),
            Guard.NotEmpty(name, "manufacturing.name-required", "Name"));
}

public enum IssueMethod
{
    Manual = 1,

    /// <summary>Issued automatically in proportion to finished goods received.</summary>
    Backflush = 2,
}

/// <summary>One component of a BOM version. Stored as JSON on the version.</summary>
public sealed class BomLine
{
    public Guid LineId { get; set; }

    public Guid ComponentProductId { get; set; }

    /// <summary>Quantity of the component (in <see cref="UomId"/>) per <see cref="BomVersion.OutputQuantity"/> of output.</summary>
    public decimal QuantityPerOutput { get; set; }

    public Guid UomId { get; set; }

    public decimal ScrapPercent { get; set; }

    public IssueMethod IssueMethod { get; set; } = IssueMethod.Manual;

    /// <summary>Alternative components in priority order (first = preferred), substituted 1:1 in base units when this one is short.</summary>
    public List<Guid> Alternatives { get; set; } = [];

    /// <summary>Optional variant of the component to issue.</summary>
    public Guid? ComponentVariantId { get; set; }

    /// <summary>
    /// Variant condition: the line applies only to output variants having all these attribute values (e.g. Size = L).
    /// Null or empty = all variants (and the product without variants).
    /// </summary>
    public Dictionary<string, string>? AppliesTo { get; set; }

    public bool AppliesToVariant(IReadOnlyDictionary<string, string>? attributes) =>
        AppliesTo is not { Count: > 0 } ||
        (attributes is not null && AppliesTo.All(c => attributes.Any(a =>
            string.Equals(a.Key.Trim(), c.Key.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Value.Trim(), c.Value.Trim(), StringComparison.OrdinalIgnoreCase))));
}

public enum BomVersionStatus
{
    Draft = 1,
    Active = 2,
    Superseded = 3,
    Obsolete = 4,
}

/// <summary>A dated version of a BOM. Active and superseded versions are used for their effective period; obsolete ones never.</summary>
[Audited]
public sealed class BomVersion : AggregateRoot, ICompanyScoped
{
    private BomVersion()
    {
    }

    private BomVersion(Guid id, Guid companyId, Guid bomId, Guid productId, int versionNo)
        : base(id)
    {
        CompanyId = companyId;
        BomId = bomId;
        ProductId = productId;
        VersionNo = versionNo;
        Status = BomVersionStatus.Draft;
    }

    public Guid CompanyId { get; private set; }

    public Guid BomId { get; private set; }

    /// <summary>Output product (copied from the BOM for planning queries).</summary>
    public Guid ProductId { get; private set; }

    public int VersionNo { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public DateOnly? EffectiveTo { get; private set; }

    public BomVersionStatus Status { get; private set; }

    /// <summary>Output quantity in the product's base unit that the component quantities produce.</summary>
    public decimal OutputQuantity { get; private set; }

    public decimal ExpectedYieldPercent { get; private set; }

    public List<BomLine> Lines { get; private set; } = [];

    public bool IsUsable => Status is BomVersionStatus.Active or BomVersionStatus.Superseded;

    public static BomVersion Create(BillOfMaterials bom, int versionNo, DateOnly from, DateOnly? to, decimal outputQuantity, decimal yieldPercent, IEnumerable<BomLine> lines)
    {
        var version = new BomVersion(SequentialGuid.New(), bom.CompanyId, bom.Id, bom.ProductId, versionNo);
        version.Update(from, to, outputQuantity, yieldPercent, lines);
        return version;
    }

    public void Update(DateOnly from, DateOnly? to, decimal outputQuantity, decimal yieldPercent, IEnumerable<BomLine> lines)
    {
        if (Status != BomVersionStatus.Draft)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        if (to is { } end && end < from)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.dates-invalid", "Effective-to must be on or after effective-from."));
        }

        if (outputQuantity <= 0 || yieldPercent is <= 0 or > 100)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.bom-output-invalid", "Output quantity must be positive and yield between 0 and 100 %."));
        }

        var list = lines.ToList();
        if (list.Count == 0)
        {
            throw new DomainException(ManufacturingErrors.Invalid("manufacturing.bom-empty", "A BOM version needs at least one component."));
        }

        foreach (var line in list)
        {
            if (line.QuantityPerOutput <= 0 || line.ScrapPercent is < 0 or >= 100 || line.ComponentProductId == Guid.Empty || line.UomId == Guid.Empty)
            {
                throw new DomainException(ManufacturingErrors.Invalid("manufacturing.bom-line-invalid",
                    "Components need a product, a unit, a positive quantity and a scrap percentage from 0 to below 100."));
            }

            if (line.ComponentProductId == ProductId)
            {
                throw new DomainException(ManufacturingErrors.BomCycle);
            }

            if (line.LineId == Guid.Empty)
            {
                line.LineId = SequentialGuid.New();
            }
        }

        EffectiveFrom = from;
        EffectiveTo = to;
        OutputQuantity = outputQuantity;
        ExpectedYieldPercent = yieldPercent;
        Lines = list;
    }

    public bool IsEffectiveOn(DateOnly date) => IsUsable && EffectiveFrom <= date && (EffectiveTo is null || EffectiveTo >= date);

    /// <summary>
    /// Activates the version. Other usable versions of the same BOM may not overlap it, except an open-ended version that
    /// starts earlier: that one is ended the day before and marked superseded.
    /// </summary>
    public void Activate(IReadOnlyCollection<BomVersion> otherVersionsOfBom)
    {
        if (Status != BomVersionStatus.Draft)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        foreach (var other in otherVersionsOfBom.Where(v => v.Id != Id && v.IsUsable))
        {
            if (!Overlaps(other))
            {
                continue;
            }

            if (other.EffectiveTo is null && other.EffectiveFrom < EffectiveFrom)
            {
                other.EffectiveTo = EffectiveFrom.AddDays(-1);
                other.Status = BomVersionStatus.Superseded;
                continue;
            }

            throw new DomainException(ManufacturingErrors.VersionOverlap);
        }

        Status = BomVersionStatus.Active;
    }

    public void MakeObsolete()
    {
        if (Status == BomVersionStatus.Obsolete)
        {
            throw new DomainException(ManufacturingErrors.InvalidTransition);
        }

        Status = BomVersionStatus.Obsolete;
    }

    private bool Overlaps(BomVersion other)
    {
        var thisEnd = EffectiveTo ?? DateOnly.MaxValue;
        var otherEnd = other.EffectiveTo ?? DateOnly.MaxValue;
        return EffectiveFrom <= otherEnd && other.EffectiveFrom <= thisEnd;
    }
}

public sealed record ComponentRequirement(Guid LineId, Guid ProductId, Guid UomId, IssueMethod IssueMethod, decimal QuantityPerUnit, decimal Quantity)
{
    public Guid? VariantId { get; init; }

    public IReadOnlyList<Guid> Alternatives { get; init; } = [];
}

/// <summary>Pure BOM arithmetic and graph checks.</summary>
public static class BomCalculator
{
    /// <summary>
    /// Component quantities for <paramref name="outputQuantity"/> base units of output:
    /// qty = output / bomOutput × qtyPerOutput × (1 + scrap%) / yield%. Rounded to 6 decimals, half away from zero.
    /// </summary>
    /// <remarks>Lines with a variant condition are included only when <paramref name="variantAttributes"/> satisfy it.</remarks>
    public static IReadOnlyList<ComponentRequirement> Explode(BomVersion version, decimal outputQuantity, IReadOnlyDictionary<string, string>? variantAttributes = null) =>
        version.Lines.Where(line => line.AppliesToVariant(variantAttributes)).Select(line =>
        {
            var perUnit = line.QuantityPerOutput / version.OutputQuantity * (1 + (line.ScrapPercent / 100m)) / (version.ExpectedYieldPercent / 100m);
            return new ComponentRequirement(line.LineId, line.ComponentProductId, line.UomId, line.IssueMethod, perUnit, MfgRounding.Quantity(perUnit * outputQuantity))
            {
                VariantId = line.ComponentVariantId,
                Alternatives = line.Alternatives,
            };
        }).ToList();

    /// <summary>The version effective on the date among the BOM versions of one product (latest start wins if data is inconsistent).</summary>
    public static BomVersion? Effective(IEnumerable<BomVersion> versionsOfProduct, DateOnly date) =>
        versionsOfProduct.Where(v => v.IsEffectiveOn(date)).OrderByDescending(v => v.EffectiveFrom).FirstOrDefault();

    /// <summary>True when the product reaches itself through component edges (product → components).</summary>
    public static bool HasCycle(IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> components, Guid start)
    {
        var stack = new Stack<Guid>(components.GetValueOrDefault(start) ?? []);
        var seen = new HashSet<Guid>();
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (next == start)
            {
                return true;
            }

            if (!seen.Add(next))
            {
                continue;
            }

            foreach (var child in components.GetValueOrDefault(next) ?? [])
            {
                stack.Push(child);
            }
        }

        return false;
    }

    /// <summary>
    /// Low-level codes: the deepest level at which a product appears in any structure (top items 0). Products are planned in
    /// ascending code order so all dependent demand is known before a component is netted. Assumes the graph is acyclic.
    /// </summary>
    public static IReadOnlyDictionary<Guid, int> LowLevelCodes(IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> components, IEnumerable<Guid> products)
    {
        var levels = products.Distinct().ToDictionary(p => p, _ => 0);
        foreach (var edge in components.SelectMany(kv => kv.Value.Append(kv.Key)))
        {
            levels.TryAdd(edge, 0);
        }

        var changed = true;
        var guard = 0;
        while (changed && guard++ < 1000)
        {
            changed = false;
            foreach (var (parent, children) in components)
            {
                foreach (var child in children)
                {
                    if (levels[child] < levels[parent] + 1)
                    {
                        levels[child] = levels[parent] + 1;
                        changed = true;
                    }
                }
            }
        }

        return levels;
    }
}
