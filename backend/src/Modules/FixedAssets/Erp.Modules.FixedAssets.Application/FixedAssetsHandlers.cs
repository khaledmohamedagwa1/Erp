using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.FixedAssets.Contracts;
using Erp.Modules.FixedAssets.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.FixedAssets.Application;

// ======================= DTOs =======================
public sealed record CategoryDto(Guid Id, string Code, string NameEn, string NameAr, string DefaultMethod, int UsefulLifeMonths, decimal SalvagePercent,
    decimal? DecliningRatePercent, string? PostingGroup, bool IsActive);

public sealed record AssetDto(Guid Id, string? Number, string NameEn, string NameAr, Guid CategoryId, Guid LegalEntityId, Guid? BranchId, string? Location,
    Guid? CustodianId, DateOnly AcquisitionDate, DateOnly InServiceDate, decimal AcquisitionCost, decimal Cost, decimal SalvageValue, int UsefulLifeMonths,
    string Method, decimal? DecliningRatePercent, decimal? TotalUnits, string Status, decimal AccumulatedDepreciation, decimal NetBookValue,
    string? AcquisitionMode, Guid? AcquisitionJournalId, string? SourceDocType, Guid? SourceDocId, Guid? SourceLineId,
    DateOnly? DisposalDate, decimal? DisposalProceeds, decimal? DisposalGainLoss, Guid? DisposalJournalId);

public sealed record ScheduleLineDto(DateOnly Period, decimal Amount, decimal Cumulative, decimal NetBookValue, decimal? Units, bool Posted, Guid? RunId);

public sealed record AssetEventDto(DateTime AtUtc, DateOnly Date, string Kind, string Description, decimal? Amount, Guid? JournalId, Guid? ReferenceId);

public sealed record RunLineDto(Guid AssetId, string AssetNumber, Guid CategoryId, string? PostingGroup, decimal Amount);

public sealed record RunDto(Guid Id, Guid LegalEntityId, DateOnly Period, string Status, decimal Total, Guid? JournalId, DateOnly? ReversalDate,
    Guid? ReversalJournalId, IReadOnlyList<RunLineDto> Lines, bool AlreadyPosted);

public sealed record RunPreviewDto(Guid LegalEntityId, DateOnly Period, decimal Total, IReadOnlyList<RunLineDto> Lines);

public sealed record RegisterLineDto(Guid AssetId, string? Number, string NameEn, Guid CategoryId, string CategoryCode, string Status, DateOnly InServiceDate,
    decimal Cost, decimal AccumulatedDepreciation, decimal NetBookValue);

public sealed record RegisterCategoryDto(Guid CategoryId, string CategoryCode, decimal Cost, decimal AccumulatedDepreciation, decimal NetBookValue);

public sealed record RegisterDto(Guid LegalEntityId, DateOnly AsOf, IReadOnlyList<RegisterLineDto> Assets, IReadOnlyList<RegisterCategoryDto> Categories,
    decimal TotalCost, decimal TotalAccumulatedDepreciation, decimal TotalNetBookValue);

internal static class AssetMapping
{
    public static CategoryDto ToDto(AssetCategory c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.DefaultMethod.ToString(), c.UsefulLifeMonths, c.SalvagePercent,
        c.DecliningRatePercent, c.PostingGroup, c.IsActive);

    public static AssetDto ToDto(Asset a) => new(a.Id, a.Number, a.NameEn, a.NameAr, a.CategoryId, a.LegalEntityId, a.BranchId, a.Location, a.CustodianId,
        a.AcquisitionDate, a.InServiceDate, a.AcquisitionCost, a.Cost, a.SalvageValue, a.UsefulLifeMonths, a.Method.ToString(), a.DecliningRatePercent, a.TotalUnits,
        a.Status.ToString(), a.AccumulatedDepreciation, a.NetBookValue, a.AcquisitionMode?.ToString(), a.AcquisitionJournalId, a.SourceDocType, a.SourceDocId,
        a.SourceLineId, a.DisposalDate, a.DisposalProceeds, a.DisposalGainLoss, a.DisposalJournalId);

    public static RunDto ToDto(DepreciationRun r, bool alreadyPosted = false) => new(r.Id, r.LegalEntityId, r.Period, r.Status.ToString(), r.Total, r.JournalId,
        r.ReversalDate, r.ReversalJournalId, r.Lines.Select(l => new RunLineDto(l.AssetId, l.AssetNumber, l.CategoryId, l.PostingGroup, l.Amount)).ToList(), alreadyPosted);

    public static DepreciationMethod? Method(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<DepreciationMethod>(value, true);

    public static bool IsMethod(string? value) => value is null || Enum.TryParse<DepreciationMethod>(value, true, out _);
}

// ======================= Categories =======================
public sealed record CreateCategoryCommand(string Code, string NameEn, string NameAr, string DefaultMethod, int UsefulLifeMonths, decimal SalvagePercent,
    decimal? DecliningRatePercent, string? PostingGroup) : ICommand<CategoryDto>;

public sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DefaultMethod).NotEmpty().Must(AssetMapping.IsMethod).WithMessage("Method must be StraightLine, DecliningBalance or UnitsOfProduction.");
        RuleFor(x => x.PostingGroup).MaximumLength(30);
    }
}

public sealed class CreateCategoryHandler(IFixedAssetsDbContext db, ITenantContext tenant) : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<Result<CategoryDto>> Handle(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Categories.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return FixedAssetsAppErrors.CategoryCodeTaken;
        }

        var category = AssetCategory.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr, AssetMapping.Method(command.DefaultMethod)!.Value,
            command.UsefulLifeMonths, command.SalvagePercent, command.DecliningRatePercent, command.PostingGroup);
        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(category);
    }
}

public sealed record UpdateCategoryCommand(Guid Id, string NameEn, string NameAr, string DefaultMethod, int UsefulLifeMonths, decimal SalvagePercent,
    decimal? DecliningRatePercent, string? PostingGroup, bool IsActive) : ICommand<CategoryDto>;

public sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator()
    {
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DefaultMethod).NotEmpty().Must(AssetMapping.IsMethod).WithMessage("Method must be StraightLine, DecliningBalance or UnitsOfProduction.");
        RuleFor(x => x.PostingGroup).MaximumLength(30);
    }
}

public sealed class UpdateCategoryHandler(IFixedAssetsDbContext db) : ICommandHandler<UpdateCategoryCommand, CategoryDto>
{
    public async Task<Result<CategoryDto>> Handle(UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (category is null)
        {
            return FixedAssetsAppErrors.CategoryNotFound;
        }

        // Existing assets keep the method, life and posting group they were created with; changes apply to new assets.
        category.Update(command.NameEn, command.NameAr, AssetMapping.Method(command.DefaultMethod)!.Value, command.UsefulLifeMonths, command.SalvagePercent,
            command.DecliningRatePercent, command.PostingGroup, command.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(category);
    }
}

public sealed record ListCategoriesQuery : IQuery<IReadOnlyList<CategoryDto>>;

public sealed class ListCategoriesHandler(IFixedAssetsDbContext db) : IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(ListCategoriesQuery query, CancellationToken cancellationToken)
    {
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Code).ToListAsync(cancellationToken);
        return categories.Select(AssetMapping.ToDto).ToList();
    }
}

// ======================= Assets (drafts) =======================
public sealed record AssetInput(string NameEn, string NameAr, Guid CategoryId, Guid LegalEntityId, Guid? BranchId, string? Location, Guid? CustodianId,
    DateOnly AcquisitionDate, DateOnly InServiceDate, decimal AcquisitionCost, decimal? SalvageValue, int? UsefulLifeMonths, string? Method,
    decimal? DecliningRatePercent, decimal? TotalUnits);

public sealed record CreateAssetCommand(AssetInput Asset) : ICommand<AssetDto>;

public sealed record UpdateAssetCommand(Guid Id, AssetInput Asset) : ICommand<AssetDto>;

public sealed class AssetInputValidator : AbstractValidator<AssetInput>
{
    public AssetInputValidator()
    {
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Location).MaximumLength(200);
        RuleFor(x => x.AcquisitionCost).GreaterThan(0);
        RuleFor(x => x.Method).Must(AssetMapping.IsMethod).WithMessage("Method must be StraightLine, DecliningBalance or UnitsOfProduction.");
    }
}

public sealed class CreateAssetValidator : AbstractValidator<CreateAssetCommand>
{
    public CreateAssetValidator() => RuleFor(x => x.Asset).NotNull().SetValidator(new AssetInputValidator());
}

public sealed class UpdateAssetValidator : AbstractValidator<UpdateAssetCommand>
{
    public UpdateAssetValidator() => RuleFor(x => x.Asset).NotNull().SetValidator(new AssetInputValidator());
}

public sealed class CreateAssetHandler(IFixedAssetsDbContext db, ITenantContext tenant, AssetScope scope) : ICommandHandler<CreateAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(CreateAssetCommand command, CancellationToken cancellationToken)
    {
        var input = command.Asset;
        var le = await scope.LegalEntityAsync(input.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.CategoryId && c.IsActive, cancellationToken);
        if (category is null)
        {
            return FixedAssetsAppErrors.CategoryNotFound;
        }

        var asset = Asset.CreateDraft(tenant.CompanyId!.Value, category, input.LegalEntityId, input.NameEn, input.NameAr, input.BranchId, input.Location,
            input.CustodianId, input.AcquisitionDate, input.InServiceDate, input.AcquisitionCost, input.SalvageValue, input.UsefulLifeMonths,
            AssetMapping.Method(input.Method), input.DecliningRatePercent, input.TotalUnits);
        db.Assets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

public sealed class UpdateAssetHandler(IFixedAssetsDbContext db, AssetScope scope) : ICommandHandler<UpdateAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(UpdateAssetCommand command, CancellationToken cancellationToken)
    {
        var input = command.Asset;
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        if (asset.LegalEntityId != input.LegalEntityId)
        {
            return FixedAssetsAppErrors.CrossLegalEntity;
        }

        var le = await scope.LegalEntityAsync(input.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.CategoryId && c.IsActive, cancellationToken);
        if (category is null)
        {
            return FixedAssetsAppErrors.CategoryNotFound;
        }

        asset.Update(category, input.NameEn, input.NameAr, input.BranchId, input.Location, input.CustodianId, input.AcquisitionDate, input.InServiceDate,
            input.AcquisitionCost, input.SalvageValue, input.UsefulLifeMonths, AssetMapping.Method(input.Method), input.DecliningRatePercent, input.TotalUnits);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

public sealed record DeleteAssetCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteAssetHandler(IFixedAssetsDbContext db) : ICommandHandler<DeleteAssetCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteAssetCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        if (asset.Status != AssetStatus.Draft)
        {
            return AssetErrors.NotDraft;
        }

        db.Assets.Remove(asset);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

public sealed record GetAssetQuery(Guid Id) : IQuery<AssetDto>;

public sealed class GetAssetHandler(IFixedAssetsDbContext db) : IQueryHandler<GetAssetQuery, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(GetAssetQuery query, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.Id, cancellationToken);
        return asset is null ? FixedAssetsAppErrors.AssetNotFound : AssetMapping.ToDto(asset);
    }
}

public sealed record ListAssetsQuery(PageRequest Page, Guid? LegalEntityId, Guid? CategoryId, string? Status, string? Search) : IQuery<PagedResult<AssetDto>>;

public sealed class ListAssetsHandler(IFixedAssetsDbContext db) : IQueryHandler<ListAssetsQuery, PagedResult<AssetDto>>
{
    public async Task<Result<PagedResult<AssetDto>>> Handle(ListAssetsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Assets.AsNoTracking();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(a => a.LegalEntityId == le);
        }

        if (query.CategoryId is { } category)
        {
            source = source.Where(a => a.CategoryId == category);
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<AssetStatus>(query.Status, true, out var status))
        {
            source = source.Where(a => a.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(a => a.NameEn.Contains(term) || a.NameAr.Contains(term) || (a.Number != null && a.Number.Contains(term)));
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(a => a.Number ?? "~").ThenBy(a => a.NameEn).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AssetDto>(items.Select(AssetMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record GetScheduleQuery(Guid Id) : IQuery<IReadOnlyList<ScheduleLineDto>>;

public sealed class GetScheduleHandler(IFixedAssetsDbContext db) : IQueryHandler<GetScheduleQuery, IReadOnlyList<ScheduleLineDto>>
{
    public async Task<Result<IReadOnlyList<ScheduleLineDto>>> Handle(GetScheduleQuery query, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        // Cumulative and NBV are computed over the schedule as planned; revaluations shift the starting cost of later lines.
        var lines = new List<ScheduleLineDto>();
        decimal cumulative = 0;
        var cost = asset.Cost;
        foreach (var line in asset.Schedule.OrderBy(s => s.Period))
        {
            cumulative += line.Amount;
            lines.Add(new ScheduleLineDto(line.Period, line.Amount, cumulative, cost - cumulative, line.Units, line.IsPosted, line.RunId));
        }

        return lines;
    }
}

public sealed record GetHistoryQuery(Guid Id) : IQuery<IReadOnlyList<AssetEventDto>>;

public sealed class GetHistoryHandler(IFixedAssetsDbContext db) : IQueryHandler<GetHistoryQuery, IReadOnlyList<AssetEventDto>>
{
    public async Task<Result<IReadOnlyList<AssetEventDto>>> Handle(GetHistoryQuery query, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.Id, cancellationToken);
        return asset is null
            ? FixedAssetsAppErrors.AssetNotFound
            : asset.History.OrderBy(h => h.AtUtc).Select(h => new AssetEventDto(h.AtUtc, h.Date, h.Kind, h.Description, h.Amount, h.JournalId, h.ReferenceId)).ToList();
    }
}

// ======================= Lifecycle with postings =======================
public sealed record ActivateAssetCommand(Guid Id, string Mode, Guid? ClearingAccountId, string? SourceDocType, Guid? SourceDocId, Guid? SourceLineId)
    : ICommand<AssetDto>;

public sealed class ActivateAssetValidator : AbstractValidator<ActivateAssetCommand>
{
    public ActivateAssetValidator() =>
        RuleFor(x => x.Mode).Must(m => Enum.TryParse<AcquisitionMode>(m, true, out _)).WithMessage("Mode must be ManualJournal or PurchaseInvoice.");
}

public sealed class ActivateAssetHandler(
    IFixedAssetsDbContext db,
    AssetScope scope,
    IJournalPostingService journals,
    IDocumentNumberGenerator numbers,
    IClock clock) : ICommandHandler<ActivateAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(ActivateAssetCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        if (asset.Status != AssetStatus.Draft)
        {
            return AssetErrors.NotDraft;
        }

        var le = await scope.LegalEntityAsync(asset.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var mode = Enum.Parse<AcquisitionMode>(command.Mode, true);
        Guid? journalId = null;
        if (mode == AcquisitionMode.ManualJournal)
        {
            if (command.ClearingAccountId is not { } clearing)
            {
                return FixedAssetsAppErrors.ClearingAccountRequired;
            }

            var valid = await scope.ValidateAccountAsync(clearing, cancellationToken);
            if (valid.IsFailure)
            {
                return valid.Error;
            }

            var lines = new AssetJournal()
                .Debit(PostingRoles.FixedAssetCost, null, asset.AcquisitionCost, asset.PostingGroup, asset.NameEn)
                .Credit(null, clearing, asset.AcquisitionCost, description: asset.NameEn);
            var posted = await journals.PostAsync(new JournalRequest(asset.LegalEntityId, asset.AcquisitionDate, SourceModules.FixedAssets,
                FixedAssetDocumentTypes.Acquisition, asset.Id, "acquire", $"Asset acquisition {asset.NameEn}", le.Value.FunctionalCurrency, 1m, lines.Lines), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            journalId = posted.Value.JournalId;
        }
        else if (command.SourceDocId is null || string.IsNullOrWhiteSpace(command.SourceDocType))
        {
            return FixedAssetsAppErrors.SourceRequired;
        }

        var number = await numbers.NextAsync(new NumberScope(asset.CompanyId, "FixedAsset", "FA"), cancellationToken);
        asset.Activate(number, mode, journalId, mode == AcquisitionMode.PurchaseInvoice ? command.SourceDocType!.Trim() : null,
            mode == AcquisitionMode.PurchaseInvoice ? command.SourceDocId : null, mode == AcquisitionMode.PurchaseInvoice ? command.SourceLineId : null, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

public sealed record TransferAssetCommand(Guid Id, DateOnly Date, Guid? BranchId, string? Location, Guid? CustodianId, string? Note) : ICommand<AssetDto>;

public sealed class TransferAssetHandler(IFixedAssetsDbContext db, IClock clock) : ICommandHandler<TransferAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(TransferAssetCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        asset.Transfer(command.Date, command.BranchId, command.Location, command.CustodianId, command.Note, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

public sealed record RecordUsageCommand(Guid Id, DateOnly Period, decimal Units) : ICommand<AssetDto>;

public sealed class RecordUsageValidator : AbstractValidator<RecordUsageCommand>
{
    public RecordUsageValidator() => RuleFor(x => x.Units).GreaterThan(0);
}

public sealed class RecordUsageHandler(IFixedAssetsDbContext db, IClock clock) : ICommandHandler<RecordUsageCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(RecordUsageCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        asset.RecordUsage(command.Period, command.Units, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

/// <summary>
/// Revalues the net book value. The difference is posted against the chosen account (normally a revaluation reserve in equity,
/// or an expense account for an impairment): increase Dr FixedAssetCost / Cr account, decrease the opposite.
/// </summary>
public sealed record RevalueAssetCommand(Guid Id, DateOnly Date, decimal NewNetBookValue, Guid ReserveAccountId, int? NewUsefulLifeMonths) : ICommand<AssetDto>;

public sealed class RevalueAssetHandler(IFixedAssetsDbContext db, AssetScope scope, IJournalPostingService journals, IClock clock)
    : ICommandHandler<RevalueAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(RevalueAssetCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        var le = await scope.LegalEntityAsync(asset.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var valid = await scope.ValidateAccountAsync(command.ReserveAccountId, cancellationToken);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var delta = asset.ValidateRevaluation(command.NewNetBookValue, command.NewUsefulLifeMonths);
        var eventId = SequentialGuid.New();
        var journal = new AssetJournal();
        if (delta > 0)
        {
            journal.Debit(PostingRoles.FixedAssetCost, null, delta, asset.PostingGroup, asset.Number).Credit(null, command.ReserveAccountId, delta, description: asset.Number);
        }
        else
        {
            journal.Debit(null, command.ReserveAccountId, -delta, description: asset.Number).Credit(PostingRoles.FixedAssetCost, null, -delta, asset.PostingGroup, asset.Number);
        }

        Guid journalId = Guid.Empty;
        if (delta != 0)
        {
            var posted = await journals.PostAsync(new JournalRequest(asset.LegalEntityId, command.Date, SourceModules.FixedAssets, FixedAssetDocumentTypes.Revaluation,
                eventId, "revalue", $"Revaluation of asset {asset.Number}", le.Value.FunctionalCurrency, 1m, journal.Lines), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            journalId = posted.Value.JournalId;
        }

        asset.Revalue(command.Date, command.NewNetBookValue, command.NewUsefulLifeMonths, journalId, eventId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

public sealed record DisposeAssetCommand(Guid Id, DateOnly Date, decimal Proceeds, Guid? ProceedsAccountId) : ICommand<AssetDto>;

public sealed class DisposeAssetValidator : AbstractValidator<DisposeAssetCommand>
{
    public DisposeAssetValidator() => RuleFor(x => x.Proceeds).GreaterThanOrEqualTo(0);
}

/// <summary>Dr AccumulatedDepreciation + Dr proceeds account / Cr FixedAssetCost, the difference to AssetDisposalGainLoss.</summary>
public sealed class DisposeAssetHandler(IFixedAssetsDbContext db, AssetScope scope, IJournalPostingService journals, IClock clock)
    : ICommandHandler<DisposeAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(DisposeAssetCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        var le = await scope.LegalEntityAsync(asset.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (command.Proceeds > 0)
        {
            if (command.ProceedsAccountId is not { } proceedsAccount)
            {
                return FixedAssetsAppErrors.ProceedsAccountRequired;
            }

            var valid = await scope.ValidateAccountAsync(proceedsAccount, cancellationToken);
            if (valid.IsFailure)
            {
                return valid.Error;
            }
        }

        var gainLoss = asset.DisposalResult(command.Date, command.Proceeds);
        var journal = new AssetJournal()
            .Debit(PostingRoles.AccumulatedDepreciation, null, asset.AccumulatedDepreciation, asset.PostingGroup, asset.Number)
            .Debit(null, command.ProceedsAccountId, AssetMath.Money(command.Proceeds), description: asset.Number)
            .Credit(PostingRoles.FixedAssetCost, null, asset.Cost, asset.PostingGroup, asset.Number);
        if (gainLoss > 0)
        {
            journal.Credit(PostingRoles.AssetDisposalGainLoss, null, gainLoss, asset.PostingGroup, "Gain on disposal");
        }
        else
        {
            journal.Debit(PostingRoles.AssetDisposalGainLoss, null, -gainLoss, asset.PostingGroup, "Loss on disposal");
        }

        var posted = await journals.PostAsync(new JournalRequest(asset.LegalEntityId, command.Date, SourceModules.FixedAssets, FixedAssetDocumentTypes.Disposal,
            asset.Id, "dispose", $"Disposal of asset {asset.Number}", le.Value.FunctionalCurrency, 1m, journal.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        asset.Dispose(command.Date, command.Proceeds, posted.Value.JournalId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

// ======================= Depreciation runs =======================
internal static class RunPlanner
{
    public static async Task<(List<Asset> Assets, List<DepreciationRunLine> Lines)> PlanAsync(IFixedAssetsDbContext db, Guid legalEntityId, DateOnly period,
        bool tracking, CancellationToken ct)
    {
        var query = db.Assets.Where(a => a.LegalEntityId == legalEntityId && a.Status == AssetStatus.Active);
        var assets = tracking ? await query.ToListAsync(ct) : await query.AsNoTracking().ToListAsync(ct);
        var lines = assets
            .Select(a => new DepreciationRunLine
            {
                AssetId = a.Id, AssetNumber = a.Number ?? string.Empty, CategoryId = a.CategoryId, PostingGroup = a.PostingGroup,
                Amount = a.DueLines(period).Sum(l => l.Amount),
            })
            .Where(l => l.Amount > 0)
            .OrderBy(l => l.AssetNumber)
            .ToList();
        return (assets, lines);
    }
}

public sealed record PreviewRunQuery(Guid LegalEntityId, DateOnly Period) : IQuery<RunPreviewDto>;

public sealed class PreviewRunHandler(IFixedAssetsDbContext db, AssetScope scope) : IQueryHandler<PreviewRunQuery, RunPreviewDto>
{
    public async Task<Result<RunPreviewDto>> Handle(PreviewRunQuery query, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var (_, lines) = await RunPlanner.PlanAsync(db, query.LegalEntityId, query.Period, false, cancellationToken);
        return new RunPreviewDto(query.LegalEntityId, AssetMath.MonthStart(query.Period), lines.Sum(l => l.Amount),
            lines.Select(l => new RunLineDto(l.AssetId, l.AssetNumber, l.CategoryId, l.PostingGroup, l.Amount)).ToList());
    }
}

/// <summary>
/// Posts every due (and overdue) schedule line of the legal entity up to the period as one journal dated the last day of the
/// period, grouped by posting group (J-12). Idempotent per legal entity and period.
/// </summary>
public sealed record PostRunCommand(Guid LegalEntityId, DateOnly Period) : ICommand<RunDto>;

public sealed class PostRunHandler(IFixedAssetsDbContext db, AssetScope scope, IJournalPostingService journals, ITenantContext tenant, IClock clock)
    : ICommandHandler<PostRunCommand, RunDto>
{
    public async Task<Result<RunDto>> Handle(PostRunCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var period = AssetMath.MonthStart(command.Period);
        var existing = await db.DepreciationRuns.FirstOrDefaultAsync(
            r => r.LegalEntityId == command.LegalEntityId && r.Period == period && r.Status == DepreciationRunStatus.Posted, cancellationToken);
        if (existing is not null)
        {
            return AssetMapping.ToDto(existing, alreadyPosted: true);
        }

        var (assets, lines) = await RunPlanner.PlanAsync(db, command.LegalEntityId, period, true, cancellationToken);
        if (lines.Count == 0)
        {
            return FixedAssetsAppErrors.NothingToDepreciate;
        }

        var run = DepreciationRun.Create(SequentialGuid.New(), tenant.CompanyId!.Value, command.LegalEntityId, period);
        var journal = new AssetJournal();
        foreach (var group in lines.GroupBy(l => l.PostingGroup))
        {
            var amount = group.Sum(l => l.Amount);
            journal.Debit(PostingRoles.DepreciationExpense, null, amount, group.Key, $"Depreciation {period:yyyy-MM}")
                .Credit(PostingRoles.AccumulatedDepreciation, null, amount, group.Key, $"Depreciation {period:yyyy-MM}");
        }

        var posted = await journals.PostAsync(new JournalRequest(command.LegalEntityId, AssetMath.MonthEnd(period), SourceModules.FixedAssets,
            FixedAssetDocumentTypes.DepreciationRun, run.Id, "post", $"Depreciation {period:yyyy-MM}", le.Value.FunctionalCurrency, 1m, journal.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var now = clock.UtcNow;
        foreach (var asset in assets.Where(a => lines.Any(l => l.AssetId == a.Id)))
        {
            asset.PostDepreciation(period, run.Id, posted.Value.JournalId, now);
        }

        run.SetPosted(lines, posted.Value.JournalId);
        db.DepreciationRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(run);
    }
}

public sealed record ReverseRunCommand(Guid Id, DateOnly? Date) : ICommand<RunDto>;

public sealed class ReverseRunHandler(IFixedAssetsDbContext db, AssetScope scope, IJournalPostingService journals, IClock clock)
    : ICommandHandler<ReverseRunCommand, RunDto>
{
    public async Task<Result<RunDto>> Handle(ReverseRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.DepreciationRuns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (run is null)
        {
            return FixedAssetsAppErrors.RunNotFound;
        }

        var le = await scope.LegalEntityAsync(run.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (await db.DepreciationRuns.AnyAsync(r => r.LegalEntityId == run.LegalEntityId && r.Status == DepreciationRunStatus.Posted && r.Period > run.Period, cancellationToken))
        {
            return FixedAssetsAppErrors.NotLatestRun;
        }

        var assetIds = run.Lines.Select(l => l.AssetId).ToList();
        var assets = await db.Assets.Where(a => assetIds.Contains(a.Id)).ToListAsync(cancellationToken);
        if (assets.Any(a => a.Status == AssetStatus.Disposed))
        {
            return AssetErrors.Disposed;
        }

        var date = command.Date ?? AssetMath.MonthEnd(run.Period);
        var reversed = await journals.ReverseAsync(run.LegalEntityId, FixedAssetDocumentTypes.DepreciationRun, run.Id, "post", date, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        var now = clock.UtcNow;
        foreach (var asset in assets)
        {
            asset.ReverseDepreciation(run.Id, date, reversed.Value.JournalId, now);
        }

        run.MarkReversed(date, reversed.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(run);
    }
}

public sealed record ListRunsQuery(Guid? LegalEntityId) : IQuery<IReadOnlyList<RunDto>>;

public sealed class ListRunsHandler(IFixedAssetsDbContext db) : IQueryHandler<ListRunsQuery, IReadOnlyList<RunDto>>
{
    public async Task<Result<IReadOnlyList<RunDto>>> Handle(ListRunsQuery query, CancellationToken cancellationToken)
    {
        var source = db.DepreciationRuns.AsNoTracking();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(r => r.LegalEntityId == le);
        }

        var runs = await source.OrderByDescending(r => r.Period).ToListAsync(cancellationToken);
        return runs.Select(r => AssetMapping.ToDto(r)).ToList();
    }
}

// ======================= Register =======================
/// <summary>
/// Asset register as of a date: cost (acquisition cost + revaluations dated on or before), accumulated depreciation
/// (schedule lines of posted runs whose journal date is on or before) and NBV. Assets disposed on or before the date and drafts are excluded.
/// </summary>
public sealed record RegisterQuery(Guid LegalEntityId, DateOnly AsOf) : IQuery<RegisterDto>;

public sealed class RegisterHandler(IFixedAssetsDbContext db, AssetScope scope) : IQueryHandler<RegisterQuery, RegisterDto>
{
    public async Task<Result<RegisterDto>> Handle(RegisterQuery query, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);

        // A schedule line counts once its run's journal (dated the run period's last day) is on or before the date, so a
        // catch-up run lands where the ledger has it.
        var runEnds = (await db.DepreciationRuns.AsNoTracking()
                .Where(r => r.LegalEntityId == query.LegalEntityId && r.Status == DepreciationRunStatus.Posted)
                .Select(r => new { r.Id, r.Period })
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.Id, r => AssetMath.MonthEnd(r.Period));
        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.LegalEntityId == query.LegalEntityId && a.Status != AssetStatus.Draft && a.AcquisitionDate <= query.AsOf)
            .ToListAsync(cancellationToken);
        var lines = assets
            .Where(a => a.DisposalDate is null || a.DisposalDate > query.AsOf)
            .Select(a =>
            {
                var cost = a.AcquisitionCost + a.History.Where(h => h.Kind == AssetEventKinds.Revalued && h.Date <= query.AsOf).Sum(h => h.Amount ?? 0);
                var accumulated = a.Schedule
                    .Where(s => s.RunId is { } run && runEnds.TryGetValue(run, out var end) && end <= query.AsOf)
                    .Sum(s => s.Amount);
                return new RegisterLineDto(a.Id, a.Number, a.NameEn, a.CategoryId, categories.GetValueOrDefault(a.CategoryId, "?"), a.Status.ToString(),
                    a.InServiceDate, cost, accumulated, cost - accumulated);
            })
            .OrderBy(l => l.Number)
            .ToList();
        var byCategory = lines.GroupBy(l => (l.CategoryId, l.CategoryCode))
            .Select(g => new RegisterCategoryDto(g.Key.CategoryId, g.Key.CategoryCode, g.Sum(l => l.Cost), g.Sum(l => l.AccumulatedDepreciation), g.Sum(l => l.NetBookValue)))
            .OrderBy(c => c.CategoryCode)
            .ToList();
        return new RegisterDto(query.LegalEntityId, query.AsOf, lines, byCategory, lines.Sum(l => l.Cost), lines.Sum(l => l.AccumulatedDepreciation),
            lines.Sum(l => l.NetBookValue));
    }
}
