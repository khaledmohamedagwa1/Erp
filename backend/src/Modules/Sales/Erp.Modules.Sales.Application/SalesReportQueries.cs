using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Documents;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Application;

/// <summary>Posted-sales reads for the Reporting module (facts in functional currency) and printable documents.</summary>
public sealed class SalesReportQueries(ISalesDbContext db) : ISalesReportQueries
{
    private static readonly SalesInvoiceStatus[] PostedInvoice =
        [SalesInvoiceStatus.Posted, SalesInvoiceStatus.PartiallyPaid, SalesInvoiceStatus.Paid, SalesInvoiceStatus.Closed];

    public async Task<IReadOnlyList<SalesFact>> GetSalesFactsAsync(SalesReportFilter filter, CancellationToken cancellationToken = default)
    {
        var invoices = await Filter(db.Invoices.AsNoTracking().Where(i => PostedInvoice.Contains(i.Status)), filter).ToListAsync(cancellationToken);
        var returns = await Filter(db.Returns.AsNoTracking().Where(r => r.Status == SalesReturnStatus.Posted), filter).ToListAsync(cancellationToken);
        var products = filter.ProductIds is { Count: > 0 } ids ? ids.ToHashSet() : null;

        var facts = new List<SalesFact>();
        foreach (var invoice in invoices)
        {
            facts.AddRange(invoice.Lines.Where(l => products is null || products.Contains(l.ProductId))
                .Select(l => Fact(SalesDocumentTypes.Invoice, invoice, l, 1, l.CostAmount)));
        }

        foreach (var salesReturn in returns)
        {
            // Credited goods reverse COGS only when they were put back into stock.
            facts.AddRange(salesReturn.Lines.Where(l => products is null || products.Contains(l.ProductId))
                .Select(l => Fact(SalesDocumentTypes.Return, salesReturn, l, -1, l.Restock ? l.CostAmount : 0m)));
        }

        return facts.OrderBy(f => f.Date).ThenBy(f => f.DocumentNumber).ToList();
    }

    public async Task<PrintableDocument?> GetInvoicePrintAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && PostedInvoice.Contains(i.Status), cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        var fields = new List<PrintField>();
        if (!string.IsNullOrWhiteSpace(invoice.Reference))
        {
            fields.Add(new PrintField("Customer reference", "مرجع العميل", invoice.Reference));
        }

        var totals = Totals(invoice).ToList();
        totals.Add(new PrintTotal("Amount due", "المبلغ المستحق", invoice.OpenAmount));
        return Print("sales-invoice", "Tax invoice", "فاتورة ضريبية", invoice, invoice.Status.ToString(), fields, totals);
    }

    public async Task<PrintableDocument?> GetReturnPrintAsync(Guid returnId, CancellationToken cancellationToken = default)
    {
        var salesReturn = await db.Returns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == returnId && r.Status == SalesReturnStatus.Posted, cancellationToken);
        if (salesReturn is null)
        {
            return null;
        }

        var invoiceNumber = await db.Invoices.AsNoTracking().Where(i => i.Id == salesReturn.SalesInvoiceId).Select(i => i.Number).FirstOrDefaultAsync(cancellationToken);
        var fields = new List<PrintField>
        {
            new("Original invoice", "الفاتورة الأصلية", invoiceNumber ?? string.Empty),
            new("Reason", "السبب", salesReturn.Reason),
        };
        return Print("sales-credit-note", "Credit note", "إشعار دائن", salesReturn, salesReturn.Status.ToString(), fields, Totals(salesReturn).ToList());
    }

    private static IQueryable<T> Filter<T>(IQueryable<T> source, SalesReportFilter filter)
        where T : SalesDocument
    {
        source = source.Where(d => d.DocumentDate >= filter.From && d.DocumentDate <= filter.To);
        if (filter.BranchId is { } branch)
        {
            source = source.Where(d => d.BranchId == branch);
        }

        if (filter.WarehouseId is { } warehouse)
        {
            source = source.Where(d => d.WarehouseId == warehouse);
        }

        if (filter.CustomerId is { } customer)
        {
            source = source.Where(d => d.CustomerId == customer);
        }

        return source;
    }

    private static SalesFact Fact(string type, SalesDocument document, SalesLine line, int sign, decimal cost) => new(
        type, document.Id, document.Number, document.DocumentDate, document.BranchId, document.WarehouseId, document.CustomerId,
        line.ProductId, line.Sku, line.Description, line.IsService,
        sign * line.BaseQuantity, sign * ToBase(line.NetAmount, document.ExchangeRate), sign * ToBase(line.TaxAmount, document.ExchangeRate), sign * ToBase(cost, 1m));

    private static decimal ToBase(decimal amount, decimal rate) => Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);

    private static IEnumerable<PrintTotal> Totals(SalesDocument document) =>
    [
        new PrintTotal("Net amount", "الصافي", document.NetTotal),
        new PrintTotal("Tax", "الضريبة", document.TaxTotal),
        new PrintTotal("Total", "الإجمالي", document.GrossTotal, Emphasis: true),
    ];

    private static PrintableDocument Print(string type, string titleEn, string titleAr, SalesDocument document, string status,
        IReadOnlyList<PrintField> fields, IReadOnlyList<PrintTotal> totals)
    {
        var taxes = document.Lines.SelectMany(l => l.Taxes)
            .GroupBy(t => new { t.Code, t.Rate })
            .Select(g => new PrintTax(g.Key.Code, g.Key.Rate, g.Sum(t => t.Base), g.Sum(t => t.Amount)))
            .ToList();
        var lines = document.Lines.Select(l => new PrintLine(l.Sku, l.Description, l.Quantity, l.ProductId, l.UomId, l.UnitPrice, l.DiscountPercent,
            l.NetAmount, l.TaxAmount, l.GrossAmount)).ToList();
        return new PrintableDocument(type, titleEn, titleAr, document.Number, document.DocumentDate, document.Currency, status,
            document.CompanyId, document.LegalEntityId, new PrintParty("Customer", document.CustomerId), fields, lines, taxes, totals, document.Notes);
    }
}
