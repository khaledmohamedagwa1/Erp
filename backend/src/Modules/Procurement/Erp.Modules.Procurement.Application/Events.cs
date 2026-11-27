using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.Modules.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

/// <summary>
/// Applies an approval decision from the workflow engine to the purchase request, order or invoice. Outbox scopes carry the
/// tenant but no company, so queries bypass the company filter and restrict to the tenant explicitly.
/// </summary>
public sealed class ProcurementApprovalCompletedHandler(IUnitOfWork unitOfWork, IInbox inbox, IOutboxMessageContext message, IProcurementDbContext db, ITenantContext tenant)
    : InboxIntegrationEventHandler<ApprovalCompleted>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(ApprovalCompleted integrationEvent, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? Guid.Empty;
        switch (integrationEvent.DocumentType)
        {
            case ApprovalDocumentTypes.PurchaseRequest:
                var request = await db.Requests.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(r => r.Id == integrationEvent.DocumentId && EF.Property<Guid>(r, "TenantId") == tenantId, cancellationToken);
                if (request is { Status: PurchaseRequestStatus.Submitted })
                {
                    request.CompleteApproval(integrationEvent.Approved, integrationEvent.Reason);
                }

                break;
            case ApprovalDocumentTypes.PurchaseOrder:
                var order = await db.Orders.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(o => o.Id == integrationEvent.DocumentId && EF.Property<Guid>(o, "TenantId") == tenantId, cancellationToken);
                if (order is { Status: PurchaseOrderStatus.PendingApproval })
                {
                    order.CompleteApproval(integrationEvent.Approved);
                }

                break;
            case ApprovalDocumentTypes.PurchaseInvoice:
                var invoice = await db.Invoices.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.Id == integrationEvent.DocumentId && EF.Property<Guid>(i, "TenantId") == tenantId, cancellationToken);
                if (invoice is { Status: PurchaseInvoiceStatus.PendingApproval })
                {
                    invoice.CompleteApproval(integrationEvent.Approved);
                }

                break;
            default:
                return;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Mirrors the payment status Finance reports for a supplier invoice (Open / PartiallyPaid / Paid).</summary>
public sealed class ProcurementInvoiceSettledHandler(IUnitOfWork unitOfWork, IInbox inbox, IOutboxMessageContext message, IProcurementDbContext db, ITenantContext tenant)
    : InboxIntegrationEventHandler<InvoiceSettled>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(InvoiceSettled integrationEvent, CancellationToken cancellationToken)
    {
        if (integrationEvent.SourceModule != SourceModules.Procurement || integrationEvent.SourceDocType != ProcurementDocumentTypes.PurchaseInvoice)
        {
            return;
        }

        var tenantId = tenant.TenantId ?? Guid.Empty;
        var invoice = await db.Invoices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == integrationEvent.SourceDocId && EF.Property<Guid>(i, "TenantId") == tenantId, cancellationToken);
        if (invoice is null)
        {
            return;
        }

        invoice.ApplySettlement(integrationEvent.Status, integrationEvent.OpenAmount);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Open purchase-order supply (ordered, not yet received) for planning (MRP / availability).</summary>
public sealed class ProcurementQueries(IProcurementDbContext db, ProductLookup products) : IProcurementQueries
{
    public async Task<IReadOnlyList<OpenSupplyLine>> GetOpenSupplyAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken cancellationToken = default) =>
        productIds.Count == 0 ? [] : await LoadOpenSupplyAsync(productIds.ToHashSet(), warehouseId, cancellationToken);

    public Task<IReadOnlyList<OpenSupplyLine>> GetAllOpenSupplyAsync(Guid? warehouseId = null, CancellationToken cancellationToken = default) =>
        LoadOpenSupplyAsync(null, warehouseId, cancellationToken);

    private async Task<IReadOnlyList<OpenSupplyLine>> LoadOpenSupplyAsync(HashSet<Guid>? wanted, Guid? warehouseId, CancellationToken cancellationToken)
    {

        var source = db.Orders.AsNoTracking()
            .Where(o => o.Status == PurchaseOrderStatus.Approved || o.Status == PurchaseOrderStatus.Sent || o.Status == PurchaseOrderStatus.PartiallyReceived);
        if (warehouseId is { } id)
        {
            source = source.Where(o => o.WarehouseId == id);
        }

        var orders = await source.ToListAsync(cancellationToken);
        var lines = orders.SelectMany(o => o.Lines.Where(l => (wanted is null || wanted.Contains(l.ProductId)) && l.OpenQuantity > 0).Select(l => (Order: o, Line: l))).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        var catalog = await products.LoadAsync(lines.Select(x => x.Line.ProductId), cancellationToken);
        return lines.Select(x =>
        {
            var factor = catalog.IsSuccess && catalog.Value.TryGetValue(x.Line.ProductId, out var product) ? ProductLookup.Factor(product, x.Line.UomId) ?? 1m : 1m;
            return new OpenSupplyLine(x.Line.ProductId, x.Line.VariantId, x.Order.WarehouseId, x.Order.Id, x.Line.LineId, x.Order.Number, x.Order.SupplierId,
                Money.Quantity(x.Line.OpenQuantity * factor), x.Line.ExpectedDate ?? x.Order.ExpectedDate);
        }).ToList();
    }
}

public sealed class PurchaseInvoiceQueries(IProcurementDbContext db) : IPurchaseInvoiceQueries
{
    public async Task<PurchaseInvoiceSummary?> GetPurchaseInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        return invoice is null
            ? null
            : new PurchaseInvoiceSummary(invoice.Id, invoice.LegalEntityId, invoice.SupplierId, invoice.Number, invoice.SupplierInvoiceNumber, invoice.InvoiceDate,
                invoice.IsPosted,
                invoice.Lines.Select(l => new PurchaseInvoiceLineSummary(l.LineId, l.Kind.ToString(), l.AccountId, l.ProductId, l.Description, l.Quantity,
                    invoice.IsPosted ? l.BaseNet : 0)).ToList());
    }
}
