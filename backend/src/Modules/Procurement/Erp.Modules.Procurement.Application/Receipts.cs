using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

public sealed record ReceiptLineDto(Guid LineId, Guid PurchaseOrderLineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal ReceivedQuantity,
    decimal AcceptedQuantity, decimal RejectedQuantity, string? BatchNumber, DateOnly? ManufactureDate, DateOnly? ExpiryDate, IReadOnlyList<string> Serials,
    decimal UnitPrice, decimal BaseValue, decimal InvoicedQuantity, decimal ReturnedQuantity, decimal InvoiceableQuantity, decimal ReturnableQuantity);

public sealed record GoodsReceiptDto(Guid Id, Guid LegalEntityId, Guid PurchaseOrderId, Guid SupplierId, Guid WarehouseId, string Number, DateOnly Date,
    string Currency, decimal ExchangeRate, string Status, string? Notes, string? InspectionNotes, decimal TotalBaseValue, Guid? JournalId,
    IReadOnlyList<ReceiptLineDto> Lines);

internal static class ReceiptMapping
{
    public static GoodsReceiptDto ToDto(GoodsReceipt r) => new(r.Id, r.LegalEntityId, r.PurchaseOrderId, r.SupplierId, r.WarehouseId, r.Number, r.Date,
        r.Currency, r.ExchangeRate, r.Status.ToString(), r.Notes, r.InspectionNotes, r.TotalBaseValue, r.InventoryJournalId,
        r.Lines.Select(l => new ReceiptLineDto(l.LineId, l.PurchaseOrderLineId, l.ProductId, l.VariantId, l.UomId, l.ReceivedQuantity, l.AcceptedQuantity,
            l.RejectedQuantity, l.BatchNumber, l.ManufactureDate, l.ExpiryDate, l.Serials ?? [], l.UnitPrice, l.BaseValue, l.InvoicedQuantity, l.ReturnedQuantity,
            l.InvoiceableQuantity, l.ReturnableQuantity)).ToList());
}

public sealed record ReceiptLineInput(Guid PurchaseOrderLineId, decimal ReceivedQuantity, decimal? AcceptedQuantity, string? BatchNumber,
    DateOnly? ManufactureDate, DateOnly? ExpiryDate, IReadOnlyList<string>? Serials);

public sealed record CreateGoodsReceiptCommand(Guid PurchaseOrderId, DateOnly Date, string? Notes, IReadOnlyList<ReceiptLineInput> Lines) : ICommand<GoodsReceiptDto>;

public sealed class CreateGoodsReceiptValidator : AbstractValidator<CreateGoodsReceiptCommand>
{
    public CreateGoodsReceiptValidator()
    {
        RuleFor(x => x.PurchaseOrderId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.PurchaseOrderLineId).NotEmpty();
            l.RuleFor(x => x.ReceivedQuantity).GreaterThan(0);
            l.RuleFor(x => x.AcceptedQuantity).GreaterThanOrEqualTo(0).When(x => x.AcceptedQuantity is not null);
        });
    }
}

public sealed class CreateGoodsReceiptHandler(IProcurementDbContext db, ProductLookup products, IDocumentNumberGenerator numbers, ITenantContext tenant)
    : ICommandHandler<CreateGoodsReceiptCommand, GoodsReceiptDto>
{
    public async Task<Result<GoodsReceiptDto>> Handle(CreateGoodsReceiptCommand command, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == command.PurchaseOrderId, cancellationToken);
        if (order is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (!order.CanReceive)
        {
            return ProcurementAppErrors.OrderNotReceivable;
        }

        var orderLines = order.Lines.ToDictionary(l => l.LineId);
        if (command.Lines.Any(l => !orderLines.ContainsKey(l.PurchaseOrderLineId)))
        {
            return Error.Validation("procurement.order-line-not-found", "A receipt line references a line that is not on the purchase order.");
        }

        var catalog = await products.LoadAsync(command.Lines.Select(l => orderLines[l.PurchaseOrderLineId].ProductId), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        if (catalog.Value.Values.Any(p => !p.IsStockable))
        {
            return ProcurementAppErrors.NotStockable;
        }

        var number = await numbers.NextAsync(new NumberScope(tenant.CompanyId!.Value, "GoodsReceipt", "GRN", order.LegalEntityId, order.BranchId,
            FiscalYear: command.Date.Year), cancellationToken);
        var receipt = new GoodsReceipt(tenant.CompanyId.Value, order.LegalEntityId, order.Id, order.SupplierId, order.WarehouseId, number, command.Date,
            order.Currency, order.ExchangeRate, command.Notes,
            command.Lines.Select(l =>
            {
                var orderLine = orderLines[l.PurchaseOrderLineId];
                return new GoodsReceiptLine(Guid.Empty, orderLine.LineId, orderLine.ProductId, orderLine.VariantId, orderLine.UomId, l.ReceivedQuantity,
                    l.AcceptedQuantity ?? l.ReceivedQuantity, 0, l.BatchNumber, l.ManufactureDate, l.ExpiryDate, l.Serials?.ToList(), orderLine.NetUnitPrice);
            }));
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        return ReceiptMapping.ToDto(receipt);
    }
}

public sealed record InspectGoodsReceiptCommand(Guid Id, IReadOnlyDictionary<Guid, decimal> AcceptedQuantities, string? Notes) : ICommand<GoodsReceiptDto>;

public sealed class InspectGoodsReceiptHandler(IProcurementDbContext db) : ICommandHandler<InspectGoodsReceiptCommand, GoodsReceiptDto>
{
    public async Task<Result<GoodsReceiptDto>> Handle(InspectGoodsReceiptCommand command, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (receipt is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        receipt.Inspect(command.AcceptedQuantities, command.Notes);
        await db.SaveChangesAsync(cancellationToken);
        return ReceiptMapping.ToDto(receipt);
    }
}

public enum ReceiptAction
{
    Approve,
    Cancel,
}

public sealed record GoodsReceiptActionCommand(Guid Id, ReceiptAction Action) : ICommand<GoodsReceiptDto>;

public sealed class GoodsReceiptActionHandler(IProcurementDbContext db) : ICommandHandler<GoodsReceiptActionCommand, GoodsReceiptDto>
{
    public async Task<Result<GoodsReceiptDto>> Handle(GoodsReceiptActionCommand command, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (receipt is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (command.Action == ReceiptAction.Approve)
        {
            receipt.Approve();
        }
        else
        {
            receipt.Cancel();
        }

        await db.SaveChangesAsync(cancellationToken);
        return ReceiptMapping.ToDto(receipt);
    }
}

/// <summary>
/// Posts an approved receipt: stock IN at the order's net unit price converted to the functional currency (Dr Inventory /
/// Cr Goods received not invoiced, posted by Inventory). Quantity that was invoiced before receipt clears Goods invoiced not
/// received at the invoiced value instead.
/// </summary>
public sealed record PostGoodsReceiptCommand(Guid Id) : ICommand<GoodsReceiptDto>;

public sealed class PostGoodsReceiptHandler(IProcurementDbContext db, ProductLookup products, ProcurementPolicy policy, IInventoryPostingService inventory)
    : ICommandHandler<PostGoodsReceiptCommand, GoodsReceiptDto>
{
    public async Task<Result<GoodsReceiptDto>> Handle(PostGoodsReceiptCommand command, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (receipt is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (receipt.Status == GoodsReceiptStatus.Posted)
        {
            return ReceiptMapping.ToDto(receipt);
        }

        if (receipt.Status != GoodsReceiptStatus.Approved)
        {
            return ProcurementErrors.InvalidState("goods receipt", receipt.Status);
        }

        var order = await db.Orders.FirstAsync(o => o.Id == receipt.PurchaseOrderId, cancellationToken);
        var settings = await policy.GetAsync(receipt.CompanyId, cancellationToken);
        var catalog = await products.LoadAsync(receipt.Lines.Select(l => l.ProductId), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        var postingLines = new List<InventoryPostingLine>();
        var counterRoles = new Dictionary<Guid, string>();
        foreach (var line in receipt.Lines.Where(l => l.AcceptedQuantity > 0))
        {
            var product = catalog.Value[line.ProductId];
            var factor = ProductLookup.Factor(product, line.UomId) ?? 1m;
            var orderLine = order.Line(line.PurchaseOrderLineId);
            decimal unitCost;
            string counterRole;
            if (orderLine.InvoicedNotReceivedQuantity > 0)
            {
                if (orderLine.InvoicedNotReceivedQuantity < line.AcceptedQuantity)
                {
                    return ProcurementAppErrors.PartialInvoicedReceipt;
                }

                var value = order.ConsumeInvoicedNotReceived(orderLine.LineId, line.AcceptedQuantity);
                unitCost = Money.Cost(value / (line.AcceptedQuantity * factor));
                counterRole = PostingRoles.GoodsInvoicedNotReceived;
            }
            else
            {
                unitCost = Money.Cost(orderLine.NetUnitPrice * order.ExchangeRate / factor);
                counterRole = PostingRoles.GoodsReceivedNotInvoiced;
            }

            order.ApplyReceipt(orderLine.LineId, line.AcceptedQuantity, settings.QuantityTolerancePercent);
            counterRoles[line.LineId] = counterRole;
            postingLines.Add(new InventoryPostingLine(line.LineId, line.ProductId, line.VariantId, receipt.WarehouseId, line.UomId, line.AcceptedQuantity,
                InventoryMovementTypes.PurchaseReceipt, unitCost,
                string.IsNullOrWhiteSpace(line.BatchNumber) ? null : new BatchInput(line.BatchNumber, line.ManufactureDate, line.ExpiryDate, receipt.SupplierId),
                line.Serials is { Count: > 0 } ? line.Serials : null, CounterRole: counterRole));
        }

        var posted = await inventory.PostAsync(new InventoryPostingRequest(receipt.LegalEntityId, receipt.Date, SourceModules.Procurement,
            ProcurementDocumentTypes.GoodsReceipt, receipt.Id, receipt.Number, postingLines, PostJournal: true,
            JournalDescription: $"Goods receipt {receipt.Number}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var results = posted.Value.Lines.ToDictionary(l => l.LineId);
        var postedLines = receipt.Lines.Select(l => results.TryGetValue(l.LineId, out var r)
            ? l with
            {
                MovementId = r.Movements.FirstOrDefault()?.MovementId,
                BaseQuantity = r.BaseQuantity,
                BaseValue = r.TotalCost,
                CounterRole = counterRoles[l.LineId],
            }
            : l).ToList();
        receipt.MarkPosted(postedLines, posted.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return ReceiptMapping.ToDto(receipt);
    }
}

/// <summary>Reverses a posted receipt that was neither invoiced nor returned (stock and journal reversed by Inventory).</summary>
public sealed record ReverseGoodsReceiptCommand(Guid Id, DateOnly? Date) : ICommand<GoodsReceiptDto>;

public sealed class ReverseGoodsReceiptHandler(IProcurementDbContext db, IInventoryPostingService inventory, IClock clock)
    : ICommandHandler<ReverseGoodsReceiptCommand, GoodsReceiptDto>
{
    public async Task<Result<GoodsReceiptDto>> Handle(ReverseGoodsReceiptCommand command, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (receipt is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (receipt.Status == GoodsReceiptStatus.Reversed)
        {
            return ReceiptMapping.ToDto(receipt);
        }

        receipt.Reverse();
        var order = await db.Orders.FirstAsync(o => o.Id == receipt.PurchaseOrderId, cancellationToken);
        foreach (var line in receipt.Lines.Where(l => l.AcceptedQuantity > 0))
        {
            order.ApplyReceipt(line.PurchaseOrderLineId, -line.AcceptedQuantity, 0);
            if (line.CounterRole == PostingRoles.GoodsInvoicedNotReceived)
            {
                order.RestoreInvoicedNotReceived(line.PurchaseOrderLineId, line.AcceptedQuantity, line.BaseValue);
            }
        }

        var reversed = await inventory.ReverseAsync(ProcurementDocumentTypes.GoodsReceipt, receipt.Id,
            command.Date ?? DateOnly.FromDateTime(clock.UtcNow), cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ReceiptMapping.ToDto(receipt);
    }
}

public sealed record GetGoodsReceiptQuery(Guid Id) : IQuery<GoodsReceiptDto>;

public sealed class GetGoodsReceiptHandler(IProcurementDbContext db) : IQueryHandler<GetGoodsReceiptQuery, GoodsReceiptDto>
{
    public async Task<Result<GoodsReceiptDto>> Handle(GetGoodsReceiptQuery query, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
        return receipt is null ? ProcurementAppErrors.NotFound : ReceiptMapping.ToDto(receipt);
    }
}

public sealed record ListGoodsReceiptsQuery(Guid? PurchaseOrderId, Guid? SupplierId, string? Status, PageRequest Page) : IQuery<PagedResult<DocumentSummaryDto>>;

public sealed class ListGoodsReceiptsHandler(IProcurementDbContext db) : IQueryHandler<ListGoodsReceiptsQuery, PagedResult<DocumentSummaryDto>>
{
    public async Task<Result<PagedResult<DocumentSummaryDto>>> Handle(ListGoodsReceiptsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Receipts.AsNoTracking();
        if (query.PurchaseOrderId is { } orderId)
        {
            source = source.Where(r => r.PurchaseOrderId == orderId);
        }

        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(r => r.SupplierId == supplierId);
        }

        if (Enum.TryParse<GoodsReceiptStatus>(query.Status, true, out var status))
        {
            source = source.Where(r => r.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(r => r.Date).ThenByDescending(r => r.Number).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(r => new DocumentSummaryDto(r.Id, r.Number, r.Date, r.SupplierId, r.Status.ToString(), r.TotalBaseValue, null)).ToListAsync(cancellationToken);
        return new PagedResult<DocumentSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}
