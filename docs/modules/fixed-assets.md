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
| Activate, `PurchaseInvoice` | **none:** the asset only stores `SourceDocType`/`SourceDocId`/`SourceLineId` | — |
| Depreciation run | per posting group: Dr DepreciationExpense / Cr AccumulatedDepreciation (J-12) | last day of the period |
| Revaluation up | Dr FixedAssetCost / Cr chosen reserve account | revaluation date |
| Revaluation down | Dr chosen account / Cr FixedAssetCost | revaluation date |
| Disposal | Dr AccumulatedDepreciation, Dr chosen proceeds account / Cr FixedAssetCost; the difference goes to AssetDisposalGainLoss (Cr for a gain, Dr for a loss) | disposal date |

Role lines carry `PostingContext(ProductPostingGroup: category.PostingGroup)`, so a posting rule for a specific asset class (for example `VEH` → 1520 Vehicles) takes priority. Without one, the generic rule applies: FixedAssetCost 1510, AccumulatedDepreciation 1590, DepreciationExpense 5510, AssetDisposalGainLoss 4940.

User-chosen accounts (clearing, proceeds, revaluation reserve) must be active posting accounts that are not control accounts. Otherwise the request fails with `assets.account-invalid`.

**Why a purchase-invoice acquisition posts nothing:** the procurement invoice line has already debited the asset account (its expense account is the PPE account). A second journal would double-count the asset. The link is informational and is not checked against Procurement, which has no contract for it yet.

**Revaluation reserve:** no `AssetRevaluationReserve` posting role exists. Adding one would leave readiness checks and existing charts unmapped, so the user picks the account on each revaluation. This is a simplification of IAS 16. Decreases also go to the chosen account (pick an expense account for an impairment). The reserve is not recycled to retained earnings on disposal.

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
| POST | `/assets/{id}/revalue` `{date, newNetBookValue, reserveAccountId, newUsefulLifeMonths?}` | assets.revalue, idempotent |
| POST | `/assets/{id}/dispose` `{date, proceeds, proceedsAccountId?}` | assets.dispose, idempotent |
| GET | `/depreciation-runs?legalEntityId` | read |
| POST | `/depreciation-runs/preview` `{legalEntityId, period}` | read |
| POST | `/depreciation-runs` `{legalEntityId, period}` | assets.depreciation.post, idempotent |
| POST | `/depreciation-runs/{id}/reverse` `{date?}` | assets.depreciation.post, idempotent |
| GET | `/reports/register?legalEntityId&asOf` | read |

Enums travel as strings (`StraightLine`, `DecliningBalance`, `UnitsOfProduction`; `Draft`, `Active`, `FullyDepreciated`, `Disposed`).

### Asset register

The register shows each non-draft asset of the legal entity that was acquired on or before `asOf` and not disposed by then. For each asset:

- **Cost:** acquisition cost plus revaluations dated on or before `asOf`.
- **Accumulated depreciation:** lines of posted runs whose journal date (period end) is on or before `asOf`.
- **NBV.**

It also gives totals per category and overall. For manual acquisitions, the totals tie to the GL balances of FixedAssetCost and AccumulatedDepreciation. This is checked by an integration test. Assets acquired from purchase invoices tie only if the invoice line used the same account.

## Errors

`assets.not-draft`, `assets.not-active`, `assets.disposed`, `assets.cost-invalid`, `assets.salvage-invalid`, `assets.useful-life-invalid`, `assets.rate-invalid`, `assets.units-invalid`, `assets.dates-invalid`, `assets.not-units-of-production`, `assets.period-posted`, `assets.period-before-service`, `assets.depreciation-not-up-to-date`, `assets.value-below-salvage`, `assets.no-remaining-life`, `assets.asset-not-found`, `assets.category-not-found`, `assets.category-code-taken`, `assets.run-not-found`, `assets.legal-entity-not-found`, `assets.account-invalid`, `assets.clearing-account-required`, `assets.source-required`, `assets.proceeds-account-required`, `assets.nothing-to-depreciate`, `assets.not-latest-run`, `assets.cross-legal-entity`.

## Known limitations

- Single book (no separate tax/IFRS books) and functional currency only.
- No partial disposals, asset splits/merges or transfers between legal entities (dispose and re-acquire instead).
- The purchase-invoice link is not validated against Procurement and does not prevent the same invoice line being linked twice.
- Category changes apply only to new assets. Existing assets keep the method, life and posting group they were created with.
- Revaluation uses the NBV method (the difference goes to cost). Accumulated depreciation is not restated proportionally.
- Depreciation runs are triggered by API, not scheduled.
