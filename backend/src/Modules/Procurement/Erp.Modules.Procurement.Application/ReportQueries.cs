using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.SharedKernel.Documents;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

/// <summary>Posted procurement reads for the Reporting module and printable documents. The company filter scopes every query.</summary>
public sealed class ProcurementReportQueries(IProcurementDbContext db, ICatalogQueries catalog) : IProcurementReportQueries
{
    private static readonly PurchaseInvoiceStatus[] PostedInvoice = [PurchaseInvoiceStatus.Posted, PurchaseInvoiceStatus.PartiallyPaid, PurchaseInvoiceStatus.Paid];

    private static readonly PurchaseOrderStatus[] PrintableOrder =
        [PurchaseOrderStatus.Approved, PurchaseOrderStatus.Sent, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.FullyReceived, PurchaseOrderStatus.Closed];

    public async Task<IReadOnlyList<PurchaseFact>> GetPurchaseFactsAsync(PurchaseReportFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.Invoices.AsNoTracking().Where(i => PostedInvoice.Contains(i.Status)
            && (i.PostingDate ?? i.InvoiceDate) >= filter.From && (i.PostingDate ?? i.InvoiceDate) <= filter.To);
        if (filter.SupplierId is { } supplier)
        {
            query = query.Where(i => i.SupplierId == supplier);
        }

        var invoices = await query.ToListAsync(cancellationToken);
        var lines = invoices.SelectMany(i => i.Lines.Select(l => (Invoice: i, Line: l))).ToList();

        var orderIds = lines.Select(x => x.Line.PurchaseOrderId).OfType<Guid>().Distinct().ToList();
        var receiptIds = lines.Select(x => x.Line.GoodsReceiptId).OfType<Guid>().Distinct().ToList();
        var orders = orderIds.Count == 0 ? [] : await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        var receipts = receiptIds.Count == 0 ? [] : await db.Receipts.AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var products = await LoadProductsAsync(lines.Select(x => x.Line.ProductId).OfType<Guid>(), cancellationToken);
        var wanted = filter.ProductIds is { Count: > 0 } ids ? ids.ToHashSet() : null;

        var facts = new List<PurchaseFact>();
        foreach (var (invoice, line) in lines)
        {
            if (wanted is not null && (line.ProductId is not { } pid || !wanted.Contains(pid)))
            {
                continue;
            }

            var order = line.PurchaseOrderId is { } oid ? orders.GetValueOrDefault(oid) : null;
            var receipt = line.GoodsReceiptId is { } rid ? receipts.GetValueOrDefault(rid) : null;
            var receiptLine = receipt?.Lines.FirstOrDefault(l => l.LineId == line.GoodsReceiptLineId);
            var orderLine = order?.Lines.FirstOrDefault(l => l.LineId == line.PurchaseOrderLineId);
            Guid? uom = receiptLine?.UomId ?? orderLine?.UomId;
            var warehouse = receipt?.WarehouseId ?? order?.WarehouseId;
            var branch = order?.BranchId;
            if ((filter.WarehouseId is { } w && warehouse != w) || (filter.BranchId is { } b && branch != b))
            {
                continue;
            }

            var factor = line.ProductId is { } productId && uom is { } u && products.TryGetValue(productId, out var product)
                ? (u == product.BaseUomId ? 1m : product.Units.FirstOrDefault(x => x.UomId == u)?.FactorToBase ?? 1m)
                : 1m;
            var netBase = line.BaseNet != 0 ? line.BaseNet : ToBase(line.NetAmount, invoice.ExchangeRate);
            facts.Add(new PurchaseFact(invoice.Id, invoice.Number ?? string.Empty, invoice.SupplierInvoiceNumber, invoice.PostingDate ?? invoice.InvoiceDate,
                invoice.SupplierId, warehouse, branch, line.Kind.ToString(), line.ProductId, line.Description, uom, line.Quantity,
                Math.Round(line.Quantity * factor, 6), line.UnitPrice, invoice.Currency, invoice.ExchangeRate, netBase, ToBase(line.TaxAmount, invoice.ExchangeRate)));
        }

        return facts.OrderBy(f => f.Date).ThenBy(f => f.InvoiceNumber).ToList();
    }

    public async Task<IReadOnlyList<ReceiptFact>> GetReceiptFactsAsync(PurchaseReportFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.Receipts.AsNoTracking().Where(r => r.Status == GoodsReceiptStatus.Posted && r.Date >= filter.From && r.Date <= filter.To);
        if (filter.SupplierId is { } supplier)
        {
            query = query.Where(r => r.SupplierId == supplier);
        }

        if (filter.WarehouseId is { } warehouse)
        {
            query = query.Where(r => r.WarehouseId == warehouse);
        }

        var receipts = await query.ToListAsync(cancellationToken);
        var orderIds = receipts.Select(r => r.PurchaseOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? []
            : await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.Number, o.BranchId }).ToDictionaryAsync(o => o.Id, cancellationToken);
        var wanted = filter.ProductIds is { Count: > 0 } ids ? ids.ToHashSet() : null;

        return receipts
            .Where(r => filter.BranchId is not { } branch || (orders.TryGetValue(r.PurchaseOrderId, out var o) && o.BranchId == branch))
            .SelectMany(r => r.Lines.Where(l => wanted is null || wanted.Contains(l.ProductId)).Select(l => new ReceiptFact(
                r.Id, r.Number, r.Date, r.SupplierId, r.WarehouseId, r.PurchaseOrderId, orders.TryGetValue(r.PurchaseOrderId, out var order) ? order.Number : string.Empty,
                l.ProductId, l.UomId, l.ReceivedQuantity, l.AcceptedQuantity, l.RejectedQuantity, l.InvoicedQuantity, l.ReturnedQuantity, l.InvoiceableQuantity, l.BaseValue,
                l.UnitBaseValue, l.BatchNumber, l.ExpiryDate)))
            .OrderBy(f => f.Date).ThenBy(f => f.ReceiptNumber)
            .ToList();
    }

    public async Task<PrintableDocument?> GetPurchaseOrderPrintAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == purchaseOrderId && PrintableOrder.Contains(o.Status), cancellationToken);
        if (order is null)
        {
            return null;
        }

        var products = await LoadProductsAsync(order.Lines.Select(l => l.ProductId), cancellationToken);
        var fields = new List<PrintField>();
        if (order.ExpectedDate is { } expected)
        {
            fields.Add(new PrintField("Expected delivery", "تاريخ التوريد المتوقع", expected.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var lines = order.Lines.Select(l =>
        {
            var product = products.GetValueOrDefault(l.ProductId);
            return new PrintLine(product?.Sku ?? string.Empty, product?.NameEn ?? string.Empty, l.Quantity, l.ProductId, l.UomId, l.UnitPrice, l.DiscountPercent,
                l.NetAmount, l.TaxAmount, l.NetAmount + l.TaxAmount);
        }).ToList();
        PrintTotal[] totals =
        [
            new("Net amount", "الصافي", order.NetTotal),
            new("Tax", "الضريبة", order.TaxTotal),
            new("Total", "الإجمالي", order.GrandTotal, Emphasis: true),
        ];
        return new PrintableDocument("purchase-order", "Purchase order", "أمر شراء", order.Number, order.Date, order.Currency, order.Status.ToString(),
            order.CompanyId, order.LegalEntityId, new PrintParty("Supplier", order.SupplierId), fields, lines, [], totals, order.Notes);
    }

    public async Task<PrintableDocument?> GetGoodsReceiptPrintAsync(Guid receiptId, CancellationToken cancellationToken = default)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId && r.Status == GoodsReceiptStatus.Posted, cancellationToken);
        if (receipt is null)
        {
            return null;
        }

        var orderNumber = await db.Orders.AsNoTracking().Where(o => o.Id == receipt.PurchaseOrderId).Select(o => o.Number).FirstOrDefaultAsync(cancellationToken);
        var products = await LoadProductsAsync(receipt.Lines.Select(l => l.ProductId), cancellationToken);
        var lines = receipt.Lines.Select(l =>
        {
            var product = products.GetValueOrDefault(l.ProductId);
            var description = product?.NameEn ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(l.BatchNumber))
            {
                description += $" — {l.BatchNumber}";
            }

            if (l.RejectedQuantity != 0)
            {
                description += $" (rejected / مرفوض {l.RejectedQuantity:0.######})";
            }

            return new PrintLine(product?.Sku ?? string.Empty, description, l.AcceptedQuantity, l.ProductId, l.UomId);
        }).ToList();
        var fields = new List<PrintField> { new("Purchase order", "أمر الشراء", orderNumber ?? string.Empty) };
        return new PrintableDocument("goods-receipt", "Goods receipt note", "إذن استلام بضاعة", receipt.Number, receipt.Date, receipt.Currency,
            receipt.Status.ToString(), receipt.CompanyId, receipt.LegalEntityId, new PrintParty("Supplier", receipt.SupplierId), fields, lines, [], [], receipt.Notes);
    }

    private async Task<Dictionary<Guid, ProductInfo>> LoadProductsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? [] : (await catalog.GetProductsAsync(list, cancellationToken)).ToDictionary(p => p.Id);
    }

    private static decimal ToBase(decimal amount, decimal rate) => Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
}
