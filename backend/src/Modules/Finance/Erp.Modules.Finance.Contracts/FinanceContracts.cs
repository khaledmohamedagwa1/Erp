using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Finance.Contracts;

public enum PartyKind
{
    Customer = 1,
    Supplier = 2,
}

/// <summary>
/// Opens receivable/payable items for a posted source document (invoice, credit note, supplier invoice, debit note).
/// <see cref="TotalAmount"/> is in the document currency: positive for invoices, negative for credit/debit notes.
/// The installment split comes from the payment terms (Parties); credit notes are always one item due on the document date.
/// </summary>
public sealed record OpenItemRequest(
    PartyKind PartyType,
    Guid PartyId,
    Guid LegalEntityId,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string DocumentNumber,
    DateOnly DocumentDate,
    string Currency,
    decimal ExchangeRate,
    decimal TotalAmount,
    Guid? PaymentTermsId);

public sealed record OpenItemSummary(
    Guid Id,
    int Installment,
    DateOnly DueDate,
    string Currency,
    decimal OriginalAmount,
    decimal OpenAmount,
    decimal OriginalBase,
    decimal OpenBase,
    string Status);

/// <summary>
/// Receivables/payables sub-ledger for Sales, Procurement and POS. Every method must run inside the caller's database transaction
/// (the same one that posts the source document's journal), so the document, its journal and its open items commit together.
/// </summary>
public interface ISubledgerService
{
    /// <summary>Idempotent per (SourceDocType, SourceDocId): calling again returns the existing items.</summary>
    Task<Result<IReadOnlyList<OpenItemSummary>>> CreateOpenItemsAsync(OpenItemRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels the open items of a reversed source document. Refused (<c>finance.open-item-allocated</c>) when anything was allocated.</summary>
    Task<Result> CancelOpenItemsAsync(string sourceDocType, Guid sourceDocId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles a credit/debit note against an invoice of the same party and currency (or, when no invoice is given, against the
    /// party's oldest open invoices). Returns the amount applied in document currency.
    /// </summary>
    Task<Result<decimal>> ApplyCreditNoteAsync(string creditNoteDocType, Guid creditNoteDocId, string? invoiceDocType, Guid? invoiceDocId, DateOnly date, CancellationToken cancellationToken = default);
}

public sealed record CustomerExposure(Guid CustomerId, decimal OpenReceivablesBase, decimal UnallocatedAdvancesBase, decimal NetExposureBase);

/// <summary>Customer credit exposure from the sub-ledger. Sales adds its own uninvoiced confirmed orders on top.</summary>
public interface ICreditExposureQueries
{
    Task<CustomerExposure> GetCustomerExposureAsync(Guid customerId, CancellationToken cancellationToken = default);
}

public sealed record AgingRow(Guid PartyId, decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);

public sealed record AgingReport(PartyKind PartyType, Guid LegalEntityId, DateOnly AsOf, IReadOnlyList<AgingRow> Rows, AgingRow Totals);

public sealed record StatementLine(DateOnly Date, string DocumentType, string Reference, string Description, decimal Debit, decimal Credit, decimal Balance);

/// <summary>Party statement in functional currency. For customers the balance is what the customer owes (receivables minus advances).</summary>
public sealed record PartyStatement(PartyKind PartyType, Guid PartyId, Guid LegalEntityId, DateOnly From, DateOnly To, decimal OpeningBalance, IReadOnlyList<StatementLine> Lines, decimal ClosingBalance);

public sealed record OpenItemView(
    Guid Id,
    PartyKind PartyType,
    Guid PartyId,
    Guid LegalEntityId,
    string SourceModule,
    string SourceDocType,
    Guid SourceDocId,
    string DocumentNumber,
    DateOnly DocumentDate,
    int Installment,
    DateOnly DueDate,
    string Currency,
    decimal ExchangeRate,
    int Sign,
    decimal OriginalAmount,
    decimal OpenAmount,
    decimal OriginalBase,
    decimal OpenBase,
    string Status);

public interface IOpenItemQueries
{
    Task<IReadOnlyList<OpenItemView>> GetOpenItemsAsync(PartyKind partyType, Guid partyId, bool includeSettled = false, CancellationToken cancellationToken = default);

    /// <summary>Aging by due date in functional currency, as of a date (allocations after the date are ignored).</summary>
    Task<AgingReport> GetAgingAsync(PartyKind partyType, Guid legalEntityId, DateOnly asOf, CancellationToken cancellationToken = default);

    Task<PartyStatement> GetStatementAsync(PartyKind partyType, Guid partyId, Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Settlement status of a source document's open items: Open, PartiallyPaid or Paid.</summary>
public static class SettlementStatuses
{
    public const string Open = "Open";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Paid = "Paid";
}

/// <summary>
/// Published (outbox) whenever allocations change the settlement status of a source document, so Sales/Procurement can show
/// "partially paid / paid" on their invoices. <see cref="OpenAmount"/> is in document currency.
/// </summary>
public sealed record InvoiceSettled(string SourceModule, string SourceDocType, Guid SourceDocId, string Status, decimal OpenAmount) : IDomainEvent;

public static class FinancePermissions
{
    public const string Read = "finance.read";
    public const string AccountManage = "finance.account.manage";
    public const string ReceiptManage = "finance.receipt.manage";
    public const string ReceiptPost = "finance.receipt.post";
    public const string PaymentManage = "finance.payment.manage";
    public const string PaymentPost = "finance.payment.post";
    public const string AllocationManage = "finance.allocation.manage";
    public const string TransferPost = "finance.transfer.post";
    public const string ExpenseManage = "finance.expense.manage";
    public const string ExpensePost = "finance.expense.post";
    public const string ReconciliationManage = "finance.reconciliation.manage";
    public const string RevaluationRun = "finance.revaluation.run";
    public const string ReceiptReverse = "finance.receipt.reverse";
    public const string PaymentReverse = "finance.payment.reverse";
    public const string WithholdingCertificateManage = "finance.withholding-certificate.manage";
}

/// <summary>
/// A posted receipt or payment with tax withheld. Receipts: a customer withheld tax from us (receivable); payments: we withheld from a supplier
/// (payable). Amounts in the document currency, <c>…Base</c> in the functional currency. Reversed documents are not returned.
/// </summary>
public sealed record WithholdingFact(
    string Direction,
    Guid SettlementId,
    string Number,
    DateOnly Date,
    Guid LegalEntityId,
    PartyKind PartyType,
    Guid PartyId,
    Guid? TaxGroupId,
    string Currency,
    decimal TaxableAmount,
    decimal WithheldAmount,
    decimal TaxableBase,
    decimal WithheldBase,
    string? CertificateNumber);

/// <summary>Withholding tax data for the tax reports (Reporting module).</summary>
public interface IWithholdingReportQueries
{
    Task<IReadOnlyList<WithholdingFact>> GetWithholdingFactsAsync(DateOnly from, DateOnly to, PartyKind? partyType = null, Guid? partyId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A cash drawer/safe or bank account as other modules need it (e.g. POS terminals post to their cash account).</summary>
public sealed record CashBankAccountInfo(Guid Id, Guid LegalEntityId, string Kind, string Code, string Name, string Currency, Guid? GlAccountId, bool IsActive);

/// <summary>Read access to cash and bank accounts of the active company.</summary>
public interface ICashBankAccountQueries
{
    Task<CashBankAccountInfo?> GetAsync(Guid cashBankAccountId, CancellationToken cancellationToken = default);
}
