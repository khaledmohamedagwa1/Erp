# Finance module — receivables, payables, cash & bank (Phase 8)

Schema `finance` · migration order 90 · module code `finance` (core, depends on accounting).
All GL effects go through `Accounting.Contracts.IJournalPostingService`; Finance never references account ids except a
cash/bank account's own GL account (optional) and expense-line accounts chosen by the user.

## Contracts (`Erp.Modules.Finance.Contracts`) — for Sales, Procurement, POS

```csharp
public enum PartyKind { Customer = 1, Supplier = 2 }

public sealed record OpenItemRequest(PartyKind PartyType, Guid PartyId, Guid LegalEntityId, string SourceModule, string SourceDocType,
    Guid SourceDocId, string DocumentNumber, DateOnly DocumentDate, string Currency, decimal ExchangeRate, decimal TotalAmount,
    Guid? PaymentTermsId);   // TotalAmount: + invoice, − credit/debit note (document currency)

public interface ISubledgerService
{
    Task<Result<IReadOnlyList<OpenItemSummary>>> CreateOpenItemsAsync(OpenItemRequest request, CancellationToken ct = default);
    Task<Result> CancelOpenItemsAsync(string sourceDocType, Guid sourceDocId, CancellationToken ct = default);
    Task<Result<decimal>> ApplyCreditNoteAsync(string creditNoteDocType, Guid creditNoteDocId, string? invoiceDocType, Guid? invoiceDocId,
        DateOnly date, CancellationToken ct = default);
}

public interface ICreditExposureQueries { Task<CustomerExposure> GetCustomerExposureAsync(Guid customerId, CancellationToken ct = default); }
// CustomerExposure(CustomerId, OpenReceivablesBase, UnallocatedAdvancesBase, NetExposureBase)

public interface IOpenItemQueries
{
    Task<IReadOnlyList<OpenItemView>> GetOpenItemsAsync(PartyKind partyType, Guid partyId, bool includeSettled = false, CancellationToken ct = default);
    Task<AgingReport> GetAgingAsync(PartyKind partyType, Guid legalEntityId, DateOnly asOf, CancellationToken ct = default);
    Task<PartyStatement> GetStatementAsync(PartyKind partyType, Guid partyId, Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

// Outbox event (subscribe with IIntegrationEventHandler<InvoiceSettled>):
public sealed record InvoiceSettled(string SourceModule, string SourceDocType, Guid SourceDocId, string Status, decimal OpenAmount) : IDomainEvent;
// Status: SettlementStatuses.Open | PartiallyPaid | Paid
```

Rules:

| Rule | Error |
|---|---|
| Sub-ledger methods must run inside the caller's transaction (the one posting the source document's journal). | `finance.transaction-required` |
| `CreateOpenItemsAsync` is idempotent per (SourceDocType, SourceDocId). Invoices are split by the party's payment terms (`Parties.GetDueScheduleAsync`, rounded to the currency's minor units); credit/debit notes are one item due on the document date. The base amount is split over installments so it sums exactly to `round(total × rate, 2)`. | `finance.party-not-found`, `finance.legal-entity-not-found` |
| The caller posts its own journal (e.g. Sales: Dr CustomerReceivable / Cr revenue, tax). Finance books only settlements and exchange differences. | — |
| `CancelOpenItemsAsync` is refused when anything was allocated. | `finance.open-item-allocated` |
| `ApplyCreditNoteAsync` settles credit-note items against invoices of the same party, legal entity and currency (the given invoice, or oldest due first) and books the exchange difference between the two historical rates. | — |
| If the scope has no company, the legal entity's company is adopted (like the posting engine). | — |

## Accounting treatment

Finance journals are posted **in the functional currency (rate 1)** with base amounts computed by Finance, because open items are
relieved at their **historical** rate while cash moves at the settlement rate — the posting engine converts every line at a single
rate, so a foreign receipt could not otherwise relieve AR at the invoice rate. The sub-ledger keeps transaction-currency amounts.

| Document | Journal (functional currency) |
|---|---|
| Receipt | Dr cash/bank (`amount × rate`) · Dr WithholdingTaxReceivable · Cr CustomerReceivable (base relieved at historical rate) · Cr CustomerAdvance (unallocated remainder) · Cr RealizedFxGain / Dr RealizedFxLoss |
| Payment | Cr cash/bank · Cr WithholdingTaxPayable · Dr SupplierPayable (relieved) · Dr SupplierAdvance (remainder) · FX |
| Advance allocation | Dr CustomerAdvance (at receipt rate) · Cr CustomerReceivable (relieved) · FX (suppliers mirrored) |
| Credit-note application | only the exchange difference: control account vs RealizedFx* (nothing when both documents share a rate) |
| Internal transfer | Dr destination (`toAmount × toRate`) · Dr BankCharges · Cr source (`(amount + charges) × fromRate`) · FX |
| Expense | Dr each line's account (net + non-recoverable tax) with cost centre · Dr InputTax (recoverable, tax-code context) · Cr cash/bank or Cr SupplierPayable (+ AP open item) |
| FX revaluation (as-of date) | AR / AP / foreign cash-bank lines vs UnrealizedFxGain / UnrealizedFxLoss; reversed automatically on the next day |

Cash/bank lines use the account's own GL account when set, otherwise the `CashAccount` / `BankAccount` role.
Realized difference: customers `settlement base − relieved base`, suppliers `relieved − settlement` (positive = gain).
Worked examples covered by tests: J-6 (payment 6,893 + withholding 61 against 6,954) and J-10 (USD 1,000 invoiced at 50, received at 51 → gain 1,000).

## Endpoints (`/api/v1/finance`, all require `X-Company-Id`)

| Method & path | Permission | Notes |
|---|---|---|
| `GET/POST /cash-accounts`, `GET/POST /bank-accounts` | finance.read / finance.account.manage | `{legalEntityId, code, name, currency, glAccountId?, bankName?, accountNumber?, iban?}`; list includes balance (account currency) and base balance. |
| `GET /cash-bank-accounts/{id}/movements?unreconciled&page&pageSize` | finance.read | Cash movements (+ in / − out). |
| `POST /receipts` · `POST /payments` | finance.receipt.manage / finance.payment.manage | Draft: `{legalEntityId, partyId, date, method (Cash/BankTransfer/Cheque/Card), cashBankAccountId, amount, exchangeRate?, withholdingTaxGroupId?, withholdingBase?, allocations:[{openItemId, amount}], reference?, description?}`. Currency = the cash/bank account's; rate = explicit, else the recorded spot rate (`finance.rate-missing`). Allocations ≤ amount + withholding (`finance.over-allocation`), each ≤ open (`finance.allocation-exceeds-open`), same party/legal entity/currency. |
| `POST /receipts/{id}/post` · `POST /payments/{id}/post` | finance.receipt.post / finance.payment.post (sensitive) | **Idempotency-Key required.** Number `RCT-/PAY-{year}-000001` (gapless per legal entity). |
| `POST /receipts/{id}/allocations` · `POST /payments/{id}/allocations` | finance.allocation.manage | `{date, allocations}` — allocates the posted advance. Idempotency-Key required. |
| `GET /receipts`, `/receipts/{id}`, `/payments`, `/payments/{id}` | finance.read | |
| `GET /open-items?partyType&partyId&legalEntityId&openOnly=true&page&pageSize` | finance.read | |
| `GET /reports/ar-aging?legalEntityId&asOf` · `/reports/ap-aging` | finance.read | Buckets current / 1–30 / 31–60 / 61–90 / >90 days past due, base currency, signed (credit notes negative); allocations after `asOf` ignored. Unallocated advances are not in the aging. |
| `GET /reports/customer-statement?customerId&legalEntityId&from&to` · `/reports/supplier-statement?supplierId&…` | finance.read | Opening balance, documents, receipts/payments (incl. tax withheld), exchange differences, running balance. Balance = receivables − advances (customers) / payables − advances (suppliers). |
| `GET/POST /internal-transfers`, `POST /internal-transfers/{id}/post` | finance.read / finance.transfer.post | `{legalEntityId, date, fromAccountId, fromAmount, fromRate?, toAccountId, toAmount, toRate?, charges, description?}`; same-currency transfers must move equal amounts. Post needs Idempotency-Key. Number `ITR-…`. |
| `GET/POST /expenses`, `GET /expenses/{id}`, `POST /expenses/{id}/post` | finance.read / finance.expense.manage / finance.expense.post | `{legalEntityId, date, payee: CashBank|Supplier, cashBankAccountId?, supplierId?, currency?, exchangeRate?, reference?, description?, lines:[{accountId, description?, netAmount, taxGroupId?, costCenterId?}]}`. Tax groups may contain input tax only. Number `EXP-…`. |
| `POST /bank-reconciliations` | finance.reconciliation.manage | `{cashBankAccountId, statementDate, statementBalance, lines:[{date, amount (+/−), description?, reference?}]}`; auto-matches equal amounts within 3 days, preferring equal reference. |
| `GET /bank-reconciliations/{id}`, `PUT /bank-reconciliations/{id}/lines/{lineId} {movementId|null}`, `POST /bank-reconciliations/{id}/complete` | read / manage | Completes only when every line is matched and the book balance equals the statement balance; matched movements are marked reconciled. |
| `GET /fx-revaluations?legalEntityId`, `POST /fx-revaluations {legalEntityId, asOf}` | finance.read / finance.revaluation.run (sensitive) | Closing rate (fallback spot) per foreign currency; one run per legal entity and date (`finance.revaluation-exists`). The next day must be in an open period. Idempotency-Key required. |

## Data model

`OpenItem` (one per installment; positive amounts with `Sign` ±1; historical base; status Open/PartiallySettled/Settled/Cancelled; rowversion),
`Allocation` (immutable; amount, base relieved, settlement base, realized difference), `CashBankAccount`, `Settlement` (receipts and
payments, requested allocations as JSON), `InternalTransfer`, `Expense` (lines as JSON), `CashMovement`, `BankReconciliation`
(statement lines as JSON), `FxRevaluationRun` (lines as JSON). All tables carry `TenantId` (row-level security) and are company-scoped.

## Limitations

* Expense lines are validated through `Accounting.Contracts.IAccountQueries`: only active, non-control posting accounts of type Expense or Asset are accepted (`finance.invalid-expense-account`).
  non-posting/inactive accounts, but because Finance is an allowed source for receivables/payables control accounts, an expense line
  could target AR/AP. Needed from Accounting: an account query (type, posting, control sub-ledger, manual-allowed) in its contracts.
* GL lines of Finance journals carry base amounts only (functional currency); the transaction-currency amount is in the sub-ledger (`JournalLineRequest.BaseAmount` now exists in Accounting.Contracts and could be used to post transaction-currency amounts with fixed base amounts; Finance has not been switched over yet).
  (see *Accounting treatment*). A per-line base override in the posting contract would remove this.
* Allocations require the open item's currency; cross-currency settlement is not supported.
* Posted receipts/payments cannot yet be reversed (no un-allocation flow).
* Bank reconciliation matches Finance cash movements only; manual GL journals to a bank account are not visible to it.
* Withholding certificates and withholding tax reports are not implemented.
