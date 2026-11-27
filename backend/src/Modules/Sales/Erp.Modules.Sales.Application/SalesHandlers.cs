using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Workflow.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Application;

/// <summary>A line as entered by the user: price from the price list unless <see cref="UnitPrice"/> is given (needs permission).</summary>
public sealed record SalesLineRequest(Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal? UnitPrice = null, decimal DiscountPercent = 0);

public sealed record SourceLineRequest(Guid LineId, decimal Quantity);

public sealed record SalesListFilter(string? Status, Guid? CustomerId, DateOnly? From, DateOnly? To, Guid? BranchId, PageRequest Page);

public sealed class SalesLineRequestValidator : AbstractValidator<SalesLineRequest>
{
    public SalesLineRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.UomId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0).When(x => x.UnitPrice is not null);
        RuleFor(x => x.DiscountPercent).InclusiveBetween(0, 100);
    }
}

internal static class SalesHandlerHelpers
{
    public static IReadOnlyList<LineInput> ToInputs(IEnumerable<SalesLineRequest> lines) =>
        lines.Select(l => new LineInput(l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice, l.DiscountPercent)).ToList();

    public static Result Try(Action action)
    {
        try
        {
            action();
            return Result.Success();
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    public static async Task<SalesListResult> ListAsync<T>(IQueryable<T> source, SalesListFilter filter, Func<T, SalesDocumentDto> map, CancellationToken ct)
        where T : SalesDocument
    {
        if (filter.CustomerId is { } customer)
        {
            source = source.Where(d => d.CustomerId == customer);
        }

        if (filter.From is { } from)
        {
            source = source.Where(d => d.DocumentDate >= from);
        }

        if (filter.To is { } to)
        {
            source = source.Where(d => d.DocumentDate <= to);
        }

        if (filter.BranchId is { } branch)
        {
            source = source.Where(d => d.BranchId == branch);
        }

        var total = await source.CountAsync(ct);
        var sums = await source.GroupBy(_ => 1)
            .Select(g => new { Net = g.Sum(d => d.NetTotal), Tax = g.Sum(d => d.TaxTotal), Gross = g.Sum(d => d.GrossTotal) })
            .FirstOrDefaultAsync(ct);
        var items = await source.OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.Number)
            .Skip(filter.Page.Skip).Take(filter.Page.SafePageSize).ToListAsync(ct);
        return new SalesListResult(items.Select(map).ToList(), filter.Page.SafePage, filter.Page.SafePageSize, total, sums?.Net ?? 0, sums?.Tax ?? 0, sums?.Gross ?? 0);
    }

    public static bool StatusIs<TEnum>(string? status, out TEnum value)
        where TEnum : struct, Enum => Enum.TryParse(status, true, out value) && !string.IsNullOrWhiteSpace(status);
}

// ============================ Quotations ============================

public sealed record CreateQuotationCommand(Guid CustomerId, Guid WarehouseId, DateOnly Date, DateOnly ValidUntil, Guid? PriceListId, string? Reference, string? Notes,
    IReadOnlyList<SalesLineRequest> Lines, Guid? SalespersonId = null) : ICommand<SalesDocumentDto>;

public sealed class CreateQuotationValidator : AbstractValidator<CreateQuotationCommand>
{
    public CreateQuotationValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new SalesLineRequestValidator());
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public sealed class CreateQuotationHandler(ISalesDbContext db, SalesScope scope, SalesPricing pricing, SalespersonResolver salespersons)
    : ICommandHandler<CreateQuotationCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CreateQuotationCommand c, CancellationToken ct)
    {
        var setting = await scope.ResolveAsync(c.CustomerId, c.WarehouseId, c.Date, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var s = setting.Value;
        var priceList = c.PriceListId ?? s.Customer.PriceListId;
        var priced = await pricing.PriceAsync(new PricingContext(priceList, s.Currency, c.Date, false), SalesHandlerHelpers.ToInputs(c.Lines), ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var salesperson = await salespersons.ResolveAsync(c.SalespersonId, null, s.Customer, ct);
        if (salesperson.IsFailure)
        {
            return salesperson.Error;
        }

        try
        {
            var quotation = new SalesQuotation(s.CompanyId, s.LegalEntityId, s.BranchId, s.WarehouseId, s.Customer.Id, s.Customer.PostingGroup, s.Currency, s.Rate,
                priceList, priced.Value.TaxInclusive, c.Date, c.ValidUntil, c.Reference, c.Notes, priced.Value.Lines);
            quotation.AssignSalesperson(salesperson.Value);
            db.Quotations.Add(quotation);
            await db.SaveChangesAsync(ct);
            return SalesMapping.ToDto(quotation);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed record UpdateQuotationLinesCommand(Guid Id, IReadOnlyList<SalesLineRequest> Lines) : ICommand<SalesDocumentDto>;

public sealed class UpdateQuotationLinesHandler(ISalesDbContext db, SalesScope scope, SalesPricing pricing) : ICommandHandler<UpdateQuotationLinesCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(UpdateQuotationLinesCommand c, CancellationToken ct)
    {
        var q = await db.Quotations.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (q is null)
        {
            return SalesAppErrors.NotFound;
        }

        var setting = await scope.ResolveAsync(q.CustomerId, q.WarehouseId, q.DocumentDate, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var priced = await pricing.PriceAsync(new PricingContext(q.PriceListId, q.Currency, q.DocumentDate, false), SalesHandlerHelpers.ToInputs(c.Lines), ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var result = SalesHandlerHelpers.Try(() => q.ReplaceLines(priced.Value.Lines, setting.Value.Rate, q.PriceListId, priced.Value.TaxInclusive));
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(q);
    }
}

public enum QuotationAction
{
    Send,
    Revise,
    Accept,
    Reject,
}

public sealed record QuotationActionCommand(Guid Id, QuotationAction Action) : ICommand<SalesDocumentDto>;

public sealed class QuotationActionHandler(ISalesDbContext db, IDocumentNumberGenerator numbers, IClock clock) : ICommandHandler<QuotationActionCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(QuotationActionCommand c, CancellationToken ct)
    {
        var q = await db.Quotations.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (q is null)
        {
            return SalesAppErrors.NotFound;
        }

        var number = c.Action == QuotationAction.Send && !q.IsNumbered
            ? await SalesNumbers.NextAsync(numbers, q, SalesDocumentTypes.Quotation, "QUO", ct)
            : q.Number;
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var result = SalesHandlerHelpers.Try(() =>
        {
            switch (c.Action)
            {
                case QuotationAction.Send: q.Send(number); break;
                case QuotationAction.Revise: q.Revise(); break;
                case QuotationAction.Accept: q.Accept(today); break;
                case QuotationAction.Reject: q.Reject(); break;
            }
        });

        // An expired quotation is persisted as Expired even though the acceptance failed.
        await db.SaveChangesAsync(ct);
        return result.IsFailure ? result.Error : SalesMapping.ToDto(q);
    }
}

public sealed record ConvertQuotationCommand(Guid Id, DateOnly? Date) : ICommand<SalesDocumentDto>;

public sealed class ConvertQuotationHandler(ISalesDbContext db, SalesScope scope, SalesPricing pricing, IClock clock) : ICommandHandler<ConvertQuotationCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(ConvertQuotationCommand c, CancellationToken ct)
    {
        var q = await db.Quotations.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (q is null)
        {
            return SalesAppErrors.NotFound;
        }

        var date = c.Date ?? DateOnly.FromDateTime(clock.UtcNow);
        var setting = await scope.ResolveAsync(q.CustomerId, q.WarehouseId, date, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        // The quoted (agreed) prices and discounts carry over; amounts and taxes are recomputed.
        var priced = await pricing.PriceAsync(new PricingContext(q.PriceListId, q.Currency, date, q.TaxInclusive),
            q.Lines.Select(l => new LineInput(l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice, l.DiscountPercent, TrustedUnitPrice: true)).ToList(), ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        try
        {
            var order = new SalesOrder(q.CompanyId, q.LegalEntityId, q.BranchId, q.WarehouseId, q.CustomerId, q.CustomerPostingGroup, q.Currency, setting.Value.Rate,
                q.PriceListId, q.TaxInclusive, date, null, q.Id, q.Number, q.Notes, priced.Value.Lines);
            order.AssignSalesperson(q.SalespersonId);
            q.MarkConverted(order.Id);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            return SalesMapping.ToDto(order);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed record GetQuotationQuery(Guid Id) : IQuery<SalesDocumentDto>;

public sealed class GetQuotationHandler(ISalesDbContext db) : IQueryHandler<GetQuotationQuery, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(GetQuotationQuery query, CancellationToken ct)
    {
        var q = await db.Quotations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.Id, ct);
        return q is null ? SalesAppErrors.NotFound : SalesMapping.ToDto(q);
    }
}

public sealed record ListQuotationsQuery(SalesListFilter Filter) : IQuery<SalesListResult>;

public sealed class ListQuotationsHandler(ISalesDbContext db) : IQueryHandler<ListQuotationsQuery, SalesListResult>
{
    public async Task<Result<SalesListResult>> Handle(ListQuotationsQuery query, CancellationToken ct)
    {
        var source = db.Quotations.AsNoTracking();
        if (SalesHandlerHelpers.StatusIs<QuotationStatus>(query.Filter.Status, out var status))
        {
            source = source.Where(x => x.Status == status);
        }

        return await SalesHandlerHelpers.ListAsync(source, query.Filter, SalesMapping.ToDto, ct);
    }
}

// ============================ Sales orders ============================

public sealed record CreateOrderCommand(Guid CustomerId, Guid WarehouseId, DateOnly Date, DateOnly? RequestedDate, Guid? PriceListId, string? Reference, string? Notes,
    IReadOnlyList<SalesLineRequest> Lines, Guid? SalespersonId = null) : ICommand<SalesDocumentDto>;

public sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new SalesLineRequestValidator());
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public sealed class CreateOrderHandler(ISalesDbContext db, SalesScope scope, SalesPricing pricing, SalespersonResolver salespersons)
    : ICommandHandler<CreateOrderCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CreateOrderCommand c, CancellationToken ct)
    {
        var setting = await scope.ResolveAsync(c.CustomerId, c.WarehouseId, c.Date, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var s = setting.Value;
        var priceList = c.PriceListId ?? s.Customer.PriceListId;
        var priced = await pricing.PriceAsync(new PricingContext(priceList, s.Currency, c.Date, false), SalesHandlerHelpers.ToInputs(c.Lines), ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var salesperson = await salespersons.ResolveAsync(c.SalespersonId, null, s.Customer, ct);
        if (salesperson.IsFailure)
        {
            return salesperson.Error;
        }

        try
        {
            var order = new SalesOrder(s.CompanyId, s.LegalEntityId, s.BranchId, s.WarehouseId, s.Customer.Id, s.Customer.PostingGroup, s.Currency, s.Rate,
                priceList, priced.Value.TaxInclusive, c.Date, c.RequestedDate, null, c.Reference, c.Notes, priced.Value.Lines);
            order.AssignSalesperson(salesperson.Value);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            return SalesMapping.ToDto(order);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

public sealed record UpdateOrderLinesCommand(Guid Id, IReadOnlyList<SalesLineRequest> Lines) : ICommand<SalesDocumentDto>;

public sealed class UpdateOrderLinesHandler(ISalesDbContext db, SalesScope scope, SalesPricing pricing) : ICommandHandler<UpdateOrderLinesCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(UpdateOrderLinesCommand c, CancellationToken ct)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (o is null)
        {
            return SalesAppErrors.NotFound;
        }

        var setting = await scope.ResolveAsync(o.CustomerId, o.WarehouseId, o.DocumentDate, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var priced = await pricing.PriceAsync(new PricingContext(o.PriceListId, o.Currency, o.DocumentDate, false), SalesHandlerHelpers.ToInputs(c.Lines), ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var result = SalesHandlerHelpers.Try(() => o.ReplaceLines(priced.Value.Lines, setting.Value.Rate, o.PriceListId, priced.Value.TaxInclusive));
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(o);
    }
}

/// <summary>Submits a draft order: credit check and approval rules, then confirmation (legal number + stock reservations).</summary>
public sealed record SubmitOrderCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class SubmitOrderHandler(ISalesDbContext db, SalesScope scope, SalesSubmission submission, OrderConfirmation confirmation)
    : ICommandHandler<SubmitOrderCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(SubmitOrderCommand c, CancellationToken ct)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (o is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (o.Status != SalesOrderStatus.Draft)
        {
            return SalesErrors.InvalidTransition("sales order", o.Status.ToString(), "submitted");
        }

        var setting = await scope.ResolveAsync(o.CustomerId, o.WarehouseId, o.DocumentDate, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var outcome = await submission.SubmitAsync(o, ApprovalDocumentTypes.SalesOrder, setting.Value.Customer, o.Id, ct);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        if (outcome.Value.PendingApproval)
        {
            o.SubmitForApproval(outcome.Value.InstanceId!.Value, outcome.Value.ForCredit);
        }
        else
        {
            var confirmed = await confirmation.ConfirmAsync(o, ct);
            if (confirmed.IsFailure)
            {
                return confirmed.Error;
            }
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(o, outcome.Value.Warning is null ? null : [outcome.Value.Warning]);
    }
}

public sealed record CancelOrderCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class CancelOrderHandler(ISalesDbContext db, OrderConfirmation confirmation, IApprovalService approvals) : ICommandHandler<CancelOrderCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CancelOrderCommand c, CancellationToken ct)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (o is null)
        {
            return SalesAppErrors.NotFound;
        }

        var pending = o.Status == SalesOrderStatus.PendingApproval;
        var result = SalesHandlerHelpers.Try(o.Cancel);
        if (result.IsFailure)
        {
            return result.Error;
        }

        if (pending)
        {
            await approvals.WithdrawAsync(ApprovalDocumentTypes.SalesOrder, o.Id, ct);
        }

        await confirmation.ReleaseAsync(o, ct);
        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(o);
    }
}

public sealed record ShortCloseOrderCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class ShortCloseOrderHandler(ISalesDbContext db, OrderConfirmation confirmation) : ICommandHandler<ShortCloseOrderCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(ShortCloseOrderCommand c, CancellationToken ct)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (o is null)
        {
            return SalesAppErrors.NotFound;
        }

        var result = SalesHandlerHelpers.Try(o.ShortClose);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await confirmation.ReleaseAsync(o, ct);
        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(o);
    }
}

public sealed record GetOrderQuery(Guid Id) : IQuery<SalesDocumentDto>;

public sealed class GetOrderHandler(ISalesDbContext db) : IQueryHandler<GetOrderQuery, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(GetOrderQuery query, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.Id, ct);
        return o is null ? SalesAppErrors.NotFound : SalesMapping.ToDto(o);
    }
}

public sealed record ListOrdersQuery(SalesListFilter Filter) : IQuery<SalesListResult>;

public sealed class ListOrdersHandler(ISalesDbContext db) : IQueryHandler<ListOrdersQuery, SalesListResult>
{
    public async Task<Result<SalesListResult>> Handle(ListOrdersQuery query, CancellationToken ct)
    {
        var source = db.Orders.AsNoTracking();
        if (SalesHandlerHelpers.StatusIs<SalesOrderStatus>(query.Filter.Status, out var status))
        {
            source = source.Where(x => x.Status == status);
        }

        return await SalesHandlerHelpers.ListAsync(source, query.Filter, o => SalesMapping.ToDto(o), ct);
    }
}

// ============================ Delivery notes ============================

/// <summary>Delivery from an order; omitted lines mean "all open quantities".</summary>
public sealed record CreateDeliveryCommand(Guid SalesOrderId, DateOnly Date, string? Reference, string? Notes, IReadOnlyList<SourceLineRequest>? Lines) : ICommand<SalesDocumentDto>;

public sealed class CreateDeliveryHandler(ISalesDbContext db, SalesPricing pricing, IPolicyReader policies) : ICommandHandler<CreateDeliveryCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CreateDeliveryCommand c, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(x => x.Id == c.SalesOrderId, ct);
        if (order is null)
        {
            return SalesAppErrors.NotFound;
        }

        if ((await policies.GetSalesPolicyAsync(order.CompanyId, ct)).StockIssueTiming != "AtDelivery")
        {
            return SalesAppErrors.DeliveriesDisabled;
        }

        if (!order.IsOpen)
        {
            return SalesErrors.InvalidTransition("sales order", order.Status.ToString(), "delivered");
        }

        // Quantities already on undelivered (draft/picked) notes count as taken.
        var drafts = await db.Deliveries.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id && (d.Status == DeliveryStatus.Draft || d.Status == DeliveryStatus.Picked)).ToListAsync(ct);
        var pending = drafts.SelectMany(d => d.Lines).GroupBy(l => l.SourceLineUid!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var requested = c.Lines is { Count: > 0 }
            ? c.Lines.Select(l => (l.LineId, l.Quantity)).ToList()
            : order.Lines.Where(l => l.IsStockable).Select(l => (LineId: l.Uid, Quantity: l.OpenToDeliver - pending.GetValueOrDefault(l.Uid))).Where(x => x.Quantity > 0).ToList();
        var inputs = new List<LineInput>();
        foreach (var (lineId, quantity) in requested)
        {
            var source = order.Lines.FirstOrDefault(l => l.Uid == lineId);
            if (source is null || !source.IsStockable)
            {
                return SalesAppErrors.SourceMismatch;
            }

            if (quantity <= 0 || quantity > source.OpenToDeliver - pending.GetValueOrDefault(lineId))
            {
                return SalesErrors.DeliveryExceedsOrdered;
            }

            inputs.Add(new LineInput(source.ProductId, source.VariantId, source.UomId, quantity, source.UnitPrice, source.DiscountPercent, source.Uid, TrustedUnitPrice: true));
        }

        if (inputs.Count == 0)
        {
            return SalesErrors.NoLines;
        }

        var priced = await pricing.PriceAsync(new PricingContext(order.PriceListId, order.Currency, c.Date, order.TaxInclusive), inputs, ct);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var delivery = new DeliveryNote(order, c.Date, c.Reference, c.Notes, priced.Value.Lines);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(delivery);
    }
}

public sealed record PickLine(Guid LineId, string? BatchNumber, IReadOnlyList<string>? Serials);

/// <summary>Records batches/serials to issue. Lines without a batch are allocated first-expiry-first at posting.</summary>
public sealed record PickDeliveryCommand(Guid Id, IReadOnlyList<PickLine> Lines) : ICommand<SalesDocumentDto>;

public sealed class PickDeliveryHandler(ISalesDbContext db) : ICommandHandler<PickDeliveryCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(PickDeliveryCommand c, CancellationToken ct)
    {
        var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (d is null)
        {
            return SalesAppErrors.NotFound;
        }

        var result = SalesHandlerHelpers.Try(() => d.Pick((c.Lines ?? []).ToDictionary(l => l.LineId, l => (l.BatchNumber, l.Serials))));
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(d);
    }
}

/// <summary>Posts the delivery: stock out at inventory cost (Dr COGS / Cr Inventory by the inventory engine), reservation consumed, order progress.</summary>
public sealed record PostDeliveryCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class PostDeliveryHandler(ISalesDbContext db, IInventoryPostingService inventory, IDocumentNumberGenerator numbers, IPolicyReader policies)
    : ICommandHandler<PostDeliveryCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(PostDeliveryCommand c, CancellationToken ct)
    {
        var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (d is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (d.Status is not (DeliveryStatus.Draft or DeliveryStatus.Picked))
        {
            return SalesErrors.InvalidTransition("delivery note", d.Status.ToString(), "posted");
        }

        if ((await policies.GetSalesPolicyAsync(d.CompanyId, ct)).StockIssueTiming != "AtDelivery")
        {
            return SalesAppErrors.DeliveriesDisabled;
        }

        var order = await db.Orders.FirstAsync(o => o.Id == d.SalesOrderId, ct);
        var number = await SalesNumbers.NextAsync(numbers, d, SalesDocumentTypes.Delivery, "DN", ct);
        var posted = await inventory.PostAsync(new InventoryPostingRequest(d.LegalEntityId, d.DocumentDate, "sales", SalesDocumentTypes.Delivery, d.Id, number,
            d.Lines.Select(l => StockLine(l, d.WarehouseId, -l.Quantity, InventoryMovementTypes.SalesIssue, order.StockReserved ? new ReservationRef(SalesDocumentTypes.Order, order.Id, l.SourceLineUid!.Value) : null))
                .ToList(), PostJournal: true, JournalDescription: $"Delivery {number}"), ct);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        foreach (var result in posted.Value.Lines)
        {
            var line = d.Line(result.LineId);
            var first = result.Movements.FirstOrDefault();
            line.RecordCost(Math.Abs(result.TotalCost), first?.MovementId, first?.BatchNumber);
        }

        var applied = SalesHandlerHelpers.Try(() =>
        {
            foreach (var line in d.Lines)
            {
                order.RegisterDelivery(line.SourceLineUid!.Value, line.Quantity);
            }

            d.TouchLines();
            d.MarkPosted(number, posted.Value.JournalId);
        });
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(d);
    }

    internal static InventoryPostingLine StockLine(SalesLine l, Guid warehouseId, decimal quantity, string movementType, ReservationRef? reservation, Guid? costSource = null) =>
        new(l.Uid, l.ProductId, l.VariantId, warehouseId, l.UomId, quantity, movementType,
            Batch: l.BatchNumber is null ? null : new BatchInput(l.BatchNumber),
            Serials: l.SerialList.Count > 0 ? l.SerialList : null,
            CostSourceMovementId: costSource,
            ConsumeReservation: reservation,
            CounterRole: Erp.Modules.Accounting.Contracts.PostingRoles.CostOfGoodsSold);
}

public sealed record ReverseDeliveryCommand(Guid Id, DateOnly? Date) : ICommand<SalesDocumentDto>;

public sealed class ReverseDeliveryHandler(ISalesDbContext db, IInventoryPostingService inventory, IClock clock) : ICommandHandler<ReverseDeliveryCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(ReverseDeliveryCommand c, CancellationToken ct)
    {
        var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (d is null)
        {
            return SalesAppErrors.NotFound;
        }

        var check = SalesHandlerHelpers.Try(d.Reverse);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var reversed = await inventory.ReverseAsync(SalesDocumentTypes.Delivery, d.Id, c.Date ?? DateOnly.FromDateTime(clock.UtcNow), ct);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        var order = await db.Orders.FirstAsync(o => o.Id == d.SalesOrderId, ct);
        var applied = SalesHandlerHelpers.Try(() =>
        {
            foreach (var line in d.Lines)
            {
                order.RegisterDelivery(line.SourceLineUid!.Value, -line.Quantity);
            }
        });
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(d);
    }
}

public sealed record CancelDeliveryCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class CancelDeliveryHandler(ISalesDbContext db) : ICommandHandler<CancelDeliveryCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CancelDeliveryCommand c, CancellationToken ct)
    {
        var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (d is null)
        {
            return SalesAppErrors.NotFound;
        }

        var result = SalesHandlerHelpers.Try(d.Cancel);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(d);
    }
}

public sealed record GetDeliveryQuery(Guid Id) : IQuery<SalesDocumentDto>;

public sealed class GetDeliveryHandler(ISalesDbContext db) : IQueryHandler<GetDeliveryQuery, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(GetDeliveryQuery query, CancellationToken ct)
    {
        var d = await db.Deliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.Id, ct);
        return d is null ? SalesAppErrors.NotFound : SalesMapping.ToDto(d);
    }
}

public sealed record ListDeliveriesQuery(SalesListFilter Filter) : IQuery<SalesListResult>;

public sealed class ListDeliveriesHandler(ISalesDbContext db) : IQueryHandler<ListDeliveriesQuery, SalesListResult>
{
    public async Task<Result<SalesListResult>> Handle(ListDeliveriesQuery query, CancellationToken ct)
    {
        var source = db.Deliveries.AsNoTracking();
        if (SalesHandlerHelpers.StatusIs<DeliveryStatus>(query.Filter.Status, out var status))
        {
            source = source.Where(x => x.Status == status);
        }

        return await SalesHandlerHelpers.ListAsync(source, query.Filter, SalesMapping.ToDto, ct);
    }
}
