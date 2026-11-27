using System.Text.RegularExpressions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Organization.Domain;

public static partial class OrganizationRules
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,29}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public static string Code(string? value, string field)
    {
        var code = Guard.NotEmpty(value, "organization.code-required", field).ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainException(Error.Validation("organization.code-invalid",
                $"{field} must be 2–30 characters: letters, digits, '-' or '_'."));
        }

        return code;
    }

    public static string Currency(string? value)
    {
        var currency = Guard.NotEmpty(value, "organization.currency-required", "Currency").ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(currency))
        {
            throw new DomainException(Error.Validation("organization.currency-invalid", "Currency must be a 3-letter ISO 4217 code."));
        }

        return currency;
    }

    public static string Name(string? value, string field) =>
        Guard.MaxLength(Guard.NotEmpty(value, "organization.name-required", field), 200, "organization.name-too-long", field);
}

public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>The isolation boundary (a SaaS customer or an on-prem installation). Not itself tenant-scoped.</summary>
public sealed class Tenant : AggregateRoot
{
    private Tenant()
    {
    }

    private Tenant(Guid id, string code, string name)
        : base(id)
    {
        Code = code;
        Name = name;
        Status = TenantStatus.Active;
        IsolationMode = "Shared";
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    /// <summary>Shared (TenantId column) or Dedicated (own database, later phase).</summary>
    public string IsolationMode { get; private set; } = string.Empty;

    public static Tenant Create(Guid id, string code, string name)
    {
        var tenant = new Tenant(id, OrganizationRules.Code(code, "Tenant code"), OrganizationRules.Name(name, "Tenant name"));
        tenant.Raise(new TenantProvisioned(id, tenant.Code));
        return tenant;
    }

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Activate() => Status = TenantStatus.Active;
}

[Audited]
public sealed class Company : AggregateRoot, ITenantScoped
{
    private Company()
    {
    }

    private Company(Guid id, string code, string nameEn, string nameAr, string baseCurrency, string businessPreset)
        : base(id)
    {
        Code = code;
        NameEn = nameEn;
        NameAr = nameAr;
        BaseCurrency = baseCurrency;
        BusinessPreset = businessPreset;
    }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public string BaseCurrency { get; private set; } = string.Empty;

    /// <summary>Only the starting point used at setup; behaviour comes from module activation and policies (ADR-020).</summary>
    public string BusinessPreset { get; private set; } = string.Empty;

    public static Company Create(string code, string nameEn, string nameAr, string baseCurrency, string businessPreset)
    {
        var company = new Company(
            SequentialGuid.New(),
            OrganizationRules.Code(code, "Company code"),
            OrganizationRules.Name(nameEn, "English name"),
            OrganizationRules.Name(nameAr, "Arabic name"),
            OrganizationRules.Currency(baseCurrency),
            Guard.NotEmpty(businessPreset, "organization.preset-required", "Business preset"));
        company.Raise(new CompanyCreated(company.Id, company.Code));
        return company;
    }

    public void Rename(string nameEn, string nameAr)
    {
        NameEn = OrganizationRules.Name(nameEn, "English name");
        NameAr = OrganizationRules.Name(nameAr, "Arabic name");
    }
}

/// <summary>A legally registered entity with its own books, tax registration and functional currency.</summary>
[Audited]
public sealed class LegalEntity : AggregateRoot, ICompanyScoped
{
    private LegalEntity()
    {
    }

    private LegalEntity(Guid id, Guid companyId, string name, string functionalCurrency, string? taxRegistrationNo)
        : base(id)
    {
        CompanyId = companyId;
        Name = name;
        FunctionalCurrency = functionalCurrency;
        TaxRegistrationNo = taxRegistrationNo;
    }

    public Guid CompanyId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Immutable once journals exist (enforced when accounting is built).</summary>
    public string FunctionalCurrency { get; private set; } = string.Empty;

    public string? TaxRegistrationNo { get; private set; }

    public static LegalEntity Create(Guid companyId, string name, string functionalCurrency, string? taxRegistrationNo) =>
        new(SequentialGuid.New(), companyId, OrganizationRules.Name(name, "Legal entity name"),
            OrganizationRules.Currency(functionalCurrency), string.IsNullOrWhiteSpace(taxRegistrationNo) ? null : taxRegistrationNo.Trim());
}

[Audited]
public sealed class Branch : AggregateRoot, ICompanyScoped
{
    private Branch()
    {
    }

    private Branch(Guid id, Guid companyId, Guid legalEntityId, string code, string name)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        Code = code;
        Name = name;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public static Branch Create(Guid companyId, Guid legalEntityId, string code, string name) =>
        new(SequentialGuid.New(), companyId, legalEntityId, OrganizationRules.Code(code, "Branch code"), OrganizationRules.Name(name, "Branch name"));

    public void Deactivate() => IsActive = false;
}

public enum WarehouseType
{
    Physical = 1,

    /// <summary>System location holding stock issued on a transfer and not yet received. One per legal entity.</summary>
    Transit = 2,
    Production = 3,
    Virtual = 4,
}

[Audited]
public sealed class Warehouse : AggregateRoot, ICompanyScoped
{
    private Warehouse()
    {
    }

    private Warehouse(Guid id, Guid companyId, Guid legalEntityId, Guid? branchId, string code, string name, WarehouseType type)
        : base(id)
    {
        CompanyId = companyId;
        LegalEntityId = legalEntityId;
        BranchId = branchId;
        Code = code;
        Name = name;
        Type = type;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid? BranchId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public WarehouseType Type { get; private set; }

    public bool IsActive { get; private set; }

    public static Warehouse Create(Branch branch, string code, string name, WarehouseType type)
    {
        if (type == WarehouseType.Transit)
        {
            throw new DomainException(Error.Validation("organization.transit-warehouse-is-system", "Transit warehouses are created by the system."));
        }

        return new Warehouse(SequentialGuid.New(), branch.CompanyId, branch.LegalEntityId, branch.Id,
            OrganizationRules.Code(code, "Warehouse code"), OrganizationRules.Name(name, "Warehouse name"), type);
    }

    public static Warehouse CreateTransit(LegalEntity legalEntity, string code) =>
        new(SequentialGuid.New(), legalEntity.CompanyId, legalEntity.Id, null, OrganizationRules.Code(code, "Warehouse code"),
            $"In transit — {legalEntity.Name}", WarehouseType.Transit);
}

/// <summary>Everything a new company needs to be usable: one legal entity, a head-office branch, a main and a transit warehouse.</summary>
public sealed record CompanyStructure(Company Company, LegalEntity LegalEntity, Branch HeadOffice, Warehouse MainWarehouse, Warehouse TransitWarehouse)
{
    public static CompanyStructure Create(string code, string nameEn, string nameAr, string baseCurrency, string businessPreset, string? taxRegistrationNo)
    {
        var company = Company.Create(code, nameEn, nameAr, baseCurrency, businessPreset);
        var legalEntity = LegalEntity.Create(company.Id, company.NameEn, company.BaseCurrency, taxRegistrationNo);
        var headOffice = Branch.Create(company.Id, legalEntity.Id, "HQ", "Head office");
        var main = Warehouse.Create(headOffice, "MAIN", "Main warehouse", WarehouseType.Physical);
        var transit = Warehouse.CreateTransit(legalEntity, "TRANSIT");
        return new CompanyStructure(company, legalEntity, headOffice, main, transit);
    }
}

public sealed record TenantProvisioned(Guid TenantId, string Code) : IDomainEvent;

public sealed record CompanyCreated(Guid CompanyId, string Code) : IDomainEvent;
