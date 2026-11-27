using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Application;

// ---------------- DTOs ----------------
public sealed record StockLineInput(
    Guid? LineId,
    Guid ProductId,
    Guid? VariantId,
    Guid UomId,
    decimal Quantity,
    decimal? UnitCost,
    string? BatchNumber,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    IReadOnlyList<string>? Serials,
    Guid? BinId = null,
    Guid? ToBinId = null);

public sealed record StockLineDto(
    Guid LineId,
    Guid ProductId,
    Guid? VariantId,
    Guid UomId,
    decimal Quantity,
    decimal? UnitCost,
    string? BatchNumber,
    DateOnly? ExpiryDate,
    IReadOnlyList<string> Serials,
    decimal? PostedBaseQuantity,
    decimal? PostedUnitCost,
    decimal? PostedTotalCost,
    decimal ReceivedBaseQuantity,
    decimal LostBaseQuantity);

public sealed record OpeningStockDto(Guid Id, string Number, Guid LegalEntityId, Guid WarehouseId, DateOnly Date, string Status, decimal TotalCost, Guid? JournalId, IReadOnlyList<StockLineDto> Lines);

public sealed record AdjustmentDto(Guid Id, string Number, Guid LegalEntityId, Guid WarehouseId, DateOnly Date, string Reason, string? Notes, string Status, decimal NetCost, Guid? JournalId, Guid? StockCountId, IReadOnlyList<StockLineDto> Lines);

public sealed record TransferDto(Guid Id, string Number, Guid LegalEntityId, Guid FromWarehouseId, Guid ToWarehouseId, Guid TransitWarehouseId, DateOnly Date, string? Notes, string Status, int ReceiptCount, IReadOnlyList<StockLineDto> Lines, bool Direct);

public sealed record CountLineDto(Guid LineId, Guid ProductId, Guid? VariantId, Guid? BatchId, string? BatchNumber, decimal SnapshotQuantity, decimal? CountedQuantity, decimal MovementsSinceSnapshot, decimal Variance, string? Serial);

public sealed record CountDto(Guid Id, string Number, Guid LegalEntityId, Guid WarehouseId, DateOnly Date, string Status, DateTime? SnapshotAtUtc, Guid? AdjustmentId, IReadOnlyList<CountLineDto> Lines);

/// <summary>Stock figures in base units; see <see cref="StockInquiryHandler"/> for how incoming/outgoing/projected are derived.</summary>
public sealed record StockRowDto(Guid ProductId, Guid? VariantId, Guid WarehouseId, decimal OnHand, decimal Reserved, decimal Available, decimal Incoming, decimal Outgoing, decimal Projected);

public sealed record MovementDto(
    Guid Id, DateOnly TransactionDate, string MovementType, Guid ProductId, Guid? VariantId, Guid WarehouseId, Guid? BatchId, Guid? SerialId,
    decimal BaseQuantity, decimal UnitCost, decimal TotalCost, string SourceModule, string SourceDocType, Guid SourceDocId, string? ReferenceNo,
    Guid? ReversalOfId, long PostingSequence, DateTime CreatedAtUtc);

public sealed record ValuationDto(Guid ValuationAreaId, Guid ProductId, Guid? VariantId, string CostingMethod, decimal Quantity, decimal Value, decimal AverageCost, decimal LastCost);

public sealed record BatchDto(Guid Id, Guid ProductId, string Number, DateOnly? ManufactureDate, DateOnly? ExpiryDate, Guid? SupplierId);

public sealed record SerialDto(Guid Id, Guid ProductId, string Serial, Guid? CurrentWarehouseId, string Status, Guid? LastMovementId);

internal static class InventoryMapping
{
    public static StockDocumentLine ToLine(StockLineInput i) => new()
    {
        LineId = i.LineId ?? Guid.Empty,
        ProductId = i.ProductId,
        VariantId = i.VariantId,
        UomId = i.UomId,
        Quantity = i.Quantity,
        UnitCost = i.UnitCost,
        BatchNumber = string.IsNullOrWhiteSpace(i.BatchNumber) ? null : i.BatchNumber.Trim().ToUpperInvariant(),
        ManufactureDate = i.ManufactureDate,
        ExpiryDate = i.ExpiryDate,
        Serials = i.Serials?.ToList() ?? [],
        BinId = i.BinId,
        ToBinId = i.ToBinId,
    };

    public static StockLineDto ToDto(StockDocumentLine l) => new(
        l.LineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitCost, l.BatchNumber, l.ExpiryDate, l.Serials,
        l.PostedBaseQuantity, l.PostedUnitCost, l.PostedTotalCost, l.ReceivedBaseQuantity, l.LostBaseQuantity);

    public static OpeningStockDto ToDto(OpeningStockDocument d) => new(d.Id, d.Number, d.LegalEntityId, d.WarehouseId, d.Date, d.Status.ToString(), d.TotalCost, d.JournalId, d.Lines.Select(ToDto).ToList());

    public static AdjustmentDto ToDto(StockAdjustment d) => new(d.Id, d.Number, d.LegalEntityId, d.WarehouseId, d.Date, d.Reason.ToString(), d.Notes, d.Status.ToString(), d.NetCost, d.JournalId, d.StockCountId, d.Lines.Select(ToDto).ToList());

    public static TransferDto ToDto(StockTransfer d) => new(d.Id, d.Number, d.LegalEntityId, d.FromWarehouseId, d.ToWarehouseId, d.TransitWarehouseId, d.Date, d.Notes, d.Status.ToString(), d.ReceiptCount, d.Lines.Select(ToDto).ToList(), d.Direct);

    public static CountDto ToDto(StockCount d) => new(d.Id, d.Number, d.LegalEntityId, d.WarehouseId, d.Date, d.Status.ToString(), d.SnapshotAtUtc, d.AdjustmentId,
        d.Lines.Select(l => new CountLineDto(l.LineId, l.ProductId, l.VariantId, l.BatchId, l.BatchNumber, l.SnapshotQuantity, l.CountedQuantity, l.MovementsSinceSnapshot, l.Variance, l.Serial)).ToList());

    public static BatchInput? Batch(StockDocumentLine l) =>
        l.BatchNumber is null ? null : new BatchInput(l.BatchNumber, l.ManufactureDate, l.ExpiryDate);

    public static IReadOnlyList<string>? Serials(StockDocumentLine l) => l.Serials.Count == 0 ? null : l.Serials;
}

public sealed class StockLineInputValidator : AbstractValidator<StockLineInput>
{
    public StockLineInputValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.UomId).NotEmpty();
        RuleFor(x => x.Quantity).NotEqual(0);
        RuleFor(x => x.BatchNumber).MaximumLength(50);
    }
}

/// <summary>Shared helpers for inventory's own documents: numbering and warehouse/legal-entity resolution.</summary>
public sealed class InventoryDocumentSupport(IDocumentNumberGenerator numbers, PostingContextResolver contexts)
{
    public Task<string> NextNumberAsync(Guid companyId, Guid legalEntityId, string documentType, string prefix, DateOnly date, CancellationToken ct) =>
        numbers.NextAsync(new NumberScope(companyId, documentType, prefix, legalEntityId, FiscalYear: date.Year), ct);

    public Task<Result<PostingContextResolver.Resolved>> ResolveAsync(IEnumerable<Guid> warehouseIds, CancellationToken ct) =>
        contexts.ResolveAsync(warehouseIds, null, ct);
}

public static class SourceDocTypes
{
    public const string OpeningStock = "OpeningStock";
    public const string StockAdjustment = "StockAdjustment";
    public const string StockTransferIssue = "StockTransferIssue";
    public const string StockTransferIssueTransit = "StockTransferIssueTransit";
    public const string StockTransferReceiptOut = "StockTransferReceiptOut";
    public const string StockTransferReceipt = "StockTransferReceipt";
    public const string StockTransferClose = "StockTransferClose";
}

// ---------------- Opening stock ----------------
public sealed record CreateOpeningStockCommand(Guid WarehouseId, DateOnly Date, IReadOnlyList<StockLineInput> Lines) : ICommand<OpeningStockDto>;

public sealed class CreateOpeningStockValidator : AbstractValidator<CreateOpeningStockCommand>
{
    public CreateOpeningStockValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new StockLineInputValidator());
    }
}

public sealed class CreateOpeningStockHandler(IInventoryDbContext db, InventoryDocumentSupport support) : ICommandHandler<CreateOpeningStockCommand, OpeningStockDto>
{
    public async Task<Result<OpeningStockDto>> Handle(CreateOpeningStockCommand command, CancellationToken cancellationToken)
    {
        var ctx = await support.ResolveAsync([command.WarehouseId], cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        var number = await support.NextNumberAsync(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, SourceDocTypes.OpeningStock, "OS", command.Date, cancellationToken);
        var doc = new OpeningStockDocument(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, command.WarehouseId, number, command.Date, command.Lines.Select(InventoryMapping.ToLine));
        db.OpeningStocks.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record UpdateOpeningStockLinesCommand(Guid Id, IReadOnlyList<StockLineInput> Lines) : ICommand<OpeningStockDto>;

public sealed class UpdateOpeningStockLinesHandler(IInventoryDbContext db) : ICommandHandler<UpdateOpeningStockLinesCommand, OpeningStockDto>
{
    public async Task<Result<OpeningStockDto>> Handle(UpdateOpeningStockLinesCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.OpeningStocks.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        doc.SetLines(command.Lines.Select(InventoryMapping.ToLine));
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public enum DocumentAction
{
    Submit,
    ReturnToDraft,
    Approve,
    Cancel,
}

public sealed record OpeningStockActionCommand(Guid Id, DocumentAction Action) : ICommand<OpeningStockDto>;

public sealed class OpeningStockActionHandler(IInventoryDbContext db) : ICommandHandler<OpeningStockActionCommand, OpeningStockDto>
{
    public async Task<Result<OpeningStockDto>> Handle(OpeningStockActionCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.OpeningStocks.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        switch (command.Action)
        {
            case DocumentAction.Submit: doc.Submit(); break;
            case DocumentAction.ReturnToDraft: doc.ReturnToDraft(); break;
            case DocumentAction.Approve: doc.Approve(); break;
            case DocumentAction.Cancel: doc.Cancel(); break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record PostOpeningStockCommand(Guid Id) : ICommand<OpeningStockDto>;

public sealed class PostOpeningStockHandler(IInventoryDbContext db, IInventoryPostingService posting) : ICommandHandler<PostOpeningStockCommand, OpeningStockDto>
{
    public async Task<Result<OpeningStockDto>> Handle(PostOpeningStockCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.OpeningStocks.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        if (doc.Status != OpeningStockStatus.Approved)
        {
            return InventoryErrors.InvalidTransition;
        }

        var result = await posting.PostAsync(new InventoryPostingRequest(
            doc.LegalEntityId, doc.Date, SourceModules.Inventory, SourceDocTypes.OpeningStock, doc.Id, doc.Number,
            doc.Lines.Select(l => new InventoryPostingLine(l.LineId, l.ProductId, l.VariantId, doc.WarehouseId, l.UomId, l.Quantity,
                InventoryMovementTypes.OpeningStock, l.UnitCost, InventoryMapping.Batch(l), InventoryMapping.Serials(l),
                CounterRole: PostingRoles.OpeningBalanceEquity, BinId: l.BinId)).ToList(),
            PostJournal: true, JournalDescription: $"Opening stock {doc.Number}"), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        foreach (var line in doc.Lines)
        {
            var r = result.Value.Lines.First(x => x.LineId == line.LineId);
            line.PostedBaseQuantity = r.BaseQuantity;
            line.PostedUnitCost = r.UnitCost;
            line.PostedTotalCost = r.TotalCost;
        }

        doc.MarkPosted(result.Value.TotalCost, result.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record GetOpeningStockQuery(Guid Id) : IQuery<OpeningStockDto>;

public sealed class GetOpeningStockHandler(IInventoryDbContext db) : IQueryHandler<GetOpeningStockQuery, OpeningStockDto>
{
    public async Task<Result<OpeningStockDto>> Handle(GetOpeningStockQuery query, CancellationToken cancellationToken)
    {
        var doc = await db.OpeningStocks.AsNoTracking().FirstOrDefaultAsync(d => d.Id == query.Id, cancellationToken);
        return doc is null ? InventoryErrors.NotFound : InventoryMapping.ToDto(doc);
    }
}

public sealed record ListOpeningStocksQuery(PageRequest Page) : IQuery<PagedResult<OpeningStockDto>>;

public sealed class ListOpeningStocksHandler(IInventoryDbContext db) : IQueryHandler<ListOpeningStocksQuery, PagedResult<OpeningStockDto>>
{
    public async Task<Result<PagedResult<OpeningStockDto>>> Handle(ListOpeningStocksQuery query, CancellationToken cancellationToken)
    {
        var total = await db.OpeningStocks.CountAsync(cancellationToken);
        var docs = await db.OpeningStocks.AsNoTracking().OrderByDescending(d => d.Date).ThenByDescending(d => d.Number)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<OpeningStockDto>(docs.Select(InventoryMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ---------------- Adjustments ----------------
public sealed record CreateAdjustmentCommand(Guid WarehouseId, DateOnly Date, string Reason, string? Notes, IReadOnlyList<StockLineInput> Lines) : ICommand<AdjustmentDto>;

public sealed class CreateAdjustmentValidator : AbstractValidator<CreateAdjustmentCommand>
{
    public CreateAdjustmentValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Reason).Must(r => Enum.TryParse<AdjustmentReason>(r, true, out _)).WithMessage("Unknown reason.");
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new StockLineInputValidator());
    }
}

public sealed class CreateAdjustmentHandler(IInventoryDbContext db, InventoryDocumentSupport support) : ICommandHandler<CreateAdjustmentCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(CreateAdjustmentCommand command, CancellationToken cancellationToken)
    {
        var ctx = await support.ResolveAsync([command.WarehouseId], cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        var number = await support.NextNumberAsync(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, SourceDocTypes.StockAdjustment, "ADJ", command.Date, cancellationToken);
        var doc = new StockAdjustment(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, command.WarehouseId, number, command.Date,
            Enum.Parse<AdjustmentReason>(command.Reason, true), command.Notes, command.Lines.Select(InventoryMapping.ToLine));
        db.Adjustments.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record AdjustmentActionCommand(Guid Id, DocumentAction Action) : ICommand<AdjustmentDto>;

public sealed class AdjustmentActionHandler(IInventoryDbContext db) : ICommandHandler<AdjustmentActionCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(AdjustmentActionCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Adjustments.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        switch (command.Action)
        {
            case DocumentAction.Approve: doc.Approve(); break;
            case DocumentAction.Cancel: doc.Cancel(); break;
            default: return InventoryErrors.InvalidTransition;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

/// <summary>Posts an approved adjustment: gains Dr Inventory / Cr adjustment gain, losses Dr adjustment loss / Cr Inventory.</summary>
public sealed class AdjustmentPoster(IInventoryPostingService posting)
{
    public async Task<Result> PostAsync(StockAdjustment doc, CancellationToken ct)
    {
        if (doc.Status != AdjustmentStatus.Approved)
        {
            return InventoryErrors.InvalidTransition;
        }

        var result = await posting.PostAsync(new InventoryPostingRequest(
            doc.LegalEntityId, doc.Date, SourceModules.Inventory, SourceDocTypes.StockAdjustment, doc.Id, doc.Number,
            doc.Lines.Select(l => new InventoryPostingLine(l.LineId, l.ProductId, l.VariantId, doc.WarehouseId, l.UomId, l.Quantity,
                l.Quantity > 0 ? InventoryMovementTypes.AdjustmentGain : InventoryMovementTypes.AdjustmentLoss,
                l.Quantity > 0 ? l.UnitCost : null, InventoryMapping.Batch(l), InventoryMapping.Serials(l),
                CounterRole: l.Quantity > 0 ? PostingRoles.InventoryAdjustmentGain : PostingRoles.InventoryAdjustmentLoss, BinId: l.BinId)).ToList(),
            PostJournal: true, JournalDescription: $"Stock adjustment {doc.Number} ({doc.Reason})"), ct);
        if (result.IsFailure)
        {
            return result.Error;
        }

        foreach (var line in doc.Lines)
        {
            var r = result.Value.Lines.First(x => x.LineId == line.LineId);
            line.PostedBaseQuantity = r.BaseQuantity;
            line.PostedUnitCost = r.UnitCost;
            line.PostedTotalCost = r.TotalCost;
        }

        doc.MarkPosted(result.Value.TotalCost, result.Value.JournalId);
        return Result.Success();
    }
}

public sealed record PostAdjustmentCommand(Guid Id) : ICommand<AdjustmentDto>;

public sealed class PostAdjustmentHandler(IInventoryDbContext db, AdjustmentPoster poster) : ICommandHandler<PostAdjustmentCommand, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(PostAdjustmentCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Adjustments.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        var posted = await poster.PostAsync(doc, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record GetAdjustmentQuery(Guid Id) : IQuery<AdjustmentDto>;

public sealed class GetAdjustmentHandler(IInventoryDbContext db) : IQueryHandler<GetAdjustmentQuery, AdjustmentDto>
{
    public async Task<Result<AdjustmentDto>> Handle(GetAdjustmentQuery query, CancellationToken cancellationToken)
    {
        var doc = await db.Adjustments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == query.Id, cancellationToken);
        return doc is null ? InventoryErrors.NotFound : InventoryMapping.ToDto(doc);
    }
}

public sealed record ListAdjustmentsQuery(PageRequest Page) : IQuery<PagedResult<AdjustmentDto>>;

public sealed class ListAdjustmentsHandler(IInventoryDbContext db) : IQueryHandler<ListAdjustmentsQuery, PagedResult<AdjustmentDto>>
{
    public async Task<Result<PagedResult<AdjustmentDto>>> Handle(ListAdjustmentsQuery query, CancellationToken cancellationToken)
    {
        var total = await db.Adjustments.CountAsync(cancellationToken);
        var docs = await db.Adjustments.AsNoTracking().OrderByDescending(d => d.Date).ThenByDescending(d => d.Number)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AdjustmentDto>(docs.Select(InventoryMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ---------------- Transfers ----------------
/// <param name="Direct">Move the stock in one posting without the transit warehouse (issue = receipt).</param>
public sealed record CreateTransferCommand(Guid FromWarehouseId, Guid ToWarehouseId, DateOnly Date, string? Notes, IReadOnlyList<StockLineInput> Lines, bool Direct = false) : ICommand<TransferDto>;

public sealed class CreateTransferValidator : AbstractValidator<CreateTransferCommand>
{
    public CreateTransferValidator()
    {
        RuleFor(x => x.FromWarehouseId).NotEmpty();
        RuleFor(x => x.ToWarehouseId).NotEmpty().NotEqual(x => x.FromWarehouseId);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new StockLineInputValidator());
    }
}

public sealed class CreateTransferHandler(IInventoryDbContext db, InventoryDocumentSupport support, IOrganizationQueries organization)
    : ICommandHandler<CreateTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(CreateTransferCommand command, CancellationToken cancellationToken)
    {
        var ctx = await support.ResolveAsync([command.FromWarehouseId, command.ToWarehouseId], cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        var transit = await organization.GetTransitWarehouseAsync(ctx.Value.LegalEntity.Id, cancellationToken);
        if (transit is null)
        {
            return Error.Conflict("inventory.transit-warehouse-missing", "The legal entity has no transit warehouse.");
        }

        if (ctx.Value.Warehouses.Values.Any(w => w.Type == "Transit"))
        {
            return Error.Validation("inventory.transit-not-allowed", "Transfers are between stock warehouses; the transit warehouse is used automatically.");
        }

        var number = await support.NextNumberAsync(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, "StockTransfer", "TRF", command.Date, cancellationToken);
        var doc = new StockTransfer(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, command.FromWarehouseId, command.ToWarehouseId, transit.Id,
            number, command.Date, command.Notes, command.Lines.Select(InventoryMapping.ToLine), command.Direct);
        db.Transfers.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record TransferActionCommand(Guid Id, DocumentAction Action) : ICommand<TransferDto>;

public sealed class TransferActionHandler(IInventoryDbContext db) : ICommandHandler<TransferActionCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(TransferActionCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Transfers.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        switch (command.Action)
        {
            case DocumentAction.Approve: doc.Approve(); break;
            case DocumentAction.Cancel: doc.Cancel(); break;
            default: return InventoryErrors.InvalidTransition;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

/// <summary>
/// Moving stock between two warehouses in one posting: an outbound leg (cost computed) then an inbound leg at the same cost.
/// The journal (when wanted) is posted on the leg that carries a counter role.
/// </summary>
public sealed class TransferLegPoster(IInventoryPostingService posting, ICatalogQueries catalog)
{
    /// <param name="CarriedTotalCost">Exact value to carry into the destination for the leg (spread over its movements); null = the out leg's cost.</param>
    public sealed record Leg(Guid LineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, string? BatchNumber, IReadOnlyList<string>? Serials, decimal? CarriedUnitCost,
        decimal? CarriedTotalCost = null, Guid? FromBinId = null, Guid? ToBinId = null);

    public async Task<Result<IReadOnlyDictionary<Guid, InventoryLineResult>>> MoveAsync(
        Guid legalEntityId, DateOnly date, string referenceNo, Guid sourceDocId, string outDocType, string inDocType,
        Guid fromWarehouseId, Guid toWarehouseId, IReadOnlyList<Leg> legs, string? outCounterRole, string? inCounterRole, CancellationToken ct)
    {
        var outResult = await posting.PostAsync(new InventoryPostingRequest(
            legalEntityId, date, SourceModules.Inventory, outDocType, sourceDocId, referenceNo,
            legs.Select(l => new InventoryPostingLine(l.LineId, l.ProductId, l.VariantId, fromWarehouseId, l.UomId, -l.Quantity,
                InventoryMovementTypes.TransferOut, Batch: l.BatchNumber is null ? null : new BatchInput(l.BatchNumber), Serials: l.Serials,
                CounterWarehouseId: toWarehouseId, CounterRole: outCounterRole, BinId: l.FromBinId)).ToList(),
            PostJournal: outCounterRole is not null, JournalDescription: $"Transfer {referenceNo}"), ct);
        if (outResult.IsFailure)
        {
            return outResult.Error;
        }

        var products = (await catalog.GetProductsAsync(legs.Select(l => l.ProductId).Distinct().ToList(), ct)).ToDictionary(p => p.Id);
        var inLines = new List<InventoryPostingLine>();
        var legByLine = legs.ToDictionary(l => l.LineId);
        foreach (var line in outResult.Value.Lines)
        {
            var leg = legByLine[line.LineId];
            var product = products[leg.ProductId];
            var unitCost = leg.CarriedUnitCost ?? line.UnitCost;
            var carried = 0m;
            for (var m = 0; m < line.Movements.Count; m++)
            {
                var movement = line.Movements[m];
                // Exact value per movement: the out movement's own cost, or the carried total spread with the last movement taking the remainder.
                decimal total;
                if (leg.CarriedTotalCost is { } carriedTotal)
                {
                    total = m == line.Movements.Count - 1
                        ? carriedTotal - carried
                        : InventoryRounding.Money(carriedTotal * (movement.BaseQuantity / line.BaseQuantity));
                }
                else if (leg.CarriedUnitCost is { } carriedUnit)
                {
                    total = InventoryRounding.Money(-movement.BaseQuantity * carriedUnit);
                }
                else
                {
                    total = -movement.TotalCost;
                }

                carried += total;
                inLines.Add(new InventoryPostingLine(movement.MovementId, leg.ProductId, leg.VariantId, toWarehouseId, product.BaseUomId, -movement.BaseQuantity,
                    InventoryMovementTypes.TransferIn, unitCost,
                    Batch: movement.BatchNumber is null ? null : new BatchInput(movement.BatchNumber),
                    Serials: movement.SerialNumber is null ? null : [movement.SerialNumber],
                    CounterWarehouseId: fromWarehouseId, CounterRole: inCounterRole, TotalCost: total, BinId: leg.ToBinId));
            }
        }

        var inResult = await posting.PostAsync(new InventoryPostingRequest(
            legalEntityId, date, SourceModules.Inventory, inDocType, sourceDocId, referenceNo, inLines,
            PostJournal: inCounterRole is not null, JournalDescription: $"Transfer {referenceNo}"), ct);
        if (inResult.IsFailure)
        {
            return inResult.Error;
        }

        return outResult.Value.Lines.ToDictionary(l => l.LineId);
    }
}

public sealed record IssueTransferCommand(Guid Id, DateOnly? Date) : ICommand<TransferDto>;

public sealed class IssueTransferHandler(IInventoryDbContext db, TransferLegPoster legs) : ICommandHandler<IssueTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(IssueTransferCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Transfers.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        if (doc.Status != TransferStatus.Approved)
        {
            return InventoryErrors.InvalidTransition;
        }

        if (doc.Direct)
        {
            // Source → destination in one posting. The two legs clear each other through InventoryInTransit within the same posting,
            // so warehouse-specific inventory accounts are moved correctly and nothing is left in transit.
            var direct = await legs.MoveAsync(doc.LegalEntityId, command.Date ?? doc.Date, doc.Number, doc.Id,
                SourceDocTypes.StockTransferIssue, SourceDocTypes.StockTransferReceipt, doc.FromWarehouseId, doc.ToWarehouseId,
                doc.Lines.Select(l => new TransferLegPoster.Leg(l.LineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.BatchNumber, InventoryMapping.Serials(l), null,
                    FromBinId: l.BinId, ToBinId: l.ToBinId)).ToList(),
                PostingRoles.InventoryInTransit, PostingRoles.InventoryInTransit, cancellationToken);
            if (direct.IsFailure)
            {
                return direct.Error;
            }

            doc.MarkMovedDirectly(direct.Value.ToDictionary(kv => kv.Key, kv => (Math.Abs(kv.Value.BaseQuantity), kv.Value.UnitCost, Math.Abs(kv.Value.TotalCost))));
            await db.SaveChangesAsync(cancellationToken);
            return InventoryMapping.ToDto(doc);
        }

        var moved = await legs.MoveAsync(doc.LegalEntityId, command.Date ?? doc.Date, doc.Number, doc.Id,
            SourceDocTypes.StockTransferIssue, SourceDocTypes.StockTransferIssueTransit, doc.FromWarehouseId, doc.TransitWarehouseId,
            doc.Lines.Select(l => new TransferLegPoster.Leg(l.LineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.BatchNumber, InventoryMapping.Serials(l), null,
                FromBinId: l.BinId)).ToList(),
            PostingRoles.InventoryInTransit, null, cancellationToken);
        if (moved.IsFailure)
        {
            return moved.Error;
        }

        doc.MarkIssued(moved.Value.ToDictionary(kv => kv.Key, kv => (Math.Abs(kv.Value.BaseQuantity), kv.Value.UnitCost, Math.Abs(kv.Value.TotalCost))));
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record TransferReceiptLine(Guid LineId, decimal Quantity, IReadOnlyList<string>? Serials);

public sealed record ReceiveTransferCommand(Guid Id, DateOnly? Date, IReadOnlyList<TransferReceiptLine>? Lines) : ICommand<TransferDto>;

public sealed class ReceiveTransferHandler(IInventoryDbContext db, TransferLegPoster legs, ICatalogQueries catalog) : ICommandHandler<ReceiveTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(ReceiveTransferCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Transfers.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        if (doc.Status is not (TransferStatus.InTransit or TransferStatus.PartiallyReceived))
        {
            return InventoryErrors.InvalidTransition;
        }

        // No lines = receive everything still in transit.
        var requested = command.Lines is { Count: > 0 }
            ? command.Lines
            : doc.Lines.Where(l => doc.OpenBaseQuantity(l) > 0)
                .Select(l => new TransferReceiptLine(l.LineId, doc.OpenBaseQuantity(l) * (l.Quantity / l.PostedBaseQuantity!.Value), l.Serials.Except(l.ReceivedSerials).ToList()))
                .ToList();

        var products = (await catalog.GetProductsAsync(doc.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var receipt = new Dictionary<Guid, (decimal BaseQuantity, IReadOnlyList<string> Serials)>();
        var carriedCosts = new Dictionary<Guid, decimal>();
        var legList = new List<TransferLegPoster.Leg>();
        foreach (var r in requested)
        {
            var line = doc.Lines.FirstOrDefault(l => l.LineId == r.LineId);
            if (line is null)
            {
                return Error.Validation("inventory.line-not-found", "Unknown transfer line.");
            }

            var factor = line.PostedBaseQuantity!.Value / line.Quantity;
            var baseQuantity = InventoryRounding.Quantity(r.Quantity * factor);
            if (baseQuantity > 0 && baseQuantity <= doc.OpenBaseQuantity(line))
            {
                // The receipt that empties the line carries the exact remainder of the issued value: no residue in transit.
                carriedCosts[line.LineId] = doc.CarriedCost(line, baseQuantity);
            }

            receipt[line.LineId] = (baseQuantity, r.Serials ?? []);
            legList.Add(new TransferLegPoster.Leg(line.LineId, line.ProductId, line.VariantId, products[line.ProductId].BaseUomId, baseQuantity,
                line.BatchNumber, r.Serials is { Count: > 0 } ? r.Serials : null, line.PostedUnitCost, carriedCosts.GetValueOrDefault(line.LineId), ToBinId: line.ToBinId));
        }

        var receiptNo = doc.RecordReceipt(receipt, carriedCosts);
        var receiptId = DeterministicId(doc.Id, receiptNo);
        var moved = await legs.MoveAsync(doc.LegalEntityId, command.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), $"{doc.Number}/R{receiptNo}", receiptId,
            SourceDocTypes.StockTransferReceiptOut, SourceDocTypes.StockTransferReceipt, doc.TransitWarehouseId, doc.ToWarehouseId, legList,
            null, PostingRoles.InventoryInTransit, cancellationToken);
        if (moved.IsFailure)
        {
            return moved.Error;
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }

    /// <summary>Stable id per (transfer, receipt number), so a retried receipt maps to the same inventory source.</summary>
    internal static Guid DeterministicId(Guid transferId, int receiptNo)
    {
        var bytes = transferId.ToByteArray();
        var n = BitConverter.GetBytes(receiptNo);
        for (var i = 0; i < 4; i++)
        {
            bytes[12 + i] ^= n[i];
        }

        bytes[7] ^= 0x5A;
        return new Guid(bytes);
    }
}

public sealed record CloseTransferCommand(Guid Id, DateOnly? Date) : ICommand<TransferDto>;

/// <summary>Writes off stock still in transit (Dr inventory adjustment loss / Cr inventory in transit) and completes the transfer.</summary>
public sealed class CloseTransferHandler(IInventoryDbContext db, IInventoryPostingService posting) : ICommandHandler<CloseTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(CloseTransferCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Transfers.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        var lost = doc.CloseWithDiscrepancy();
        if (lost.Count > 0)
        {
            var result = await posting.PostAsync(new InventoryPostingRequest(
                doc.LegalEntityId, command.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), SourceModules.Inventory, SourceDocTypes.StockTransferClose, doc.Id, doc.Number,
                lost.Select(l => new InventoryPostingLine(l.Line.LineId, l.Line.ProductId, l.Line.VariantId, doc.TransitWarehouseId, l.Line.UomId,
                    -(l.BaseQuantity * (l.Line.Quantity / l.Line.PostedBaseQuantity!.Value)), InventoryMovementTypes.AdjustmentLoss,
                    Batch: l.Line.BatchNumber is null ? null : new BatchInput(l.Line.BatchNumber),
                    Serials: l.Line.Serials.Count == 0 ? null : l.Line.Serials.Except(l.Line.ReceivedSerials).ToList(),
                    CounterRole: PostingRoles.InventoryAdjustmentLoss)).ToList(),
                PostJournal: true, JournalDescription: $"Transfer {doc.Number} closed with discrepancy"), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record GetTransferQuery(Guid Id) : IQuery<TransferDto>;

public sealed class GetTransferHandler(IInventoryDbContext db) : IQueryHandler<GetTransferQuery, TransferDto>
{
    public async Task<Result<TransferDto>> Handle(GetTransferQuery query, CancellationToken cancellationToken)
    {
        var doc = await db.Transfers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == query.Id, cancellationToken);
        return doc is null ? InventoryErrors.NotFound : InventoryMapping.ToDto(doc);
    }
}

public sealed record ListTransfersQuery(PageRequest Page) : IQuery<PagedResult<TransferDto>>;

public sealed class ListTransfersHandler(IInventoryDbContext db) : IQueryHandler<ListTransfersQuery, PagedResult<TransferDto>>
{
    public async Task<Result<PagedResult<TransferDto>>> Handle(ListTransfersQuery query, CancellationToken cancellationToken)
    {
        var total = await db.Transfers.CountAsync(cancellationToken);
        var docs = await db.Transfers.AsNoTracking().OrderByDescending(d => d.Date).ThenByDescending(d => d.Number)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<TransferDto>(docs.Select(InventoryMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ---------------- Stock counts ----------------
public sealed record CreateCountCommand(Guid WarehouseId, DateOnly Date, string? Notes) : ICommand<CountDto>;

public sealed class CreateCountHandler(IInventoryDbContext db, InventoryDocumentSupport support) : ICommandHandler<CreateCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(CreateCountCommand command, CancellationToken cancellationToken)
    {
        var ctx = await support.ResolveAsync([command.WarehouseId], cancellationToken);
        if (ctx.IsFailure)
        {
            return ctx.Error;
        }

        var number = await support.NextNumberAsync(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, "StockCount", "CNT", command.Date, cancellationToken);
        var doc = new StockCount(ctx.Value.CompanyId, ctx.Value.LegalEntity.Id, command.WarehouseId, number, command.Date, command.Notes);
        db.Counts.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record SnapshotCountCommand(Guid Id) : ICommand<CountDto>;

public sealed class SnapshotCountHandler(IInventoryDbContext db, ICatalogQueries catalog, IClock clock) : ICommandHandler<SnapshotCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(SnapshotCountCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Counts.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        var balances = await db.Balances.AsNoTracking().Where(b => b.WarehouseId == doc.WarehouseId && b.OnHand != 0).ToListAsync(cancellationToken);
        var products = (await catalog.GetProductsAsync(balances.Select(b => b.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var batchIds = balances.Where(b => b.BatchKey != Guid.Empty).Select(b => b.BatchKey).Distinct().ToList();
        var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var serialProducts = products.Values.Where(p => p.Tracking == "Serial").Select(p => p.Id).ToList();
        var serialLines = (await db.Serials.AsNoTracking()
                .Where(x => serialProducts.Contains(x.ProductId) && x.CurrentWarehouseId == doc.WarehouseId && x.Status == SerialStatus.InStock)
                .ToListAsync(cancellationToken))
            .Select(x => new StockCountLine
            {
                ProductId = x.ProductId,
                BaseUomId = products[x.ProductId].BaseUomId,
                Serial = x.Serial,
                SnapshotQuantity = 1,
            });
        var lines = balances
            .Where(b => products.TryGetValue(b.ProductId, out var p) && p.Tracking != "Serial")
            .Select(b => new StockCountLine
            {
                ProductId = b.ProductId,
                VariantId = Keys.Nullable(b.VariantKey),
                BatchId = Keys.Nullable(b.BatchKey),
                BatchNumber = b.BatchKey == Guid.Empty ? null : batches.GetValueOrDefault(b.BatchKey)?.Number,
                BaseUomId = products[b.ProductId].BaseUomId,
                SnapshotQuantity = b.OnHand,
            })
            .Concat(serialLines)
            .OrderBy(l => l.ProductId).ThenBy(l => l.BatchNumber).ThenBy(l => l.Serial)
            .ToList();
        doc.TakeSnapshot(clock.UtcNow, lines);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

/// <param name="Serials">Serial-tracked products: the serial numbers found (the quantity is ignored).</param>
public sealed record CountEntry(Guid ProductId, Guid? VariantId, string? BatchNumber, decimal CountedQuantity, IReadOnlyList<string>? Serials = null);

public sealed record EnterCountsCommand(Guid Id, IReadOnlyList<CountEntry> Entries) : ICommand<CountDto>;

public sealed class EnterCountsHandler(IInventoryDbContext db, ICatalogQueries catalog) : ICommandHandler<EnterCountsCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(EnterCountsCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Counts.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        var products = (await catalog.GetProductsAsync(command.Entries.Select(e => e.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        foreach (var entry in command.Entries)
        {
            if (!products.TryGetValue(entry.ProductId, out var product) || !product.IsStockable)
            {
                return Error.NotFound("inventory.product-not-found", "A product was not found in this company.");
            }

            if (product.Tracking == "Serial")
            {
                doc.EnterSerialCount(entry.ProductId, entry.VariantId, product.BaseUomId, entry.Serials ?? []);
                continue;
            }

            Guid? batchId = null;
            string? batchNumber = null;
            if (!string.IsNullOrWhiteSpace(entry.BatchNumber))
            {
                batchNumber = entry.BatchNumber.Trim().ToUpperInvariant();
                var batch = await db.Batches.AsNoTracking().FirstOrDefaultAsync(b => b.ProductId == entry.ProductId && b.Number == batchNumber, cancellationToken);
                if (batch is null)
                {
                    return Error.NotFound("inventory.batch-not-found", $"Batch {batchNumber} was not found.");
                }

                batchId = batch.Id;
            }

            doc.EnterCount(entry.ProductId, entry.VariantId, batchId, batchNumber, product.BaseUomId, entry.CountedQuantity);
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public enum CountAction
{
    Finish,
    Reopen,
    Approve,
    Cancel,
}

public sealed record CountActionCommand(Guid Id, CountAction Action) : ICommand<CountDto>;

public sealed class CountActionHandler(IInventoryDbContext db) : ICommandHandler<CountActionCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(CountActionCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Counts.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        switch (command.Action)
        {
            case CountAction.Finish:
                doc.CalculateVariances(await CountSupport.CurrentOnHandAsync(db, doc, cancellationToken));
                break;
            case CountAction.Reopen: doc.ReopenCounting(); break;
            case CountAction.Approve: doc.Approve(); break;
            case CountAction.Cancel: doc.Cancel(); break;
        }
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

internal static class CountSupport
{
    public static async Task<IReadOnlyDictionary<Guid, decimal>> CurrentOnHandAsync(IInventoryDbContext db, StockCount doc, CancellationToken ct)
    {
        var balances = await db.Balances.AsNoTracking().Where(b => b.WarehouseId == doc.WarehouseId).ToListAsync(ct);
        var serialNumbers = doc.Lines.Where(l => l.Serial is not null).Select(l => l.Serial!).Distinct().ToList();
        var inStock = serialNumbers.Count == 0
            ? []
            : (await db.Serials.AsNoTracking()
                .Where(s => serialNumbers.Contains(s.Serial) && s.CurrentWarehouseId == doc.WarehouseId && s.Status == SerialStatus.InStock)
                .Select(s => new { s.ProductId, s.Serial }).ToListAsync(ct))
              .Select(s => (s.ProductId, s.Serial.ToUpperInvariant())).ToHashSet();
        return doc.Lines.ToDictionary(l => l.LineId, l => l.Serial is not null
            ? inStock.Contains((l.ProductId, l.Serial.ToUpperInvariant())) ? 1m : 0m
            : balances
                .Where(b => b.ProductId == l.ProductId && b.VariantKey == Keys.Of(l.VariantId) && b.BatchKey == Keys.Of(l.BatchId))
                .Sum(b => b.OnHand));
    }
}

public sealed record PostCountCommand(Guid Id, DateOnly? Date) : ICommand<CountDto>;

/// <summary>Recomputes variances against current stock, then creates, approves and posts a count-variance adjustment.</summary>
public sealed class PostCountHandler(IInventoryDbContext db, InventoryDocumentSupport support, AdjustmentPoster poster) : ICommandHandler<PostCountCommand, CountDto>
{
    public async Task<Result<CountDto>> Handle(PostCountCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.Counts.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (doc is null)
        {
            return InventoryErrors.NotFound;
        }

        if (doc.Status != CountStatus.Approved)
        {
            return InventoryErrors.InvalidTransition;
        }

        doc.CalculateVariances(await CountSupport.CurrentOnHandAsync(db, doc, cancellationToken));
        var variances = doc.Lines.Where(l => l.Variance != 0).ToList();
        Guid? adjustmentId = null;
        if (variances.Count > 0)
        {
            var date = command.Date ?? doc.Date;
            var number = await support.NextNumberAsync(doc.CompanyId, doc.LegalEntityId, SourceDocTypes.StockAdjustment, "ADJ", date, cancellationToken);
            // Serial lines (±1 each) are grouped per product and direction into one adjustment line carrying the serial numbers.
            var serialAdjustments = variances.Where(l => l.Serial is not null)
                .GroupBy(l => (l.ProductId, l.VariantId, l.BaseUomId, Gain: l.Variance > 0))
                .Select(g => new StockDocumentLine
                {
                    LineId = SequentialGuid.New(),
                    ProductId = g.Key.ProductId,
                    VariantId = g.Key.VariantId,
                    UomId = g.Key.BaseUomId,
                    Quantity = g.Sum(l => l.Variance),
                    Serials = g.Select(l => l.Serial!).ToList(),
                });
            var adjustment = new StockAdjustment(doc.CompanyId, doc.LegalEntityId, doc.WarehouseId, number, date, AdjustmentReason.CountVariance,
                $"Stock count {doc.Number}", variances.Where(l => l.Serial is null).Select(l => new StockDocumentLine
                {
                    LineId = SequentialGuid.New(),
                    ProductId = l.ProductId,
                    VariantId = l.VariantId,
                    UomId = l.BaseUomId,
                    Quantity = l.Variance,
                    BatchNumber = l.BatchNumber,
                }).Concat(serialAdjustments), doc.Id);
            db.Adjustments.Add(adjustment);
            adjustment.Approve();
            var posted = await poster.PostAsync(adjustment, cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            adjustmentId = adjustment.Id;
        }

        doc.MarkPosted(adjustmentId);
        await db.SaveChangesAsync(cancellationToken);
        return InventoryMapping.ToDto(doc);
    }
}

public sealed record GetCountQuery(Guid Id) : IQuery<CountDto>;

public sealed class GetCountHandler(IInventoryDbContext db) : IQueryHandler<GetCountQuery, CountDto>
{
    public async Task<Result<CountDto>> Handle(GetCountQuery query, CancellationToken cancellationToken)
    {
        var doc = await db.Counts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == query.Id, cancellationToken);
        return doc is null ? InventoryErrors.NotFound : InventoryMapping.ToDto(doc);
    }
}

// ---------------- Inquiries ----------------
public sealed record StockInquiryQuery(Guid? ProductId, Guid? WarehouseId, PageRequest Page) : IQuery<PagedResult<StockRowDto>>;

/// <summary>
/// Stock per product/variant/warehouse. Available = OnHand − Reserved; Incoming = open purchase-order supply (when Procurement is
/// installed) + quantities in transit on inbound transfers; Outgoing = open sales-order demand (when Sales is installed);
/// Projected = Available + Incoming − Outgoing. Rows with only incoming/outgoing quantities are listed too.
/// </summary>
public sealed class StockInquiryHandler(
    IInventoryDbContext db,
    IEnumerable<IProcurementQueries> procurement,
    IEnumerable<ISalesQueries> sales) : IQueryHandler<StockInquiryQuery, PagedResult<StockRowDto>>
{
    public async Task<Result<PagedResult<StockRowDto>>> Handle(StockInquiryQuery query, CancellationToken cancellationToken)
    {
        var source = db.Balances.AsNoTracking();
        if (query.ProductId is { } p)
        {
            source = source.Where(b => b.ProductId == p);
        }

        if (query.WarehouseId is { } w)
        {
            source = source.Where(b => b.WarehouseId == w);
        }

        var balances = await source.GroupBy(b => new { b.ProductId, b.VariantKey, b.WarehouseId })
            .Select(g => new { g.Key.ProductId, g.Key.VariantKey, g.Key.WarehouseId, OnHand = g.Sum(b => b.OnHand), Reserved = g.Sum(b => b.Reserved) })
            .Where(r => r.OnHand != 0 || r.Reserved != 0)
            .ToListAsync(cancellationToken);

        var rows = new Dictionary<(Guid Product, Guid Variant, Guid Warehouse), StockFigures>();
        StockFigures Row(Guid product, Guid? variant, Guid warehouse)
        {
            var key = (product, variant ?? Guid.Empty, warehouse);
            if (!rows.TryGetValue(key, out var row))
            {
                row = new StockFigures();
                rows[key] = row;
            }

            return row;
        }

        foreach (var b in balances)
        {
            var row = Row(b.ProductId, Keys.Nullable(b.VariantKey), b.WarehouseId);
            row.OnHand += b.OnHand;
            row.Reserved += b.Reserved;
        }

        bool Wanted(Guid product, Guid warehouse) =>
            (query.ProductId is not { } pid || pid == product) && (query.WarehouseId is not { } wid || wid == warehouse);

        // Inbound transfers: shipped to transit but not yet received (or written off as lost) at the destination.
        var transfers = await db.Transfers.AsNoTracking()
            .Where(t => t.Status == TransferStatus.InTransit || t.Status == TransferStatus.PartiallyReceived)
            .ToListAsync(cancellationToken);
        foreach (var t in transfers)
        {
            foreach (var l in t.Lines.Where(l => Wanted(l.ProductId, t.ToWarehouseId)))
            {
                var open = (l.PostedBaseQuantity ?? 0) - l.ReceivedBaseQuantity - l.LostBaseQuantity;
                if (open > 0)
                {
                    Row(l.ProductId, l.VariantId, t.ToWarehouseId).Incoming += open;
                }
            }
        }

        foreach (var supplier in procurement)
        {
            var supply = query.ProductId is { } productId
                ? await supplier.GetOpenSupplyAsync([productId], query.WarehouseId, cancellationToken)
                : await supplier.GetAllOpenSupplyAsync(query.WarehouseId, cancellationToken);
            foreach (var s in supply.Where(s => Wanted(s.ProductId, s.WarehouseId)))
            {
                Row(s.ProductId, s.VariantId, s.WarehouseId).Incoming += s.OpenBaseQuantity;
            }
        }

        foreach (var demandSource in sales)
        {
            var demand = query.ProductId is { } productId
                ? await demandSource.GetOpenDemandAsync([productId], query.WarehouseId, cancellationToken)
                : await demandSource.GetAllOpenDemandAsync(query.WarehouseId, cancellationToken);
            foreach (var d in demand.Where(d => Wanted(d.ProductId, d.WarehouseId)))
            {
                Row(d.ProductId, d.VariantId, d.WarehouseId).Outgoing += d.OpenBaseQuantity;
            }
        }

        var ordered = rows.OrderBy(r => r.Key.Product).ThenBy(r => r.Key.Warehouse).ThenBy(r => r.Key.Variant).ToList();
        var page = ordered.Skip(query.Page.Skip).Take(query.Page.SafePageSize).Select(r =>
        {
            var f = r.Value;
            var available = f.OnHand - f.Reserved;
            return new StockRowDto(r.Key.Product, Keys.Nullable(r.Key.Variant), r.Key.Warehouse, f.OnHand, f.Reserved, available, f.Incoming, f.Outgoing,
                available + f.Incoming - f.Outgoing);
        }).ToList();
        return new PagedResult<StockRowDto>(page, query.Page.SafePage, query.Page.SafePageSize, ordered.Count);
    }

    private sealed class StockFigures
    {
        public decimal OnHand { get; set; }

        public decimal Reserved { get; set; }

        public decimal Incoming { get; set; }

        public decimal Outgoing { get; set; }
    }
}

public sealed record MovementsQuery(Guid? ProductId, Guid? WarehouseId, DateOnly? From, DateOnly? To, string? SourceDocType, Guid? SourceDocId, PageRequest Page)
    : IQuery<PagedResult<MovementDto>>;

public sealed class MovementsHandler(IInventoryDbContext db) : IQueryHandler<MovementsQuery, PagedResult<MovementDto>>
{
    public async Task<Result<PagedResult<MovementDto>>> Handle(MovementsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Movements.AsNoTracking();
        if (query.ProductId is { } p)
        {
            source = source.Where(m => m.ProductId == p);
        }

        if (query.WarehouseId is { } w)
        {
            source = source.Where(m => m.WarehouseId == w);
        }

        if (query.From is { } from)
        {
            source = source.Where(m => m.TransactionDate >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(m => m.TransactionDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceDocType))
        {
            source = source.Where(m => m.SourceDocType == query.SourceDocType);
        }

        if (query.SourceDocId is { } docId)
        {
            source = source.Where(m => m.SourceDocId == docId);
        }

        var total = await source.CountAsync(cancellationToken);
        var rows = await source.OrderBy(m => m.TransactionDate).ThenBy(m => m.CreatedAtUtc).ThenBy(m => m.Id)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(m => new MovementDto(m.Id, m.TransactionDate, m.MovementType, m.ProductId, m.VariantId, m.WarehouseId, m.BatchId, m.SerialId,
                m.BaseQuantity, m.UnitCost, m.TotalCost, m.SourceModule, m.SourceDocType, m.SourceDocId, m.ReferenceNo, m.ReversalOfId, m.PostingSequence, m.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return new PagedResult<MovementDto>(rows, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record ValuationQuery(Guid? ProductId) : IQuery<IReadOnlyList<ValuationDto>>;

public sealed class ValuationHandler(IInventoryDbContext db) : IQueryHandler<ValuationQuery, IReadOnlyList<ValuationDto>>
{
    public async Task<Result<IReadOnlyList<ValuationDto>>> Handle(ValuationQuery query, CancellationToken cancellationToken)
    {
        var source = db.Valuations.AsNoTracking();
        if (query.ProductId is { } p)
        {
            source = source.Where(v => v.ProductId == p);
        }

        var rows = await source.OrderBy(v => v.ProductId).ToListAsync(cancellationToken);
        return rows.Select(v => new ValuationDto(v.ValuationAreaId, v.ProductId, Keys.Nullable(v.VariantKey), v.CostingMethod, v.Quantity, v.Value, v.AverageCost, v.LastCost)).ToList();
    }
}

public sealed record BatchesQuery(Guid? ProductId) : IQuery<IReadOnlyList<BatchDto>>;

public sealed class BatchesHandler(IInventoryDbContext db) : IQueryHandler<BatchesQuery, IReadOnlyList<BatchDto>>
{
    public async Task<Result<IReadOnlyList<BatchDto>>> Handle(BatchesQuery query, CancellationToken cancellationToken)
    {
        var source = db.Batches.AsNoTracking();
        if (query.ProductId is { } p)
        {
            source = source.Where(b => b.ProductId == p);
        }

        return await source.OrderBy(b => b.ExpiryDate == null).ThenBy(b => b.ExpiryDate).ThenBy(b => b.Number)
            .Select(b => new BatchDto(b.Id, b.ProductId, b.Number, b.ManufactureDate, b.ExpiryDate, b.SupplierId)).ToListAsync(cancellationToken);
    }
}

public sealed record SerialsQuery(Guid? ProductId, string? Status) : IQuery<IReadOnlyList<SerialDto>>;

public sealed class SerialsHandler(IInventoryDbContext db) : IQueryHandler<SerialsQuery, IReadOnlyList<SerialDto>>
{
    public async Task<Result<IReadOnlyList<SerialDto>>> Handle(SerialsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Serials.AsNoTracking();
        if (query.ProductId is { } p)
        {
            source = source.Where(s => s.ProductId == p);
        }

        if (Enum.TryParse<SerialStatus>(query.Status, true, out var status))
        {
            source = source.Where(s => s.Status == status);
        }

        var rows = await source.OrderBy(s => s.Serial).Take(PageRequest.MaxPageSize * 5).ToListAsync(cancellationToken);
        return rows.Select(s => new SerialDto(s.Id, s.ProductId, s.Serial, s.CurrentWarehouseId, s.Status.ToString(), s.LastMovementId)).ToList();
    }
}

// ---------------- Reservations (HTTP) ----------------
public sealed record ReserveStockCommand(string DemandType, Guid DemandId, Guid DemandLineId, Guid ProductId, Guid? VariantId, Guid WarehouseId, decimal BaseQuantity)
    : ICommand<ReservationInfo>;

public sealed class ReserveStockHandler(IReservationService reservations) : ICommandHandler<ReserveStockCommand, ReservationInfo>
{
    public Task<Result<ReservationInfo>> Handle(ReserveStockCommand c, CancellationToken cancellationToken) =>
        reservations.ReserveAsync(new ReservationRequest(new ReservationRef(c.DemandType, c.DemandId, c.DemandLineId), c.ProductId, c.VariantId, c.WarehouseId, c.BaseQuantity), cancellationToken);
}

public sealed record ReleaseStockCommand(string DemandType, Guid DemandId, Guid DemandLineId, decimal? BaseQuantity) : ICommand<ReservationInfo>;

public sealed class ReleaseStockHandler(IReservationService reservations) : ICommandHandler<ReleaseStockCommand, ReservationInfo>
{
    public Task<Result<ReservationInfo>> Handle(ReleaseStockCommand c, CancellationToken cancellationToken) =>
        reservations.ReleaseAsync(new ReservationRef(c.DemandType, c.DemandId, c.DemandLineId), c.BaseQuantity, cancellationToken);
}

public sealed record ListReservationsQuery(string DemandType, Guid DemandId) : IQuery<IReadOnlyList<ReservationInfo>>;

public sealed class ListReservationsHandler(IReservationService reservations) : IQueryHandler<ListReservationsQuery, IReadOnlyList<ReservationInfo>>
{
    public async Task<Result<IReadOnlyList<ReservationInfo>>> Handle(ListReservationsQuery q, CancellationToken cancellationToken) =>
        Result.Success(await reservations.GetAsync(q.DemandType, q.DemandId, cancellationToken));
}

// ---------------- Catalog integration ----------------
/// <summary>Replaces the catalog's default probe: a product is "used" once it has any inventory movement.</summary>
public sealed class InventoryProductUsageProbe(IInventoryDbContext db) : IProductUsageProbe
{
    public Task<bool> IsUsedAsync(Guid productId, CancellationToken cancellationToken = default) =>
        db.Movements.AsNoTracking().AnyAsync(m => m.ProductId == productId, cancellationToken);
}
