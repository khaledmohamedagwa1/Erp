using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

public sealed record ReturnLineDto(Guid LineId, Guid GoodsReceiptLineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal UninvoicedQuantity,
    decimal InvoicedQuantity, decimal CostBase, decimal GrniBase, decimal DebitNoteNet, decimal DebitNoteTax, IReadOnlyList<string> Serials);

public sealed record PurchaseReturnDto(Guid Id, Guid LegalEntityId, Guid SupplierId, Guid GoodsReceiptId, Guid WarehouseId, string? Number, DateOnly Date,
    string? Reason, string Status, decimal TotalCost, decimal DebitNoteTotal, string? DebitNoteCurrency, Guid? JournalId, IReadOnlyList<ReturnLineDto> Lines);

internal static class ReturnMapping
{
    public static PurchaseReturnDto ToDto(PurchaseReturn r) => new(r.Id, r.LegalEntityId, r.SupplierId, r.GoodsReceiptId, r.WarehouseId, r.Number, r.Date, r.Reason,
        r.Status.ToString(), r.TotalCost, r.DebitNoteTotal, r.DebitNoteCurrency, r.JournalId,
        r.Lines.Select(l => new ReturnLineDto(l.LineId, l.GoodsReceiptLineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UninvoicedQuantity, l.InvoicedQuantity,
            l.CostBase, l.GrniBase, l.DebitNoteNet, l.DebitNoteTax, l.Serials ?? [])).ToList());
}

public sealed record ReturnLineInput(Guid GoodsReceiptLineId, decimal Quantity, IReadOnlyList<string>? Serials);

public sealed record CreatePurchaseReturnCommand(Guid GoodsReceiptId, DateOnly Date, string? Reason, IReadOnlyList<ReturnLineInput> Lines) : ICommand<PurchaseReturnDto>;

public sealed class CreatePurchaseReturnValidator : AbstractValidator<CreatePurchaseReturnCommand>
{
    public CreatePurchaseReturnValidator()
    {
        RuleFor(x => x.GoodsReceiptId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThan(0));
    }
}

public sealed class CreatePurchaseReturnHandler(IProcurementDbContext db, ITenantContext tenant) : ICommandHandler<CreatePurchaseReturnCommand, PurchaseReturnDto>
{
    public async Task<Result<PurchaseReturnDto>> Handle(CreatePurchaseReturnCommand command, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == command.GoodsReceiptId, cancellationToken);
        if (receipt is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (receipt.Status != GoodsReceiptStatus.Posted)
        {
            return ProcurementAppErrors.ReceiptNotPosted;
        }

        var lines = new List<PurchaseReturnLine>();
        foreach (var input in command.Lines)
        {
            var receiptLine = receipt.Lines.FirstOrDefault(l => l.LineId == input.GoodsReceiptLineId);
            if (receiptLine is null)
            {
                return Error.NotFound("procurement.line-not-found", "A referenced line was not found.");
            }

            if (receiptLine.CounterRole == GoodsReceipt.GoodsInvoicedNotReceivedRole)
            {
                return ProcurementAppErrors.InvoiceBeforeReceiptReturn;
            }

            if (input.Quantity > receiptLine.ReturnableQuantity)
            {
                return ProcurementErrors.OverReturn;
            }

            lines.Add(new PurchaseReturnLine(Guid.Empty, receiptLine.LineId, input.Quantity, receiptLine.ProductId, receiptLine.VariantId, receiptLine.UomId,
                Serials: input.Serials?.ToList()));
        }

        var purchaseReturn = new PurchaseReturn(tenant.CompanyId!.Value, receipt.LegalEntityId, receipt.SupplierId, receipt.Id, receipt.WarehouseId, command.Date,
            command.Reason, lines);
        db.Returns.Add(purchaseReturn);
        await db.SaveChangesAsync(cancellationToken);
        return ReturnMapping.ToDto(purchaseReturn);
    }
}

public enum ReturnAction
{
    Approve,
    Cancel,
}

public sealed record PurchaseReturnActionCommand(Guid Id, ReturnAction Action) : ICommand<PurchaseReturnDto>;

public sealed class PurchaseReturnActionHandler(IProcurementDbContext db) : ICommandHandler<PurchaseReturnActionCommand, PurchaseReturnDto>
{
    public async Task<Result<PurchaseReturnDto>> Handle(PurchaseReturnActionCommand command, CancellationToken cancellationToken)
    {
        var purchaseReturn = await db.Returns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (purchaseReturn is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (command.Action == ReturnAction.Approve)
        {
            purchaseReturn.Approve();
        }
        else
        {
            purchaseReturn.Cancel();
        }

        await db.SaveChangesAsync(cancellationToken);
        return ReturnMapping.ToDto(purchaseReturn);
    }
}

/// <summary>
/// Posts a return to the supplier in one transaction: stock OUT at the original receipt cost; the not-yet-invoiced part clears
/// GRNI, the invoiced part becomes a debit note at the invoiced price (Dr Supplier payable / Cr Input tax) settled against the
/// supplier's open invoices. Differences between the receipt value and the stock credit go to purchase price variance.
/// </summary>
public sealed record PostPurchaseReturnCommand(Guid Id) : ICommand<PurchaseReturnDto>;

public sealed class PostPurchaseReturnHandler(
    IProcurementDbContext db,
    ProcurementScope scope,
    ProductLookup products,
    PurchaseTax taxes,
    IPartyQueries parties,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostPurchaseReturnCommand, PurchaseReturnDto>
{
    public async Task<Result<PurchaseReturnDto>> Handle(PostPurchaseReturnCommand command, CancellationToken cancellationToken)
    {
        var purchaseReturn = await db.Returns.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (purchaseReturn is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (purchaseReturn.Status == PurchaseReturnStatus.Posted)
        {
            return ReturnMapping.ToDto(purchaseReturn);
        }

        if (purchaseReturn.Status != PurchaseReturnStatus.Approved)
        {
            return ProcurementErrors.InvalidState("purchase return", purchaseReturn.Status);
        }

        var le = await scope.LegalEntityAsync(purchaseReturn.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var supplier = await parties.GetSupplierAsync(purchaseReturn.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return ProcurementAppErrors.SupplierNotFound;
        }

        var receipt = await db.Receipts.FirstAsync(r => r.Id == purchaseReturn.GoodsReceiptId, cancellationToken);
        var order = await db.Orders.FirstAsync(o => o.Id == receipt.PurchaseOrderId, cancellationToken);
        var catalog = await products.LoadAsync(purchaseReturn.Lines.Select(l => l.ProductId), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        var split = new List<PurchaseReturnLine>();
        var stockLines = new List<InventoryPostingLine>();
        foreach (var line in purchaseReturn.Lines)
        {
            var receiptLine = receipt.Line(line.GoodsReceiptLineId);
            if (receiptLine.CounterRole == GoodsReceipt.GoodsInvoicedNotReceivedRole)
            {
                return ProcurementAppErrors.InvoiceBeforeReceiptReturn;
            }

            var (uninvoiced, invoiced, relief) = receipt.ApplyReturn(receiptLine.LineId, line.Quantity);
            order.ApplyReceipt(receiptLine.PurchaseOrderLineId, -line.Quantity, 0);
            if (invoiced > 0)
            {
                order.ApplyInvoice(receiptLine.PurchaseOrderLineId, -invoiced);
            }

            split.Add(line with { UninvoicedQuantity = uninvoiced, InvoicedQuantity = invoiced, GrniBase = relief });
            stockLines.Add(new InventoryPostingLine(line.LineId, line.ProductId, line.VariantId, purchaseReturn.WarehouseId, line.UomId, -line.Quantity,
                InventoryMovementTypes.PurchaseReturn,
                Batch: string.IsNullOrWhiteSpace(receiptLine.BatchNumber) ? null : new BatchInput(receiptLine.BatchNumber),
                Serials: line.Serials is { Count: > 0 } ? line.Serials : null,
                CostSourceMovementId: receiptLine.MovementId));
        }

        var number = await numbers.NextAsync(new NumberScope(purchaseReturn.CompanyId, "PurchaseReturn", "PRT", purchaseReturn.LegalEntityId,
            FiscalYear: purchaseReturn.Date.Year), cancellationToken);
        var stock = await inventory.PostAsync(new InventoryPostingRequest(purchaseReturn.LegalEntityId, purchaseReturn.Date, SourceModules.Procurement,
            ProcurementDocumentTypes.PurchaseReturn, purchaseReturn.Id, number, stockLines), cancellationToken);
        if (stock.IsFailure)
        {
            return stock.Error;
        }

        var costs = stock.Value.Lines.ToDictionary(l => l.LineId, l => -l.TotalCost);
        var journal = new JournalBuilder();
        var invoicedLines = new List<(int Index, PurchaseReturnLine Line, GoodsReceiptLine ReceiptLine, decimal CostShare, LineTax Tax)>();
        var currency = receipt.Currency;
        var decimals = await scope.MinorUnitsAsync(currency, cancellationToken);
        decimal? rate = null;
        for (var i = 0; i < split.Count; i++)
        {
            var line = split[i];
            var receiptLine = receipt.Line(line.GoodsReceiptLineId);
            var product = catalog.Value[line.ProductId];
            var cost = costs[line.LineId];
            var uninvoicedCost = line.InvoicedQuantity == 0 ? cost : Money.Round(cost * line.UninvoicedQuantity / line.Quantity);
            journal.Add(PostingRoles.Inventory, -cost, ProcurementPostingContext.Stock(product, purchaseReturn.WarehouseId), product.Sku);
            journal.Add(PostingRoles.GoodsReceivedNotInvoiced, line.GrniBase, ProcurementPostingContext.Product(product), receipt.Number);
            journal.Add(PostingRoles.PurchasePriceVariance, uninvoicedCost - line.GrniBase, ProcurementPostingContext.Product(product), product.Sku);
            split[i] = line with { CostBase = cost };
            if (line.InvoicedQuantity > 0)
            {
                var net = Money.Round(line.InvoicedQuantity * receiptLine.InvoicedUnitPrice, decimals);
                var tax = await taxes.CalculateAsync(receiptLine.InvoiceTaxGroupId, net, currency, purchaseReturn.Date, cancellationToken);
                if (tax.IsFailure)
                {
                    return tax.Error;
                }

                rate ??= receiptLine.InvoiceRate > 0 ? receiptLine.InvoiceRate : receipt.ExchangeRate;
                invoicedLines.Add((i, split[i], receiptLine, cost - uninvoicedCost, tax.Value));
            }
        }

        decimal debitNoteTotal = 0;
        if (invoicedLines.Count > 0)
        {
            // Debit note: transaction-currency buckets (cost part per line, recoverable tax per code) fitted to round(gross × rate).
            var buckets = new List<(int Line, Guid? TaxCodeId, decimal Amount)>();
            foreach (var item in invoicedLines)
            {
                buckets.Add((item.Index, null, item.Tax.Net + item.Tax.NonRecoverable));
                buckets.AddRange(item.Tax.Lines.Where(t => t.Recoverable && t.Amount != 0).GroupBy(t => t.TaxCodeId)
                    .Select(g => (item.Index, (Guid?)g.Key, g.Sum(t => t.Amount))));
            }

            debitNoteTotal = buckets.Sum(b => b.Amount);
            var baseGross = Money.Round(debitNoteTotal * rate!.Value);
            var parts = BaseAmounts.FitToTotal(buckets.Select(b => b.Amount).ToList(), rate.Value, baseGross);
            journal.Add(PostingRoles.SupplierPayable, baseGross, ProcurementPostingContext.Party(supplier.PostingGroup), number, nameof(PartyKind.Supplier), supplier.Id);
            for (var b = 0; b < buckets.Count; b++)
            {
                var bucket = buckets[b];
                if (bucket.TaxCodeId is { } taxCodeId)
                {
                    journal.Add(PostingRoles.InputTax, -parts[b], ProcurementPostingContext.Tax(taxCodeId));
                    continue;
                }

                var item = invoicedLines.First(x => x.Index == bucket.Line);
                var product = catalog.Value[item.Line.ProductId];
                journal.Add(PostingRoles.PurchasePriceVariance, -(parts[b] - item.CostShare), ProcurementPostingContext.Product(product), product.Sku);
                split[item.Index] = split[item.Index] with { DebitNoteNet = item.Tax.Net, DebitNoteTax = item.Tax.Recoverable + item.Tax.NonRecoverable };
            }
        }

        Guid? journalId = null;
        if (!journal.IsEmpty)
        {
            var posted = await journals.PostAsync(journal.Build(purchaseReturn.LegalEntityId, purchaseReturn.Date, ProcurementDocumentTypes.PurchaseReturn,
                purchaseReturn.Id, "return", $"Purchase return {number}", le.Value.FunctionalCurrency), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            journalId = posted.Value.JournalId;
        }

        if (debitNoteTotal > 0)
        {
            var items = await subledger.CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Supplier, supplier.Id, purchaseReturn.LegalEntityId, SourceModules.Procurement,
                ProcurementDocumentTypes.PurchaseReturn, purchaseReturn.Id, number, purchaseReturn.Date, currency, rate!.Value, -debitNoteTotal, null), cancellationToken);
            if (items.IsFailure)
            {
                return items.Error;
            }

            var applied = await subledger.ApplyCreditNoteAsync(ProcurementDocumentTypes.PurchaseReturn, purchaseReturn.Id, null, null, purchaseReturn.Date, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        purchaseReturn.MarkPosted(number, split, debitNoteTotal, debitNoteTotal > 0 ? currency : null, journalId);
        await db.SaveChangesAsync(cancellationToken);
        return ReturnMapping.ToDto(purchaseReturn);
    }
}

public sealed record GetPurchaseReturnQuery(Guid Id) : IQuery<PurchaseReturnDto>;

public sealed class GetPurchaseReturnHandler(IProcurementDbContext db) : IQueryHandler<GetPurchaseReturnQuery, PurchaseReturnDto>
{
    public async Task<Result<PurchaseReturnDto>> Handle(GetPurchaseReturnQuery query, CancellationToken cancellationToken)
    {
        var purchaseReturn = await db.Returns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
        return purchaseReturn is null ? ProcurementAppErrors.NotFound : ReturnMapping.ToDto(purchaseReturn);
    }
}

public sealed record ListPurchaseReturnsQuery(Guid? SupplierId, PageRequest Page) : IQuery<PagedResult<DocumentSummaryDto>>;

public sealed class ListPurchaseReturnsHandler(IProcurementDbContext db) : IQueryHandler<ListPurchaseReturnsQuery, PagedResult<DocumentSummaryDto>>
{
    public async Task<Result<PagedResult<DocumentSummaryDto>>> Handle(ListPurchaseReturnsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Returns.AsNoTracking();
        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(r => r.SupplierId == supplierId);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(r => r.Date).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(r => new DocumentSummaryDto(r.Id, r.Number, r.Date, r.SupplierId, r.Status.ToString(), r.TotalCost, null)).ToListAsync(cancellationToken);
        return new PagedResult<DocumentSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}
