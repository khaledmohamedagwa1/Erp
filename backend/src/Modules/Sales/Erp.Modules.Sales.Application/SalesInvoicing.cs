using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Workflow.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Application;

// ============================ Invoices ============================

/// <summary>
/// Creates a draft invoice from free lines (customer + lines), from a sales order (order lines) or from a delivery note
/// (delivery-driven policy). Source quantities default to everything still open.
/// </summary>
public sealed record CreateInvoiceCommand(
    Guid? CustomerId,
    Guid? WarehouseId,
    DateOnly Date,
    Guid? PriceListId,
    Guid? SalesOrderId,
    Guid? DeliveryNoteId,
    string? Reference,
    string? Notes,
    IReadOnlyList<SalesLineRequest>? Lines,
    IReadOnlyList<SourceLineRequest>? SourceLines) : ICommand<SalesDocumentDto>;

public sealed class CreateInvoiceValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceValidator()
    {
        RuleFor(x => x).Must(x => x.SalesOrderId is null || x.DeliveryNoteId is null).WithMessage("Give either a sales order or a delivery note, not both.");
        RuleFor(x => x.CustomerId).NotEmpty().When(x => x.SalesOrderId is null && x.DeliveryNoteId is null);
        RuleFor(x => x.WarehouseId).NotEmpty().When(x => x.SalesOrderId is null && x.DeliveryNoteId is null);
        RuleFor(x => x.Lines).NotEmpty().When(x => x.SalesOrderId is null && x.DeliveryNoteId is null);
        RuleForEach(x => x.Lines).SetValidator(new SalesLineRequestValidator());
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public sealed record InvoiceDraft(SalesSetting Setting, Guid? PriceListId, Guid? SalesOrderId, Guid? DeliveryNoteId, PricedLines Priced);

/// <summary>Builds and prices invoice lines for the three creation modes (shared by create and preview).</summary>
public sealed class InvoiceBuilder(ISalesDbContext db, SalesScope scope, SalesPricing pricing, IPolicyReader policies)
{
    public async Task<Result<InvoiceDraft>> BuildAsync(CreateInvoiceCommand c, CancellationToken ct)
    {
        var atDelivery = (await policies.GetSalesPolicyAsync(scope.CompanyId, ct)).StockIssueTiming == "AtDelivery";
        if (c.DeliveryNoteId is { } deliveryId)
        {
            var delivery = await db.Deliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
            if (delivery is null)
            {
                return SalesAppErrors.NotFound;
            }

            if (delivery.Status != DeliveryStatus.Posted)
            {
                return SalesErrors.InvalidTransition("delivery note", delivery.Status.ToString(), "invoiced");
            }

            var lines = Pick(delivery.Lines, c.SourceLines, l => l.OpenToInvoice);
            if (lines.IsFailure)
            {
                return lines.Error;
            }

            var inputs = lines.Value.Select(x => new LineInput(x.Line.ProductId, x.Line.VariantId, x.Line.UomId, x.Quantity, x.Line.UnitPrice, x.Line.DiscountPercent,
                x.Line.SourceLineUid, x.Line.Uid, TrustedUnitPrice: true, x.Line.BatchNumber)).ToList();
            return await PriceAsync(delivery.CustomerId, delivery.WarehouseId, c.Date, delivery.PriceListId, delivery.TaxInclusive, delivery.SalesOrderId, delivery.Id, inputs, ct);
        }

        if (c.SalesOrderId is { } orderId)
        {
            var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
            {
                return SalesAppErrors.NotFound;
            }

            if (!order.IsOpen)
            {
                return SalesErrors.InvalidTransition("sales order", order.Status.ToString(), "invoiced");
            }

            // With delivery-driven stock, stock lines are invoiced from deliveries; only service lines can be invoiced from the order.
            var candidates = order.Lines.Where(l => !atDelivery || !l.IsStockable).ToList();
            if (atDelivery && c.SourceLines is { Count: > 0 } && c.SourceLines.Any(s => order.Lines.Any(l => l.Uid == s.LineId && l.IsStockable)))
            {
                return SalesAppErrors.InvoiceRequiresDelivery;
            }

            var lines = Pick(candidates, c.SourceLines, l => l.OpenToInvoice);
            if (lines.IsFailure)
            {
                return lines.Error;
            }

            var inputs = lines.Value.Select(x => new LineInput(x.Line.ProductId, x.Line.VariantId, x.Line.UomId, x.Quantity, x.Line.UnitPrice, x.Line.DiscountPercent,
                x.Line.Uid, TrustedUnitPrice: true)).ToList();
            return await PriceAsync(order.CustomerId, order.WarehouseId, c.Date, order.PriceListId, order.TaxInclusive, order.Id, null, inputs, ct);
        }

        var free = SalesHandlerHelpers.ToInputs(c.Lines ?? []);
        var draft = await PriceAsync(c.CustomerId!.Value, c.WarehouseId!.Value, c.Date, c.PriceListId, null, null, null, free, ct);
        if (draft.IsSuccess && atDelivery && draft.Value.Priced.Lines.Any(l => l.IsStockable))
        {
            return SalesAppErrors.InvoiceRequiresDelivery;
        }

        return draft;
    }

    private async Task<Result<InvoiceDraft>> PriceAsync(Guid customerId, Guid warehouseId, DateOnly date, Guid? priceListId, bool? inclusive, Guid? orderId, Guid? deliveryId,
        IReadOnlyList<LineInput> inputs, CancellationToken ct)
    {
        var setting = await scope.ResolveAsync(customerId, warehouseId, date, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var list = priceListId ?? setting.Value.Customer.PriceListId;
        var priced = await pricing.PriceAsync(new PricingContext(list, setting.Value.Currency, date, inclusive ?? false), inputs, ct);
        return priced.IsFailure ? priced.Error : new InvoiceDraft(setting.Value, list, orderId, deliveryId, priced.Value);
    }

    private static Result<List<(SalesLine Line, decimal Quantity)>> Pick(IReadOnlyList<SalesLine> source, IReadOnlyList<SourceLineRequest>? requested, Func<SalesLine, decimal> open)
    {
        var result = new List<(SalesLine, decimal)>();
        if (requested is not { Count: > 0 })
        {
            result.AddRange(source.Where(l => open(l) > 0).Select(l => (l, open(l))));
        }
        else
        {
            foreach (var r in requested)
            {
                var line = source.FirstOrDefault(l => l.Uid == r.LineId);
                if (line is null)
                {
                    return SalesAppErrors.SourceMismatch;
                }

                if (r.Quantity <= 0 || r.Quantity > open(line))
                {
                    return SalesErrors.InvoiceExceedsSource;
                }

                result.Add((line, r.Quantity));
            }
        }

        return result.Count == 0 ? SalesErrors.NoLines : result;
    }
}

public sealed class CreateInvoiceHandler(ISalesDbContext db, InvoiceBuilder builder) : ICommandHandler<CreateInvoiceCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CreateInvoiceCommand c, CancellationToken ct)
    {
        var draft = await builder.BuildAsync(c, ct);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var (s, priceList, orderId, deliveryId, priced) = draft.Value;
        try
        {
            var invoice = new SalesInvoice(s.CompanyId, s.LegalEntityId, s.BranchId, s.WarehouseId, s.Customer.Id, s.Customer.PostingGroup, s.Currency, s.Rate, priceList,
                priced.TaxInclusive, c.Date, orderId, deliveryId, s.Customer.PaymentTermsId, c.Reference, c.Notes, priced.Lines);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);
            return SalesMapping.ToDto(invoice);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

/// <summary>Computes the totals of an invoice request without saving anything (for the UI preview).</summary>
public sealed record PreviewInvoiceQuery(CreateInvoiceCommand Request) : IQuery<SalesDocumentDto>;

public sealed class PreviewInvoiceHandler(InvoiceBuilder builder) : IQueryHandler<PreviewInvoiceQuery, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(PreviewInvoiceQuery query, CancellationToken ct)
    {
        var c = query.Request;
        var draft = await builder.BuildAsync(c, ct);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var (s, priceList, orderId, deliveryId, priced) = draft.Value;
        try
        {
            var invoice = new SalesInvoice(s.CompanyId, s.LegalEntityId, s.BranchId, s.WarehouseId, s.Customer.Id, s.Customer.PostingGroup, s.Currency, s.Rate, priceList,
                priced.TaxInclusive, c.Date, orderId, deliveryId, s.Customer.PaymentTermsId, c.Reference, c.Notes, priced.Lines);
            return SalesMapping.ToDto(invoice) with { Status = "Preview" };
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed record UpdateInvoiceLinesCommand(Guid Id, IReadOnlyList<SalesLineRequest> Lines) : ICommand<SalesDocumentDto>;

public sealed class UpdateInvoiceLinesHandler(ISalesDbContext db, SalesScope scope, SalesPricing pricing, IPolicyReader policies)
    : ICommandHandler<UpdateInvoiceLinesCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(UpdateInvoiceLinesCommand c, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (invoice is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (invoice.SalesOrderId is not null || invoice.DeliveryNoteId is not null)
        {
            return Error.Conflict("sales.invoice-from-source", "Invoices created from an order or delivery are changed by discarding and recreating them.");
        }

        var setting = await scope.ResolveAsync(invoice.CustomerId, invoice.WarehouseId, invoice.DocumentDate, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var priced = await pricing.PriceAsync(new PricingContext(invoice.PriceListId, invoice.Currency, invoice.DocumentDate, invoice.TaxInclusive), SalesHandlerHelpers.ToInputs(c.Lines), ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        if ((await policies.GetSalesPolicyAsync(invoice.CompanyId, ct)).StockIssueTiming == "AtDelivery" && priced.Value.Lines.Any(l => l.IsStockable))
        {
            return SalesAppErrors.InvoiceRequiresDelivery;
        }

        var result = SalesHandlerHelpers.Try(() => invoice.ReplaceLines(priced.Value.Lines, setting.Value.Rate, invoice.PriceListId, priced.Value.TaxInclusive));
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(invoice);
    }
}

public sealed record SubmitInvoiceCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class SubmitInvoiceHandler(ISalesDbContext db, SalesScope scope, SalesSubmission submission) : ICommandHandler<SubmitInvoiceCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(SubmitInvoiceCommand c, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (invoice is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (invoice.Status != SalesInvoiceStatus.Draft)
        {
            return SalesErrors.InvalidTransition("sales invoice", invoice.Status.ToString(), "submitted");
        }

        var setting = await scope.ResolveAsync(invoice.CustomerId, invoice.WarehouseId, invoice.DocumentDate, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var outcome = await submission.SubmitAsync(invoice, ApprovalDocumentTypes.SalesInvoice, setting.Value.Customer, invoice.SalesOrderId, ct);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        if (outcome.Value.PendingApproval)
        {
            invoice.SubmitForApproval(outcome.Value.InstanceId!.Value, outcome.Value.ForCredit);
        }
        else
        {
            invoice.Approve();
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(invoice, outcome.Value.Warning is null ? null : [outcome.Value.Warning]);
    }
}

/// <summary>
/// Posts an approved invoice in one transaction: legal number; stock issue at inventory cost with the COGS journal
/// (invoice-driven policy, written by the inventory engine, purpose "inventory"); the sales journal
/// Dr receivable / Cr revenue / Cr output tax (purpose "invoice"); receivable open items; order/delivery progress.
/// </summary>
public sealed record PostInvoiceCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class PostInvoiceHandler(
    ISalesDbContext db,
    SalesScope scope,
    SalesPricing pricing,
    SalesCreditService credit,
    IPolicyReader policies,
    IModuleActivationService modules,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostInvoiceCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(PostInvoiceCommand c, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (invoice is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (invoice.Status != SalesInvoiceStatus.Approved)
        {
            return SalesErrors.InvalidTransition("sales invoice", invoice.Status.ToString(), "posted");
        }

        var setting = await scope.ResolveAsync(invoice.CustomerId, invoice.WarehouseId, invoice.DocumentDate, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        // Final, server-side amounts: re-price the approved lines (same prices/discounts, current tax rules).
        var repriced = await pricing.RepriceAsync(new PricingContext(invoice.PriceListId, invoice.Currency, invoice.DocumentDate, invoice.TaxInclusive), invoice.Lines, ct);
        if (repriced.IsFailure)
        {
            return repriced.Error;
        }

        invoice.Reprice(setting.Value.Rate, repriced.Value.Lines);

        var warnings = new List<string>();
        var grossBase = SalesMath.Round(invoice.GrossTotal * invoice.ExchangeRate, 2);
        var check = await credit.EvaluateAsync(setting.Value.Customer, invoice.CompanyId, grossBase, invoice.SalesOrderId, ct);
        switch (check.Decision)
        {
            case CreditDecision.OnHold:
                return SalesAppErrors.CustomerOnHold;
            case CreditDecision.Block:
            case CreditDecision.RequireApproval when !invoice.CreditApproved:
                return SalesAppErrors.CreditLimitExceeded;
            case CreditDecision.Warn:
                warnings.Add($"Credit limit {check.Limit:0.00} exceeded: exposure is now {check.ExposureAfter:0.00}.");
                break;
        }

        var policy = await policies.GetSalesPolicyAsync(invoice.CompanyId, ct);
        var atDelivery = policy.StockIssueTiming == "AtDelivery";
        var stockLines = invoice.Lines.Where(l => l.IsStockable).ToList();
        if (stockLines.Count > 0 && !await modules.IsEnabledAsync(invoice.CompanyId, "inventory", ct))
        {
            return SalesAppErrors.InventoryDisabled;
        }

        var order = invoice.SalesOrderId is { } orderId ? await db.Orders.FirstAsync(o => o.Id == orderId, ct) : null;
        var delivery = invoice.DeliveryNoteId is { } deliveryId ? await db.Deliveries.FirstAsync(d => d.Id == deliveryId, ct) : null;
        if (atDelivery && stockLines.Any(l => l.DeliveryLineUid is null))
        {
            return SalesAppErrors.InvoiceRequiresDelivery;
        }

        var number = await SalesNumbers.NextAsync(numbers, invoice, SalesDocumentTypes.Invoice, "INV", ct);
        var issueStock = !atDelivery && stockLines.Count > 0;
        if (issueStock)
        {
            var posted = await inventory.PostAsync(new InventoryPostingRequest(invoice.LegalEntityId, invoice.DocumentDate, "sales", SalesDocumentTypes.Invoice, invoice.Id, number,
                stockLines.Select(l => PostDeliveryHandler.StockLine(l, invoice.WarehouseId, -l.Quantity, InventoryMovementTypes.SalesIssue,
                    order is { StockReserved: true } && l.SourceLineUid is { } orderLine ? new ReservationRef(SalesDocumentTypes.Order, order.Id, orderLine) : null)).ToList(),
                PostJournal: true, JournalDescription: $"Cost of sales {number}"), ct);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            foreach (var result in posted.Value.Lines)
            {
                var first = result.Movements.FirstOrDefault();
                invoice.Line(result.LineId).RecordCost(Math.Abs(result.TotalCost), first?.MovementId, first?.BatchNumber);
            }
        }
        else if (delivery is not null)
        {
            // Cost was booked at delivery; keep it on the invoice line for margins and returns.
            foreach (var line in stockLines)
            {
                var source = delivery.Line(line.DeliveryLineUid!.Value);
                line.RecordCost(SalesMath.Prorate(source.CostAmount, line.Quantity, source.Quantity, 2), source.CostMovementId, source.BatchNumber);
            }
        }

        var journal = new SalesJournal()
            .Add(PostingRoles.CustomerReceivable, invoice.GrossTotal, 0, new PostingContext(PartyPostingGroup: invoice.CustomerPostingGroup), invoice.CustomerId, $"Invoice {number}")
            .AddRevenueAndTax(invoice.Lines, credit: true, PostingRoles.SalesRevenue, PostingRoles.ServiceRevenue);
        var postedJournal = await journals.PostAsync(new JournalRequest(invoice.LegalEntityId, invoice.DocumentDate, SourceModules.Sales, SalesDocumentTypes.Invoice,
            invoice.Id, "invoice", $"Sales invoice {number}", invoice.Currency, invoice.ExchangeRate, journal.Lines), ct);
        if (postedJournal.IsFailure)
        {
            return postedJournal.Error;
        }

        if (invoice.GrossTotal != 0)
        {
            var items = await subledger.CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Customer, invoice.CustomerId, invoice.LegalEntityId, SourceModules.Sales,
                SalesDocumentTypes.Invoice, invoice.Id, number, invoice.DocumentDate, invoice.Currency, invoice.ExchangeRate, invoice.GrossTotal, invoice.PaymentTermsId), ct);
            if (items.IsFailure)
            {
                return items.Error;
            }
        }

        var applied = SalesHandlerHelpers.Try(() =>
        {
            foreach (var line in invoice.Lines)
            {
                if (delivery is not null && line.DeliveryLineUid is { } deliveryLine)
                {
                    delivery.RegisterInvoice(deliveryLine, line.Quantity);
                }

                if (order is not null && line.SourceLineUid is { } orderLine)
                {
                    if (!atDelivery || !line.IsStockable)
                    {
                        // Invoice-driven: the invoice is the delivery. Services are "delivered" when invoiced.
                        order.RegisterDelivery(orderLine, line.Quantity);
                    }

                    order.RegisterInvoice(orderLine, line.Quantity);
                }
            }

            invoice.MarkPosted(number, postedJournal.Value.JournalId, issueStock);
        });
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(invoice, warnings.Count == 0 ? null : warnings);
    }
}

/// <summary>Cancels a posted, unpaid invoice by full reversal: receivable items, sales journal, and (if issued) stock with its COGS journal.</summary>
public sealed record CancelInvoiceCommand(Guid Id, DateOnly? Date) : ICommand<SalesDocumentDto>;

public sealed class CancelInvoiceHandler(
    ISalesDbContext db,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IApprovalService approvals,
    IClock clock) : ICommandHandler<CancelInvoiceCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CancelInvoiceCommand c, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (invoice is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (!invoice.IsPosted)
        {
            var pending = invoice.Status == SalesInvoiceStatus.PendingApproval;
            var discarded = SalesHandlerHelpers.Try(invoice.Discard);
            if (discarded.IsFailure)
            {
                return discarded.Error;
            }

            if (pending)
            {
                await approvals.WithdrawAsync(ApprovalDocumentTypes.SalesInvoice, invoice.Id, ct);
            }

            await db.SaveChangesAsync(ct);
            return SalesMapping.ToDto(invoice);
        }

        var date = c.Date ?? DateOnly.FromDateTime(clock.UtcNow);
        var check = SalesHandlerHelpers.Try(() => invoice.Cancel(date));
        if (check.IsFailure)
        {
            return check.Error;
        }

        if (invoice.GrossTotal != 0)
        {
            var cancelled = await subledger.CancelOpenItemsAsync(SalesDocumentTypes.Invoice, invoice.Id, ct);
            if (cancelled.IsFailure)
            {
                return cancelled.Error;
            }
        }

        var reversed = await journals.ReverseAsync(invoice.LegalEntityId, SalesDocumentTypes.Invoice, invoice.Id, "invoice", date, ct);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        if (invoice.IssuedStock)
        {
            var stock = await inventory.ReverseAsync(SalesDocumentTypes.Invoice, invoice.Id, date, ct);
            if (stock.IsFailure)
            {
                return stock.Error;
            }
        }

        var order = invoice.SalesOrderId is { } orderId ? await db.Orders.FirstAsync(o => o.Id == orderId, ct) : null;
        var delivery = invoice.DeliveryNoteId is { } deliveryId ? await db.Deliveries.FirstAsync(d => d.Id == deliveryId, ct) : null;
        var applied = SalesHandlerHelpers.Try(() =>
        {
            foreach (var line in invoice.Lines)
            {
                if (delivery is not null && line.DeliveryLineUid is { } deliveryLine)
                {
                    delivery.RegisterInvoice(deliveryLine, -line.Quantity);
                }

                if (order is not null && line.SourceLineUid is { } orderLine)
                {
                    order.RegisterInvoice(orderLine, -line.Quantity);
                    if (invoice.IssuedStock || !line.IsStockable)
                    {
                        order.RegisterDelivery(orderLine, -line.Quantity);
                    }
                }
            }
        });
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(invoice);
    }
}

public sealed record GetInvoiceQuery(Guid Id) : IQuery<SalesDocumentDto>;

public sealed class GetInvoiceHandler(ISalesDbContext db) : IQueryHandler<GetInvoiceQuery, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(GetInvoiceQuery query, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.Id, ct);
        return i is null ? SalesAppErrors.NotFound : SalesMapping.ToDto(i);
    }
}

public sealed record ListInvoicesQuery(SalesListFilter Filter) : IQuery<SalesListResult>;

public sealed class ListInvoicesHandler(ISalesDbContext db) : IQueryHandler<ListInvoicesQuery, SalesListResult>
{
    public async Task<Result<SalesListResult>> Handle(ListInvoicesQuery query, CancellationToken ct)
    {
        var source = db.Invoices.AsNoTracking();
        if (SalesHandlerHelpers.StatusIs<SalesInvoiceStatus>(query.Filter.Status, out var status))
        {
            source = source.Where(x => x.Status == status);
        }

        return await SalesHandlerHelpers.ListAsync(source, query.Filter, i => SalesMapping.ToDto(i), ct);
    }
}

// ============================ Returns / credit notes ============================

public sealed record ReturnLineRequest(Guid InvoiceLineId, decimal Quantity, bool Restock = true, IReadOnlyList<string>? Serials = null);

public sealed record CreateReturnCommand(Guid SalesInvoiceId, DateOnly Date, string Reason, string? Notes, IReadOnlyList<ReturnLineRequest> Lines) : ICommand<SalesDocumentDto>;

public sealed class CreateReturnValidator : AbstractValidator<CreateReturnCommand>
{
    public CreateReturnValidator()
    {
        RuleFor(x => x.SalesInvoiceId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThan(0));
    }
}

public sealed class CreateReturnHandler(ISalesDbContext db) : ICommandHandler<CreateReturnCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CreateReturnCommand c, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == c.SalesInvoiceId, ct);
        if (invoice is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (!invoice.IsPosted)
        {
            return SalesErrors.InvalidTransition("sales invoice", invoice.Status.ToString(), "returned");
        }

        // Quantities on other draft returns of the same invoice are already claimed.
        var drafts = await db.Returns.AsNoTracking().Where(r => r.SalesInvoiceId == invoice.Id && r.Status == SalesReturnStatus.Draft).ToListAsync(ct);
        var claimed = drafts.SelectMany(r => r.Lines).GroupBy(l => l.SourceLineUid!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var lines = new List<SalesLine>();
        try
        {
            foreach (var r in c.Lines)
            {
                var source = invoice.Lines.FirstOrDefault(l => l.Uid == r.InvoiceLineId);
                if (source is null)
                {
                    return SalesAppErrors.SourceMismatch;
                }

                if (r.Quantity > source.OpenToReturn - claimed.GetValueOrDefault(source.Uid))
                {
                    return SalesErrors.ReturnExceedsInvoiced;
                }

                lines.Add(SalesReturnLines.From(source, r.Quantity, r.Restock && source.IsStockable, r.Serials));
            }

            var salesReturn = new SalesReturn(invoice, c.Date, c.Reason, c.Notes, lines);
            db.Returns.Add(salesReturn);
            await db.SaveChangesAsync(ct);
            return SalesMapping.ToDto(salesReturn);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public static class SalesReturnLines
{
    /// <summary>A return line credits exactly the invoiced share (net and each tax prorated), so the credit note mirrors the invoice.</summary>
    public static SalesLine From(SalesLine source, decimal quantity, bool restock, IReadOnlyList<string>? serials)
    {
        var line = new SalesLine(source.ProductId, source.VariantId, source.Sku, source.Description, source.UomId, quantity, source.ToBase(quantity),
            source.UnitPrice, source.DiscountPercent, source.IsStockable, source.IsService, source.PostingGroup, source.Uid);
        var taxes = source.Taxes.Select(t => new LineTax(t.TaxCodeId, t.Code, t.Rate, SalesMath.Prorate(t.Base, quantity, source.Quantity, 2),
            SalesMath.Prorate(t.Amount, quantity, source.Quantity, 2))).ToList();
        var net = SalesMath.Prorate(source.NetAmount, quantity, source.Quantity, 2);
        var tax = taxes.Sum(t => t.Amount);
        line.SetAmounts(SalesMath.Prorate(source.LineAmount, quantity, source.Quantity, 2), SalesMath.Prorate(source.DiscountAmount, quantity, source.Quantity, 2),
            net, tax, net + tax, source.TaxGroupId, taxes);
        line.SetStockDetail(source.BatchNumber, serials);
        line.RecordCost(SalesMath.Prorate(source.CostAmount, quantity, source.Quantity, 2), source.CostMovementId, null);
        line.SetRestock(restock);
        return line;
    }
}

/// <summary>
/// Posts a credit note: restock at the original issue cost (Dr Inventory / Cr COGS by the inventory engine), the sales journal
/// Dr SalesReturns + Dr OutputTax / Cr receivable, a negative open item applied to the invoice.
/// </summary>
public sealed record PostReturnCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class PostReturnHandler(
    ISalesDbContext db,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostReturnCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(PostReturnCommand c, CancellationToken ct)
    {
        var salesReturn = await db.Returns.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (salesReturn is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (salesReturn.Status != SalesReturnStatus.Draft)
        {
            return SalesErrors.InvalidTransition("sales return", salesReturn.Status.ToString(), "posted");
        }

        var invoice = await db.Invoices.FirstAsync(i => i.Id == salesReturn.SalesInvoiceId, ct);
        var registered = SalesHandlerHelpers.Try(() =>
        {
            foreach (var line in salesReturn.Lines)
            {
                invoice.RegisterReturn(line.SourceLineUid!.Value, line.Quantity);
            }
        });
        if (registered.IsFailure)
        {
            return registered.Error;
        }

        var number = await SalesNumbers.NextAsync(numbers, salesReturn, SalesDocumentTypes.Return, "CN", ct);
        var restock = salesReturn.Lines.Where(l => l.Restock && l.IsStockable && l.CostMovementId is not null).ToList();
        if (restock.Count > 0)
        {
            var posted = await inventory.PostAsync(new InventoryPostingRequest(salesReturn.LegalEntityId, salesReturn.DocumentDate, "sales", SalesDocumentTypes.Return,
                salesReturn.Id, number, restock.Select(l => PostDeliveryHandler.StockLine(l, salesReturn.WarehouseId, l.Quantity, InventoryMovementTypes.SalesReturn, null, l.CostMovementId)).ToList(),
                PostJournal: true, JournalDescription: $"Return to stock {number}"), ct);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            foreach (var result in posted.Value.Lines)
            {
                salesReturn.Line(result.LineId).RecordCost(Math.Abs(result.TotalCost), result.Movements.FirstOrDefault()?.MovementId, null);
            }

            salesReturn.TouchLines();
        }

        var journal = new SalesJournal()
            .AddRevenueAndTax(salesReturn.Lines, credit: false, PostingRoles.SalesReturns, PostingRoles.SalesReturns)
            .Add(PostingRoles.CustomerReceivable, 0, salesReturn.GrossTotal, new PostingContext(PartyPostingGroup: salesReturn.CustomerPostingGroup), salesReturn.CustomerId, $"Credit note {number}");
        var postedJournal = await journals.PostAsync(new JournalRequest(salesReturn.LegalEntityId, salesReturn.DocumentDate, SourceModules.Sales, SalesDocumentTypes.Return,
            salesReturn.Id, "return", $"Sales return {number}", salesReturn.Currency, salesReturn.ExchangeRate, journal.Lines), ct);
        if (postedJournal.IsFailure)
        {
            return postedJournal.Error;
        }

        if (salesReturn.GrossTotal != 0)
        {
            var items = await subledger.CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Customer, salesReturn.CustomerId, salesReturn.LegalEntityId, SourceModules.Sales,
                SalesDocumentTypes.Return, salesReturn.Id, number, salesReturn.DocumentDate, salesReturn.Currency, salesReturn.ExchangeRate, -salesReturn.GrossTotal, null), ct);
            if (items.IsFailure)
            {
                return items.Error;
            }

            var applied = await subledger.ApplyCreditNoteAsync(SalesDocumentTypes.Return, salesReturn.Id, SalesDocumentTypes.Invoice, invoice.Id, salesReturn.DocumentDate, ct);
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        var done = SalesHandlerHelpers.Try(() => salesReturn.MarkPosted(number, postedJournal.Value.JournalId));
        if (done.IsFailure)
        {
            return done.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(salesReturn);
    }
}

public sealed record CancelReturnCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class CancelReturnHandler(ISalesDbContext db) : ICommandHandler<CancelReturnCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CancelReturnCommand c, CancellationToken ct)
    {
        var r = await db.Returns.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (r is null)
        {
            return SalesAppErrors.NotFound;
        }

        var result = SalesHandlerHelpers.Try(r.Cancel);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(r);
    }
}

public sealed record GetReturnQuery(Guid Id) : IQuery<SalesDocumentDto>;

public sealed class GetReturnHandler(ISalesDbContext db) : IQueryHandler<GetReturnQuery, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(GetReturnQuery query, CancellationToken ct)
    {
        var r = await db.Returns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.Id, ct);
        return r is null ? SalesAppErrors.NotFound : SalesMapping.ToDto(r);
    }
}

public sealed record ListReturnsQuery(SalesListFilter Filter) : IQuery<SalesListResult>;

public sealed class ListReturnsHandler(ISalesDbContext db) : IQueryHandler<ListReturnsQuery, SalesListResult>
{
    public async Task<Result<SalesListResult>> Handle(ListReturnsQuery query, CancellationToken ct)
    {
        var source = db.Returns.AsNoTracking();
        if (SalesHandlerHelpers.StatusIs<SalesReturnStatus>(query.Filter.Status, out var status))
        {
            source = source.Where(x => x.Status == status);
        }

        return await SalesHandlerHelpers.ListAsync(source, query.Filter, SalesMapping.ToDto, ct);
    }
}

// ============================ Integration events ============================

/// <summary>
/// Loads a document for an event handler: handlers run with the tenant set but no company, so the company filter is lifted and the
/// tenant is applied explicitly (row-level security still applies); the document's company then becomes the scope's company.
/// </summary>
internal static class SalesEventLoading
{
    public static async Task<T?> LoadAsync<T>(IQueryable<T> source, ITenantContext tenant, Guid id, CancellationToken ct)
        where T : SalesDocument
    {
        var tenantId = tenant.TenantId!.Value;
        var document = await source.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id && EF.Property<Guid>(d, "TenantId") == tenantId, ct);
        if (document is not null && tenant.CompanyId is null)
        {
            tenant.SetCompany(document.CompanyId);
        }

        return document;
    }
}

/// <summary>Moves orders and invoices on when the workflow engine decides an approval (exactly once per message).</summary>
public sealed class ApprovalCompletedHandler(
    IUnitOfWork unitOfWork,
    IInbox inbox,
    IOutboxMessageContext message,
    ISalesDbContext db,
    ITenantContext tenant,
    OrderConfirmation confirmation) : InboxIntegrationEventHandler<ApprovalCompleted>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(ApprovalCompleted e, CancellationToken ct)
    {
        if (e.DocumentType == ApprovalDocumentTypes.SalesOrder)
        {
            var order = await SalesEventLoading.LoadAsync(db.Orders, tenant, e.DocumentId, ct);
            if (order is not { Status: SalesOrderStatus.PendingApproval })
            {
                return;
            }

            if (e.Approved)
            {
                var confirmed = await confirmation.ConfirmAsync(order, ct);
                if (confirmed.IsFailure)
                {
                    throw new InvalidOperationException($"Order {order.Number} could not be confirmed after approval: {confirmed.Error.Code}");
                }
            }
            else
            {
                order.RejectApproval();
            }

            await db.SaveChangesAsync(ct);
        }
        else if (e.DocumentType == ApprovalDocumentTypes.SalesInvoice)
        {
            var invoice = await SalesEventLoading.LoadAsync(db.Invoices, tenant, e.DocumentId, ct);
            if (invoice is not { Status: SalesInvoiceStatus.PendingApproval })
            {
                return;
            }

            if (e.Approved)
            {
                invoice.Approve();
            }
            else
            {
                invoice.RejectApproval();
            }

            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>Mirrors the receivable's settlement status (from Finance) on the invoice. Idempotent: it sets, never adds.</summary>
public sealed class InvoiceSettledHandler(ISalesDbContext db, ITenantContext tenant) : IIntegrationEventHandler<InvoiceSettled>
{
    public async Task HandleAsync(InvoiceSettled e, CancellationToken ct)
    {
        if (e.SourceDocType != SalesDocumentTypes.Invoice)
        {
            return;
        }

        var invoice = await SalesEventLoading.LoadAsync(db.Invoices, tenant, e.SourceDocId, ct);
        if (invoice is null)
        {
            return;
        }

        invoice.ApplySettlement(e.Status, e.OpenAmount);
        await db.SaveChangesAsync(ct);
    }
}
