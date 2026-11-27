# Accounting module (Phase 7a — accounting core)

Schema `accounting` · migration order 70 · module code `accounting` (core, always on).
Books are kept **per legal entity**; the chart of accounts, cost centres and posting rules are **per company** (ADR-017).

## Posting contract (for every other module)

`Erp.Modules.Accounting.Contracts` — the only accounting types other modules may use.

```csharp
public interface IJournalPostingService
{
    Task<Result<PostedJournal>> PostAsync(JournalRequest request, CancellationToken ct = default);
    Task<Result<PostedJournal>> ReverseAsync(Guid legalEntityId, string sourceDocType, Guid sourceDocId, string purpose,
                                             DateOnly reversalDate, CancellationToken ct = default);
}

public sealed record JournalRequest(Guid LegalEntityId, DateOnly JournalDate, string SourceModule, string SourceDocType,
    Guid SourceDocId, string Purpose, string Description, string Currency, decimal ExchangeRate,
    IReadOnlyList<JournalLineRequest> Lines);

public sealed record JournalLineRequest(string? Role, Guid? AccountId, decimal Debit, decimal Credit,
    PostingContext? Context = null, Guid? CostCenterId = null, string? PartyType = null, Guid? PartyId = null,
    string? Description = null);

public sealed record PostingContext(string? ProductPostingGroup = null, string? PartyPostingGroup = null,
    Guid? TaxCodeId = null, Guid? WarehouseId = null);
```

Rules the engine enforces:

| Rule | Error code |
|---|---|
| Must run inside the caller's transaction (ADR-006). Call it from a command handler; the journal commits or rolls back with the source document. | `accounting.transaction-required` |
| Each line names a **role** (normal case) or an **account id** (e.g. a specific bank account chosen by the user). Roles are resolved through the company's posting rules: most specific match on (legal entity, product posting group, party posting group, tax code, warehouse), then highest priority. | `accounting.posting-rule-missing` (message names role and context), `accounting.unknown-posting-role` |
| Lines are one-sided and positive; at least two lines; transaction-currency debits = credits. | `accounting.line-one-sided`, `accounting.too-few-lines`, `accounting.journal-unbalanced` |
| Base amounts = tx amount × rate, rounded per line, half away from zero, 2 decimals (MR-6). A base residual ≤ 0.01 is booked to the `FxRoundingDifference` role; more is rejected. | `accounting.journal-unbalanced` |
| Functional-currency journals must use rate 1; other currencies need a positive rate. | `accounting.invalid-exchange-rate` |
| Only active posting (leaf) accounts. Control accounts accept lines only from their sub-ledger module (Receivables: finance/sales/pos; Payables: finance/procurement; Inventory: inventory/procurement/sales/pos/manufacturing; GRNI/GINR: procurement/inventory; WIP: manufacturing). | `accounting.not-posting-account`, `accounting.account-inactive`, `accounting.control-account-restricted` |
| The date must fall in an **open** period of the legal entity's fiscal year. Soft-closed periods accept postings only from a user holding `accounting.period.post-soft-closed` (never from background postings). | `accounting.period-closed`, `accounting.period-not-found` |
| The legal entity must belong to the active company. If the scope has no company yet (e.g. an outbox handler) the engine adopts the legal entity's company. | `accounting.legal-entity-not-found`, `accounting.legal-entity-not-in-company` |
| **Idempotent:** (legal entity, SourceDocType, SourceDocId, Purpose) is unique. Posting the same request again returns the existing journal with `AlreadyPosted = true`. | — |
| Journal numbers `JV-{fiscal year}-{000001}` are gapless per legal entity and fiscal year, allocated inside the transaction (ADR-021). | — |
| `ReverseAsync` posts a journal with every line swapped (purpose `"{purpose}:reversal"`), marks the original `Reversed`, and is idempotent. | `accounting.journal-not-found`, `accounting.journal-already-reversed` |

Other contracts: `IPeriodGuard.EnsureOpenAsync(legalEntityId, date)`; `ILedgerQueries` — account balance as of a date, role balance,
trial balance, and `GetUnresolvedRolesAsync` (accounting readiness check used before enabling a module).

### Posting roles

`PostingRoles` (45 constants): CustomerReceivable, CustomerAdvance, SupplierPayable, SupplierAdvance, SalesRevenue, SalesReturns,
SalesDiscount, ServiceRevenue, OutputTax, InputTax, NonRecoverableInputTax, WithholdingTaxPayable, WithholdingTaxReceivable, Inventory,
CostOfGoodsSold, InventoryAdjustmentLoss, InventoryAdjustmentGain, InventoryInTransit, GoodsReceivedNotInvoiced, GoodsInvoicedNotReceived,
PurchasePriceVariance, LandedCostClearing, OpeningBalanceEquity, CostTrueUp, WorkInProgress, AppliedLabor, AppliedMachine, AppliedOverhead,
ProductionVariance, AbnormalScrap, CashAccount, BankAccount, BankCharges, CashOverShort, InternalTransferClearing, RealizedFxGain,
RealizedFxLoss, UnrealizedFxGain, UnrealizedFxLoss, FxRoundingDifference, FixedAssetCost, AccumulatedDepreciation, DepreciationExpense,
AssetDisposalGainLoss, RetainedEarnings.

Source module codes: `SourceModules` (accounting, finance, sales, pos, procurement, inventory, manufacturing, fixed-assets).

### Example (sales invoice J-1)

```csharp
await postingService.PostAsync(new JournalRequest(legalEntityId, invoiceDate, SourceModules.Sales, "SalesInvoice", invoice.Id,
    "invoice", $"Invoice {invoice.Number}", "EGP", 1m,
[
    new(PostingRoles.CustomerReceivable, null, 1140m, 0m, PartyType: "Customer", PartyId: customerId),
    new(PostingRoles.SalesRevenue,       null, 0m, 1000m, new PostingContext(ProductPostingGroup: "TRADING")),
    new(PostingRoles.OutputTax,          null, 0m, 140m,  new PostingContext(TaxCodeId: vatId)),
    new(PostingRoles.CostOfGoodsSold,    null, 625m, 0m),
    new(PostingRoles.Inventory,          null, 0m, 625m),
]), ct);
```

## HTTP endpoints (`/api/v1/accounting`, all require `X-Company-Id`)

| Method & path | Permission | Notes |
|---|---|---|
| `GET /accounts` | accounting.account.read | Flat list ordered by code with `parentId` (build the tree client-side), normal balance, control sub-ledger. |
| `POST /accounts` | accounting.account.manage | `{code, nameEn, nameAr, type, parentId?, isPostingAccount, controlSubledger?, allowManualPosting}`. Parent must be a group of the same type. |
| `PUT /accounts/{id}` | accounting.account.manage | `{nameEn, nameAr, allowManualPosting}` (control accounts never allow manual posting). |
| `POST /accounts/{id}/deactivate`, `/activate` | accounting.account.manage | |
| `POST /coa-templates/generic` | account.manage + posting-rule.manage | Seeds the IFRS-style chart (EN/AR, 57 accounts) and a default rule for every posting role. Idempotent: returns `{accountsCreated, rulesCreated, unresolvedRoles}`. |
| `GET /fiscal-years?legalEntityId` | accounting.account.read | With periods and status. |
| `POST /fiscal-years` | accounting.period.manage | `{legalEntityId, startDate (1st of a month), months = 12}`; overlaps → `accounting.fiscal-year-overlaps`. |
| `PUT /periods/{id}/status` | accounting.period.manage | `{status: Open | SoftClosed | Closed}`. Close in order, reopen in reverse order (`accounting.period-order`). Audited. |
| `GET /cost-centers`, `POST /cost-centers` | account.read / account.manage | |
| `GET /posting-rules` | accounting.account.read | |
| `PUT /posting-rules` | accounting.posting-rule.manage | Upsert by (role + context keys): `{role, accountId, legalEntityId?, productPostingGroup?, partyPostingGroup?, taxCodeId?, warehouseId?, priority}`. |
| `GET /posting-roles` | accounting.account.read | The role catalogue. |
| `GET /journals?legalEntityId&from&to&status&sourceModule&page&pageSize` | accounting.journal.read | Paged summaries. |
| `GET /journals/{id}` | accounting.journal.read | Header, lines with account codes, base and transaction amounts. |
| `POST /journals` | accounting.journal.create | Manual draft: `{legalEntityId, journalDate, description, currency?, exchangeRate?, lines:[{accountId, debit, credit, costCenterId?, description?}]}`. |
| `POST /journals/{id}/post` | accounting.journal.post | Manual journals cannot use control accounts or accounts with manual posting disabled. |
| `POST /journals/{id}/reverse` | accounting.journal.reverse | `{reversalDate}`. Only manual journals; module journals are reversed by cancelling their source document (`accounting.reverse-from-source-document`). |
| `GET /reports/trial-balance?legalEntityId&from&to` | accounting.report.read | Opening, period debit/credit, closing per account; `isBalanced`. From posted journal lines only. |
| `GET /reports/general-ledger?legalEntityId&accountId&from&to` | accounting.report.read | Opening balance, lines with running balance, closing. |

## Data model

`Account`, `CostCenter`, `FiscalYear` → `FiscalPeriod`, `JournalEntry` → `JournalLine`, `AccountDetermination` (posting rules),
`AccountBalance` (running totals per legal entity/account/period/cost centre/currency, upserted with `UPDLOCK, HOLDLOCK` in the posting
transaction, locks taken in account order). All tables carry `TenantId` and are covered by row-level security.
The posting rule set is the company's collection of `AccountDetermination` rows (no separate rule-set aggregate).

## Not yet implemented

Year-end closing entry, month-end FX revaluation (OQ-ACC-4/5), opening-balance import, multi-currency balances reporting,
and database-level prevention of UPDATE/DELETE on posted lines (immutability is enforced by the domain and API).
