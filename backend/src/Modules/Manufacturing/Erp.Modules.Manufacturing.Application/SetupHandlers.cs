using Erp.Application.Messaging;
using Erp.Modules.Manufacturing.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing.Application;

// ---------- DTOs ----------
public sealed record WorkCenterDto(Guid Id, string Code, string NameEn, string NameAr, decimal CapacityHoursPerDay, decimal LaborRatePerHour,
    decimal MachineRatePerHour, decimal OverheadRatePerHour, string OverheadBasis, bool IsActive, IReadOnlyList<string> WorkingDays);

public sealed record RoutingOperationDto(int Sequence, string Name, Guid WorkCenterId, decimal SetupHours, decimal RunHoursPerUnit);

public sealed record RoutingDto(Guid Id, Guid ProductId, string Code, string Name, bool IsActive, IReadOnlyList<RoutingOperationDto> Operations);

public sealed record BomLineDto(Guid LineId, Guid ComponentProductId, decimal QuantityPerOutput, Guid UomId, decimal ScrapPercent, string IssueMethod, IReadOnlyList<Guid> Alternatives,
    Guid? ComponentVariantId, IReadOnlyDictionary<string, string>? AppliesTo);

public sealed record BomVersionDto(Guid Id, Guid BomId, Guid ProductId, int VersionNo, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status,
    decimal OutputQuantity, decimal ExpectedYieldPercent, IReadOnlyList<BomLineDto> Lines);

public sealed record BomDto(Guid Id, Guid ProductId, string Code, string Name, IReadOnlyList<BomVersionDto> Versions);

public sealed record PlanningParametersDto(Guid Id, Guid ProductId, decimal SafetyStock, int LeadTimeDays, string LotSizeRule, decimal LotSize, string? Procurement,
    string? CostingMethod, Guid? PreferredSupplierId);

internal static class SetupMapping
{
    public static WorkCenterDto ToDto(WorkCenter w) => new(w.Id, w.Code, w.NameEn, w.NameAr, w.CapacityHoursPerDay, w.LaborRatePerHour,
        w.MachineRatePerHour, w.OverheadRatePerHour, w.OverheadBasis.ToString(), w.IsActive, Domain.WorkingDays.ToNames(w.WorkingDaysMask));

    public static RoutingDto ToDto(Routing r) => new(r.Id, r.ProductId, r.Code, r.Name, r.IsActive,
        r.Operations.Select(o => new RoutingOperationDto(o.Sequence, o.Name, o.WorkCenterId, o.SetupHours, o.RunHoursPerUnit)).ToList());

    public static BomVersionDto ToDto(BomVersion v) => new(v.Id, v.BomId, v.ProductId, v.VersionNo, v.EffectiveFrom, v.EffectiveTo, v.Status.ToString(),
        v.OutputQuantity, v.ExpectedYieldPercent,
        v.Lines.Select(l => new BomLineDto(l.LineId, l.ComponentProductId, l.QuantityPerOutput, l.UomId, l.ScrapPercent, l.IssueMethod.ToString(), l.Alternatives,
            l.ComponentVariantId, l.AppliesTo)).ToList());

    public static PlanningParametersDto ToDto(ItemPlanningParameters p) =>
        new(p.Id, p.ProductId, p.SafetyStock, p.LeadTimeDays, p.LotSizeRule.ToString(), p.LotSize, p.Procurement?.ToString(), p.CostingMethod?.ToString(), p.PreferredSupplierId);
}

// ---------- Work centers ----------
public sealed record SaveWorkCenterCommand(Guid? Id, string Code, string NameEn, string NameAr, decimal CapacityHoursPerDay, decimal LaborRatePerHour,
    decimal MachineRatePerHour, decimal OverheadRatePerHour, string? OverheadBasis, IReadOnlyList<string>? WorkingDays = null) : ICommand<WorkCenterDto>;

public sealed class SaveWorkCenterValidator : AbstractValidator<SaveWorkCenterCommand>
{
    public SaveWorkCenterValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OverheadBasis).Must(b => b is null || Enum.TryParse<OverheadBasis>(b, true, out _)).WithMessage("OverheadBasis must be LaborHours or MachineHours.");
        RuleForEach(x => x.WorkingDays).Must(d => Enum.TryParse<DayOfWeek>(d, true, out _)).WithMessage("Working days must be day names (Sunday … Saturday).");
    }
}

public sealed class SaveWorkCenterHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<SaveWorkCenterCommand, WorkCenterDto>
{
    public async Task<Result<WorkCenterDto>> Handle(SaveWorkCenterCommand command, CancellationToken cancellationToken)
    {
        var basis = command.OverheadBasis is null ? OverheadBasis.LaborHours : Enum.Parse<OverheadBasis>(command.OverheadBasis, true);
        WorkCenter wc;
        if (command.Id is { } id)
        {
            wc = await db.WorkCenters.FirstOrDefaultAsync(w => w.Id == id, cancellationToken) ?? throw new Erp.SharedKernel.Domain.DomainException(MfgErrors.WorkCenterNotFound);
            wc.Update(command.NameEn, command.NameAr, command.CapacityHoursPerDay, command.LaborRatePerHour, command.MachineRatePerHour, command.OverheadRatePerHour, basis);
        }
        else
        {
            var code = command.Code.Trim().ToUpperInvariant();
            if (await db.WorkCenters.AnyAsync(w => w.Code == code, cancellationToken))
            {
                return ManufacturingErrors.CodeTaken;
            }

            wc = WorkCenter.Create(support.CompanyId, code, command.NameEn, command.NameAr, command.CapacityHoursPerDay, command.LaborRatePerHour,
                command.MachineRatePerHour, command.OverheadRatePerHour, basis);
            db.WorkCenters.Add(wc);
        }

        if (command.WorkingDays is { } days)
        {
            wc.SetWorkingDays(Domain.WorkingDays.FromNames(days));
        }

        await db.SaveChangesAsync(cancellationToken);
        return SetupMapping.ToDto(wc);
    }
}

public sealed record ListWorkCentersQuery : IQuery<IReadOnlyList<WorkCenterDto>>;

public sealed class ListWorkCentersHandler(IManufacturingDbContext db) : IQueryHandler<ListWorkCentersQuery, IReadOnlyList<WorkCenterDto>>
{
    public async Task<Result<IReadOnlyList<WorkCenterDto>>> Handle(ListWorkCentersQuery query, CancellationToken cancellationToken) =>
        (await db.WorkCenters.AsNoTracking().OrderBy(w => w.Code).ToListAsync(cancellationToken)).Select(SetupMapping.ToDto).ToList();
}

// ---------- Routings ----------
public sealed record SaveRoutingCommand(Guid? Id, Guid ProductId, string Code, string Name, IReadOnlyList<RoutingOperationDto> Operations) : ICommand<RoutingDto>;

public sealed class SaveRoutingValidator : AbstractValidator<SaveRoutingCommand>
{
    public SaveRoutingValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Operations).NotEmpty();
    }
}

public sealed class SaveRoutingHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<SaveRoutingCommand, RoutingDto>
{
    public async Task<Result<RoutingDto>> Handle(SaveRoutingCommand command, CancellationToken cancellationToken)
    {
        var workCenterIds = command.Operations.Select(o => o.WorkCenterId).Distinct().ToList();
        if (await db.WorkCenters.CountAsync(w => workCenterIds.Contains(w.Id) && w.IsActive, cancellationToken) != workCenterIds.Count)
        {
            return MfgErrors.WorkCenterNotFound;
        }

        var operations = command.Operations.Select(o => new RoutingOperation
        {
            Sequence = o.Sequence,
            Name = o.Name,
            WorkCenterId = o.WorkCenterId,
            SetupHours = o.SetupHours,
            RunHoursPerUnit = o.RunHoursPerUnit,
        });
        Routing routing;
        if (command.Id is { } id)
        {
            routing = await db.Routings.FirstOrDefaultAsync(r => r.Id == id, cancellationToken) ?? throw new Erp.SharedKernel.Domain.DomainException(MfgErrors.RoutingNotFound);
            routing.Update(command.Name, operations);
        }
        else
        {
            var products = await support.StockableProductsAsync([command.ProductId], cancellationToken);
            if (products.IsFailure)
            {
                return products.Error;
            }

            var code = command.Code.Trim().ToUpperInvariant();
            if (await db.Routings.AnyAsync(r => r.Code == code, cancellationToken))
            {
                return ManufacturingErrors.CodeTaken;
            }

            routing = Routing.Create(support.CompanyId, command.ProductId, code, command.Name, operations);
            db.Routings.Add(routing);
        }

        await db.SaveChangesAsync(cancellationToken);
        return SetupMapping.ToDto(routing);
    }
}

public sealed record ListRoutingsQuery(Guid? ProductId) : IQuery<IReadOnlyList<RoutingDto>>;

public sealed class ListRoutingsHandler(IManufacturingDbContext db) : IQueryHandler<ListRoutingsQuery, IReadOnlyList<RoutingDto>>
{
    public async Task<Result<IReadOnlyList<RoutingDto>>> Handle(ListRoutingsQuery query, CancellationToken cancellationToken) =>
        (await db.Routings.AsNoTracking().Where(r => query.ProductId == null || r.ProductId == query.ProductId).OrderBy(r => r.Code).ToListAsync(cancellationToken))
        .Select(SetupMapping.ToDto).ToList();
}

// ---------- Bills of materials ----------
public sealed record CreateBomCommand(Guid ProductId, string Code, string Name) : ICommand<BomDto>;

public sealed class CreateBomValidator : AbstractValidator<CreateBomCommand>
{
    public CreateBomValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateBomHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<CreateBomCommand, BomDto>
{
    public async Task<Result<BomDto>> Handle(CreateBomCommand command, CancellationToken cancellationToken)
    {
        var products = await support.StockableProductsAsync([command.ProductId], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Boms.AnyAsync(b => b.Code == code, cancellationToken))
        {
            return ManufacturingErrors.CodeTaken;
        }

        var bom = BillOfMaterials.Create(support.CompanyId, command.ProductId, code, command.Name);
        db.Boms.Add(bom);
        await db.SaveChangesAsync(cancellationToken);
        return new BomDto(bom.Id, bom.ProductId, bom.Code, bom.Name, []);
    }
}

public sealed record BomLineInput(Guid? LineId, Guid ComponentProductId, decimal QuantityPerOutput, Guid UomId, decimal ScrapPercent, string? IssueMethod, IReadOnlyList<Guid>? Alternatives,
    Guid? ComponentVariantId = null, IReadOnlyDictionary<string, string>? AppliesTo = null);

public sealed record SaveBomVersionCommand(Guid BomId, Guid? VersionId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal OutputQuantity, decimal? ExpectedYieldPercent,
    IReadOnlyList<BomLineInput> Lines) : ICommand<BomVersionDto>;

public sealed class SaveBomVersionValidator : AbstractValidator<SaveBomVersionCommand>
{
    public SaveBomVersionValidator()
    {
        RuleFor(x => x.OutputQuantity).GreaterThan(0);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.QuantityPerOutput).GreaterThan(0);
            l.RuleFor(x => x.IssueMethod).Must(m => m is null || Enum.TryParse<IssueMethod>(m, true, out _)).WithMessage("IssueMethod must be Manual or Backflush.");
        });
    }
}

public sealed class SaveBomVersionHandler(IManufacturingDbContext db, ManufacturingSupport support) : ICommandHandler<SaveBomVersionCommand, BomVersionDto>
{
    public async Task<Result<BomVersionDto>> Handle(SaveBomVersionCommand command, CancellationToken cancellationToken)
    {
        var bom = await db.Boms.FirstOrDefaultAsync(b => b.Id == command.BomId, cancellationToken);
        if (bom is null)
        {
            return MfgErrors.BomNotFound;
        }

        var products = await support.StockableProductsAsync(command.Lines.SelectMany(l => (l.Alternatives ?? []).Append(l.ComponentProductId)), cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        if (command.Lines.Any(l => ManufacturingSupport.Factor(products.Value[l.ComponentProductId], l.UomId) is null))
        {
            return MfgErrors.UomInvalid;
        }

        var lines = command.Lines.Select(l => new BomLine
        {
            LineId = l.LineId ?? Guid.Empty,
            ComponentProductId = l.ComponentProductId,
            QuantityPerOutput = l.QuantityPerOutput,
            UomId = l.UomId,
            ScrapPercent = l.ScrapPercent,
            IssueMethod = l.IssueMethod is null ? IssueMethod.Manual : Enum.Parse<IssueMethod>(l.IssueMethod, true),
            Alternatives = l.Alternatives?.Distinct().Where(a => a != l.ComponentProductId).ToList() ?? [],
            ComponentVariantId = l.ComponentVariantId,
            AppliesTo = l.AppliesTo is { Count: > 0 } a ? new Dictionary<string, string>(a) : null,
        });
        BomVersion version;
        if (command.VersionId is { } id)
        {
            version = await db.BomVersions.FirstOrDefaultAsync(v => v.Id == id && v.BomId == bom.Id, cancellationToken)
                ?? throw new Erp.SharedKernel.Domain.DomainException(MfgErrors.VersionNotFound);
            version.Update(command.EffectiveFrom, command.EffectiveTo, command.OutputQuantity, command.ExpectedYieldPercent ?? 100m, lines);
        }
        else
        {
            var next = (await db.BomVersions.Where(v => v.BomId == bom.Id).MaxAsync(v => (int?)v.VersionNo, cancellationToken) ?? 0) + 1;
            version = BomVersion.Create(bom, next, command.EffectiveFrom, command.EffectiveTo, command.OutputQuantity, command.ExpectedYieldPercent ?? 100m, lines);
            db.BomVersions.Add(version);
        }

        await db.SaveChangesAsync(cancellationToken);
        return SetupMapping.ToDto(version);
    }
}

public sealed record BomVersionActionCommand(Guid BomId, Guid VersionId, bool Activate) : ICommand<BomVersionDto>;

/// <summary>Activates (cycle and overlap checks) or makes a version obsolete.</summary>
public sealed class BomVersionActionHandler(IManufacturingDbContext db) : ICommandHandler<BomVersionActionCommand, BomVersionDto>
{
    public async Task<Result<BomVersionDto>> Handle(BomVersionActionCommand command, CancellationToken cancellationToken)
    {
        var versions = await db.BomVersions.Where(v => v.BomId == command.BomId).ToListAsync(cancellationToken);
        var version = versions.FirstOrDefault(v => v.Id == command.VersionId);
        if (version is null)
        {
            return MfgErrors.VersionNotFound;
        }

        if (!command.Activate)
        {
            version.MakeObsolete();
            await db.SaveChangesAsync(cancellationToken);
            return SetupMapping.ToDto(version);
        }

        // Multi-level cycle check over every usable version in the company plus the one being activated.
        var usable = await db.BomVersions.AsNoTracking()
            .Where(v => v.Status == BomVersionStatus.Active || v.Status == BomVersionStatus.Superseded)
            .ToListAsync(cancellationToken);
        var graph = BuildGraph(usable.Where(v => v.Id != version.Id).Append(version));
        if (BomCalculator.HasCycle(graph, version.ProductId))
        {
            return ManufacturingErrors.BomCycle;
        }

        version.Activate(versions);
        await db.SaveChangesAsync(cancellationToken);
        return SetupMapping.ToDto(version);
    }

    public static IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> BuildGraph(IEnumerable<BomVersion> versions) =>
        versions.GroupBy(v => v.ProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)g.SelectMany(v => v.Lines.Select(l => l.ComponentProductId)).Distinct().ToList());
}

public sealed record ListBomsQuery(Guid? ProductId) : IQuery<IReadOnlyList<BomDto>>;

public sealed class ListBomsHandler(IManufacturingDbContext db) : IQueryHandler<ListBomsQuery, IReadOnlyList<BomDto>>
{
    public async Task<Result<IReadOnlyList<BomDto>>> Handle(ListBomsQuery query, CancellationToken cancellationToken)
    {
        var boms = await db.Boms.AsNoTracking().Where(b => query.ProductId == null || b.ProductId == query.ProductId).OrderBy(b => b.Code).ToListAsync(cancellationToken);
        var ids = boms.Select(b => b.Id).ToList();
        var versions = await db.BomVersions.AsNoTracking().Where(v => ids.Contains(v.BomId)).ToListAsync(cancellationToken);
        return boms.Select(b => new BomDto(b.Id, b.ProductId, b.Code, b.Name,
            versions.Where(v => v.BomId == b.Id).OrderBy(v => v.VersionNo).Select(SetupMapping.ToDto).ToList())).ToList();
    }
}

public sealed record GetBomQuery(Guid BomId) : IQuery<BomDto>;

public sealed class GetBomHandler(IManufacturingDbContext db) : IQueryHandler<GetBomQuery, BomDto>
{
    public async Task<Result<BomDto>> Handle(GetBomQuery query, CancellationToken cancellationToken)
    {
        var bom = await db.Boms.AsNoTracking().FirstOrDefaultAsync(b => b.Id == query.BomId, cancellationToken);
        if (bom is null)
        {
            return MfgErrors.BomNotFound;
        }

        var versions = await db.BomVersions.AsNoTracking().Where(v => v.BomId == bom.Id).OrderBy(v => v.VersionNo).ToListAsync(cancellationToken);
        return new BomDto(bom.Id, bom.ProductId, bom.Code, bom.Name, versions.Select(SetupMapping.ToDto).ToList());
    }
}

// ---------- Planning parameters ----------
public sealed record SavePlanningParametersCommand(Guid ProductId, decimal SafetyStock, int LeadTimeDays, string? LotSizeRule, decimal LotSize, string? Procurement,
    string? CostingMethod = null, Guid? PreferredSupplierId = null) : ICommand<PlanningParametersDto>;

public sealed class SavePlanningParametersValidator : AbstractValidator<SavePlanningParametersCommand>
{
    public SavePlanningParametersValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.LotSizeRule).Must(r => r is null || Enum.TryParse<LotSizeRule>(r, true, out _)).WithMessage("LotSizeRule must be LotForLot, FixedMinimum or Multiple.");
        RuleFor(x => x.Procurement).Must(p => p is null || Enum.TryParse<ProcurementType>(p, true, out _)).WithMessage("Procurement must be Make or Buy.");
        RuleFor(x => x.CostingMethod).Must(m => m is null || Enum.TryParse<ManufacturingCostingMethod>(m, true, out _)).WithMessage("CostingMethod must be Actual or Standard.");
    }
}

public sealed class SavePlanningParametersHandler(IManufacturingDbContext db, ManufacturingSupport support, Erp.Modules.Parties.Contracts.IPartyQueries parties)
    : ICommandHandler<SavePlanningParametersCommand, PlanningParametersDto>
{
    public async Task<Result<PlanningParametersDto>> Handle(SavePlanningParametersCommand command, CancellationToken cancellationToken)
    {
        var products = await support.StockableProductsAsync([command.ProductId], cancellationToken);
        if (products.IsFailure)
        {
            return products.Error;
        }

        if (command.PreferredSupplierId is { } supplierId && await parties.GetSupplierAsync(supplierId, cancellationToken) is not { IsActive: true })
        {
            return Error.Validation("manufacturing.supplier-invalid", "The preferred supplier was not found or is inactive.");
        }

        var parameters = await db.PlanningParameters.FirstOrDefaultAsync(p => p.ProductId == command.ProductId, cancellationToken);
        if (parameters is null)
        {
            parameters = ItemPlanningParameters.Create(support.CompanyId, command.ProductId);
            db.PlanningParameters.Add(parameters);
        }

        parameters.Update(command.SafetyStock, command.LeadTimeDays,
            command.LotSizeRule is null ? LotSizeRule.LotForLot : Enum.Parse<LotSizeRule>(command.LotSizeRule, true), command.LotSize,
            command.Procurement is null ? null : Enum.Parse<ProcurementType>(command.Procurement, true),
            command.CostingMethod is null ? null : Enum.Parse<ManufacturingCostingMethod>(command.CostingMethod, true), command.PreferredSupplierId);
        await db.SaveChangesAsync(cancellationToken);
        return SetupMapping.ToDto(parameters);
    }
}

public sealed record ListPlanningParametersQuery : IQuery<IReadOnlyList<PlanningParametersDto>>;

public sealed class ListPlanningParametersHandler(IManufacturingDbContext db) : IQueryHandler<ListPlanningParametersQuery, IReadOnlyList<PlanningParametersDto>>
{
    public async Task<Result<IReadOnlyList<PlanningParametersDto>>> Handle(ListPlanningParametersQuery query, CancellationToken cancellationToken) =>
        (await db.PlanningParameters.AsNoTracking().ToListAsync(cancellationToken)).Select(SetupMapping.ToDto).ToList();
}
