using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Configuration.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Configuration.Application;

public interface IConfigurationDbContext
{
    DbSet<CompanyModule> CompanyModules { get; }

    DbSet<CompanyPolicy> CompanyPolicies { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IModuleActivationCache
{
    void Invalidate(Guid companyId);
}

public static class PolicyJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

// ---------- Provisioning ----------
public sealed class ConfigurationProvisioning(IConfigurationDbContext db, IClock clock) : IConfigurationProvisioning
{
    public async Task InitializeCompanyAsync(Guid companyId, string businessPreset, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var enabled = ModuleCatalog.ModulesForPreset(businessPreset).ToHashSet();
        foreach (var module in ModuleCatalog.All.Where(m => !m.IsCore))
        {
            db.CompanyModules.Add(CompanyModule.Create(companyId, module.Code, enabled.Contains(module.Code), now));
        }

        foreach (var section in PolicySections.Types.Keys)
        {
            var defaults = PolicySections.Default(section);
            db.CompanyPolicies.Add(CompanyPolicy.Create(companyId, section, JsonSerializer.Serialize(defaults, defaults.GetType(), PolicyJson.Options)));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

// ---------- Modules ----------
public sealed record ModuleStateDto(string Code, string NameEn, string NameAr, bool IsCore, bool IsEnabled, IReadOnlyCollection<string> DependsOn);

public sealed record ListModulesQuery : IQuery<IReadOnlyList<ModuleStateDto>>;

public sealed class ListModulesHandler(IConfigurationDbContext db) : IQueryHandler<ListModulesQuery, IReadOnlyList<ModuleStateDto>>
{
    public async Task<Result<IReadOnlyList<ModuleStateDto>>> Handle(ListModulesQuery query, CancellationToken cancellationToken)
    {
        var enabled = (await db.CompanyModules.AsNoTracking().Where(m => m.IsEnabled).Select(m => m.ModuleCode).ToListAsync(cancellationToken)).ToHashSet();
        return ModuleCatalog.All
            .Select(m => new ModuleStateDto(m.Code, m.NameEn, m.NameAr, m.IsCore, ModuleCatalog.IsOn(m.Code, enabled), m.DependsOn))
            .ToList();
    }
}

public sealed record SetModuleEnabledCommand(string ModuleCode, bool Enabled) : ICommand<ModuleStateDto>;

public sealed class SetModuleEnabledHandler(IConfigurationDbContext db, ITenantContext tenant, IClock clock, IModuleActivationCache cache)
    : ICommandHandler<SetModuleEnabledCommand, ModuleStateDto>
{
    public async Task<Result<ModuleStateDto>> Handle(SetModuleEnabledCommand command, CancellationToken cancellationToken)
    {
        var rows = await db.CompanyModules.ToListAsync(cancellationToken);
        var enabled = rows.Where(r => r.IsEnabled).Select(r => r.ModuleCode).ToHashSet();
        var validation = ModuleCatalog.ValidateChange(command.ModuleCode, command.Enabled, enabled);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var companyId = tenant.CompanyId!.Value;
        var row = rows.FirstOrDefault(r => r.ModuleCode == command.ModuleCode);
        if (row is null)
        {
            row = CompanyModule.Create(companyId, command.ModuleCode, command.Enabled, clock.UtcNow);
            db.CompanyModules.Add(row);
        }
        else
        {
            row.Set(command.Enabled, clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate(companyId);
        var definition = ModuleCatalog.Find(command.ModuleCode)!;
        return new ModuleStateDto(definition.Code, definition.NameEn, definition.NameAr, false, command.Enabled, definition.DependsOn);
    }
}

// ---------- Policies ----------
public sealed record PolicyDto(string Section, int Version, JsonElement Values, byte[] RowVersion);

public sealed record GetPolicyQuery(string Section) : IQuery<PolicyDto>;

public sealed class GetPolicyHandler(IConfigurationDbContext db) : IQueryHandler<GetPolicyQuery, PolicyDto>
{
    public async Task<Result<PolicyDto>> Handle(GetPolicyQuery query, CancellationToken cancellationToken)
    {
        if (!PolicySections.Types.ContainsKey(query.Section))
        {
            return ConfigurationErrors.UnknownSection;
        }

        var policy = await db.CompanyPolicies.FirstOrDefaultAsync(p => p.Section == query.Section, cancellationToken);
        if (policy is null)
        {
            return ConfigurationErrors.UnknownSection;
        }

        return ToDto(db, policy);
    }

    internal static PolicyDto ToDto(IConfigurationDbContext db, CompanyPolicy policy) => new(
        policy.Section,
        policy.Version,
        JsonDocument.Parse(policy.Json).RootElement.Clone(),
        (byte[])db.Entry(policy).Property("RowVersion").CurrentValue!);
}

public sealed record UpdatePolicyCommand(string Section, JsonElement Values, byte[] ExpectedRowVersion) : ICommand<PolicyDto>;

public sealed class UpdatePolicyHandler(IConfigurationDbContext db) : ICommandHandler<UpdatePolicyCommand, PolicyDto>
{
    public async Task<Result<PolicyDto>> Handle(UpdatePolicyCommand command, CancellationToken cancellationToken)
    {
        if (!PolicySections.Types.TryGetValue(command.Section, out var type))
        {
            return ConfigurationErrors.UnknownSection;
        }

        object typed;
        try
        {
            typed = command.Values.Deserialize(type, PolicyJson.Options) ?? throw new JsonException("Empty policy.");
        }
        catch (JsonException ex)
        {
            return Error.Validation("configuration.policy-invalid", ex.Message);
        }

        var validation = PolicyValidation.Validate(typed);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var policy = await db.CompanyPolicies.FirstOrDefaultAsync(p => p.Section == command.Section, cancellationToken);
        if (policy is null)
        {
            return ConfigurationErrors.UnknownSection;
        }

        var current = (byte[])db.Entry(policy).Property("RowVersion").CurrentValue!;
        if (!current.AsSpan().SequenceEqual(command.ExpectedRowVersion))
        {
            return new Error("concurrency-conflict", "The policy was changed by someone else. Reload and try again.", ErrorType.PreconditionFailed);
        }

        // Also make the database enforce it, in case of a concurrent update after this read.
        db.Entry(policy).Property("RowVersion").OriginalValue = command.ExpectedRowVersion;
        policy.Update(JsonSerializer.Serialize(typed, type, PolicyJson.Options));
        await db.SaveChangesAsync(cancellationToken);
        return GetPolicyHandler.ToDto(db, policy);
    }
}

public static class PolicyValidation
{
    private static readonly string[] CostingMethods = ["WeightedAverage", "FIFO"];
    private static readonly string[] ValuationAreas = ["LegalEntity", "Warehouse"];
    private static readonly string[] Backdating = ["WithinOpenPeriod", "NotAllowed"];
    private static readonly string[] IssueTimings = ["AtInvoice", "AtDelivery"];
    private static readonly string[] CreditActions = ["Warn", "RequireApproval", "Block"];

    public static Result Validate(object policy)
    {
        var errors = new Dictionary<string, string[]>();
        switch (policy)
        {
            case InventoryPolicy p:
                Check(errors, "defaultCostingMethod", CostingMethods.Contains(p.DefaultCostingMethod), CostingMethods);
                Check(errors, "valuationArea", ValuationAreas.Contains(p.ValuationArea), ValuationAreas);
                Check(errors, "backdatedPostings", Backdating.Contains(p.BackdatedPostings), Backdating);
                if (p.AllowNegativeStock && p.DefaultCostingMethod == "FIFO")
                {
                    errors["allowNegativeStock"] = ["Negative stock cannot be combined with FIFO costing."];
                }

                break;
            case SalesPolicy p:
                Check(errors, "stockIssueTiming", IssueTimings.Contains(p.StockIssueTiming), IssueTimings);
                Check(errors, "creditLimitAction", CreditActions.Contains(p.CreditLimitAction), CreditActions);
                break;
            case ProcurementPolicy p:
                if (p.PriceTolerancePercent is < 0 or > 100)
                {
                    errors["priceTolerancePercent"] = ["Must be between 0 and 100."];
                }

                if (p.QuantityTolerancePercent is < 0 or > 100)
                {
                    errors["quantityTolerancePercent"] = ["Must be between 0 and 100."];
                }

                break;
        }

        return errors.Count == 0
            ? Result.Success()
            : Error.Validation("validation-failed", "The policy is invalid.") with { Details = errors };
    }

    private static void Check(Dictionary<string, string[]> errors, string field, bool ok, string[] allowed)
    {
        if (!ok)
        {
            errors[field] = [$"Must be one of: {string.Join(", ", allowed)}."];
        }
    }
}

public static class ConfigurationErrors
{
    public static readonly Error UnknownSection = Error.NotFound("configuration.unknown-policy-section", "Unknown policy section.");
}

public sealed class UpdatePolicyValidator : AbstractValidator<UpdatePolicyCommand>
{
    public UpdatePolicyValidator() => RuleFor(x => x.Section).NotEmpty();
}
