using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Manufacturing.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing.Application;

// ---------- DTOs ----------
public sealed record ManufacturingSettingsDto(string DefaultCostingMethod);

public sealed record StandardCostItemDto(Guid ProductId, decimal Material, decimal Labor, decimal Machine, decimal Overhead, decimal Conversion, decimal UnitCost,
    string Source, string? Note);

public sealed record CostVersionDto(Guid Id, string Code, string Name, DateOnly EffectiveFrom, string Status, Guid? ApprovedBy, DateOnly? ActivatedOn,
    IReadOnlyList<StandardCostItemDto> Items);

public sealed record RevaluationDto(Guid ProductId, decimal OldUnitCost, decimal NewUnitCost, decimal Change, Guid? JournalId);

public sealed record CostVersionActivationDto(CostVersionDto Version, IReadOnlyList<RevaluationDto> Revaluations);

public sealed record CostPoolDto(Guid Id, string Code, string Name, string Kind, IReadOnlyList<Guid> WorkCenterIds, IReadOnlyList<Guid> ExpenseAccountIds, bool IsActive);

public sealed record AbsorptionAccountDto(Guid AccountId, decimal Amount);

public sealed record AbsorptionLineDto(Guid PoolId, string PoolCode, string Kind, decimal Applied, decimal Actual, decimal Variance, IReadOnlyList<AbsorptionAccountDto> Accounts);

public sealed record AbsorptionRunDto(Guid? Id, Guid LegalEntityId, DateOnly PeriodFrom, DateOnly PeriodTo, bool Posted, Guid? JournalId, decimal TotalVariance,
    IReadOnlyList<AbsorptionLineDto> Lines);

internal static class CostingMapping
{
    public static StandardCostItemDto ToDto(StandardCostItem i) =>
        new(i.ProductId, i.Material, i.Labor, i.Machine, i.Overhead, i.Conversion, i.UnitCost, i.Source.ToString(), i.Note);

    public static CostVersionDto ToDto(CostVersion v) => new(v.Id, v.Code, v.Name, v.EffectiveFrom, v.Status.ToString(), v.ApprovedBy, v.ActivatedOn,
        v.Items.OrderBy(i => i.ProductId).Select(ToDto).ToList());

    public static CostPoolDto ToDto(CostPool p) => new(p.Id, p.Code, p.Name, p.Kind.ToString(), p.WorkCenterIds, p.ExpenseAccountIds, p.IsActive);

    public static AbsorptionRunDto ToDto(AbsorptionRun r, bool posted) => new(posted ? r.Id : null, r.LegalEntityId, r.PeriodFrom, r.PeriodTo, posted, r.JournalId,
        r.TotalVariance, r.Lines.Select(l => new AbsorptionLineDto(l.PoolId, l.PoolCode, l.Kind.ToString(), l.Applied, l.Actual, l.Variance,
            l.Accounts.Select(a => new AbsorptionAccountDto(a.AccountId, a.Amount)).ToList())).ToList());
}

public static class CostingDocTypes
{
    public const string CostVersion = "manufacturing.cost-version";
    public const string Absorption = "manufacturing.absorption";
}

/// <summary>Resolves which costing method applies to a product: the item override, else the company default.</summary>
public sealed class CostingPolicy(IManufacturingDbContext db)
{
    public async Task<ManufacturingCostingMethod> DefaultAsync(CancellationToken ct) =>
        (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.DefaultCostingMethod ?? ManufacturingCostingMethod.Actual;

    public async Task<ManufacturingCostingMethod> MethodForAsync(Guid productId, CancellationToken ct)
    {
        var item = await db.PlanningParameters.AsNoTracking().Where(p => p.ProductId == productId).Select(p => p.CostingMethod).FirstOrDefaultAsync(ct);
        return item ?? await DefaultAsync(ct);
    }

    public async Task<CostVersion?> ActiveVersionAsync(CancellationToken ct) =>
        await db.CostVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Status == CostVersionStatus.Active, ct);
}

// ---------- Settings ----------
public sealed record GetManufacturingSettingsQuery : IQuery<ManufacturingSettingsDto>;

public sealed class GetManufacturingSettingsHandler(CostingPolicy policy) : IQueryHandler<GetManufacturingSettingsQuery, ManufacturingSettingsDto>
{
    public async Task<Result<ManufacturingSettingsDto>> Handle(GetManufacturingSettingsQuery query, CancellationToken cancellationToken) =>
        new ManufacturingSettingsDto((await policy.DefaultAsync(cancellationToken)).ToString());
}

public sealed record SaveManufacturingSettingsCommand(string DefaultCostingMethod) : ICommand<ManufacturingSettingsDto>;

public sealed class SaveManufacturingSettingsValidator : AbstractValidator<SaveManufacturingSettingsCommand>
{
    public SaveManufacturingSettingsValidator() =>
        RuleFor(x => x.DefaultCostingMethod).Must(m => Enum.TryParse<ManufacturingCostingMethod>(m, true, out _)).WithMessage("DefaultCostingMethod must be Actual or Standard.");
}

public sealed class SaveManufacturingSettingsHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<SaveManufacturingSettingsCommand, ManufacturingSettingsDto>
{
    public async Task<Result<ManufacturingSettingsDto>> Handle(SaveManufacturingSettingsCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = ManufacturingSettings.Create(support.CompanyId);
            db.Settings.Add(settings);
        }

        settings.Update(Enum.Parse<ManufacturingCostingMethod>(command.DefaultCostingMethod, true));
        await db.SaveChangesAsync(cancellationToken);
        return new ManufacturingSettingsDto(settings.DefaultCostingMethod.ToString());
    }
}

// ---------- Cost versions ----------
public sealed record CreateCostVersionCommand(string Code, string Name, DateOnly EffectiveFrom) : ICommand<CostVersionDto>;

public sealed class CreateCostVersionValidator : AbstractValidator<CreateCostVersionCommand>
{
    public CreateCostVersionValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateCostVersionHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<CreateCostVersionCommand, CostVersionDto>
{
    public async Task<Result<CostVersionDto>> Handle(CreateCostVersionCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.CostVersions.AnyAsync(v => v.Code == code, cancellationToken))
        {
            return ManufacturingErrors.CodeTaken;
        }

        var version = CostVersion.Create(support.CompanyId, code, command.Name, command.EffectiveFrom);
        db.CostVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return CostingMapping.ToDto(version);
    }
}

public sealed record StandardCostInput(Guid ProductId, decimal Material, decimal Labor, decimal Machine, decimal Overhead, string? Note);

public sealed record SetStandardCostsCommand(Guid VersionId, IReadOnlyList<StandardCostInput> Items) : ICommand<CostVersionDto>;

public sealed class SetStandardCostsValidator : AbstractValidator<SetStandardCostsCommand>
{
    public SetStandardCostsValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleFor(x => x.Items).Must(i => i.Select(x => x.ProductId).Distinct().Count() == i.Count).WithMessage("Each product may appear only once.");
    }
}

/// <summary>Enters manual standards (typically for purchased materials); a roll-up keeps them unless told to overwrite.</summary>
public sealed class SetStandardCostsHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<SetStandardCostsCommand, CostVersionDto>
{
    public async Task<Result<CostVersionDto>> Handle(SetStandardCostsCommand command, CancellationToken cancellationToken)
    {
        var version = await db.CostVersions.FirstOrDefaultAsync(v => v.Id == command.VersionId, cancellationToken);
        if (version is null)
        {
            return MfgErrors.CostVersionNotFound;
        }

        var products = await support.StockableProductsAsync(command.Items.Select(i => i.ProductId), cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        version.SetItems(command.Items.Select(i => new StandardCostItem
        {
            ProductId = i.ProductId,
            Material = i.Material,
            Labor = i.Labor,
            Machine = i.Machine,
            Overhead = i.Overhead,
            Source = StandardCostSource.Manual,
            Note = i.Note,
        }));
        await db.SaveChangesAsync(cancellationToken);
        return CostingMapping.ToDto(version);
    }
}

public sealed record RollUpCostVersionCommand(Guid VersionId, Guid? CostWarehouseId, bool OverwriteManual, IReadOnlyList<Guid>? ProductIds) : ICommand<CostVersionDto>;

/// <summary>
/// Rolls up standard costs bottom-up: a manufactured product's material = Σ BOM component quantity (base units, scrap and yield
/// included, for a costing lot = the item's lot size or the BOM output quantity) × component standard, ÷ lot; its labour,
/// machine and overhead = routing hours for the lot (setup + run) × work-center rates, ÷ lot. Purchased components without a
/// manual standard take the current average (else last) inventory cost in <c>CostWarehouseId</c>. Variant-conditional BOM lines
/// are not included (standards are per product, not per variant).
/// </summary>
public sealed class RollUpCostVersionHandler(IManufacturingDbContext db, ManufacturingSupport support, IStockQueries stock)
    : ICommandHandler<RollUpCostVersionCommand, CostVersionDto>
{
    public async Task<Result<CostVersionDto>> Handle(RollUpCostVersionCommand command, CancellationToken cancellationToken)
    {
        var version = await db.CostVersions.FirstOrDefaultAsync(v => v.Id == command.VersionId, cancellationToken);
        if (version is null)
        {
            return MfgErrors.CostVersionNotFound;
        }

        if (version.Status != CostVersionStatus.Draft)
        {
            return ManufacturingErrors.InvalidTransition;
        }

        var boms = (await db.BomVersions.AsNoTracking().Where(v => v.Status == BomVersionStatus.Active || v.Status == BomVersionStatus.Superseded).ToListAsync(cancellationToken))
            .GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var routings = (await db.Routings.AsNoTracking().Where(r => r.IsActive).ToListAsync(cancellationToken))
            .GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Code, StringComparer.Ordinal).First());
        var workCenters = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(w => w.Id, cancellationToken);
        var lotSizes = await db.PlanningParameters.AsNoTracking().Where(p => p.LotSize > 0).ToDictionaryAsync(p => p.ProductId, p => p.LotSize, cancellationToken);

        var targets = command.ProductIds is { Count: > 0 } ids
            ? ids.Distinct().ToList()
            : boms.Where(kv => BomCalculator.Effective(kv.Value, version.EffectiveFrom) is not null).Select(kv => kv.Key).ToList();
        var allProducts = targets.Concat(boms.Values.SelectMany(v => v).SelectMany(v => v.Lines.Select(l => l.ComponentProductId))).Distinct().ToList();
        var products = await support.ProductsAsync(allProducts, cancellationToken);

        var computed = new Dictionary<Guid, StandardCostItem>();
        var visiting = new HashSet<Guid>();

        async Task<StandardCostItem> CostAsync(Guid productId)
        {
            if (computed.TryGetValue(productId, out var done))
            {
                return done;
            }

            var existing = version.Item(productId);
            if (existing is { Source: StandardCostSource.Manual } && !command.OverwriteManual)
            {
                computed[productId] = existing;
                return existing;
            }

            if (!visiting.Add(productId))
            {
                throw new DomainException(ManufacturingErrors.BomCycle);
            }

            StandardCostItem item;
            var bom = BomCalculator.Effective(boms.GetValueOrDefault(productId) ?? [], version.EffectiveFrom);
            if (bom is null)
            {
                decimal unit = 0;
                string? note = null;
                if (command.CostWarehouseId is { } warehouse && await stock.GetItemCostAsync(productId, null, warehouse, cancellationToken) is { } cost)
                {
                    unit = cost.AverageCost > 0 ? cost.AverageCost : cost.LastCost;
                }

                if (unit == 0)
                {
                    note = "No standard entered and no current cost found.";
                }

                item = new StandardCostItem { ProductId = productId, Material = MfgRounding.Cost(unit), Source = StandardCostSource.CurrentCost, Note = note };
            }
            else
            {
                var lot = lotSizes.GetValueOrDefault(productId, bom.OutputQuantity);
                decimal material = 0;
                foreach (var requirement in BomCalculator.Explode(bom, lot))
                {
                    var factor = products.TryGetValue(requirement.ProductId, out var component) ? ManufacturingSupport.Factor(component, requirement.UomId) ?? 1m : 1m;
                    var componentCost = await CostAsync(requirement.ProductId);
                    material += requirement.Quantity * factor * componentCost.UnitCost;
                }

                decimal labor = 0, machine = 0, overhead = 0;
                if (routings.TryGetValue(productId, out var routing))
                {
                    foreach (var operation in routing.Operations)
                    {
                        if (workCenters.TryGetValue(operation.WorkCenterId, out var wc))
                        {
                            var (l, m, o) = wc.StandardCost(operation.PlannedHours(lot));
                            labor += l;
                            machine += m;
                            overhead += o;
                        }
                    }
                }

                item = new StandardCostItem
                {
                    ProductId = productId,
                    Material = MfgRounding.Cost(material / lot),
                    Labor = MfgRounding.Cost(labor / lot),
                    Machine = MfgRounding.Cost(machine / lot),
                    Overhead = MfgRounding.Cost(overhead / lot),
                    Source = StandardCostSource.RollUp,
                    Note = $"Lot {lot:0.######}" + (routing is null ? ", no routing" : string.Empty),
                };
            }

            visiting.Remove(productId);
            computed[productId] = item;
            return item;
        }

        foreach (var productId in targets)
        {
            await CostAsync(productId);
        }

        version.SetItems(computed.Values.Where(i => i.Source != StandardCostSource.Manual || command.OverwriteManual).ToList());
        await db.SaveChangesAsync(cancellationToken);
        return CostingMapping.ToDto(version);
    }
}

public enum CostVersionAction
{
    Approve = 1,
    Reopen = 2,
}

public sealed record CostVersionActionCommand(Guid VersionId, CostVersionAction Action) : ICommand<CostVersionDto>;

public sealed class CostVersionActionHandler(IManufacturingDbContext db, ICurrentUser currentUser) : ICommandHandler<CostVersionActionCommand, CostVersionDto>
{
    public async Task<Result<CostVersionDto>> Handle(CostVersionActionCommand command, CancellationToken cancellationToken)
    {
        var version = await db.CostVersions.FirstOrDefaultAsync(v => v.Id == command.VersionId, cancellationToken);
        if (version is null)
        {
            return MfgErrors.CostVersionNotFound;
        }

        if (command.Action == CostVersionAction.Approve)
        {
            version.Approve(currentUser.UserId);
        }
        else
        {
            version.Reopen();
        }

        await db.SaveChangesAsync(cancellationToken);
        return CostingMapping.ToDto(version);
    }
}

public sealed record ActivateCostVersionCommand(Guid VersionId, Guid LegalEntityId) : ICommand<CostVersionActivationDto>;

/// <summary>
/// Activates an approved cost version (superseding the active one) and revalues the stock on hand of every standard-costed
/// product in the legal entity to its new standard: Dr Inventory / Cr InventoryAdjustmentGain, or Dr InventoryAdjustmentLoss /
/// Cr Inventory. Open production orders keep the standards they were planned with.
/// </summary>
public sealed class ActivateCostVersionHandler(
    IManufacturingDbContext db,
    ManufacturingSupport support,
    CostingPolicy policy,
    IInventoryRevaluationService revaluation) : ICommandHandler<ActivateCostVersionCommand, CostVersionActivationDto>
{
    public async Task<Result<CostVersionActivationDto>> Handle(ActivateCostVersionCommand command, CancellationToken cancellationToken)
    {
        var versions = await db.CostVersions.Where(v => v.Id == command.VersionId || v.Status == CostVersionStatus.Active).ToListAsync(cancellationToken);
        var version = versions.FirstOrDefault(v => v.Id == command.VersionId);
        if (version is null)
        {
            return MfgErrors.CostVersionNotFound;
        }

        var legalEntityCurrency = await support.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (legalEntityCurrency.IsFailure)
        {
            return legalEntityCurrency.Error;
        }

        var previous = versions.FirstOrDefault(v => v.Id != version.Id && v.Status == CostVersionStatus.Active);
        var today = support.Today;
        version.Activate(previous, today);

        var defaultMethod = await policy.DefaultAsync(cancellationToken);
        var overrides = await db.PlanningParameters.AsNoTracking().Where(p => p.CostingMethod != null)
            .ToDictionaryAsync(p => p.ProductId, p => p.CostingMethod!.Value, cancellationToken);
        var revaluations = new List<RevaluationDto>();
        foreach (var item in version.Items.OrderBy(i => i.ProductId))
        {
            if (overrides.GetValueOrDefault(item.ProductId, defaultMethod) != ManufacturingCostingMethod.Standard)
            {
                continue;
            }

            var result = await revaluation.RevalueAsync(new InventoryRevaluationRequest(command.LegalEntityId, item.ProductId, null, item.UnitCost, today,
                ManufacturingSupport.SourceModule, CostingDocTypes.CostVersion, version.Id, PostingRoles.InventoryAdjustmentGain, PostingRoles.InventoryAdjustmentLoss,
                Description: $"Standard cost {version.Code}"), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error;
            }

            var quantity = result.Value.Areas.Sum(a => a.Quantity);
            if (quantity > 0 || result.Value.TotalChange != 0)
            {
                var before = result.Value.Areas.Sum(a => a.ValueBefore);
                revaluations.Add(new RevaluationDto(item.ProductId, quantity > 0 ? MfgRounding.Cost(before / quantity) : 0, item.UnitCost, result.Value.TotalChange,
                    result.Value.JournalId));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CostVersionActivationDto(CostingMapping.ToDto(version), revaluations);
    }
}

public sealed record ListCostVersionsQuery : IQuery<IReadOnlyList<CostVersionDto>>;

public sealed class ListCostVersionsHandler(IManufacturingDbContext db) : IQueryHandler<ListCostVersionsQuery, IReadOnlyList<CostVersionDto>>
{
    public async Task<Result<IReadOnlyList<CostVersionDto>>> Handle(ListCostVersionsQuery query, CancellationToken cancellationToken) =>
        (await db.CostVersions.AsNoTracking().OrderByDescending(v => v.EffectiveFrom).ThenBy(v => v.Code).ToListAsync(cancellationToken)).Select(CostingMapping.ToDto).ToList();
}

public sealed record GetCostVersionQuery(Guid VersionId) : IQuery<CostVersionDto>;

public sealed class GetCostVersionHandler(IManufacturingDbContext db) : IQueryHandler<GetCostVersionQuery, CostVersionDto>
{
    public async Task<Result<CostVersionDto>> Handle(GetCostVersionQuery query, CancellationToken cancellationToken)
    {
        var version = await db.CostVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == query.VersionId, cancellationToken);
        return version is null ? MfgErrors.CostVersionNotFound : CostingMapping.ToDto(version);
    }
}

// ---------- Cost pools ----------
public sealed record SaveCostPoolCommand(Guid? Id, string Code, string Name, string Kind, IReadOnlyList<Guid> WorkCenterIds, IReadOnlyList<Guid> ExpenseAccountIds) : ICommand<CostPoolDto>;

public sealed class SaveCostPoolValidator : AbstractValidator<SaveCostPoolCommand>
{
    public SaveCostPoolValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).Must(k => Enum.TryParse<CostPoolKind>(k, true, out _)).WithMessage("Kind must be Labor, Machine or Overhead.");
        RuleFor(x => x.WorkCenterIds).NotEmpty();
        RuleFor(x => x.ExpenseAccountIds).NotEmpty();
    }
}

public sealed class SaveCostPoolHandler(IManufacturingDbContext db, ManufacturingSupport support, IAccountQueries accounts) : ICommandHandler<SaveCostPoolCommand, CostPoolDto>
{
    public async Task<Result<CostPoolDto>> Handle(SaveCostPoolCommand command, CancellationToken cancellationToken)
    {
        var workCenterIds = command.WorkCenterIds.Distinct().ToList();
        if (await db.WorkCenters.CountAsync(w => workCenterIds.Contains(w.Id), cancellationToken) != workCenterIds.Count)
        {
            return MfgErrors.WorkCenterNotFound;
        }

        foreach (var accountId in command.ExpenseAccountIds.Distinct())
        {
            var account = await accounts.GetAccountAsync(accountId, cancellationToken);
            if (account is not { IsActive: true, IsPostingAccount: true, IsControl: false } || account.Type != "Expense")
            {
                return Error.Validation("manufacturing.cost-pool-account-invalid", "Expense accounts of a cost pool must be active, non-control expense posting accounts.");
            }
        }

        var others = await db.CostPools.AsNoTracking().Where(p => p.Id != command.Id && p.IsActive).ToListAsync(cancellationToken);
        if (others.Any(p => p.ExpenseAccountIds.Intersect(command.ExpenseAccountIds).Any()))
        {
            return Error.Conflict("manufacturing.cost-pool-account-taken", "An expense account can belong to only one cost pool.");
        }

        var kind = Enum.Parse<CostPoolKind>(command.Kind, true);
        CostPool pool;
        if (command.Id is { } id)
        {
            pool = await db.CostPools.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new DomainException(MfgErrors.CostPoolNotFound);
            pool.Update(command.Name, kind, workCenterIds, command.ExpenseAccountIds);
        }
        else
        {
            var code = command.Code.Trim().ToUpperInvariant();
            if (await db.CostPools.AnyAsync(p => p.Code == code, cancellationToken))
            {
                return ManufacturingErrors.CodeTaken;
            }

            pool = CostPool.Create(support.CompanyId, code, command.Name, kind, workCenterIds, command.ExpenseAccountIds);
            db.CostPools.Add(pool);
        }

        await db.SaveChangesAsync(cancellationToken);
        return CostingMapping.ToDto(pool);
    }
}

public sealed record ListCostPoolsQuery : IQuery<IReadOnlyList<CostPoolDto>>;

public sealed class ListCostPoolsHandler(IManufacturingDbContext db) : IQueryHandler<ListCostPoolsQuery, IReadOnlyList<CostPoolDto>>
{
    public async Task<Result<IReadOnlyList<CostPoolDto>>> Handle(ListCostPoolsQuery query, CancellationToken cancellationToken) =>
        (await db.CostPools.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken)).Select(CostingMapping.ToDto).ToList();
}

// ---------- Absorption clearing ----------

/// <summary>Computes applied versus actual per cost pool for a legal entity and period.</summary>
public sealed class AbsorptionCalculator(IManufacturingDbContext db, IAccountLedgerQueries ledger)
{
    public async Task<IReadOnlyList<AbsorptionLine>> ComputeAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var pools = await db.CostPools.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
        var orders = await db.ProductionOrders.AsNoTracking()
            .Where(o => o.LegalEntityId == legalEntityId && o.Status != ProductionOrderStatus.Draft && o.Status != ProductionOrderStatus.Cancelled && o.StartDate <= to)
            .ToListAsync(ct);
        var reports = orders.SelectMany(o => o.Transactions)
            .Where(t => t.Kind == ProductionTransactionKind.OperationReport && t.Date >= from && t.Date <= to && t.WorkCenterId is not null)
            .ToList();

        var lines = new List<AbsorptionLine>();
        foreach (var pool in pools)
        {
            var applied = reports.Where(t => pool.WorkCenterIds.Contains(t.WorkCenterId!.Value)).Sum(t => pool.Kind switch
            {
                CostPoolKind.Labor => t.LaborAmount,
                CostPoolKind.Machine => t.MachineAmount,
                _ => t.OverheadAmount,
            });
            var accounts = new List<AbsorptionAccountAmount>();
            foreach (var accountId in pool.ExpenseAccountIds)
            {
                var movements = await ledger.GetAccountLinesAsync(legalEntityId, accountId, to, null, ct);
                var amount = movements.Where(m => m.Date >= from && m.SourceDocType != CostingDocTypes.Absorption).Sum(m => m.BaseDebit - m.BaseCredit);
                accounts.Add(new AbsorptionAccountAmount { AccountId = accountId, Amount = MfgRounding.Money(amount) });
            }

            lines.Add(new AbsorptionLine
            {
                PoolId = pool.Id,
                PoolCode = pool.Code,
                Kind = pool.Kind,
                Applied = MfgRounding.Money(applied),
                Actual = accounts.Sum(a => a.Amount),
                Accounts = accounts,
            });
        }

        return lines;
    }

    public static string AppliedRole(CostPoolKind kind) => kind switch
    {
        CostPoolKind.Labor => PostingRoles.AppliedLabor,
        CostPoolKind.Machine => PostingRoles.AppliedMachine,
        _ => PostingRoles.AppliedOverhead,
    };
}

public sealed record PreviewAbsorptionQuery(Guid LegalEntityId, DateOnly From, DateOnly To) : IQuery<AbsorptionRunDto>;

public sealed class PreviewAbsorptionHandler(AbsorptionCalculator calculator, ManufacturingSupport support) : IQueryHandler<PreviewAbsorptionQuery, AbsorptionRunDto>
{
    public async Task<Result<AbsorptionRunDto>> Handle(PreviewAbsorptionQuery query, CancellationToken cancellationToken)
    {
        var le = await support.LegalEntityAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var lines = await calculator.ComputeAsync(query.LegalEntityId, query.From, query.To, cancellationToken);
        return CostingMapping.ToDto(AbsorptionRun.Create(support.CompanyId, query.LegalEntityId, query.From, query.To, lines), posted: false);
    }
}

public sealed record PostAbsorptionCommand(Guid LegalEntityId, DateOnly From, DateOnly To) : ICommand<AbsorptionRunDto>;

public sealed class PostAbsorptionValidator : AbstractValidator<PostAbsorptionCommand>
{
    public PostAbsorptionValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x).Must(x => x.To >= x.From).WithName("to").WithMessage("The period end cannot be before its start.");
    }
}

/// <summary>
/// Period-end clearing per cost pool: Dr the applied account (amount absorbed), Cr each expense account (its actual net debits),
/// and the difference to AbsorptionVariance (Dr = under-absorbed, Cr = over-absorbed). One posted run per legal entity and
/// period; overlapping periods are refused.
/// </summary>
public sealed class PostAbsorptionHandler(IManufacturingDbContext db, AbsorptionCalculator calculator, ManufacturingSupport support, IJournalPostingService journals)
    : ICommandHandler<PostAbsorptionCommand, AbsorptionRunDto>
{
    public async Task<Result<AbsorptionRunDto>> Handle(PostAbsorptionCommand command, CancellationToken cancellationToken)
    {
        var le = await support.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (await db.AbsorptionRuns.AnyAsync(r => r.LegalEntityId == command.LegalEntityId && r.PeriodFrom <= command.To && r.PeriodTo >= command.From, cancellationToken))
        {
            return Error.Conflict("manufacturing.absorption-already-posted", "Absorption has already been cleared for an overlapping period.");
        }

        var lines = await calculator.ComputeAsync(command.LegalEntityId, command.From, command.To, cancellationToken);
        var run = AbsorptionRun.Create(support.CompanyId, command.LegalEntityId, command.From, command.To, lines);
        db.AbsorptionRuns.Add(run);

        var journal = new List<JournalLineRequest>();
        foreach (var line in lines)
        {
            if (line.Applied != 0)
            {
                Add(journal, AbsorptionCalculator.AppliedRole(line.Kind), null, line.Applied, $"{line.PoolCode} applied");
            }

            foreach (var account in line.Accounts.Where(a => a.Amount != 0))
            {
                Add(journal, null, account.AccountId, -account.Amount, $"{line.PoolCode} actual");
            }

            if (line.Variance != 0)
            {
                Add(journal, PostingRoles.AbsorptionVariance, null, line.Variance, $"{line.PoolCode} {(line.Variance > 0 ? "under" : "over")}-absorbed");
            }
        }

        if (journal.Count > 0)
        {
            var posted = await journals.PostAsync(new JournalRequest(command.LegalEntityId, command.To, ManufacturingSupport.SourceModule, CostingDocTypes.Absorption, run.Id,
                "absorption", $"Absorption clearing {command.From:yyyy-MM-dd}..{command.To:yyyy-MM-dd}", le.Value.Currency, 1m, journal), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            run.Posted(posted.Value.JournalId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return CostingMapping.ToDto(run, posted: true);
    }

    /// <summary>Positive amount = debit, negative = credit.</summary>
    private static void Add(List<JournalLineRequest> lines, string? role, Guid? accountId, decimal amount, string description) =>
        lines.Add(amount > 0
            ? new JournalLineRequest(role, accountId, amount, 0, Description: description)
            : new JournalLineRequest(role, accountId, 0, -amount, Description: description));
}

public sealed record ListAbsorptionRunsQuery : IQuery<IReadOnlyList<AbsorptionRunDto>>;

public sealed class ListAbsorptionRunsHandler(IManufacturingDbContext db) : IQueryHandler<ListAbsorptionRunsQuery, IReadOnlyList<AbsorptionRunDto>>
{
    public async Task<Result<IReadOnlyList<AbsorptionRunDto>>> Handle(ListAbsorptionRunsQuery query, CancellationToken cancellationToken) =>
        (await db.AbsorptionRuns.AsNoTracking().OrderByDescending(r => r.PeriodTo).Take(100).ToListAsync(cancellationToken))
        .Select(r => CostingMapping.ToDto(r, posted: true)).ToList();
}
