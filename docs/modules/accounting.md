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
| `PUT /accounts/{id}/cash-flow-category` | accounting.account.manage | `{category: None | Operating | Investing | Financing | Cash}`; balance-sheet accounts only (`accounting.cash-flow-category-balance-sheet-only`). Optional `cashFlowCategory` on `POST /accounts`; new balance-sheet posting accounts default to Operating. |
| `GET /reports/income-statement?legalEntityId&from&to&compareFrom?&compareTo?&costCenterId?` | accounting.report.read | Sections revenue / cost-of-sales / other-income / operating-expenses; `netRevenue`, `grossProfit`, `netProfit`, each with `amount`, `ytd` (fiscal-year start to `to`) and `comparative`. |
| `GET /reports/balance-sheet?legalEntityId&asOf&compareAsOf?` | accounting.report.read | Assets, liabilities, equity (incl. "Current and unclosed earnings"); `isBalanced`. |
| `GET /reports/cash-flow?legalEntityId&from&to` | accounting.report.read | Indirect method: net profit, operating adjustments, investing, financing, opening/closing cash, `reconciles`. |
| `GET /reports/{trial-balance|income-statement|balance-sheet|cash-flow}.csv` | accounting.report.read | Same parameters as the JSON reports. UTF-8 with BOM, RFC 4180 quoting, invariant-culture amounts. |
| `POST /fiscal-years/{id}/close` | accounting.year-end.close | Year-end close (below). Idempotent. |
| `POST /fiscal-years/{id}/reopen` | year-end.close + period.manage | Reverses the closing journal; last period back to soft-closed. |
| `GET /opening-balances?legalEntityId` | accounting.journal.read | |
| `POST /opening-balances` | accounting.opening-balance.manage | `{legalEntityId, openingDate, description?, lines:[{accountId, debit, credit, description?}]}`; control accounts refused (`accounting.opening-control-account`). |
| `POST /opening-balances/{id}/post` | accounting.opening-balance.manage | One posted document per legal entity (`accounting.opening-balance-exists`). |
| `POST /opening-balances/{id}/reverse` | accounting.opening-balance.manage | `{reversalDate}` (open period). |
| `GET /readiness?legalEntityId&roles?` | accounting.account.read | `{missingRoles, hasChartOfAccounts, hasFiscalYearForToday, todayPeriodStatus, isReady}`. `roles` = comma-separated subset (default: all roles). |

## Financial statements (Part 12.9)

All statements are computed **only from posted journal lines** (status Posted or Reversed — a reversal is itself a posted
journal, so the pair nets to zero), in the legal entity's functional currency.

* **Income statement.** Each revenue/expense account carries a stored **income-statement group** (`ReportGroup`: Revenue, CostOfSales,
  OtherIncome, OperatingExpenses), editable with `PUT /accounts/{id}/report-group {group}` (`accounting.report-group-invalid` when it
  does not fit the account type). The generic chart derives it once at seeding from the role mapping (net-revenue roles `SalesRevenue`,
  `SalesReturns`, `SalesDiscount`, `ServiceRevenue`; cost-of-sales roles `CostOfGoodsSold`, `PurchasePriceVariance`, `CostTrueUp`,
  `ProductionVariance`, `AbnormalScrap`); new accounts default to Revenue / OperatingExpenses; the migration back-filled existing accounts
  the same way. Statements therefore no longer change when posting rules change. Gross profit = revenue − cost of sales; net profit =
  gross profit + other income − operating expenses. Year-end closing journals are excluded. The cost-centre filter applies to the income
  statement only (balance sheets are not kept per cost centre).
* **Balance sheet** "as of" a date uses cumulative balances since inception, so balance-sheet accounts carry forward across
  fiscal years without any opening entry. Revenue/expense balances not yet closed appear in equity as
  *Current and unclosed earnings*; after the year-end close they are in retained earnings instead. Assets = liabilities + equity
  always holds because every posted journal balances.
* **Cash flow (indirect).** Net profit, then each balance-sheet account's movement with the sign flipped (a rise in a non-cash
  asset uses cash; a rise in a liability or equity provides it), grouped by the account's **cash-flow category** (editable with
  `PUT /accounts/{id}/cash-flow-category`): Operating, Investing, Financing. Unclassified (`None`) balance-sheet accounts are listed in a
  separate `unclassified` section and counted with operating activities, so the statement still ties while showing what needs a category. `Cash` accounts are the ones explained.
  Year-end closing journals are excluded (they are non-cash and net to zero). The generic chart sets: 1110/1120 Cash, 1510
  Investing, 3100/3200/3300 Financing, every other balance-sheet posting account Operating — accumulated depreciation is operating
  on purpose: its increase is the depreciation add-back.

## Year-end close (OQ-ACC-4)

`POST /fiscal-years/{id}/close`:

1. Earlier fiscal years of the legal entity must already be closed (`accounting.earlier-year-open`).
2. **Precondition (decision):** periods 1…n−1 are **Closed** and the last period is **Soft-closed** (`accounting.year-end-periods-not-ready`).
   The closing entry is dated on the last day, so that period must still accept a system posting, while soft-close keeps ordinary
   users out. The close posts there regardless of the user's soft-close permission.
3. One journal (`SourceDocType = YearEndClose`, source id = fiscal year, purpose `close-{n}`) brings every revenue and expense
   balance of the year — per account and cost centre — to zero; the net goes to the `RetainedEarnings` role. A year without
   revenue/expense balances is closed without a journal.
4. The last period becomes Closed and the year is marked closed: no postings, and period status changes are refused
   (`accounting.fiscal-year-closed`) until it is reopened.
5. Running it again returns the existing result.

`POST /fiscal-years/{id}/reopen` (needs `accounting.year-end.close` **and** `accounting.period.manage`; refused when a later year
is closed): sets the last period back to Soft-closed and posts the reversal of the closing journal on the last day. A later close
posts a new journal with the next purpose number (`close-2`, …) because the (source, purpose) key is unique.

Inactive revenue/expense accounts that still carry a balance block the close (`accounting.account-inactive`): reactivate them first.

## Opening balances

`OpeningBalance` documents enter the general-ledger position when a legal entity starts using the system. Posting creates one
journal (`SourceDocType = OpeningBalance`, purpose `opening`) on the opening date; the difference between the lines goes to the
`OpeningBalanceEquity` role. **Control accounts are refused**: receivable/payable openings must be posted by the Finance module
(with party references) and inventory openings by the Inventory module (with quantities and cost), both through
`IJournalPostingService` with their own source module, so each sub-ledger agrees with its control account. Only one posted
document per legal entity (also a filtered unique index); reverse it to replace it.

## Database-level immutability

Migration `StatementsYearEndOpeningBalances` adds two triggers:

* `accounting.TR_JournalLine_Immutable` — no UPDATE or DELETE of lines whose journal is Posted or Reversed (drafts stay editable).
* `accounting.TR_JournalEntry_Immutable` — posted/reversed journals cannot be deleted; the only permitted header change is
  Posted → Reversed with the reversal link (audit stamps and rowversion may change). Any other column change raises error 51003.

EF Core is told about the triggers (`HasTrigger`) so it does not use OUTPUT clauses on these tables.

## Data model

`Account`, `CostCenter`, `FiscalYear` → `FiscalPeriod`, `JournalEntry` → `JournalLine`, `AccountDetermination` (posting rules),
`AccountBalance` (running totals per legal entity/account/period/cost centre/currency, upserted with `UPDLOCK, HOLDLOCK` in the posting
transaction, locks taken in account order), `OpeningBalance` → `OpeningBalanceLine`. `FiscalYear` carries `IsClosed`,
`ClosingJournalId`, `ClosedAtUtc`; `Account` carries `CashFlowCategory` and `ReportGroup`. `JournalLine` stores the posting role it was
resolved from, the tax code of tax lines and the taxable base (for the VAT return). All tables carry `TenantId` and are covered by row-level security.
The posting rule set is the company's collection of `AccountDetermination` rows (no separate rule-set aggregate).

Contract additions (additive only): `AccountingPermissions.OpeningBalanceManage`, `AccountingPermissions.YearEndClose`,
`AccountingDocumentTypes` (ManualJournal, OpeningBalance, YearEndClose). No existing contract signature changed.

## Not yet implemented

Multi-currency balance reporting and statements consolidated across legal entities.

## Contract additions (wave 5)

* `JournalLineRequest.BaseOnly` (functional-currency-only line, e.g. a realized exchange difference in a foreign-currency journal) and
  `JournalLineRequest.TaxableAmount` (taxable amount behind a tax line) — optional, existing callers unchanged.
* `JournalPosted` moved to Accounting.Contracts as an integration event (outbox) with `CompanyId` and `JournalDate`; Tax subscribes to it
  for e-invoicing.
* `IAccountLedgerQueries`: posted lines of an account (bank reconciliation in Finance), role → account resolution, tax lines with tax
  code and taxable base, and the movement on tax accounts (VAT return).
