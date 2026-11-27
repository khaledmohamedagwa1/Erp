# Fixed Assets module

Schema `assets`, migration order 150, module code `fixed-assets`. The module is optional: every endpoint uses `.RequireCompany()` and `.RequireModule("fixed-assets")`. The module catalog lists `accounting` as its only dependency. No business preset turns it on; switch it on with `PUT /api/v1/configuration/modules/fixed-assets {"enabled": true}`.

The module covers:

- **Asset categories:** defaults for new assets (method, life, salvage %, declining rate) and the posting group.
- **The asset lifecycle:** Draft → Active → FullyDepreciated → Disposed, with history events.
- **Depreciation:** straight-line, declining balance (switching to straight-line) and units of production.
- **Monthly depreciation runs:** one journal per legal entity and period.
- **Revaluation, transfer, disposal** and the **asset register**.

All journals go through `IJournalPostingService`, inside the command's transaction. The module never writes another module's tables.

## Contracts (`Erp.Modules.FixedAssets.Contracts`)

```csharp
public static class FixedAssetsPermissions
{
    Read = "assets.read"; Manage = "assets.manage";
    DepreciationPost = "assets.depreciation.post" /* sensitive */; Revalue = "assets.revalue" /* sensitive */; Dispose = "assets.dispose" /* sensitive */;
}

public static class FixedAssetDocumentTypes   // SourceDocType of the module's journals (SourceModule = "fixed-assets")
{
    Acquisition = "AssetAcquisition"; DepreciationRun = "DepreciationRun"; Revaluation = "AssetRevaluation"; Disposal = "AssetDisposal";
}
```

The module exposes no query service yet; other modules have not needed one.

## Domain

All aggregates are company-scoped and `[Audited]`. Child collections (schedule, history, run lines) are stored as JSON columns.

| Aggregate | Key fields |
|---|---|
| `AssetCategory` | Code (unique per company, upper-cased), names EN/AR, `DefaultMethod`, `UsefulLifeMonths` (1–1200), `SalvagePercent`, `DecliningRatePercent` (required for declining balance), `PostingGroup`, `IsActive` |
| `Asset` | Number (`FA…`, given on activation), names, category, legal entity, branch, location, custodian, acquisition and in-service dates, `AcquisitionCost`, `Cost` (acquisition cost ± revaluations), `SalvageValue`, `UsefulLifeMonths`, `Method`, `DecliningRatePercent`, `TotalUnits`, `Status`, `AccumulatedDepreciation`, `NetBookValue` (= Cost − Accumulated), acquisition link (mode, journal or source document/line), disposal fields, `Schedule`, `History` |
| `DepreciationRun` | Legal entity, period (first day of the month), `Posted`/`Reversed`, total, journal, reversal date and journal, lines (asset, number, category, posting group, amount) |

Money is `decimal(19,4)` rounded to 2 decimals (half away from zero). Percentages are `decimal(9,6)` and units are `decimal(18,6)`.

### Calculation rules (`DepreciationCalculator`, pure and unit-tested)

- **Convention:** an asset put in service on or before the 15th depreciates for that whole month. After the 15th, depreciation starts the following month.
- **Straight-line:** monthly = round((cost − salvage − accumulated) ÷ remaining months). The last month absorbs the rounding residual. J-12: 120,000 over 10 years gives 1,000 a month.
- **Declining balance:** each month takes the larger of (NBV × annual rate ÷ 12) and the straight-line amount over the remaining months. This switches the asset to straight-line once that gives more. The last month takes whatever is left down to salvage.
- **Units of production:** no forward schedule. `POST /assets/{id}/usage` records units for a month, and the amount is (cost − salvage) × units ÷ total units, capped at what is still depreciable. Recording usage again for an unposted month replaces it. Posted months are locked.
- An asset **never goes below salvage value**. When NBV reaches salvage, the asset becomes `FullyDepreciated`.
- **Rescheduling:** posted lines never change. Revaluation (and its optional new useful life) rebuilds only the unposted lines. The rebuild starts the month after the last posted line, runs over (useful life − posted months), and starts from the current cost, salvage and accumulated depreciation.

## Postings

| Event | Journal | Date |
|---|---|---|
| Activate, `ManualJournal` | Dr FixedAssetCost / Cr chosen clearing account | acquisition date |
| Activate, `PurchaseInvoice` | **none:** the asset only stores `SourceDocType`/`SourceDocId`/`SourceLineId` (validated, see below) | — |
| Depreciation run | per posting group: Dr DepreciationExpense / Cr AccumulatedDepreciation (J-12) | last day of the period |
| Revaluation up (IAS 16, no account given) | Dr FixedAssetCost / Cr ImpairmentLoss (reverses earlier write-downs first) and Cr AssetRevaluationReserve (the rest) | revaluation date |
| Revaluation down (IAS 16, no account given) | Dr AssetRevaluationReserve (up to the asset's reserve) and Dr ImpairmentLoss (the rest) / Cr FixedAssetCost | revaluation date |
| Revaluation with `reserveAccountId` (legacy) | the whole difference against the chosen account; no reserve is tracked | revaluation date |
| Impairment | Dr AssetRevaluationReserve (asset's reserve first) and Dr ImpairmentLoss / Cr FixedAssetCost | impairment date |
| Disposal | Dr AccumulatedDepreciation, Dr chosen proceeds account / Cr FixedAssetCost; the difference goes to AssetDisposalGainLoss (Cr for a gain, Dr for a loss); the asset's remaining reserve: Dr AssetRevaluationReserve / Cr RetainedEarnings | disposal date |
| Partial disposal | the same lines for the disposed share (cost, accumulated depreciation and reserve × fraction) | disposal date |
| Transfer to another legal entity | sender: Dr AccumulatedDepreciation, Dr clearing account (NBV) / Cr FixedAssetCost; receiver: Dr FixedAssetCost / Cr AccumulatedDepreciation, Cr clearing account (NBV) | transfer date |

Role lines carry `PostingContext(ProductPostingGroup: category.PostingGroup)`, so a posting rule for a specific asset class (for example `VEH` → 1520 Vehicles) takes priority. Without one, the generic rule applies: FixedAssetCost 1510, AccumulatedDepreciation 1590, DepreciationExpense 5510, AssetDisposalGainLoss 4940.

User-chosen accounts (clearing, proceeds, revaluation reserve) must be active posting accounts that are not control accounts. Otherwise the request fails with `assets.account-invalid`.

**Why a purchase-invoice acquisition posts nothing:** the procurement invoice line has already debited the asset account (its expense account is the PPE account). A second journal would double-count the asset.

**Purchase-invoice link validation** (`InvoiceLinkValidator`, through `Procurement.Contracts.IPurchaseInvoiceQueries`): `sourceDocType` must be `PurchaseInvoice`; the invoice must exist in the company (`assets.source-invoice-not-found`), be posted (`assets.source-invoice-not-posted`) and belong to the asset's legal entity; a given `sourceLineId` must exist on it. The capitalised cost of all non-draft assets linked to the line (or to the whole invoice when no line is given) may not exceed the line's posted functional amount (net + non-recoverable tax) by more than `FixedAssets:InvoiceMatchTolerance` (default 1.00) — `assets.source-amount-mismatch`. Several assets may share one line (e.g. two vans on a line of quantity 2).

**IAS 16 revaluation model:** each asset tracks `RevaluationReserve` (its share of the surplus) and `RevaluationLossInProfit` (write-downs recognised in profit or loss). An increase first reverses earlier write-downs through ImpairmentLoss, then goes to AssetRevaluationReserve; a decrease first uses up the asset's reserve, then is an expense. The realised reserve moves to RetainedEarnings on (partial) disposal — never through profit or loss. Revaluation keeps the NBV method (the difference changes cost; accumulated depreciation is not restated proportionally) and the remaining life is rescheduled. An explicit `reserveAccountId` keeps the earlier behaviour (whole difference to that account, nothing tracked). Excess-depreciation transfers from the reserve during the asset's life (IAS 16.41 option) are not made.

**Impairment (IAS 36):** `POST /assets/{id}/impair {date, recoverableAmount, reason?}` writes the NBV down to the recoverable amount (must be below NBV), lowering salvage if needed, and reschedules the remaining life. Later upward revaluations reverse it first.

**Partial disposal:** `POST /assets/{id}/dispose-partial {date, quantity | percent, proceeds, proceedsAccountId?}` removes the fraction (quantity ÷ the asset's `quantity`, or percent ÷ 100) of cost, accumulated depreciation, salvage, reserve, remaining units and every additional book; the remaining life is unchanged. Depreciation must be up to date (as for disposal). A run that contains an asset partly disposed afterwards can no longer be reversed (`assets.partially-disposed`).

**Transfers:** within the legal entity, `POST /assets/{id}/transfer` moves branch, location or custodian (no journal; the branch data scope applies). To another legal entity of the same company, `POST /assets/{id}/transfer-legal-entity {date, targetLegalEntityId, clearingAccountId, targetBranchId?}` disposes of the asset at NBV in the sender and creates a new asset (new number, `acquisitionMode = Transfer`, `sourceDocId` = the old asset) in the receiver with the same cost, accumulated depreciation, salvage and method and the remaining life, depreciating from the transfer month. Both journals go through the chosen clearing account (an inter-company account). Both legal entities must share the functional currency; assets with a revaluation reserve cannot be transferred. **Transfers between companies** (different `CompanyId`) are not supported: dispose in one company and acquire in the other.

**Additional depreciation books** (reporting only): `POST /books {code, nameEn, nameAr}` defines a book (e.g. `TAX`); `PUT /assets/{id}/books/{bookId} {method, usefulLifeMonths, salvageValue?, decliningRatePercent?, cost?, openingAccumulated?}` gives the asset its parameters in that book (straight line or declining balance; cost defaults to the acquisition cost; life counts from the first depreciation month). The book's depreciation is a schedule read as of a date — it never posts. `GET /reports/book-register?legalEntityId&bookId&asOf` lists book cost, accumulated depreciation and NBV next to the posting book's NBV and the difference (e.g. the temporary difference for deferred tax). Partial disposals shrink every book proportionally; disposals and transfers end them (the receiver gets the book values carried over). Revaluations and impairments affect the posting book only.

**Automatic depreciation:** `PUT /settings {autoDepreciationEnabled, runOnDay}` (1–28). From that day of each month the worker posts last month's run for every legal entity with active assets — each legal entity in its own transaction, idempotent (an existing run is left alone; catch-up included). The result is kept in `lastRunSummary`. `POST /depreciation-runs/scheduled/run {today?}` runs the same logic on demand. The worker checks every `FixedAssets:ScheduleIntervalMinutes` (default 60) on the worker/all role.

### Depreciation runs

- `POST /depreciation-runs {legalEntityId, period}` posts every due line up to and including the period for all `Active` assets of the legal entity. Overdue months are caught up in the same run.
- Runs are **idempotent per (legal entity, period).** If a posted run exists, it is returned with `alreadyPosted: true` (HTTP 200) and nothing is posted. A filtered unique index on (TenantId, LegalEntityId, Period) where `Status = 'Posted'` also stops concurrent duplicates.
- **Reverse** (`POST /depreciation-runs/{id}/reverse`) uses `IJournalPostingService.ReverseAsync`. That means the run can be reversed only while the reversal date's period is open; the default date is the run's period end. Only the latest posted run of the legal entity can be reversed, and not after any of its assets has been disposed. Reversal turns the lines back to unposted and frees the period.
- **Disposal requires depreciation to be up to date:** every schedule line before the disposal month must be posted (`assets.depreciation-not-up-to-date`). Unposted lines from the disposal month on are dropped.

## Endpoints (`/api/v1/assets`)

| Method | Route | Permission |
|---|---|---|
| GET / POST | `/categories` | read / manage |
| PUT | `/categories/{id}` | manage |
| GET | `/assets?page&pageSize&legalEntityId&categoryId&status&search` | read |
| POST | `/assets` (draft) | manage |
| GET / PUT / DELETE | `/assets/{id}` (PUT and DELETE only for drafts) | read / manage |
| GET | `/assets/{id}/schedule`, `/assets/{id}/history` | read |
| POST | `/assets/{id}/activate` `{mode: ManualJournal\|PurchaseInvoice, clearingAccountId?, sourceDocType?, sourceDocId?, sourceLineId?}` | manage, idempotent |
| POST | `/assets/{id}/transfer` `{date, branchId?, location?, custodianId?, note?}` | manage |
| POST | `/assets/{id}/usage` `{period, units}` | manage |
| POST | `/assets/{id}/revalue` `{date, newNetBookValue, reserveAccountId?, newUsefulLifeMonths?}` | assets.revalue, idempotent |
| POST | `/assets/{id}/impair` `{date, recoverableAmount, reason?}` | assets.revalue, idempotent |
| POST | `/assets/{id}/dispose` `{date, proceeds, proceedsAccountId?}` | assets.dispose, idempotent |
| POST | `/assets/{id}/dispose-partial` `{date, quantity?, percent?, proceeds, proceedsAccountId?}` | assets.dispose, idempotent |
| POST | `/assets/{id}/transfer-legal-entity` `{date, targetLegalEntityId, clearingAccountId, targetBranchId?}` | assets.dispose, idempotent |
| GET / POST | `/books` | read / manage |
| PUT | `/books/{id}` | manage |
| GET | `/assets/{id}/books` | read |
| PUT / DELETE | `/assets/{id}/books/{bookId}` | manage |
| GET / PUT | `/settings` | read / assets.depreciation.post |
| POST | `/depreciation-runs/scheduled/run` `{today?}` | assets.depreciation.post |
| GET | `/depreciation-runs?legalEntityId` | read |
| POST | `/depreciation-runs/preview` `{legalEntityId, period}` | read |
| POST | `/depreciation-runs` `{legalEntityId, period}` | assets.depreciation.post, idempotent |
| POST | `/depreciation-runs/{id}/reverse` `{date?}` | assets.depreciation.post, idempotent |
| GET | `/reports/register?legalEntityId&asOf` | read |
| GET | `/reports/book-register?legalEntityId&bookId&asOf` | read |

Enums travel as strings (`StraightLine`, `DecliningBalance`, `UnitsOfProduction`; `Draft`, `Active`, `FullyDepreciated`, `Disposed`).

### Asset register

The register shows each non-draft asset of the legal entity that was acquired on or before `asOf` and not disposed by then. For each asset:

- **Cost:** the cost effects of the asset's events dated on or before `asOf` (acquisition or transfer-in, revaluations, impairments, partial disposals).
- **Accumulated depreciation:** lines of posted runs whose journal date (period end) is on or before `asOf`, plus amounts carried in by a transfer or removed by partial disposals dated on or before `asOf`.
- **NBV.**

It also gives totals per category and overall. For manual acquisitions, the totals tie to the GL balances of FixedAssetCost and AccumulatedDepreciation. This is checked by an integration test. Assets acquired from purchase invoices tie only if the invoice line used the same account.

## Errors

`assets.not-draft`, `assets.not-active`, `assets.disposed`, `assets.cost-invalid`, `assets.salvage-invalid`, `assets.useful-life-invalid`, `assets.rate-invalid`, `assets.units-invalid`, `assets.dates-invalid`, `assets.not-units-of-production`, `assets.period-posted`, `assets.period-before-service`, `assets.depreciation-not-up-to-date`, `assets.value-below-salvage`, `assets.no-remaining-life`, `assets.asset-not-found`, `assets.category-not-found`, `assets.category-code-taken`, `assets.run-not-found`, `assets.legal-entity-not-found`, `assets.account-invalid`, `assets.clearing-account-required`, `assets.source-required`, `assets.proceeds-account-required`, `assets.nothing-to-depreciate`, `assets.not-latest-run`, `assets.cross-legal-entity`, `assets.impairment-invalid`, `assets.fraction-invalid`, `assets.partial-quantity-or-percent`, `assets.partially-disposed`, `assets.quantity-invalid`, `assets.same-legal-entity`, `assets.transfer-currency-mismatch`, `assets.transfer-with-reserve`, `assets.book-not-found`, `assets.book-code-taken`, `assets.book-code-invalid`, `assets.book-method-invalid`, `assets.book-locked`, `assets.run-day-invalid`, `assets.source-type-unsupported`, `assets.source-invoice-not-found`, `assets.source-invoice-not-posted`, `assets.source-invoice-other-legal-entity`, `assets.source-line-not-found`, `assets.source-amount-mismatch`.

## Known limitations

- Functional currency only. Additional books are reporting-only (they never post) and support straight line and declining balance; revaluations and impairments do not affect them.
- Revaluation uses the NBV method (the difference goes to cost); accumulated depreciation is not restated proportionally, and no yearly excess-depreciation transfer from the reserve is made (the reserve moves on disposal only). ⚖️
- Transfers between legal entities need the same functional currency and no revaluation reserve; transfers between companies are not supported (dispose and re-acquire). No asset splits/merges beyond partial disposal.
- Partial disposal is not available for units-of-production assets with recorded but unposted usage of later months (depreciation must be up to date).
- Category changes apply only to new assets. Existing assets keep the method, life and posting group they were created with.
- Automatic depreciation posts last month only once its run day is reached; a closed period makes that legal entity's run fail (recorded in `lastRunSummary`), not the others.
