using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Identity.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Organization.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Organization.Application;

public interface IOrganizationDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Company> Companies { get; }

    DbSet<LegalEntity> LegalEntities { get; }

    DbSet<Branch> Branches { get; }

    DbSet<Warehouse> Warehouses { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class OrganizationErrors
{
    public static readonly Error TenantCodeTaken = Error.Conflict("organization.tenant-code-taken", "A tenant with this code already exists.");
    public static readonly Error CompanyCodeTaken = Error.Conflict("organization.company-code-taken", "A company with this code already exists.");
    public static readonly Error CompanyNotFound = Error.NotFound("organization.company-not-found", "The company was not found.");
    public static readonly Error BranchCodeTaken = Error.Conflict("organization.branch-code-taken", "A branch with this code already exists in the company.");
    public static readonly Error BranchNotFound = Error.NotFound("organization.branch-not-found", "The branch was not found.");
    public static readonly Error LegalEntityNotFound = Error.NotFound("organization.legal-entity-not-found", "The legal entity was not found.");
    public static readonly Error WarehouseCodeTaken = Error.Conflict("organization.warehouse-code-taken", "A warehouse with this code already exists in the company.");
}

// ---------- DTOs ----------
public sealed record CompanyDto(Guid Id, string Code, string NameEn, string NameAr, string BaseCurrency, string BusinessPreset);

public sealed record LegalEntityDto(Guid Id, Guid CompanyId, string Name, string FunctionalCurrency, string? TaxRegistrationNo);

public sealed record BranchDto(Guid Id, Guid LegalEntityId, string Code, string Name, bool IsActive);

public sealed record WarehouseDto(Guid Id, Guid LegalEntityId, Guid? BranchId, string Code, string Name, string Type, bool IsActive);

public sealed record ProvisionTenantResult(Guid TenantId, string TenantCode, Guid CompanyId, Guid AdministratorUserId);

// ---------- Tenant provisioning ----------

/// <summary>Creates a tenant with its first company, structure, administrator and default configuration — atomically.</summary>
public sealed record ProvisionTenantCommand(
    string TenantCode,
    string TenantName,
    string CompanyCode,
    string CompanyNameEn,
    string CompanyNameAr,
    string BaseCurrency,
    string BusinessPreset,
    string AdminEmail,
    string AdminDisplayName,
    string AdminPassword) : ICommand<ProvisionTenantResult>, IManualTransaction;

public sealed class ProvisionTenantValidator : AbstractValidator<ProvisionTenantCommand>
{
    public ProvisionTenantValidator()
    {
        RuleFor(x => x.TenantCode).NotEmpty().MaximumLength(30);
        RuleFor(x => x.TenantName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.CompanyCode).NotEmpty().MaximumLength(30);
        RuleFor(x => x.CompanyNameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.CompanyNameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BaseCurrency).NotEmpty().Length(3);
        RuleFor(x => x.BusinessPreset).Must(p => BusinessPresets.All.Contains(p)).WithMessage("Unknown business preset.");
        RuleFor(x => x.AdminEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.AdminDisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AdminPassword).NotEmpty().MinimumLength(10).WithMessage("Password must be at least 10 characters.");
    }
}

public sealed class ProvisionTenantHandler(
    IOrganizationDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    IUnitOfWork unitOfWork,
    IIdentityProvisioning identity,
    IConfigurationProvisioning configuration) : ICommandHandler<ProvisionTenantCommand, ProvisionTenantResult>
{
    public async Task<Result<ProvisionTenantResult>> Handle(ProvisionTenantCommand command, CancellationToken cancellationToken)
    {
        if (await tenants.FindByCodeAsync(command.TenantCode.Trim().ToUpperInvariant(), cancellationToken) is not null)
        {
            return OrganizationErrors.TenantCodeTaken;
        }

        var tenantId = Erp.SharedKernel.Domain.SequentialGuid.New();
        var tenant = Tenant.Create(tenantId, command.TenantCode, command.TenantName);

        // The new tenant becomes the scope's tenant before any connection opens, so row-level security applies to every insert.
        tenantContext.SetTenant(tenantId);
        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var structure = CompanyStructure.Create(command.CompanyCode, command.CompanyNameEn, command.CompanyNameAr,
                command.BaseCurrency, command.BusinessPreset, null);
            db.Tenants.Add(tenant);
            AddStructure(db, structure);
            await db.SaveChangesAsync(cancellationToken);

            var adminId = await identity.CreateTenantAdministratorAsync(
                new TenantAdministratorRequest(structure.Company.Id, command.AdminEmail, command.AdminDisplayName, command.AdminPassword),
                cancellationToken);
            await configuration.InitializeCompanyAsync(structure.Company.Id, command.BusinessPreset, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
            return new ProvisionTenantResult(tenantId, tenant.Code, structure.Company.Id, adminId);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal static void AddStructure(IOrganizationDbContext db, CompanyStructure structure)
    {
        db.Companies.Add(structure.Company);
        db.LegalEntities.Add(structure.LegalEntity);
        db.Branches.Add(structure.HeadOffice);
        db.Warehouses.Add(structure.MainWarehouse);
        db.Warehouses.Add(structure.TransitWarehouse);
    }
}

// ---------- Companies ----------
public sealed record CreateCompanyCommand(string Code, string NameEn, string NameAr, string BaseCurrency, string BusinessPreset, string? TaxRegistrationNo)
    : ICommand<CompanyDto>;

public sealed class CreateCompanyValidator : AbstractValidator<CreateCompanyCommand>
{
    public CreateCompanyValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BaseCurrency).NotEmpty().Length(3);
        RuleFor(x => x.BusinessPreset).Must(p => BusinessPresets.All.Contains(p)).WithMessage("Unknown business preset.");
        RuleFor(x => x.TaxRegistrationNo).MaximumLength(50);
    }
}

public sealed class CreateCompanyHandler(
    IOrganizationDbContext db,
    ICurrentUser currentUser,
    IIdentityProvisioning identity,
    IConfigurationProvisioning configuration) : ICommandHandler<CreateCompanyCommand, CompanyDto>
{
    public async Task<Result<CompanyDto>> Handle(CreateCompanyCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Companies.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return OrganizationErrors.CompanyCodeTaken;
        }

        var structure = CompanyStructure.Create(code, command.NameEn, command.NameAr, command.BaseCurrency, command.BusinessPreset, command.TaxRegistrationNo);
        ProvisionTenantHandler.AddStructure(db, structure);
        await db.SaveChangesAsync(cancellationToken);

        // The creator can work in the new company straight away.
        await identity.GrantCompanyAccessAsync(currentUser.UserId!.Value, structure.Company.Id, cancellationToken);
        await configuration.InitializeCompanyAsync(structure.Company.Id, command.BusinessPreset, cancellationToken);
        return OrganizationMapping.ToDto(structure.Company);
    }
}

public sealed record ListMyCompaniesQuery : IQuery<IReadOnlyList<CompanyDto>>;

public sealed class ListMyCompaniesHandler(IOrganizationDbContext db, ICurrentUser currentUser, IUserAccessService access)
    : IQueryHandler<ListMyCompaniesQuery, IReadOnlyList<CompanyDto>>
{
    public async Task<Result<IReadOnlyList<CompanyDto>>> Handle(ListMyCompaniesQuery query, CancellationToken cancellationToken)
    {
        var ids = await access.GetAccessibleCompanyIdsAsync(currentUser.UserId!.Value, cancellationToken);
        var companies = await db.Companies.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Code)
            .Select(c => new CompanyDto(c.Id, c.Code, c.NameEn, c.NameAr, c.BaseCurrency, c.BusinessPreset))
            .ToListAsync(cancellationToken);
        return companies;
    }
}

public sealed record GetCompanyQuery(Guid CompanyId) : IQuery<CompanyDto>;

public sealed class GetCompanyHandler(IOrganizationDbContext db, ICurrentUser currentUser, IUserAccessService access)
    : IQueryHandler<GetCompanyQuery, CompanyDto>
{
    public async Task<Result<CompanyDto>> Handle(GetCompanyQuery query, CancellationToken cancellationToken)
    {
        // A company the user cannot access is reported as not found (no existence disclosure).
        if (await access.GetCompanyAccessAsync(currentUser.UserId!.Value, query.CompanyId, cancellationToken) is null)
        {
            return OrganizationErrors.CompanyNotFound;
        }

        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == query.CompanyId, cancellationToken);
        return company is null ? OrganizationErrors.CompanyNotFound : OrganizationMapping.ToDto(company);
    }
}

// ---------- Company structure (scoped to the active company) ----------
public sealed record CreateLegalEntityCommand(string Name, string FunctionalCurrency, string? TaxRegistrationNo) : ICommand<LegalEntityDto>;

public sealed class CreateLegalEntityValidator : AbstractValidator<CreateLegalEntityCommand>
{
    public CreateLegalEntityValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.FunctionalCurrency).NotEmpty().Length(3);
        RuleFor(x => x.TaxRegistrationNo).MaximumLength(50);
    }
}

public sealed class CreateLegalEntityHandler(IOrganizationDbContext db, ITenantContext tenant) : ICommandHandler<CreateLegalEntityCommand, LegalEntityDto>
{
    public async Task<Result<LegalEntityDto>> Handle(CreateLegalEntityCommand command, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity.Create(tenant.CompanyId!.Value, command.Name, command.FunctionalCurrency, command.TaxRegistrationNo);
        db.LegalEntities.Add(legalEntity);
        var transitCode = "TRANSIT-" + (await db.Warehouses.CountAsync(w => w.Type == WarehouseType.Transit, cancellationToken) + 1);
        db.Warehouses.Add(Warehouse.CreateTransit(legalEntity, transitCode));
        await db.SaveChangesAsync(cancellationToken);
        return OrganizationMapping.ToDto(legalEntity);
    }
}

public sealed record ListLegalEntitiesQuery : IQuery<IReadOnlyList<LegalEntityDto>>;

public sealed class ListLegalEntitiesHandler(IOrganizationDbContext db) : IQueryHandler<ListLegalEntitiesQuery, IReadOnlyList<LegalEntityDto>>
{
    public async Task<Result<IReadOnlyList<LegalEntityDto>>> Handle(ListLegalEntitiesQuery query, CancellationToken cancellationToken) =>
        await db.LegalEntities.AsNoTracking().OrderBy(l => l.Name)
            .Select(l => new LegalEntityDto(l.Id, l.CompanyId, l.Name, l.FunctionalCurrency, l.TaxRegistrationNo))
            .ToListAsync(cancellationToken);
}

public sealed record CreateBranchCommand(Guid LegalEntityId, string Code, string Name) : ICommand<BranchDto>;

public sealed class CreateBranchValidator : AbstractValidator<CreateBranchCommand>
{
    public CreateBranchValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateBranchHandler(IOrganizationDbContext db, ITenantContext tenant) : ICommandHandler<CreateBranchCommand, BranchDto>
{
    public async Task<Result<BranchDto>> Handle(CreateBranchCommand command, CancellationToken cancellationToken)
    {
        // Company query filter guarantees the legal entity belongs to the active company.
        if (!await db.LegalEntities.AnyAsync(l => l.Id == command.LegalEntityId, cancellationToken))
        {
            return OrganizationErrors.LegalEntityNotFound;
        }

        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Branches.AnyAsync(b => b.Code == code, cancellationToken))
        {
            return OrganizationErrors.BranchCodeTaken;
        }

        var branch = Branch.Create(tenant.CompanyId!.Value, command.LegalEntityId, code, command.Name);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        return OrganizationMapping.ToDto(branch);
    }
}

public sealed record ListBranchesQuery : IQuery<IReadOnlyList<BranchDto>>;

public sealed class ListBranchesHandler(IOrganizationDbContext db) : IQueryHandler<ListBranchesQuery, IReadOnlyList<BranchDto>>
{
    public async Task<Result<IReadOnlyList<BranchDto>>> Handle(ListBranchesQuery query, CancellationToken cancellationToken) =>
        await db.Branches.AsNoTracking().OrderBy(b => b.Code)
            .Select(b => new BranchDto(b.Id, b.LegalEntityId, b.Code, b.Name, b.IsActive))
            .ToListAsync(cancellationToken);
}

public sealed record CreateWarehouseCommand(Guid BranchId, string Code, string Name, string Type) : ICommand<WarehouseDto>;

public sealed class CreateWarehouseValidator : AbstractValidator<CreateWarehouseCommand>
{
    public CreateWarehouseValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Type).Must(t => Enum.TryParse<WarehouseType>(t, true, out var parsed) && parsed != WarehouseType.Transit)
            .WithMessage("Type must be Physical, Production or Virtual.");
    }
}

public sealed class CreateWarehouseHandler(IOrganizationDbContext db) : ICommandHandler<CreateWarehouseCommand, WarehouseDto>
{
    public async Task<Result<WarehouseDto>> Handle(CreateWarehouseCommand command, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == command.BranchId, cancellationToken);
        if (branch is null)
        {
            return OrganizationErrors.BranchNotFound;
        }

        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Warehouses.AnyAsync(w => w.Code == code, cancellationToken))
        {
            return OrganizationErrors.WarehouseCodeTaken;
        }

        var warehouse = Warehouse.Create(branch, code, command.Name, Enum.Parse<WarehouseType>(command.Type, true));
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync(cancellationToken);
        return OrganizationMapping.ToDto(warehouse);
    }
}

public sealed record ListWarehousesQuery(PageRequest Page) : IQuery<PagedResult<WarehouseDto>>;

public sealed class ListWarehousesHandler(IOrganizationDbContext db) : IQueryHandler<ListWarehousesQuery, PagedResult<WarehouseDto>>
{
    public async Task<Result<PagedResult<WarehouseDto>>> Handle(ListWarehousesQuery query, CancellationToken cancellationToken)
    {
        var source = db.Warehouses.AsNoTracking();
        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(w => w.Code).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(w => new WarehouseDto(w.Id, w.LegalEntityId, w.BranchId, w.Code, w.Name, w.Type.ToString(), w.IsActive))
            .ToListAsync(cancellationToken);
        return new PagedResult<WarehouseDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ---------- Contract implementation ----------
public sealed class OrganizationQueries(IOrganizationDbContext db) : IOrganizationQueries
{
    public async Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken = default) =>
        await db.Companies.AsNoTracking().Where(c => companyIds.Contains(c.Id)).OrderBy(c => c.Code)
            .Select(c => new CompanySummary(c.Id, c.Code, c.NameEn, c.NameAr, c.BaseCurrency))
            .ToListAsync(cancellationToken);
}

internal static class OrganizationMapping
{
    public static CompanyDto ToDto(Company c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.BaseCurrency, c.BusinessPreset);

    public static LegalEntityDto ToDto(LegalEntity l) => new(l.Id, l.CompanyId, l.Name, l.FunctionalCurrency, l.TaxRegistrationNo);

    public static BranchDto ToDto(Branch b) => new(b.Id, b.LegalEntityId, b.Code, b.Name, b.IsActive);

    public static WarehouseDto ToDto(Warehouse w) => new(w.Id, w.LegalEntityId, w.BranchId, w.Code, w.Name, w.Type.ToString(), w.IsActive);
}
