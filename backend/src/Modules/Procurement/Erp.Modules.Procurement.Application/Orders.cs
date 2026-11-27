using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Configuration.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.Modules.Workflow.Contracts;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

// ===================================================================== DTOs
public sealed record RequestLineDto(Guid LineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, DateOnly? NeededBy, string? Notes);

public sealed record PurchaseRequestDto(Guid Id, Guid LegalEntityId, Guid? WarehouseId, string Number, DateOnly Date, string Status, string? Notes,
    string? RejectionReason, IReadOnlyList<RequestLineDto> Lines);

public sealed record QuotationLineDto(Guid LineId, Guid? RequestLineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal UnitPrice, int LeadTimeDays);

public sealed record QuotationDto(Guid Id, Guid? RequestId, Guid SupplierId, string Number, string? SupplierReference, DateOnly Date, DateOnly? ValidUntil,
    string Currency, string Status, decimal Total, IReadOnlyList<QuotationLineDto> Lines);

public sealed record QuotationOfferDto(Guid QuotationId, string Number, Guid SupplierId, string Currency, decimal UnitPrice, decimal UnitPriceFunctional,
    decimal Quantity, int LeadTimeDays, bool IsBest);

public sealed record QuotationComparisonLineDto(Guid ProductId, Guid? RequestLineId, IReadOnlyList<QuotationOfferDto> Offers);

public sealed record OrderLineDto(Guid LineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent,
    Guid? TaxGroupId, decimal NetAmount, decimal TaxAmount, decimal ReceivedQuantity, decimal InvoicedQuantity, decimal OpenQuantity, DateOnly? ExpectedDate, Guid? RequestLineId);

public sealed record PurchaseOrderDto(Guid Id, Guid LegalEntityId, Guid? BranchId, Guid WarehouseId, Guid SupplierId, string Number, DateOnly Date,
    DateOnly? ExpectedDate, string Currency, decimal ExchangeRate, string Status, decimal NetTotal, decimal TaxTotal, decimal GrandTotal, Guid? RequestId,
    string? Notes, IReadOnlyList<OrderLineDto> Lines);

public sealed record DocumentSummaryDto(Guid Id, string? Number, DateOnly Date, Guid? PartyId, string Status, decimal Total, string? Currency);

internal static class OrderMapping
{
    public static PurchaseRequestDto ToDto(PurchaseRequest r) => new(r.Id, r.LegalEntityId, r.WarehouseId, r.Number, r.Date, r.Status.ToString(), r.Notes,
        r.RejectionReason, r.Lines.Select(l => new RequestLineDto(l.LineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.NeededBy, l.Notes)).ToList());

    public static QuotationDto ToDto(PurchaseQuotation q) => new(q.Id, q.RequestId, q.SupplierId, q.Number, q.SupplierReference, q.Date, q.ValidUntil, q.Currency,
        q.Status.ToString(), q.Lines.Sum(l => l.Quantity * l.UnitPrice),
        q.Lines.Select(l => new QuotationLineDto(l.LineId, l.RequestLineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice, l.LeadTimeDays)).ToList());

    public static PurchaseOrderDto ToDto(PurchaseOrder o) => new(o.Id, o.LegalEntityId, o.BranchId, o.WarehouseId, o.SupplierId, o.Number, o.Date, o.ExpectedDate,
        o.Currency, o.ExchangeRate, o.Status.ToString(), o.NetTotal, o.TaxTotal, o.GrandTotal, o.RequestId, o.Notes,
        o.Lines.Select(l => new OrderLineDto(l.LineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxGroupId, l.NetAmount,
            l.TaxAmount, l.ReceivedQuantity, l.InvoicedQuantity, l.OpenQuantity, l.ExpectedDate, l.RequestLineId)).ToList());
}

/// <summary>Submits documents to the approval engine (Workflow.Contracts); NoApprovalRequired answers "not required".</summary>
public sealed class ProcurementApprovals(IApprovalService approvals, ICurrentUser user)
{
    public async Task<bool> RequiresApprovalAsync(string documentType, Guid id, string number, Guid companyId, decimal amount, string currency,
        IReadOnlyDictionary<string, decimal>? numericFacts, CancellationToken ct)
    {
        var decision = await approvals.SubmitAsync(new ApprovalRequest(documentType, id, number, companyId, amount, currency, user.UserId ?? Guid.Empty,
            NumericFacts: numericFacts), ct);
        return decision.Required;
    }
}

// ===================================================================== Purchase requests
public sealed record RequestLineInput(Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, DateOnly? NeededBy, string? Notes);

public sealed record CreatePurchaseRequestCommand(Guid LegalEntityId, Guid? WarehouseId, DateOnly Date, string? Notes, IReadOnlyList<RequestLineInput> Lines)
    : ICommand<PurchaseRequestDto>;

public sealed class CreatePurchaseRequestValidator : AbstractValidator<CreatePurchaseRequestCommand>
{
    public CreatePurchaseRequestValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.UomId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0);
        });
    }
}

public sealed class CreatePurchaseRequestHandler(IProcurementDbContext db, ProcurementScope scope, ProductLookup products, IDocumentNumberGenerator numbers, ITenantContext tenant)
    : ICommandHandler<CreatePurchaseRequestCommand, PurchaseRequestDto>
{
    public async Task<Result<PurchaseRequestDto>> Handle(CreatePurchaseRequestCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (command.WarehouseId is { } warehouseId && (await scope.WarehouseAsync(warehouseId, le.Value.Id, cancellationToken)).IsFailure)
        {
            return ProcurementAppErrors.WarehouseNotFound;
        }

        var catalog = await products.LoadAsync(command.Lines.Select(l => l.ProductId), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        if (command.Lines.Any(l => ProductLookup.Factor(catalog.Value[l.ProductId], l.UomId) is null))
        {
            return ProcurementAppErrors.UnitNotAllowed;
        }

        var number = await numbers.NextAsync(new NumberScope(tenant.CompanyId!.Value, "PurchaseRequest", "PR", le.Value.Id, FiscalYear: command.Date.Year), cancellationToken);
        var request = new PurchaseRequest(tenant.CompanyId.Value, le.Value.Id, command.WarehouseId, number, command.Date, command.Notes,
            command.Lines.Select(l => new RequestLine(Guid.Empty, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.NeededBy, l.Notes)));
        db.Requests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(request);
    }
}

public enum RequestAction
{
    Submit,
    Revise,
    Close,
}

public sealed record PurchaseRequestActionCommand(Guid Id, RequestAction Action) : ICommand<PurchaseRequestDto>;

public sealed class PurchaseRequestActionHandler(IProcurementDbContext db, ProcurementApprovals approvals)
    : ICommandHandler<PurchaseRequestActionCommand, PurchaseRequestDto>
{
    public async Task<Result<PurchaseRequestDto>> Handle(PurchaseRequestActionCommand command, CancellationToken cancellationToken)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (request is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        switch (command.Action)
        {
            case RequestAction.Submit:
                var required = await approvals.RequiresApprovalAsync(ApprovalDocumentTypes.PurchaseRequest, request.Id, request.Number, request.CompanyId,
                    0m, string.Empty, new Dictionary<string, decimal> { ["lineCount"] = request.Lines.Count }, cancellationToken);
                request.Submit(required);
                break;
            case RequestAction.Revise:
                request.Revise();
                break;
            case RequestAction.Close:
                request.Close();
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(request);
    }
}

public sealed record GetPurchaseRequestQuery(Guid Id) : IQuery<PurchaseRequestDto>;

public sealed class GetPurchaseRequestHandler(IProcurementDbContext db) : IQueryHandler<GetPurchaseRequestQuery, PurchaseRequestDto>
{
    public async Task<Result<PurchaseRequestDto>> Handle(GetPurchaseRequestQuery query, CancellationToken cancellationToken)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
        return request is null ? ProcurementAppErrors.NotFound : OrderMapping.ToDto(request);
    }
}

public sealed record ListPurchaseRequestsQuery(string? Status, PageRequest Page) : IQuery<PagedResult<DocumentSummaryDto>>;

public sealed class ListPurchaseRequestsHandler(IProcurementDbContext db) : IQueryHandler<ListPurchaseRequestsQuery, PagedResult<DocumentSummaryDto>>
{
    public async Task<Result<PagedResult<DocumentSummaryDto>>> Handle(ListPurchaseRequestsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Requests.AsNoTracking();
        if (Enum.TryParse<PurchaseRequestStatus>(query.Status, true, out var status))
        {
            source = source.Where(r => r.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(r => r.Date).ThenByDescending(r => r.Number).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(r => new DocumentSummaryDto(r.Id, r.Number, r.Date, null, r.Status.ToString(), 0m, null)).ToListAsync(cancellationToken);
        return new PagedResult<DocumentSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

// ===================================================================== Quotations
public sealed record QuotationLineInput(Guid? RequestLineId, Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal UnitPrice, int LeadTimeDays);

public sealed record CreateQuotationCommand(Guid? RequestId, Guid SupplierId, string? SupplierReference, DateOnly Date, DateOnly? ValidUntil, string? Currency,
    IReadOnlyList<QuotationLineInput> Lines) : ICommand<QuotationDto>;

public sealed class CreateQuotationValidator : AbstractValidator<CreateQuotationCommand>
{
    public CreateQuotationValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.LeadTimeDays).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed class CreateQuotationHandler(IProcurementDbContext db, IPartyQueries parties, ProductLookup products, IDocumentNumberGenerator numbers, ITenantContext tenant)
    : ICommandHandler<CreateQuotationCommand, QuotationDto>
{
    public async Task<Result<QuotationDto>> Handle(CreateQuotationCommand command, CancellationToken cancellationToken)
    {
        var supplier = await parties.GetSupplierAsync(command.SupplierId, cancellationToken);
        if (supplier is not { IsActive: true })
        {
            return ProcurementAppErrors.SupplierNotFound;
        }

        if (command.RequestId is { } requestId && !await db.Requests.AnyAsync(r => r.Id == requestId, cancellationToken))
        {
            return ProcurementAppErrors.NotFound;
        }

        var catalog = await products.LoadAsync(command.Lines.Select(l => l.ProductId), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        var number = await numbers.NextAsync(new NumberScope(tenant.CompanyId!.Value, "PurchaseQuotation", "PQ", FiscalYear: command.Date.Year), cancellationToken);
        var currency = string.IsNullOrWhiteSpace(command.Currency) ? supplier.Currency : command.Currency.Trim().ToUpperInvariant();
        var quotation = new PurchaseQuotation(tenant.CompanyId.Value, command.RequestId, supplier.Id, number, command.SupplierReference, command.Date, command.ValidUntil,
            currency, command.Lines.Select(l => new QuotationLine(Guid.Empty, l.RequestLineId, l.ProductId, l.VariantId, l.UomId, l.Quantity, l.UnitPrice, l.LeadTimeDays)));
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(quotation);
    }
}

public enum QuotationAction
{
    Receive,
    Accept,
    Reject,
}

public sealed record QuotationActionCommand(Guid Id, QuotationAction Action) : ICommand<QuotationDto>;

public sealed class QuotationActionHandler(IProcurementDbContext db) : ICommandHandler<QuotationActionCommand, QuotationDto>
{
    public async Task<Result<QuotationDto>> Handle(QuotationActionCommand command, CancellationToken cancellationToken)
    {
        var quotation = await db.Quotations.FirstOrDefaultAsync(q => q.Id == command.Id, cancellationToken);
        if (quotation is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        switch (command.Action)
        {
            case QuotationAction.Receive:
                quotation.MarkReceived();
                break;
            case QuotationAction.Accept:
                quotation.Accept();
                break;
            case QuotationAction.Reject:
                quotation.Reject();
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(quotation);
    }
}

public sealed record ListQuotationsQuery(Guid? RequestId, PageRequest Page) : IQuery<PagedResult<QuotationDto>>;

public sealed class ListQuotationsHandler(IProcurementDbContext db) : IQueryHandler<ListQuotationsQuery, PagedResult<QuotationDto>>
{
    public async Task<Result<PagedResult<QuotationDto>>> Handle(ListQuotationsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Quotations.AsNoTracking();
        if (query.RequestId is { } requestId)
        {
            source = source.Where(q => q.RequestId == requestId);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(q => q.Date).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<QuotationDto>(items.Select(OrderMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

/// <summary>Side-by-side comparison of the open quotations of a request, prices converted to the functional currency.</summary>
public sealed record CompareQuotationsQuery(Guid RequestId) : IQuery<IReadOnlyList<QuotationComparisonLineDto>>;

public sealed class CompareQuotationsHandler(IProcurementDbContext db, ProcurementScope scope) : IQueryHandler<CompareQuotationsQuery, IReadOnlyList<QuotationComparisonLineDto>>
{
    public async Task<Result<IReadOnlyList<QuotationComparisonLineDto>>> Handle(CompareQuotationsQuery query, CancellationToken cancellationToken)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.RequestId, cancellationToken);
        if (request is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        var le = await scope.LegalEntityAsync(request.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var quotations = await db.Quotations.AsNoTracking()
            .Where(q => q.RequestId == request.Id && q.Status != QuotationStatus.Rejected).ToListAsync(cancellationToken);
        var rates = new Dictionary<string, decimal?>();
        foreach (var currency in quotations.Select(q => q.Currency).Distinct())
        {
            var rate = await scope.RateAsync(currency, le.Value.FunctionalCurrency, request.Date, null, cancellationToken);
            rates[currency] = rate.IsSuccess ? rate.Value : null;
        }

        var offers = quotations.SelectMany(q => q.Lines.Select(l => (Quotation: q, Line: l))).ToList();
        var result = offers
            .GroupBy(o => (o.Line.ProductId, o.Line.RequestLineId))
            .Select(g =>
            {
                var list = g.Select(o =>
                {
                    var rate = rates[o.Quotation.Currency];
                    return new QuotationOfferDto(o.Quotation.Id, o.Quotation.Number, o.Quotation.SupplierId, o.Quotation.Currency, o.Line.UnitPrice,
                        rate is null ? 0 : Money.Cost(o.Line.UnitPrice * rate.Value), o.Line.Quantity, o.Line.LeadTimeDays, false);
                }).OrderBy(o => o.UnitPriceFunctional).ThenBy(o => o.LeadTimeDays).ToList();
                if (list.Count > 0 && list[0].UnitPriceFunctional > 0)
                {
                    list[0] = list[0] with { IsBest = true };
                }

                return new QuotationComparisonLineDto(g.Key.ProductId, g.Key.RequestLineId, list);
            }).ToList();
        return result;
    }
}

// ===================================================================== Purchase orders
public sealed record OrderLineInput(Guid ProductId, Guid? VariantId, Guid UomId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, Guid? TaxGroupId,
    Guid? RequestLineId, DateOnly? ExpectedDate);

public sealed record CreatePurchaseOrderCommand(Guid LegalEntityId, Guid? BranchId, Guid WarehouseId, Guid SupplierId, DateOnly Date, DateOnly? ExpectedDate,
    string? Currency, decimal? ExchangeRate, Guid? RequestId, string? Notes, IReadOnlyList<OrderLineInput> Lines) : ICommand<PurchaseOrderDto>;

public sealed class CreatePurchaseOrderValidator : AbstractValidator<CreatePurchaseOrderCommand>
{
    public CreatePurchaseOrderValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.UomId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.DiscountPercent).InclusiveBetween(0, 100);
        });
    }
}

public sealed class CreatePurchaseOrderHandler(
    IProcurementDbContext db,
    ProcurementScope scope,
    ProductLookup products,
    PurchaseTax taxes,
    IPartyQueries parties,
    IDocumentNumberGenerator numbers,
    ITenantContext tenant) : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(CreatePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if ((await scope.WarehouseAsync(command.WarehouseId, le.Value.Id, cancellationToken)).IsFailure)
        {
            return ProcurementAppErrors.WarehouseNotFound;
        }

        var supplier = await parties.GetSupplierAsync(command.SupplierId, cancellationToken);
        if (supplier is not { IsActive: true })
        {
            return ProcurementAppErrors.SupplierNotFound;
        }

        PurchaseRequest? request = null;
        if (command.RequestId is { } requestId)
        {
            request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
            if (request is null)
            {
                return ProcurementAppErrors.NotFound;
            }
        }

        var currency = string.IsNullOrWhiteSpace(command.Currency) ? supplier.Currency : command.Currency.Trim().ToUpperInvariant();
        var rate = await scope.RateAsync(currency, le.Value.FunctionalCurrency, command.Date, command.ExchangeRate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var lines = await OrderLines.BuildAsync(command.Lines, products, taxes, scope, currency, command.Date, cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error;
        }

        var number = await numbers.NextAsync(new NumberScope(tenant.CompanyId!.Value, "PurchaseOrder", "PO", le.Value.Id, command.BranchId, FiscalYear: command.Date.Year), cancellationToken);
        var order = new PurchaseOrder(tenant.CompanyId.Value, le.Value.Id, command.BranchId, command.WarehouseId, supplier.Id, number, command.Date, command.ExpectedDate,
            currency, rate.Value, supplier.PaymentTermsId, command.RequestId, command.Notes, lines.Value);
        db.Orders.Add(order);
        request?.MarkOrdered();
        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(order);
    }
}

internal static class OrderLines
{
    public static async Task<Result<IReadOnlyList<PurchaseOrderLine>>> BuildAsync(IReadOnlyList<OrderLineInput> inputs, ProductLookup products, PurchaseTax taxes,
        ProcurementScope scope, string currency, DateOnly date, CancellationToken ct)
    {
        var catalog = await products.LoadAsync(inputs.Select(l => l.ProductId), ct);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        var decimals = await scope.MinorUnitsAsync(currency, ct);
        var result = new List<PurchaseOrderLine>();
        foreach (var input in inputs)
        {
            var product = catalog.Value[input.ProductId];
            if (ProductLookup.Factor(product, input.UomId) is null)
            {
                return ProcurementAppErrors.UnitNotAllowed;
            }

            var taxGroup = input.TaxGroupId ?? await taxes.ResolveAsync(product.TaxCategory, ct);
            var net = Money.Round(input.Quantity * input.UnitPrice * (1 - (input.DiscountPercent / 100m)), decimals);
            var tax = await taxes.CalculateAsync(taxGroup, net, currency, date, ct);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            result.Add(new PurchaseOrderLine(Guid.Empty, product.Id, input.VariantId, input.UomId, input.Quantity, input.UnitPrice, input.DiscountPercent, taxGroup,
                net, tax.Value.Recoverable + tax.Value.NonRecoverable, input.RequestLineId, input.ExpectedDate));
        }

        return result;
    }
}

public sealed record UpdatePurchaseOrderLinesCommand(Guid Id, IReadOnlyList<OrderLineInput> Lines) : ICommand<PurchaseOrderDto>;

public sealed class UpdatePurchaseOrderLinesHandler(IProcurementDbContext db, ProductLookup products, PurchaseTax taxes, ProcurementScope scope)
    : ICommandHandler<UpdatePurchaseOrderLinesCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(UpdatePurchaseOrderLinesCommand command, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == command.Id, cancellationToken);
        if (order is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        var lines = await OrderLines.BuildAsync(command.Lines, products, taxes, scope, order.Currency, order.Date, cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error;
        }

        order.SetLines(lines.Value);
        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(order);
    }
}

public enum OrderAction
{
    Submit,
    Send,
    Cancel,
    Close,
}

public sealed record PurchaseOrderActionCommand(Guid Id, OrderAction Action) : ICommand<PurchaseOrderDto>;

public sealed class PurchaseOrderActionHandler(IProcurementDbContext db, ProcurementApprovals approvals) : ICommandHandler<PurchaseOrderActionCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(PurchaseOrderActionCommand command, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == command.Id, cancellationToken);
        if (order is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        switch (command.Action)
        {
            case OrderAction.Submit:
                var required = await approvals.RequiresApprovalAsync(ApprovalDocumentTypes.PurchaseOrder, order.Id, order.Number, order.CompanyId,
                    order.GrandTotal, order.Currency, new Dictionary<string, decimal> { ["amountFunctional"] = Money.Round(order.GrandTotal * order.ExchangeRate) },
                    cancellationToken);
                order.Submit(required);
                break;
            case OrderAction.Send:
                order.Send();
                break;
            case OrderAction.Cancel:
                order.Cancel();
                break;
            case OrderAction.Close:
                order.Close();
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return OrderMapping.ToDto(order);
    }
}

public sealed record GetPurchaseOrderQuery(Guid Id) : IQuery<PurchaseOrderDto>;

public sealed class GetPurchaseOrderHandler(IProcurementDbContext db) : IQueryHandler<GetPurchaseOrderQuery, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(GetPurchaseOrderQuery query, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == query.Id, cancellationToken);
        return order is null ? ProcurementAppErrors.NotFound : OrderMapping.ToDto(order);
    }
}

public sealed record ListPurchaseOrdersQuery(Guid? SupplierId, string? Status, PageRequest Page) : IQuery<PagedResult<DocumentSummaryDto>>;

public sealed class ListPurchaseOrdersHandler(IProcurementDbContext db) : IQueryHandler<ListPurchaseOrdersQuery, PagedResult<DocumentSummaryDto>>
{
    public async Task<Result<PagedResult<DocumentSummaryDto>>> Handle(ListPurchaseOrdersQuery query, CancellationToken cancellationToken)
    {
        var source = db.Orders.AsNoTracking();
        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(o => o.SupplierId == supplierId);
        }

        if (Enum.TryParse<PurchaseOrderStatus>(query.Status, true, out var status))
        {
            source = source.Where(o => o.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(o => o.Date).ThenByDescending(o => o.Number).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(o => new DocumentSummaryDto(o.Id, o.Number, o.Date, o.SupplierId, o.Status.ToString(), o.GrandTotal, o.Currency)).ToListAsync(cancellationToken);
        return new PagedResult<DocumentSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

/// <summary>Reads the company's procurement policy (tolerances, invoice-before-receipt).</summary>
public sealed class ProcurementPolicy(IPolicyReader policies)
{
    public Task<ProcurementPolicySettings> GetAsync(Guid companyId, CancellationToken ct) => policies.GetProcurementPolicyAsync(companyId, ct);
}
