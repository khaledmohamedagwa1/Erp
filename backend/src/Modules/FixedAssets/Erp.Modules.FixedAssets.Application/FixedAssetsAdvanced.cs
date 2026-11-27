using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.FixedAssets.Contracts;
using Erp.Modules.FixedAssets.Domain;
using Erp.Modules.Procurement.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.FixedAssets.Application;

// ======================= Shared helpers =======================
internal static class ValueChangeJournal
{
    /// <summary>
    /// IAS 16 / IAS 36 lines for a value change. Increase: Dr FixedAssetCost / Cr ImpairmentLoss (reversal of earlier losses) and
    /// Cr AssetRevaluationReserve. Decrease: Dr AssetRevaluationReserve (up to the asset's reserve) and Dr ImpairmentLoss / Cr FixedAssetCost.
    /// </summary>
    public static void Add(AssetJournal journal, Asset asset, ValueChangePlan plan)
    {
        if (plan.Delta >= 0)
        {
            journal.Debit(PostingRoles.FixedAssetCost, null, plan.Delta, asset.PostingGroup, asset.Number)
                .Credit(PostingRoles.ImpairmentLoss, null, plan.ProfitDelta, asset.PostingGroup, $"Reversal of earlier write-down {asset.Number}")
                .Credit(PostingRoles.AssetRevaluationReserve, null, plan.ReserveDelta, asset.PostingGroup, $"Revaluation surplus {asset.Number}");
        }
        else
        {
            journal.Debit(PostingRoles.AssetRevaluationReserve, null, -plan.ReserveDelta, asset.PostingGroup, $"Revaluation surplus used {asset.Number}")
                .Debit(PostingRoles.ImpairmentLoss, null, -plan.ProfitDelta, asset.PostingGroup, $"Write-down {asset.Number}")
                .Credit(PostingRoles.FixedAssetCost, null, -plan.Delta, asset.PostingGroup, asset.Number);
        }
    }

    /// <summary>On (partial) disposal the realised revaluation surplus moves to retained earnings (IAS 16.41), never through profit or loss.</summary>
    public static void TransferReserve(AssetJournal journal, Asset asset, decimal reserve)
    {
        if (reserve > 0)
        {
            journal.Debit(PostingRoles.AssetRevaluationReserve, null, reserve, asset.PostingGroup, $"Realised revaluation surplus {asset.Number}")
                .Credit(PostingRoles.RetainedEarnings, null, reserve, asset.PostingGroup, $"Realised revaluation surplus {asset.Number}");
        }
    }
}

internal static class RegisterMath
{
    /// <summary>
    /// A schedule line counts once its run's journal (dated the run period's last day) is on or before the date, so a catch-up run lands
    /// where the ledger has it.
    /// </summary>
    public static async Task<Dictionary<Guid, DateOnly>> RunEndsAsync(IFixedAssetsDbContext db, Guid legalEntityId, CancellationToken ct) =>
        (await db.DepreciationRuns.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntityId && r.Status == DepreciationRunStatus.Posted)
            .Select(r => new { r.Id, r.Period })
            .ToListAsync(ct))
        .ToDictionary(r => r.Id, r => AssetMath.MonthEnd(r.Period));

    /// <summary>Posting-book cost and accumulated depreciation of an asset as of a date.</summary>
    public static (decimal Cost, decimal Accumulated) PostingValues(Asset asset, IReadOnlyDictionary<Guid, DateOnly> runEnds, DateOnly asOf)
    {
        var events = asset.History.Where(h => h.Date <= asOf).ToList();
        var cost = events.Sum(AssetEventKinds.CostEffect);
        var accumulated = asset.Schedule
            .Where(s => s.RunId is { } run && runEnds.TryGetValue(run, out var end) && end <= asOf)
            .Sum(s => s.Amount) + events.Sum(h => h.AccumulatedDelta ?? 0);
        return (cost, accumulated);
    }
}

/// <summary>Checks the purchase invoice an asset is capitalised from (through the Procurement contract).</summary>
public sealed class InvoiceLinkValidator(IFixedAssetsDbContext db, IPurchaseInvoiceQueries invoices, FixedAssetsOptions options)
{
    public async Task<Result> ValidateAsync(Asset asset, string sourceDocType, Guid invoiceId, Guid? lineId, CancellationToken ct)
    {
        if (!string.Equals(sourceDocType.Trim(), ProcurementDocumentTypes.PurchaseInvoice, StringComparison.OrdinalIgnoreCase))
        {
            return FixedAssetsAppErrors.SourceTypeUnsupported;
        }

        var invoice = await invoices.GetPurchaseInvoiceAsync(invoiceId, ct);
        if (invoice is null)
        {
            return FixedAssetsAppErrors.SourceInvoiceNotFound;
        }

        if (!invoice.IsPosted)
        {
            return FixedAssetsAppErrors.SourceInvoiceNotPosted;
        }

        if (invoice.LegalEntityId != asset.LegalEntityId)
        {
            return FixedAssetsAppErrors.SourceInvoiceOtherLegalEntity;
        }

        decimal available;
        if (lineId is { } id)
        {
            var line = invoice.Lines.FirstOrDefault(l => l.LineId == id);
            if (line is null)
            {
                return FixedAssetsAppErrors.SourceLineNotFound;
            }

            available = line.BaseNet;
        }
        else
        {
            available = invoice.Lines.Sum(l => l.BaseNet);
        }

        // Several assets may come from one line (e.g. three laptops): together they cannot exceed it.
        var alreadyCapitalised = await db.Assets.AsNoTracking()
            .Where(a => a.Id != asset.Id && a.Status != AssetStatus.Draft && a.SourceDocId == invoiceId && (lineId == null || a.SourceLineId == lineId))
            .SumAsync(a => (decimal?)a.AcquisitionCost, ct) ?? 0;
        return alreadyCapitalised + asset.AcquisitionCost > available + Math.Max(0, options.InvoiceMatchTolerance)
            ? FixedAssetsAppErrors.SourceAmountMismatch with
            {
                Message = $"Capitalised cost {alreadyCapitalised + asset.AcquisitionCost:0.00} exceeds the invoice amount {available:0.00} (tolerance {options.InvoiceMatchTolerance:0.00}).",
            }
            : Result.Success();
    }
}

// ======================= Impairment =======================
/// <summary>
/// Writes an asset down to its recoverable amount (IAS 36). For a revalued asset the loss first uses up its revaluation reserve;
/// the rest is Dr ImpairmentLoss. Cr FixedAssetCost (carrying amount). A later upward revaluation reverses the loss first.
/// </summary>
public sealed record ImpairAssetCommand(Guid Id, DateOnly Date, decimal RecoverableAmount, string? Reason) : ICommand<AssetDto>;

public sealed class ImpairAssetValidator : AbstractValidator<ImpairAssetCommand>
{
    public ImpairAssetValidator()
    {
        RuleFor(x => x.RecoverableAmount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class ImpairAssetHandler(IFixedAssetsDbContext db, AssetScope scope, IJournalPostingService journals, IClock clock)
    : ICommandHandler<ImpairAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(ImpairAssetCommand command, CancellationToken cancellationToken)
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

        var plan = asset.PlanValueChange(command.RecoverableAmount, null, impairment: true);
        var journal = new AssetJournal();
        ValueChangeJournal.Add(journal, asset, plan);
        var eventId = SequentialGuid.New();
        var posted = await journals.PostAsync(new JournalRequest(asset.LegalEntityId, command.Date, SourceModules.FixedAssets, FixedAssetDocumentTypes.Impairment,
            eventId, "impair", string.IsNullOrWhiteSpace(command.Reason) ? $"Impairment of asset {asset.Number}" : $"Impairment of asset {asset.Number}: {command.Reason.Trim()}",
            le.Value.FunctionalCurrency, 1m, journal.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        asset.Impair(command.Date, command.RecoverableAmount, plan, posted.Value.JournalId, eventId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

// ======================= Partial disposal =======================
/// <summary>
/// Disposes of part of an asset, by quantity (of <see cref="Asset.Quantity"/>) or percentage. Cost, accumulated depreciation, salvage,
/// revaluation reserve and additional books shrink proportionally; the remaining life is unchanged.
/// Dr AccumulatedDepreciation (part) + Dr proceeds / Cr FixedAssetCost (part), difference to AssetDisposalGainLoss; the reserve part moves to retained earnings.
/// </summary>
public sealed record PartialDisposeAssetCommand(Guid Id, DateOnly Date, decimal? Quantity, decimal? Percent, decimal Proceeds, Guid? ProceedsAccountId)
    : ICommand<AssetDto>;

public sealed class PartialDisposeAssetValidator : AbstractValidator<PartialDisposeAssetCommand>
{
    public PartialDisposeAssetValidator()
    {
        RuleFor(x => x.Proceeds).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Quantity).GreaterThan(0).When(x => x.Quantity is not null);
        RuleFor(x => x.Percent).ExclusiveBetween(0, 100).When(x => x.Percent is not null);
    }
}

public sealed class PartialDisposeAssetHandler(IFixedAssetsDbContext db, AssetScope scope, IJournalPostingService journals, IClock clock)
    : ICommandHandler<PartialDisposeAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(PartialDisposeAssetCommand command, CancellationToken cancellationToken)
    {
        if ((command.Quantity is null) == (command.Percent is null))
        {
            return FixedAssetsAppErrors.PartialQuantityOrPercent;
        }

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

        var fraction = command.Quantity is { } quantity ? quantity / asset.Quantity : command.Percent!.Value / 100m;
        var plan = asset.PlanPartialDisposal(command.Date, fraction, command.Proceeds);
        var journal = new AssetJournal()
            .Debit(PostingRoles.AccumulatedDepreciation, null, plan.Accumulated, asset.PostingGroup, asset.Number)
            .Debit(null, command.ProceedsAccountId, plan.Proceeds, description: asset.Number)
            .Credit(PostingRoles.FixedAssetCost, null, plan.Cost, asset.PostingGroup, asset.Number);
        if (plan.GainLoss > 0)
        {
            journal.Credit(PostingRoles.AssetDisposalGainLoss, null, plan.GainLoss, asset.PostingGroup, "Gain on partial disposal");
        }
        else
        {
            journal.Debit(PostingRoles.AssetDisposalGainLoss, null, -plan.GainLoss, asset.PostingGroup, "Loss on partial disposal");
        }

        ValueChangeJournal.TransferReserve(journal, asset, plan.Reserve);
        var eventId = SequentialGuid.New();
        var posted = await journals.PostAsync(new JournalRequest(asset.LegalEntityId, command.Date, SourceModules.FixedAssets, FixedAssetDocumentTypes.PartialDisposal,
            eventId, "dispose-partial", $"Partial disposal of asset {asset.Number} ({fraction:P2})", le.Value.FunctionalCurrency, 1m, journal.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        asset.DisposePartially(command.Date, plan, posted.Value.JournalId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return AssetMapping.ToDto(asset);
    }
}

// ======================= Transfer between legal entities =======================
public sealed record LegalEntityTransferDto(AssetDto Source, AssetDto Target);

/// <summary>
/// Moves an asset to another legal entity of the same company as a disposal at net book value in the sender and an acquisition in the
/// receiver (cost and accumulated depreciation carried over, remaining life kept). Both journals go through the chosen clearing account
/// (an inter-company account), so each legal entity's ledger stays balanced. Depreciation must be posted up to the month before the transfer.
/// </summary>
public sealed record TransferToLegalEntityCommand(Guid Id, DateOnly Date, Guid TargetLegalEntityId, Guid ClearingAccountId, Guid? TargetBranchId)
    : ICommand<LegalEntityTransferDto>;

public sealed class TransferToLegalEntityHandler(
    IFixedAssetsDbContext db,
    AssetScope scope,
    IJournalPostingService journals,
    IDocumentNumberGenerator numbers,
    IClock clock) : ICommandHandler<TransferToLegalEntityCommand, LegalEntityTransferDto>
{
    public async Task<Result<LegalEntityTransferDto>> Handle(TransferToLegalEntityCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        if (asset.LegalEntityId == command.TargetLegalEntityId)
        {
            return FixedAssetsAppErrors.SameLegalEntity;
        }

        var source = await scope.LegalEntityAsync(asset.LegalEntityId, cancellationToken);
        if (source.IsFailure)
        {
            return source.Error;
        }

        var target = await scope.LegalEntityAsync(command.TargetLegalEntityId, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        if (!string.Equals(source.Value.FunctionalCurrency, target.Value.FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return FixedAssetsAppErrors.TransferCurrencyMismatch;
        }

        var valid = await scope.ValidateAccountAsync(command.ClearingAccountId, cancellationToken);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        asset.DisposalResult(command.Date, asset.NetBookValue);
        if (asset.RevaluationReserve != 0)
        {
            return FixedAssetsAppErrors.TransferWithReserve;
        }

        var cost = asset.Cost;
        var accumulated = asset.AccumulatedDepreciation;
        var nbv = asset.NetBookValue;
        var outgoing = new AssetJournal()
            .Debit(PostingRoles.AccumulatedDepreciation, null, accumulated, asset.PostingGroup, asset.Number)
            .Debit(null, command.ClearingAccountId, nbv, description: $"Transfer of {asset.Number}")
            .Credit(PostingRoles.FixedAssetCost, null, cost, asset.PostingGroup, asset.Number);
        var sent = await journals.PostAsync(new JournalRequest(asset.LegalEntityId, command.Date, SourceModules.FixedAssets, FixedAssetDocumentTypes.Transfer,
            asset.Id, "transfer-out", $"Transfer of asset {asset.Number} to another legal entity", source.Value.FunctionalCurrency, 1m, outgoing.Lines), cancellationToken);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        var number = await numbers.NextAsync(new NumberScope(asset.CompanyId, "FixedAsset", "FA"), cancellationToken);
        var targetId = SequentialGuid.New();
        var incoming = new AssetJournal()
            .Debit(PostingRoles.FixedAssetCost, null, cost, asset.PostingGroup, number)
            .Credit(PostingRoles.AccumulatedDepreciation, null, accumulated, asset.PostingGroup, number)
            .Credit(null, command.ClearingAccountId, nbv, description: $"Transfer of {asset.Number}");
        var received = await journals.PostAsync(new JournalRequest(command.TargetLegalEntityId, command.Date, SourceModules.FixedAssets, FixedAssetDocumentTypes.Transfer,
            targetId, "transfer-in", $"Asset {asset.Number} received from another legal entity", target.Value.FunctionalCurrency, 1m, incoming.Lines), cancellationToken);
        if (received.IsFailure)
        {
            return received.Error;
        }

        var now = clock.UtcNow;
        var created = Asset.CreateTransferredIn(targetId, asset, command.TargetLegalEntityId, command.TargetBranchId, command.Date, number, received.Value.JournalId, now);
        asset.TransferOut(command.Date, sent.Value.JournalId, targetId, number, now);
        db.Assets.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        return new LegalEntityTransferDto(AssetMapping.ToDto(asset), AssetMapping.ToDto(created));
    }
}

// ======================= Additional depreciation books =======================
public sealed record BookDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive);

public sealed record BookLineDto(DateOnly Period, decimal Amount, decimal Accumulated, decimal NetBookValue);

public sealed record AssetBookDto(Guid BookId, string BookCode, string Method, int UsefulLifeMonths, decimal SalvageValue, decimal? DecliningRatePercent, decimal Cost,
    DateOnly StartMonth, decimal AccumulatedToDate, decimal NetBookValueToDate, IReadOnlyList<BookLineDto> Schedule);

public sealed record CreateBookCommand(string Code, string NameEn, string NameAr) : ICommand<BookDto>;

public sealed record UpdateBookCommand(Guid Id, string NameEn, string NameAr, bool IsActive) : ICommand<BookDto>;

public sealed record ListBooksQuery : IQuery<IReadOnlyList<BookDto>>;

public sealed class CreateBookValidator : AbstractValidator<CreateBookCommand>
{
    public CreateBookValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateBookHandler(IFixedAssetsDbContext db, ITenantContext tenant) : ICommandHandler<CreateBookCommand, BookDto>
{
    public async Task<Result<BookDto>> Handle(CreateBookCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Books.AnyAsync(b => b.Code == code, cancellationToken))
        {
            return FixedAssetsAppErrors.BookCodeTaken;
        }

        var book = DepreciationBook.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr);
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        return BookMapping.ToDto(book);
    }
}

public sealed class UpdateBookHandler(IFixedAssetsDbContext db) : ICommandHandler<UpdateBookCommand, BookDto>
{
    public async Task<Result<BookDto>> Handle(UpdateBookCommand command, CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == command.Id, cancellationToken);
        if (book is null)
        {
            return FixedAssetsAppErrors.BookNotFound;
        }

        book.Update(command.NameEn, command.NameAr, command.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return BookMapping.ToDto(book);
    }
}

public sealed class ListBooksHandler(IFixedAssetsDbContext db) : IQueryHandler<ListBooksQuery, IReadOnlyList<BookDto>>
{
    public async Task<Result<IReadOnlyList<BookDto>>> Handle(ListBooksQuery query, CancellationToken cancellationToken) =>
        (await db.Books.AsNoTracking().OrderBy(b => b.Code).ToListAsync(cancellationToken)).Select(BookMapping.ToDto).ToList();
}

internal static class BookMapping
{
    public static BookDto ToDto(DepreciationBook b) => new(b.Id, b.Code, b.NameEn, b.NameAr, b.IsActive);

    public static AssetBookDto ToDto(AssetBook b, string code, DateOnly today)
    {
        var lines = b.Schedule.OrderBy(l => l.Period)
            .Select(line =>
            {
                var monthEnd = AssetMath.MonthEnd(line.Period);
                var accumulated = b.AccumulatedAsOf(monthEnd);
                return new BookLineDto(line.Period, line.Amount, accumulated, b.CostAsOf(monthEnd) - accumulated);
            })
            .ToList();

        var toDate = b.AccumulatedAsOf(today);
        return new AssetBookDto(b.BookId, code, b.Method.ToString(), b.UsefulLifeMonths, b.SalvageValue, b.DecliningRatePercent, b.Cost, b.StartMonth, toDate,
            b.CostAsOf(today) - toDate, lines);
    }
}

/// <summary>Defines (or redefines) the asset's figures in an additional book. Cost defaults to the acquisition cost; the life counts from the first depreciation month.</summary>
public sealed record SetAssetBookCommand(Guid AssetId, Guid BookId, string Method, int UsefulLifeMonths, decimal? SalvageValue, decimal? DecliningRatePercent,
    decimal? Cost, decimal? OpeningAccumulated) : ICommand<IReadOnlyList<AssetBookDto>>;

public sealed class SetAssetBookValidator : AbstractValidator<SetAssetBookCommand>
{
    public SetAssetBookValidator()
    {
        RuleFor(x => x.Method).NotEmpty().Must(m => m is not null && Enum.TryParse<DepreciationMethod>(m, true, out var method) && method != DepreciationMethod.UnitsOfProduction)
            .WithMessage("Method must be StraightLine or DecliningBalance.");
        RuleFor(x => x.UsefulLifeMonths).InclusiveBetween(1, 1200);
    }
}

public sealed class SetAssetBookHandler(IFixedAssetsDbContext db, IClock clock) : ICommandHandler<SetAssetBookCommand, IReadOnlyList<AssetBookDto>>
{
    public async Task<Result<IReadOnlyList<AssetBookDto>>> Handle(SetAssetBookCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.AssetId, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == command.BookId && b.IsActive, cancellationToken);
        if (book is null)
        {
            return FixedAssetsAppErrors.BookNotFound;
        }

        asset.SetBook(book.Id, Enum.Parse<DepreciationMethod>(command.Method, true), command.UsefulLifeMonths, command.SalvageValue, command.DecliningRatePercent,
            command.Cost, command.OpeningAccumulated);
        await db.SaveChangesAsync(cancellationToken);
        return await AssetBooksQueryHandler.MapAsync(db, asset, DateOnly.FromDateTime(clock.UtcNow), cancellationToken);
    }
}

public sealed record RemoveAssetBookCommand(Guid AssetId, Guid BookId) : ICommand<Unit>;

public sealed class RemoveAssetBookHandler(IFixedAssetsDbContext db) : ICommandHandler<RemoveAssetBookCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RemoveAssetBookCommand command, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == command.AssetId, cancellationToken);
        if (asset is null)
        {
            return FixedAssetsAppErrors.AssetNotFound;
        }

        asset.RemoveBook(command.BookId);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

public sealed record AssetBooksQuery(Guid AssetId) : IQuery<IReadOnlyList<AssetBookDto>>;

public sealed class AssetBooksQueryHandler(IFixedAssetsDbContext db, IClock clock) : IQueryHandler<AssetBooksQuery, IReadOnlyList<AssetBookDto>>
{
    public async Task<Result<IReadOnlyList<AssetBookDto>>> Handle(AssetBooksQuery query, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.AssetId, cancellationToken);
        return asset is null ? FixedAssetsAppErrors.AssetNotFound : await MapAsync(db, asset, DateOnly.FromDateTime(clock.UtcNow), cancellationToken);
    }

    internal static async Task<Result<IReadOnlyList<AssetBookDto>>> MapAsync(IFixedAssetsDbContext db, Asset asset, DateOnly today, CancellationToken ct)
    {
        var codes = await db.Books.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        return asset.Books.Select(b => BookMapping.ToDto(b, codes.GetValueOrDefault(b.BookId, "?"), today)).OrderBy(b => b.BookCode).ToList();
    }
}

public sealed record BookRegisterLineDto(Guid AssetId, string? Number, string NameEn, decimal BookCost, decimal BookAccumulated, decimal BookNetBookValue,
    decimal PostingNetBookValue, decimal Difference);

public sealed record BookRegisterDto(Guid LegalEntityId, Guid BookId, string BookCode, DateOnly AsOf, IReadOnlyList<BookRegisterLineDto> Assets, int AssetsWithoutBook,
    decimal TotalBookCost, decimal TotalBookAccumulated, decimal TotalBookNetBookValue, decimal TotalPostingNetBookValue, decimal TotalDifference);

/// <summary>
/// Register in an additional book as of a date, next to the posting book's net book value. The difference is the temporary difference
/// (e.g. for deferred tax when the book is the tax book). Assets disposed on or before the date and drafts are excluded.
/// </summary>
public sealed record BookRegisterQuery(Guid LegalEntityId, Guid BookId, DateOnly AsOf) : IQuery<BookRegisterDto>;

public sealed class BookRegisterHandler(IFixedAssetsDbContext db, AssetScope scope) : IQueryHandler<BookRegisterQuery, BookRegisterDto>
{
    public async Task<Result<BookRegisterDto>> Handle(BookRegisterQuery query, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(query.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == query.BookId, cancellationToken);
        if (book is null)
        {
            return FixedAssetsAppErrors.BookNotFound;
        }

        var runEnds = await RegisterMath.RunEndsAsync(db, query.LegalEntityId, cancellationToken);
        var assets = (await db.Assets.AsNoTracking()
                .Where(a => a.LegalEntityId == query.LegalEntityId && a.Status != AssetStatus.Draft && a.AcquisitionDate <= query.AsOf)
                .ToListAsync(cancellationToken))
            .Where(a => a.DisposalDate is null || a.DisposalDate > query.AsOf)
            .ToList();
        var lines = new List<BookRegisterLineDto>();
        foreach (var asset in assets)
        {
            if (asset.Books.FirstOrDefault(b => b.BookId == book.Id) is not { } assetBook)
            {
                continue;
            }

            var bookCost = assetBook.CostAsOf(query.AsOf);
            var bookAccumulated = assetBook.AccumulatedAsOf(query.AsOf);
            var (cost, accumulated) = RegisterMath.PostingValues(asset, runEnds, query.AsOf);
            var bookNbv = bookCost - bookAccumulated;
            lines.Add(new BookRegisterLineDto(asset.Id, asset.Number, asset.NameEn, bookCost, bookAccumulated, bookNbv, cost - accumulated, bookNbv - (cost - accumulated)));
        }

        lines = lines.OrderBy(l => l.Number).ToList();
        return new BookRegisterDto(query.LegalEntityId, book.Id, book.Code, query.AsOf, lines, assets.Count - lines.Count, lines.Sum(l => l.BookCost),
            lines.Sum(l => l.BookAccumulated), lines.Sum(l => l.BookNetBookValue), lines.Sum(l => l.PostingNetBookValue), lines.Sum(l => l.Difference));
    }
}

// ======================= Settings and scheduled depreciation =======================
public sealed record AssetSettingsDto(bool AutoDepreciationEnabled, int RunOnDay, DateTime? LastRunAtUtc, string? LastRunSummary);

public sealed record GetAssetSettingsQuery : IQuery<AssetSettingsDto>;

public sealed record UpdateAssetSettingsCommand(bool AutoDepreciationEnabled, int RunOnDay) : ICommand<AssetSettingsDto>;

public sealed record RecordScheduledRunCommand(string Summary) : ICommand<Unit>;

public sealed class GetAssetSettingsHandler(IFixedAssetsDbContext db) : IQueryHandler<GetAssetSettingsQuery, AssetSettingsDto>
{
    public async Task<Result<AssetSettingsDto>> Handle(GetAssetSettingsQuery query, CancellationToken cancellationToken)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings is null ? new AssetSettingsDto(false, 1, null, null) : SettingsMapping.ToDto(settings);
    }
}

public sealed class UpdateAssetSettingsHandler(IFixedAssetsDbContext db, ITenantContext tenant) : ICommandHandler<UpdateAssetSettingsCommand, AssetSettingsDto>
{
    public async Task<Result<AssetSettingsDto>> Handle(UpdateAssetSettingsCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = AssetSettings.Create(tenant.CompanyId!.Value);
            db.Settings.Add(settings);
        }

        settings.Update(command.AutoDepreciationEnabled, command.RunOnDay);
        await db.SaveChangesAsync(cancellationToken);
        return SettingsMapping.ToDto(settings);
    }
}

public sealed class RecordScheduledRunHandler(IFixedAssetsDbContext db, IClock clock) : ICommandHandler<RecordScheduledRunCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RecordScheduledRunCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        settings?.RecordRun(clock.UtcNow, command.Summary);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

internal static class SettingsMapping
{
    public static AssetSettingsDto ToDto(AssetSettings s) => new(s.AutoDepreciationEnabled, s.RunOnDay, s.LastRunAtUtc, s.LastRunSummary);
}

public sealed record ScheduledRunLineDto(Guid LegalEntityId, string Outcome, Guid? RunId, decimal? Total, string? ErrorCode);

public sealed record ScheduledRunResultDto(DateOnly Today, DateOnly? Period, string? Skipped, IReadOnlyList<ScheduledRunLineDto> LegalEntities);

/// <summary>
/// Automatic depreciation for the active company: once the run day of the month is reached, posts last month's run for every legal entity
/// with active assets (each in its own transaction, idempotent — an existing run is left alone). Used by the worker and by the "run now" endpoint.
/// </summary>
public sealed class ScheduledDepreciationRunner(IDispatcher dispatcher, IFixedAssetsDbContext db)
{
    public async Task<ScheduledRunResultDto> RunAsync(DateOnly today, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null || !settings.AutoDepreciationEnabled)
        {
            return new ScheduledRunResultDto(today, null, "Automatic depreciation is off.", []);
        }

        if (settings.DuePeriod(today) is not { } period)
        {
            return new ScheduledRunResultDto(today, null, $"Not due before day {settings.RunOnDay} of the month.", []);
        }

        var legalEntities = await db.Assets.AsNoTracking().Where(a => a.Status == AssetStatus.Active).Select(a => a.LegalEntityId).Distinct().ToListAsync(ct);
        var lines = new List<ScheduledRunLineDto>();
        foreach (var legalEntityId in legalEntities.Order())
        {
            var result = await dispatcher.Send(new PostRunCommand(legalEntityId, period), ct);
            lines.Add(result.IsSuccess
                ? new ScheduledRunLineDto(legalEntityId, result.Value.AlreadyPosted ? "AlreadyPosted" : "Posted", result.Value.Id, result.Value.Total, null)
                : new ScheduledRunLineDto(legalEntityId, result.Error.Code == FixedAssetsAppErrors.NothingToDepreciate.Code ? "NothingToDepreciate" : "Failed", null, null,
                    result.Error.Code));
        }

        var summary = $"{period:yyyy-MM}: " + (lines.Count == 0 ? "no active assets" : string.Join("; ", lines.Select(l => $"{l.LegalEntityId}={l.Outcome}{(l.ErrorCode is null ? string.Empty : $" ({l.ErrorCode})")}")));
        await dispatcher.Send(new RecordScheduledRunCommand(summary), ct);
        return new ScheduledRunResultDto(today, period, null, lines);
    }

    /// <summary>Companies of the current tenant that switched automatic depreciation on (for the worker, which has no active company).</summary>
    public static async Task<IReadOnlyList<Guid>> CompaniesWithAutoDepreciationAsync(IFixedAssetsDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => EF.Property<Guid>(s, "TenantId") == tenantId && s.AutoDepreciationEnabled)
            .Select(s => s.CompanyId)
            .ToListAsync(ct);
}
