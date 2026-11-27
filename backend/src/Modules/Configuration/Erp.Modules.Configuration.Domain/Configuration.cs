using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Configuration.Domain;

public sealed record ModuleDefinition(string Code, string NameEn, string NameAr, bool IsCore, IReadOnlyCollection<string> DependsOn);

/// <summary>Every business module the platform knows about and how they depend on each other (capability map §3.3).</summary>
public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new("organization", "Organization", "الهيكل التنظيمي", true, []),
        new("identity", "Users & security", "المستخدمون والأمان", true, []),
        new("configuration", "Configuration", "الإعدادات", true, []),
        new("catalog", "Products & pricing", "الأصناف والتسعير", true, []),
        new("parties", "Customers & suppliers", "العملاء والموردون", true, []),
        new("tax", "Taxes & currencies", "الضرائب والعملات", true, []),
        new("accounting", "Accounting", "المحاسبة", true, []),
        new("finance", "Receivables, payables, cash & bank", "الذمم والنقدية والبنوك", true, ["accounting"]),
        new("workflow", "Approvals", "الموافقات", true, []),
        new("reporting", "Reports", "التقارير", true, []),
        new("people", "Employees & salespersons", "الموظفون ومندوبو المبيعات", true, []),
        new("inventory", "Inventory", "المخزون", false, []),
        new("sales", "Sales", "المبيعات", false, []),
        new("procurement", "Purchasing", "المشتريات", false, []),
        new("pos", "Point of sale", "نقطة البيع", false, ["sales", "inventory"]),
        new("manufacturing", "Manufacturing", "التصنيع", false, ["inventory", "procurement"]),
        new("fixed-assets", "Fixed assets", "الأصول الثابتة", false, ["accounting"]),
        new("commissions", "Sales commissions", "عمولات المبيعات", false, ["sales"]),
        new("multi-currency", "Multi-currency", "تعدد العملات", false, []),
    ];

    public static ModuleDefinition? Find(string code) => All.FirstOrDefault(m => m.Code == code);

    /// <summary>Optional modules switched on by each setup preset. Presets are a starting point only (ADR-020).</summary>
    public static IReadOnlyCollection<string> ModulesForPreset(string preset) => preset switch
    {
        "retail" => ["sales", "inventory", "procurement", "pos"],
        "wholesale" => ["sales", "inventory", "procurement"],
        "trading" => ["sales", "inventory", "procurement", "multi-currency"],
        "distribution" => ["sales", "inventory", "procurement", "commissions"],
        "manufacturing" => ["sales", "inventory", "procurement", "manufacturing"],
        "services" => ["sales", "procurement"],
        _ => throw new DomainException(Error.Validation("configuration.unknown-preset", $"Unknown business preset '{preset}'.")),
    };

    /// <summary>Checks switching a module on/off against dependencies. Core modules can never be switched off.</summary>
    public static Result ValidateChange(string code, bool enable, IReadOnlySet<string> currentlyEnabled)
    {
        var module = Find(code);
        if (module is null)
        {
            return Error.NotFound("configuration.unknown-module", $"Unknown module '{code}'.");
        }

        if (module.IsCore)
        {
            return Error.Conflict("configuration.core-module", "Core modules are always enabled.");
        }

        if (enable)
        {
            var missing = module.DependsOn.Where(d => !IsOn(d, currentlyEnabled)).ToList();
            return missing.Count == 0
                ? Result.Success()
                : Error.Conflict("configuration.module-dependency-missing", $"Enable {string.Join(", ", missing)} first.");
        }

        var dependents = All.Where(m => m.DependsOn.Contains(code) && IsOn(m.Code, currentlyEnabled)).Select(m => m.Code).ToList();
        return dependents.Count == 0
            ? Result.Success()
            : Error.Conflict("configuration.module-in-use", $"Disable {string.Join(", ", dependents)} first.");
    }

    public static bool IsOn(string code, IReadOnlySet<string> enabled) => Find(code)?.IsCore == true || enabled.Contains(code);
}

[Audited]
public sealed class CompanyModule : AggregateRoot, ICompanyScoped
{
    private CompanyModule()
    {
    }

    private CompanyModule(Guid id, Guid companyId, string moduleCode, bool isEnabled, DateTime changedAtUtc)
        : base(id)
    {
        CompanyId = companyId;
        ModuleCode = moduleCode;
        IsEnabled = isEnabled;
        ChangedAtUtc = changedAtUtc;
    }

    public Guid CompanyId { get; private set; }

    public string ModuleCode { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public DateTime ChangedAtUtc { get; private set; }

    public static CompanyModule Create(Guid companyId, string moduleCode, bool isEnabled, DateTime nowUtc) =>
        new(SequentialGuid.New(), companyId, moduleCode, isEnabled, nowUtc);

    public void Set(bool isEnabled, DateTime nowUtc)
    {
        IsEnabled = isEnabled;
        ChangedAtUtc = nowUtc;
        Raise(new ModuleActivationChanged(CompanyId, ModuleCode, isEnabled));
    }
}

/// <summary>One section of business policy (inventory, sales, procurement) stored as validated JSON, versioned and audited.</summary>
[Audited]
public sealed class CompanyPolicy : AggregateRoot, ICompanyScoped
{
    private CompanyPolicy()
    {
    }

    private CompanyPolicy(Guid id, Guid companyId, string section, string json)
        : base(id)
    {
        CompanyId = companyId;
        Section = section;
        Json = json;
        Version = 1;
    }

    public Guid CompanyId { get; private set; }

    public string Section { get; private set; } = string.Empty;

    public string Json { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public static CompanyPolicy Create(Guid companyId, string section, string json) => new(SequentialGuid.New(), companyId, section, json);

    public void Update(string json)
    {
        Json = json;
        Version++;
        Raise(new PolicyChanged(CompanyId, Section, Version));
    }
}

public sealed record ModuleActivationChanged(Guid CompanyId, string ModuleCode, bool IsEnabled) : IDomainEvent;

public sealed record PolicyChanged(Guid CompanyId, string Section, int Version) : IDomainEvent;
