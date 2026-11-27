using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.Modules.Workflow.Contracts;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

public sealed record InvoiceLineDto(Guid LineId, string Kind, Guid? GoodsReceiptId, Guid? GoodsReceiptLineId, Guid? PurchaseOrderId, Guid? PurchaseOrderLineId,
    Guid? ProductId, Guid? AccountId, string? Description, decimal Quantity, decimal UnitPrice, Guid? TaxGroupId, decimal NetAmount, decimal RecoverableTax,
    decimal NonRecoverableTax, decimal OrderUnitPrice, decimal PriceVariancePercent, bool QuantityException, bool PriceException, decimal BaseNet,
    decimal GrniBase, decimal VarianceToInventory, decimal VarianceToPpv, decimal FxDifference = 0);

public sealed record PurchaseInvoiceDto(Guid Id, Guid LegalEntityId, Guid SupplierId, string SupplierInvoiceNumber, string? Number, DateOnly InvoiceDate,
    DateOnly? PostingDate, string Currency, decimal ExchangeRate, string Status, string MatchStatus, decimal NetTotal, decimal TaxTotal, decimal GrandTotal,
    decimal BaseTotal, decimal OpenAmount, Guid? JournalId, string? Notes, string? CancelReason, IReadOnlyList<InvoiceLineDto> Lines);

internal static class InvoiceMapping
{
    public static InvoiceLineDto ToDto(PurchaseInvoiceLine l) => new(l.LineId, l.Kind.ToString(), l.GoodsReceiptId, l.GoodsReceiptLineId, l.PurchaseOrderId,
        l.PurchaseOrderLineId, l.ProductId, l.AccountId, l.Description, l.Quantity, l.UnitPrice, l.TaxGroupId, l.NetAmount, l.RecoverableTax, l.NonRecoverableTax,
        l.OrderUnitPrice, l.PriceVariancePercent, l.QuantityException, l.PriceException, l.BaseNet, l.GrniBase, l.VarianceToInventory, l.VarianceToPpv, l.FxDifference);

    public static PurchaseInvoiceDto ToDto(PurchaseInvoice i) => new(i.Id, i.LegalEntityId, i.SupplierId, i.SupplierInvoiceNumber, i.Number, i.InvoiceDate,
        i.PostingDate, i.Currency, i.ExchangeRate, i.Status.ToString(), i.MatchStatus.ToString(), i.NetTotal, i.TaxTotal, i.GrandTotal, i.BaseTotal, i.OpenAmount,
        i.JournalId, i.Notes, i.CancelReason, i.Lines.Select(ToDto).ToList());
}

/// <summary><see cref="Kind"/> is Receipt, Order or Expense.</summary>
public sealed record InvoiceLineInput(string Kind, Guid? GoodsReceiptId, Guid? GoodsReceiptLineId, Guid? PurchaseOrderId, Guid? PurchaseOrderLineId,
    Guid? AccountId, string? Description, decimal Quantity, decimal UnitPrice, Guid? TaxGroupId);

public sealed record CreatePurchaseInvoiceCommand(Guid LegalEntityId, Guid SupplierId, string SupplierInvoiceNumber, DateOnly InvoiceDate, string? Currency,
    decimal? ExchangeRate, string? Notes, IReadOnlyList<InvoiceLineInput> Lines) : ICommand<PurchaseInvoiceDto>;

public sealed class CreatePurchaseInvoiceValidator : AbstractValidator<CreatePurchaseInvoiceCommand>
{
    public CreatePurchaseInvoiceValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.SupplierInvoiceNumber).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Kind).Must(k => Enum.TryParse<InvoiceLineKind>(k, true, out var kind) && Enum.IsDefined(kind))
                .WithMessage("Kind must be Receipt, Order or Expense.");
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>Builds and validates invoice lines against receipts, orders and the chart of accounts.</summary>
public sealed class InvoiceLineBuilder(IProcurementDbContext db, PurchaseTax taxes, ProcurementScope scope, ProcurementPolicy policy, IAccountQueries accounts)
{
    public async Task<Result<IReadOnlyList<PurchaseInvoiceLine>>> BuildAsync(Guid companyId, Guid legalEntityId, Guid supplierId, string currency, DateOnly date,
        IReadOnlyList<InvoiceLineInput> inputs, CancellationToken ct)
    {
        var decimals = await scope.MinorUnitsAsync(currency, ct);
        var settings = await policy.GetAsync(companyId, ct);
        var receiptIds = inputs.Where(l => l.GoodsReceiptId is not null).Select(l => l.GoodsReceiptId!.Value).Distinct().ToList();
        var orderIds = inputs.Where(l => l.PurchaseOrderId is not null).Select(l => l.PurchaseOrderId!.Value).Distinct().ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var receiptOrderIds = receipts.Values.Select(r => r.PurchaseOrderId).ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id) || receiptOrderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        var lines = new List<PurchaseInvoiceLine>();
        foreach (var input in inputs)
        {
            Guid? productId = null;
            Guid? taxGroup = input.TaxGroupId;
            Guid? receiptId = null, receiptLineId = null, orderId = null, orderLineId = null, accountId = null;
            var kind = Enum.Parse<InvoiceLineKind>(input.Kind, true);
            switch (kind)
            {
                case InvoiceLineKind.Receipt:
                    if (input.GoodsReceiptId is null || !receipts.TryGetValue(input.GoodsReceiptId.Value, out var receipt))
                    {
                        return ProcurementAppErrors.NotFound;
                    }

                    if (receipt.Status != GoodsReceiptStatus.Posted)
                    {
                        return ProcurementAppErrors.ReceiptNotPosted;
                    }

                    var order = orders[receipt.PurchaseOrderId];
                    if (receipt.SupplierId != supplierId || receipt.LegalEntityId != legalEntityId)
                    {
                        return ProcurementAppErrors.SupplierMismatch;
                    }

                    if (!string.Equals(order.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    {
                        return ProcurementAppErrors.CurrencyMismatch;
                    }

                    var receiptLine = receipt.Lines.FirstOrDefault(l => l.LineId == input.GoodsReceiptLineId);
                    if (receiptLine is null)
                    {
                        return Error.NotFound("procurement.line-not-found", "A referenced line was not found.");
                    }

                    if (receiptLine.CounterRole == GoodsReceipt.GoodsInvoicedNotReceivedRole)
                    {
                        return ProcurementAppErrors.QuantityExceedsInvoiceable;
                    }

                    productId = receiptLine.ProductId;
                    taxGroup ??= order.Line(receiptLine.PurchaseOrderLineId).TaxGroupId;
                    (receiptId, receiptLineId, orderId, orderLineId) = (receipt.Id, receiptLine.LineId, order.Id, receiptLine.PurchaseOrderLineId);
                    break;
                case InvoiceLineKind.Order:
                    if (settings.ReceiptBeforeInvoice)
                    {
                        return ProcurementAppErrors.InvoiceBeforeReceiptDisabled;
                    }

                    if (input.PurchaseOrderId is null || !orders.TryGetValue(input.PurchaseOrderId.Value, out var poOrder))
                    {
                        return ProcurementAppErrors.NotFound;
                    }

                    if (poOrder.SupplierId != supplierId || poOrder.LegalEntityId != legalEntityId)
                    {
                        return ProcurementAppErrors.SupplierMismatch;
                    }

                    if (!string.Equals(poOrder.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    {
                        return ProcurementAppErrors.CurrencyMismatch;
                    }

                    if (!poOrder.CanReceive && poOrder.Status != PurchaseOrderStatus.FullyReceived)
                    {
                        return ProcurementAppErrors.OrderNotReceivable;
                    }

                    var poLine = poOrder.Lines.FirstOrDefault(l => l.LineId == input.PurchaseOrderLineId);
                    if (poLine is null)
                    {
                        return Error.NotFound("procurement.line-not-found", "A referenced line was not found.");
                    }

                    productId = poLine.ProductId;
                    taxGroup ??= poLine.TaxGroupId;
                    (orderId, orderLineId) = (poOrder.Id, poLine.LineId);
                    break;
                case InvoiceLineKind.Expense:
                    if (input.AccountId is null)
                    {
                        return ProcurementAppErrors.InvalidExpenseAccount;
                    }

                    var account = await accounts.GetAccountAsync(input.AccountId.Value, ct);
                    if (account is not { IsActive: true, IsPostingAccount: true, IsControl: false } || account.Type is not ("Expense" or "Asset"))
                    {
                        return ProcurementAppErrors.InvalidExpenseAccount;
                    }

                    accountId = account.Id;
                    break;
            }

            var net = Money.Round(input.Quantity * input.UnitPrice, decimals);
            var tax = await taxes.CalculateAsync(taxGroup, net, currency, date, ct);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            lines.Add(new PurchaseInvoiceLine(Guid.Empty, kind, receiptId, receiptLineId, orderId, orderLineId, productId, accountId, input.Description,
                input.Quantity, input.UnitPrice, taxGroup, net, tax.Value.Recoverable, tax.Value.NonRecoverable));
        }

        return lines;
    }
}

public sealed class CreatePurchaseInvoiceHandler(IProcurementDbContext db, ProcurementScope scope, IPartyQueries parties, InvoiceLineBuilder builder, ITenantContext tenant)
    : ICommandHandler<CreatePurchaseInvoiceCommand, PurchaseInvoiceDto>
{
    public async Task<Result<PurchaseInvoiceDto>> Handle(CreatePurchaseInvoiceCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var supplier = await parties.GetSupplierAsync(command.SupplierId, cancellationToken);
        if (supplier is not { IsActive: true })
        {
            return ProcurementAppErrors.SupplierNotFound;
        }

        var supplierNumber = command.SupplierInvoiceNumber.Trim();
        if (await InvoiceRules.IsDuplicateAsync(db, supplier.Id, supplierNumber, null, cancellationToken))
        {
            return ProcurementAppErrors.DuplicateSupplierInvoice;
        }

        var currency = string.IsNullOrWhiteSpace(command.Currency) ? supplier.Currency : command.Currency.Trim().ToUpperInvariant();
        var rate = await scope.RateAsync(currency, le.Value.FunctionalCurrency, command.InvoiceDate, command.ExchangeRate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var lines = await builder.BuildAsync(tenant.CompanyId!.Value, le.Value.Id, supplier.Id, currency, command.InvoiceDate, command.Lines, cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error;
        }

        var invoice = new PurchaseInvoice(tenant.CompanyId.Value, le.Value.Id, supplier.Id, supplierNumber, command.InvoiceDate, currency, rate.Value,
            supplier.PaymentTermsId, command.Notes, lines.Value);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return InvoiceMapping.ToDto(invoice);
    }
}

internal static class InvoiceRules
{
    public static Task<bool> IsDuplicateAsync(IProcurementDbContext db, Guid supplierId, string supplierInvoiceNumber, Guid? excludeId, CancellationToken ct) =>
        db.Invoices.AnyAsync(i => i.SupplierId == supplierId && i.SupplierInvoiceNumber == supplierInvoiceNumber && i.Status != PurchaseInvoiceStatus.Cancelled
            && (excludeId == null || i.Id != excludeId), ct);
}

/// <summary>Three-way match of the invoice lines against receipts (quantity) and orders (price) using the company tolerances.</summary>
public sealed class InvoiceMatcher(IProcurementDbContext db, ProcurementPolicy policy)
{
    public async Task<IReadOnlyList<PurchaseInvoiceLine>> MatchAsync(PurchaseInvoice invoice, CancellationToken ct)
    {
        var settings = await policy.GetAsync(invoice.CompanyId, ct);
        var receiptIds = invoice.Lines.Where(l => l.GoodsReceiptId is not null).Select(l => l.GoodsReceiptId!.Value).Distinct().ToList();
        var orderIds = invoice.Lines.Where(l => l.PurchaseOrderId is not null).Select(l => l.PurchaseOrderId!.Value).Distinct().ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        // Several invoice lines may draw on the same receipt/order line: consume the invoiceable quantity in line order.
        var used = new Dictionary<Guid, decimal>();
        return invoice.Lines.Select(line =>
        {
            switch (line.Kind)
            {
                case InvoiceLineKind.Receipt:
                {
                    var receiptLine = receipts[line.GoodsReceiptId!.Value].Line(line.GoodsReceiptLineId!.Value);
                    var available = receiptLine.InvoiceableQuantity - used.GetValueOrDefault(receiptLine.LineId);
                    used[receiptLine.LineId] = used.GetValueOrDefault(receiptLine.LineId) + line.Quantity;
                    var match = ThreeWayMatch.Evaluate(line.Quantity, line.UnitPrice, Math.Max(0, available), receiptLine.UnitPrice,
                        settings.QuantityTolerancePercent, settings.PriceTolerancePercent);
                    return line with
                    {
                        OrderUnitPrice = receiptLine.UnitPrice, PriceVariancePercent = match.PriceVariancePercent, QuantityException = match.QuantityException,
                        PriceException = match.PriceException,
                    };
                }

                case InvoiceLineKind.Order:
                {
                    var orderLine = orders[line.PurchaseOrderId!.Value].Line(line.PurchaseOrderLineId!.Value);
                    var available = orderLine.Quantity - orderLine.InvoicedQuantity - used.GetValueOrDefault(orderLine.LineId);
                    used[orderLine.LineId] = used.GetValueOrDefault(orderLine.LineId) + line.Quantity;
                    var match = ThreeWayMatch.Evaluate(line.Quantity, line.UnitPrice, Math.Max(0, available), orderLine.NetUnitPrice,
                        settings.QuantityTolerancePercent, settings.PriceTolerancePercent);
                    return line with
                    {
                        OrderUnitPrice = orderLine.NetUnitPrice, PriceVariancePercent = match.PriceVariancePercent, QuantityException = match.QuantityException,
                        PriceException = match.PriceException,
                    };
                }

                default:
                    return line with { QuantityException = false, PriceException = false };
            }
        }).ToList();
    }
}

public sealed record InvoiceMatchDto(Guid InvoiceId, string MatchStatus, decimal QuantityTolerancePercent, decimal PriceTolerancePercent, IReadOnlyList<InvoiceLineDto> Lines);

public sealed record PreviewInvoiceMatchQuery(Guid Id) : IQuery<InvoiceMatchDto>;

public sealed class PreviewInvoiceMatchHandler(IProcurementDbContext db, InvoiceMatcher matcher, ProcurementPolicy policy) : IQueryHandler<PreviewInvoiceMatchQuery, InvoiceMatchDto>
{
    public async Task<Result<InvoiceMatchDto>> Handle(PreviewInvoiceMatchQuery query, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == query.Id, cancellationToken);
        if (invoice is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        var lines = await matcher.MatchAsync(invoice, cancellationToken);
        var settings = await policy.GetAsync(invoice.CompanyId, cancellationToken);
        var status = lines.Any(l => l.QuantityException || l.PriceException) ? MatchStatus.Exceptions : MatchStatus.Matched;
        return new InvoiceMatchDto(invoice.Id, status.ToString(), settings.QuantityTolerancePercent, settings.PriceTolerancePercent,
            lines.Select(InvoiceMapping.ToDto).ToList());
    }
}

/// <summary>Matches the invoice and submits it to approval; an invoice with match exceptions needs an approval rule.</summary>
public sealed record SubmitPurchaseInvoiceCommand(Guid Id) : ICommand<PurchaseInvoiceDto>;

public sealed class SubmitPurchaseInvoiceHandler(IProcurementDbContext db, InvoiceMatcher matcher, ProcurementApprovals approvals)
    : ICommandHandler<SubmitPurchaseInvoiceCommand, PurchaseInvoiceDto>
{
    public async Task<Result<PurchaseInvoiceDto>> Handle(SubmitPurchaseInvoiceCommand command, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        var lines = await matcher.MatchAsync(invoice, cancellationToken);
        var exceptions = lines.Any(l => l.QuantityException || l.PriceException);
        var required = await approvals.RequiresApprovalAsync(ApprovalDocumentTypes.PurchaseInvoice, invoice.Id, invoice.SupplierInvoiceNumber, invoice.CompanyId,
            invoice.GrandTotal, invoice.Currency,
            new Dictionary<string, decimal>
            {
                ["matchExceptions"] = exceptions ? 1 : 0,
                ["maxPriceVariancePercent"] = lines.Count == 0 ? 0 : lines.Max(l => Math.Abs(l.PriceVariancePercent)),
                ["amountFunctional"] = Money.Round(invoice.GrandTotal * invoice.ExchangeRate),
            }, cancellationToken);
        invoice.Submit(lines, required);
        await db.SaveChangesAsync(cancellationToken);
        return InvoiceMapping.ToDto(invoice);
    }
}

/// <summary>
/// Posts an approved supplier invoice in one transaction (functional currency, balanced to the open item's base amount):
/// Dr GRNI (receipt value) ± price variance (on-hand share → Inventory via cost adjustment, consumed share → Purchase price
/// variance), Dr Goods invoiced not received (order lines), Dr expense accounts, Dr Input tax (recoverable; non-recoverable tax
/// is part of the cost) / Cr Supplier payable; then the AP open items with the supplier's payment terms.
/// </summary>
public sealed record PostPurchaseInvoiceCommand(Guid Id, DateOnly? PostingDate) : ICommand<PurchaseInvoiceDto>;

public sealed class PostPurchaseInvoiceHandler(
    IProcurementDbContext db,
    ProcurementScope scope,
    ProductLookup products,
    PurchaseTax taxes,
    IPartyQueries parties,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostPurchaseInvoiceCommand, PurchaseInvoiceDto>
{
    public async Task<Result<PurchaseInvoiceDto>> Handle(PostPurchaseInvoiceCommand command, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (invoice.IsPosted)
        {
            return InvoiceMapping.ToDto(invoice);
        }

        if (invoice.Status != PurchaseInvoiceStatus.Approved)
        {
            return ProcurementErrors.InvalidState("purchase invoice", invoice.Status);
        }

        if (await InvoiceRules.IsDuplicateAsync(db, invoice.SupplierId, invoice.SupplierInvoiceNumber, invoice.Id, cancellationToken))
        {
            return ProcurementAppErrors.DuplicateSupplierInvoice;
        }

        var le = await scope.LegalEntityAsync(invoice.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var supplier = await parties.GetSupplierAsync(invoice.SupplierId, cancellationToken);
        if (supplier is null)
        {
            return ProcurementAppErrors.SupplierNotFound;
        }

        var postingDate = command.PostingDate ?? invoice.InvoiceDate;
        var receiptIds = invoice.Lines.Where(l => l.GoodsReceiptId is not null).Select(l => l.GoodsReceiptId!.Value).Distinct().ToList();
        var orderIds = invoice.Lines.Where(l => l.PurchaseOrderId is not null).Select(l => l.PurchaseOrderId!.Value).Distinct().ToList();
        var receipts = await db.Receipts.Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var orders = await db.Orders.Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        var catalog = await products.LoadAsync(invoice.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        // Transaction-currency buckets: one cost bucket per line (net + non-recoverable tax) and one per recoverable tax code.
        var buckets = new List<(int Line, Guid? TaxCodeId, decimal Amount)>();
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            var line = invoice.Lines[i];
            var tax = await taxes.CalculateAsync(line.TaxGroupId, line.NetAmount, invoice.Currency, invoice.InvoiceDate, cancellationToken);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            buckets.Add((i, null, line.NetAmount + line.NonRecoverableTax));
            buckets.AddRange(tax.Value.Lines.Where(t => t.Recoverable && t.Amount != 0).GroupBy(t => t.TaxCodeId).Select(g => (i, (Guid?)g.Key, g.Sum(t => t.Amount))));
        }

        var baseTotal = Money.Round(invoice.GrandTotal * invoice.ExchangeRate);
        var baseParts = BaseAmounts.FitToTotal(buckets.Select(b => b.Amount).ToList(), invoice.ExchangeRate, baseTotal);
        var journal = new JournalBuilder();
        var postedLines = new List<PurchaseInvoiceLine>();
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            var line = invoice.Lines[i];
            var costBase = baseParts[buckets.FindIndex(b => b.Line == i && b.TaxCodeId is null)];
            var posted = line with { BaseNet = costBase };
            var product = line.ProductId is { } productId ? catalog.Value[productId] : null;
            switch (line.Kind)
            {
                case InvoiceLineKind.Receipt:
                {
                    var receipt = receipts[line.GoodsReceiptId!.Value];
                    var receiptLine = receipt.Line(line.GoodsReceiptLineId!.Value);
                    var relief = receipt.ApplyInvoice(receiptLine.LineId, line.Quantity, line.UnitPrice, invoice.ExchangeRate, line.TaxGroupId);
                    orders.TryGetValue(receipt.PurchaseOrderId, out var order);
                    order ??= await db.Orders.FirstAsync(o => o.Id == receipt.PurchaseOrderId, cancellationToken);
                    orders[order.Id] = order;
                    order.ApplyInvoice(receiptLine.PurchaseOrderLineId, line.Quantity);

                    // The receipt was valued at its own rate; the part of the difference caused by the rate moving between receipt and
                    // invoice is realised FX, only the price difference (at the receipt rate) adjusts inventory / goes to PPV.
                    var fx = 0m;
                    if (receiptLine.CounterRole != GoodsReceipt.GoodsInvoicedNotReceivedRole && receipt.ExchangeRate > 0 && receipt.ExchangeRate != invoice.ExchangeRate)
                    {
                        var bucket = buckets.First(b => b.Line == i && b.TaxCodeId is null).Amount;
                        fx = costBase - Money.Round(bucket * receipt.ExchangeRate);
                    }

                    var variance = costBase - fx - relief;
                    decimal toInventory = 0, toPpv = variance;
                    if (variance != 0 && receiptLine.MovementId is { } movementId)
                    {
                        var adjusted = await inventory.AdjustCostAsync(new CostAdjustmentRequest(movementId, variance, postingDate, SourceModules.Procurement,
                            ProcurementDocumentTypes.PurchaseInvoice, invoice.Id, line.LineId), cancellationToken);
                        if (adjusted.IsFailure)
                        {
                            return adjusted.Error;
                        }

                        (toInventory, toPpv) = (adjusted.Value.AppliedToInventory, adjusted.Value.ToVariance);
                    }

                    journal.Add(PostingRoles.GoodsReceivedNotInvoiced, relief, ProcurementPostingContext.Product(product!), receipt.Number);
                    journal.Add(PostingRoles.Inventory, toInventory, ProcurementPostingContext.Stock(product!, receipt.WarehouseId), product!.Sku);
                    journal.Add(PostingRoles.PurchasePriceVariance, toPpv, ProcurementPostingContext.Product(product), product.Sku);
                    journal.Add(fx > 0 ? PostingRoles.RealizedFxLoss : PostingRoles.RealizedFxGain, fx, null, $"FX receipt {receipt.Number} → invoice");
                    posted = posted with { GrniBase = relief, VarianceToInventory = toInventory, VarianceToPpv = toPpv, FxDifference = fx };
                    break;
                }

                case InvoiceLineKind.Order:
                {
                    var order = orders[line.PurchaseOrderId!.Value];
                    order.ApplyInvoiceBeforeReceipt(line.PurchaseOrderLineId!.Value, line.Quantity, costBase,
                        line.Quantity == 0 ? 0 : Money.Cost(line.NetAmount / line.Quantity), invoice.ExchangeRate, line.TaxGroupId);
                    journal.Add(PostingRoles.GoodsInvoicedNotReceived, costBase, ProcurementPostingContext.Product(product!), order.Number);
                    break;
                }

                default:
                    journal.AddAccount(line.AccountId!.Value, costBase, line.Description);
                    break;
            }

            foreach (var (index, bucket) in buckets.Select((b, index) => (index, b)).Where(x => x.b.Line == i && x.b.TaxCodeId is not null))
            {
                journal.Add(PostingRoles.InputTax, baseParts[index], ProcurementPostingContext.Tax(bucket.TaxCodeId!.Value));
            }

            postedLines.Add(posted);
        }

        journal.Add(PostingRoles.SupplierPayable, -baseTotal, ProcurementPostingContext.Party(supplier.PostingGroup), invoice.SupplierInvoiceNumber,
            nameof(PartyKind.Supplier), supplier.Id);

        var number = await numbers.NextAsync(new NumberScope(invoice.CompanyId, "PurchaseInvoice", "PI", invoice.LegalEntityId, FiscalYear: postingDate.Year),
            cancellationToken);
        var journalResult = await journals.PostAsync(journal.Build(invoice.LegalEntityId, postingDate, ProcurementDocumentTypes.PurchaseInvoice, invoice.Id,
            "invoice", $"Supplier invoice {invoice.SupplierInvoiceNumber} ({number})", le.Value.FunctionalCurrency), cancellationToken);
        if (journalResult.IsFailure)
        {
            return journalResult.Error;
        }

        var openItems = await subledger.CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Supplier, supplier.Id, invoice.LegalEntityId, SourceModules.Procurement,
            ProcurementDocumentTypes.PurchaseInvoice, invoice.Id, number, invoice.InvoiceDate, invoice.Currency, invoice.ExchangeRate, invoice.GrandTotal,
            invoice.PaymentTermsId), cancellationToken);
        if (openItems.IsFailure)
        {
            return openItems.Error;
        }

        invoice.MarkPosted(number, postingDate, postedLines, baseTotal, journalResult.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return InvoiceMapping.ToDto(invoice);
    }
}

/// <summary>
/// Cancels an invoice. A posted invoice is cancelled by reversal: open items cancelled (refused once paid), journal and cost
/// adjustments reversed, receipt/order invoiced quantities restored.
/// </summary>
public sealed record CancelPurchaseInvoiceCommand(Guid Id, string? Reason, DateOnly? Date) : ICommand<PurchaseInvoiceDto>;

public sealed class CancelPurchaseInvoiceHandler(
    IProcurementDbContext db,
    IApprovalService approvals,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IClock clock) : ICommandHandler<CancelPurchaseInvoiceCommand, PurchaseInvoiceDto>
{
    public async Task<Result<PurchaseInvoiceDto>> Handle(CancelPurchaseInvoiceCommand command, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);
        if (invoice is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (invoice.Status == PurchaseInvoiceStatus.Cancelled)
        {
            return InvoiceMapping.ToDto(invoice);
        }

        if (!invoice.IsPosted)
        {
            if (invoice.Status == PurchaseInvoiceStatus.PendingApproval)
            {
                await approvals.WithdrawAsync(ApprovalDocumentTypes.PurchaseInvoice, invoice.Id, cancellationToken);
            }

            invoice.Cancel(command.Reason ?? "Cancelled");
            await db.SaveChangesAsync(cancellationToken);
            return InvoiceMapping.ToDto(invoice);
        }

        invoice.Cancel(command.Reason ?? string.Empty);
        var date = command.Date ?? DateOnly.FromDateTime(clock.UtcNow);
        var cancelled = await subledger.CancelOpenItemsAsync(ProcurementDocumentTypes.PurchaseInvoice, invoice.Id, cancellationToken);
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        var receiptIds = invoice.Lines.Where(l => l.GoodsReceiptId is not null).Select(l => l.GoodsReceiptId!.Value).Distinct().ToList();
        var orderIds = invoice.Lines.Where(l => l.PurchaseOrderId is not null).Select(l => l.PurchaseOrderId!.Value).Distinct().ToList();
        var receipts = await db.Receipts.Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var orders = await db.Orders.Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        foreach (var line in invoice.Lines)
        {
            if (line.Kind == InvoiceLineKind.Receipt)
            {
                var receipt = receipts[line.GoodsReceiptId!.Value];
                receipt.RevertInvoice(line.GoodsReceiptLineId!.Value, line.Quantity, line.GrniBase);
                orders[line.PurchaseOrderId!.Value].ApplyInvoice(line.PurchaseOrderLineId!.Value, -line.Quantity);
            }
            else if (line.Kind == InvoiceLineKind.Order)
            {
                orders[line.PurchaseOrderId!.Value].RevertInvoiceBeforeReceipt(line.PurchaseOrderLineId!.Value, line.Quantity, line.BaseNet);
            }
        }

        if (invoice.Lines.Any(l => l.VarianceToInventory != 0))
        {
            var reversedStock = await inventory.ReverseAsync(ProcurementDocumentTypes.PurchaseInvoice, invoice.Id, date, cancellationToken);
            if (reversedStock.IsFailure && reversedStock.Error.Code != "inventory.source-not-found")
            {
                return reversedStock.Error;
            }
        }

        var reversed = await journals.ReverseAsync(invoice.LegalEntityId, ProcurementDocumentTypes.PurchaseInvoice, invoice.Id, "invoice", date, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return InvoiceMapping.ToDto(invoice);
    }
}

public sealed record GetPurchaseInvoiceQuery(Guid Id) : IQuery<PurchaseInvoiceDto>;

public sealed class GetPurchaseInvoiceHandler(IProcurementDbContext db) : IQueryHandler<GetPurchaseInvoiceQuery, PurchaseInvoiceDto>
{
    public async Task<Result<PurchaseInvoiceDto>> Handle(GetPurchaseInvoiceQuery query, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == query.Id, cancellationToken);
        return invoice is null ? ProcurementAppErrors.NotFound : InvoiceMapping.ToDto(invoice);
    }
}

public sealed record ListPurchaseInvoicesQuery(Guid? SupplierId, string? Status, PageRequest Page) : IQuery<PagedResult<DocumentSummaryDto>>;

public sealed class ListPurchaseInvoicesHandler(IProcurementDbContext db) : IQueryHandler<ListPurchaseInvoicesQuery, PagedResult<DocumentSummaryDto>>
{
    public async Task<Result<PagedResult<DocumentSummaryDto>>> Handle(ListPurchaseInvoicesQuery query, CancellationToken cancellationToken)
    {
        var source = db.Invoices.AsNoTracking();
        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(i => i.SupplierId == supplierId);
        }

        if (Enum.TryParse<PurchaseInvoiceStatus>(query.Status, true, out var status))
        {
            source = source.Where(i => i.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Number).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(i => new DocumentSummaryDto(i.Id, i.Number ?? i.SupplierInvoiceNumber, i.InvoiceDate, i.SupplierId, i.Status.ToString(), i.GrandTotal, i.Currency))
            .ToListAsync(cancellationToken);
        return new PagedResult<DocumentSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}
