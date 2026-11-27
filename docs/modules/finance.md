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

Finance journals carry the **document's transaction currency and rate** (receipts, payments, advance allocations, expenses and
same-currency internal transfers). Each line has a transaction-currency amount and a **fixed functional-currency amount**
(`JournalLineRequest.BaseAmount`): open items are relieved at their historical base while cash moves at the settlement rate, and the
realized exchange difference is a **base-only line** (`JournalLineRequest.BaseOnly`, no transaction amount). Transfers between two
currencies, credit-note exchange differences and revaluations are posted in the functional currency (rate 1).

| Document | Journal |
|---|---|
| Receipt | Dr cash/bank (`amount`, base `amount × rate`) · Dr WithholdingTaxReceivable · Cr CustomerReceivable (settlement-currency amount allocated, base relieved at historical rate) · Cr CustomerAdvance (unallocated remainder) · Cr RealizedFxGain / Dr RealizedFxLoss (base only) |
| Payment | Cr cash/bank · Cr WithholdingTaxPayable · Dr SupplierPayable (relieved) · Dr SupplierAdvance (remainder) · FX |
| Advance allocation | Dr CustomerAdvance (at receipt rate) · Cr CustomerReceivable (relieved) · FX (suppliers mirrored), in the receipt's currency |
| Receipt/payment reversal | the exact reversal of the posting journal (same currency and bases) dated the reversal date, plus — when the advance had been allocated later — one compensating journal `AdvanceAllocationReversal` (Cr advance / Dr receivable at the original bases, opposite FX) |
| Credit-note application | only the exchange difference: control account vs RealizedFx* (nothing when both documents share a rate) |
| Internal transfer | Dr destination (`toAmount × toRate`) · Dr BankCharges · Cr source (`(amount + charges) × fromRate`) · FX |
| Expense | Dr each line's account (net + non-recoverable tax) with cost centre · Dr InputTax (recoverable, tax-code context, taxable amount for the VAT return) · Cr cash/bank or Cr SupplierPayable (+ AP open item) |
| FX revaluation (as-of date) | AR / AP / foreign cash-bank lines vs UnrealizedFxGain / UnrealizedFxLoss; reversed automatically on the next day |

### Cross-currency allocation

An open item in another currency than the receipt/payment can be allocated: `allocations:[{openItemId, amount, settlementAmount?}]`.
`amount` is in the item's currency; `settlementAmount` (the receipt's currency) is the agreed amount, or — when omitted — the item amount
converted through the functional currency at the **spot rates of the document date** (`finance.cross-currency-rate-missing` when a rate is
missing). The item is relieved at its historical base, the settlement side is valued at the receipt's rate, and the difference is the
realized FX gain/loss. Allocation rows record `settlementAmount`; over-allocation checks use settlement-currency amounts.

### Reversal of receipts and payments

`POST /receipts/{id}/reverse` · `POST /payments/{id}/reverse` `{date, reason}` (reason required; date on/after the document; the
period must be open; sensitive permissions `finance.receipt.reverse` / `finance.payment.reverse`; Idempotency-Key required).
The journal is reversed, every allocation (direct and later advance allocations) gets a **negated allocation row** pointing to the
original (`reversalOfId`, so as-of aging and statements stay correct), open items are reopened (`InvoiceSettled` is published so Sales
shows the invoice open again), an opposite cash movement is added and the document becomes `Reversed`. Blocked when the cash movement is
in a **completed** bank reconciliation (`finance.movement-reconciled` — reopen it with `POST /bank-reconciliations/{id}/reopen`), matched
in a draft reconciliation (`finance.movement-in-reconciliation`), or listed on an active withholding certificate
(`finance.on-withholding-certificate`).

### Bank reconciliation against the general ledger

Cash/bank accounts with their **own GL account** (`glAccountId`) see every posted GL line on that account, not only Finance documents:
lines from journals of other modules and manual journals are imported as cash movements (`sourceDocType = GlJournalLine`, one per journal
line, unique index) when a reconciliation is created or on `POST /cash-bank-accounts/{id}/sync-gl {upTo?}`. Journals posted by Finance
itself are excluded (Finance already has their movements). Accounts posting through the shared `CashAccount`/`BankAccount` role are not
synced (a shared GL account cannot be attributed to one bank account); a foreign-currency account only imports lines of journals in its
own currency (or base-only lines when it is in the functional currency).

### Withholding tax certificates

`POST /withholding-certificates {legalEntityId, kind: Issued|Received, partyId, periodFrom, periodTo, issueDate?, externalReference?, notes?}`
collects the posted, not reversed payments (Issued — we withheld from a supplier) or receipts (Received — a customer withheld from us) of the
party in the period that are not on another active certificate. Numbers `WHT-{year}-…` (issued) / `WHR-{year}-…` (received), gapless per
legal entity. Totals in the functional currency; lines keep the document currency. `POST /withholding-certificates/{id}/cancel` releases the
documents. Printable HTML: `GET /api/v1/reports/documents/withholding-certificate/{id}`. The tax report `GET /api/v1/reports/tax/withholding`
lists every withholding with its certificate number (Finance contract `IWithholdingReportQueries`).

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
| `POST /receipts/{id}/reverse` · `POST /payments/{id}/reverse` | finance.receipt.reverse / finance.payment.reverse (sensitive) | `{date, reason}`; see *Reversal*. Idempotency-Key required. |
| `POST /bank-reconciliations/{id}/reopen` | finance.reconciliation.manage | Completed → Draft; movements become unreconciled, matches are kept. |
| `POST /cash-bank-accounts/{id}/sync-gl {upTo?}` | finance.reconciliation.manage | Imports GL lines posted outside Finance; returns `{imported}`. |
| `GET/POST /withholding-certificates`, `GET /withholding-certificates/{id}`, `POST /withholding-certificates/{id}/cancel` | finance.read / finance.withholding-certificate.manage | Create needs Idempotency-Key. |
| `GET /fx-revaluations?legalEntityId`, `POST /fx-revaluations {legalEntityId, asOf}` | finance.read / finance.revaluation.run (sensitive) | Closing rate (fallback spot) per foreign currency; one run per legal entity and date (`finance.revaluation-exists`). The next day must be in an open period. Idempotency-Key required. |

## Data model

`OpenItem` (one per installment; positive amounts with `Sign` ±1; historical base; status Open/PartiallySettled/Settled/Cancelled; rowversion),
`Allocation` (immutable; amount, base relieved, settlement base, realized difference, settlement-currency amount for cross-currency
allocations, `ReversalOfId` on negated reversal rows), `CashBankAccount`, `Settlement` (receipts and payments, requested allocations as
JSON, status Draft/Posted/Reversed with reversal date and reason, `WithholdingCertificateId`), `InternalTransfer`, `Expense` (lines as
JSON), `CashMovement` (including imported `GlJournalLine` movements, unique per journal line), `BankReconciliation` (statement lines as
JSON), `FxRevaluationRun` (lines as JSON), `WithholdingCertificate` (lines as JSON). All tables carry `TenantId` (row-level security) and are company-scoped.

## Limitations

* Expense lines are validated through `Accounting.Contracts.IAccountQueries`: only active, non-control posting accounts of type Expense or Asset are accepted (`finance.invalid-expense-account`).
* Transfers between two currencies are journalised in the functional currency (a journal has one transaction currency).
* Reversal undoes the whole document; partial reversal (un-allocating one invoice) is not offered — reverse and re-enter.
* Withholding facts exclude reversed documents entirely: reversing a document after its period was reported changes that period's report.
* GL sync for reconciliation needs a dedicated GL account per bank account (see above).
* Cross-currency amounts default to spot rates of the document date; no bank-specific buy/sell rates.
