using System.Globalization;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Documents;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Application;

public sealed class FinanceDocumentQueries(IFinanceDbContext db, FinanceScope scope) : IFinanceDocumentQueries
{
    public async Task<PrintableDocument?> GetWithholdingCertificatePrintAsync(Guid certificateId, CancellationToken cancellationToken = default)
    {
        var certificate = await db.WithholdingCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == certificateId, cancellationToken);
        if (certificate is null)
        {
            return null;
        }

        var le = await scope.LegalEntityAsync(certificate.LegalEntityId, cancellationToken);
        return WithholdingPrinting.Build(certificate, le.IsSuccess ? le.Value.FunctionalCurrency : string.Empty);
    }

    public async Task<PrintableDocument?> GetSettlementPrintAsync(Guid settlementId, CancellationToken cancellationToken = default)
    {
        var settlement = await db.Settlements.AsNoTracking().FirstOrDefaultAsync(s => s.Id == settlementId && s.Status == DocumentStatus.Posted, cancellationToken);
        if (settlement is null)
        {
            return null;
        }

        var account = await db.CashBankAccounts.AsNoTracking().Where(a => a.Id == settlement.CashBankAccountId)
            .Select(a => new { a.Code, a.Name }).FirstOrDefaultAsync(cancellationToken);
        var allocations = await (from a in db.Allocations.AsNoTracking()
                                 join o in db.OpenItems.AsNoTracking() on a.OpenItemId equals o.Id
                                 where a.SettlementId == settlement.Id
                                 orderby o.DocumentDate, o.DocumentNumber
                                 select new { o.DocumentNumber, o.SourceDocType, o.DocumentDate, a.Amount }).ToListAsync(cancellationToken);

        var receipt = settlement.Direction == SettlementDirection.Receipt;
        var fields = new List<PrintField>
        {
            new("Method", "طريقة الدفع", settlement.Method.ToString()),
            new(receipt ? "Deposited to" : "Paid from", receipt ? "أودع في" : "صرف من", account is null ? string.Empty : $"{account.Code} — {account.Name}"),
        };
        if (!string.IsNullOrWhiteSpace(settlement.Reference))
        {
            fields.Add(new PrintField("Reference", "المرجع", settlement.Reference));
        }

        var lines = allocations.Select(a => new PrintLine(a.DocumentNumber,
            $"{a.SourceDocType} {a.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", 1, Total: a.Amount)).ToList();
        var totals = new List<PrintTotal>();
        if (settlement.WithholdingAmount != 0)
        {
            totals.Add(new PrintTotal("Withholding tax", "ضريبة الخصم", settlement.WithholdingAmount));
        }

        totals.Add(new PrintTotal("Allocated", "المخصص", allocations.Sum(a => a.Amount)));
        totals.Add(new PrintTotal("Unallocated", "غير مخصص", settlement.UnallocatedAmount));
        totals.Add(new PrintTotal(receipt ? "Amount received" : "Amount paid", receipt ? "المبلغ المستلم" : "المبلغ المدفوع", settlement.Amount, Emphasis: true));

        return new PrintableDocument(receipt ? "receipt-voucher" : "payment-voucher",
            receipt ? "Receipt voucher" : "Payment voucher", receipt ? "سند قبض" : "سند صرف",
            settlement.Number ?? string.Empty, settlement.Date, settlement.Currency, settlement.Status.ToString(), settlement.CompanyId, settlement.LegalEntityId,
            new PrintParty(receipt ? "Customer" : "Supplier", settlement.PartyId), fields, lines, [], totals, settlement.Description);
    }
}
