# Tax & Currency module

Schema `tax` · migration order 60 · module code `tax` (core) · project folder `backend/src/Modules/Tax`.

## Model

| Aggregate | Scope | Notes |
|---|---|---|
| `Currency` | tenant | ISO 4217 code (unique per tenant), EN/AR names, `MinorUnits` 0–4 (rounding precision), `CashRoundingIncrement` `decimal(19,4)` (≥ one minor unit; used later for POS cash rounding). |
| `ExchangeRate` | tenant | `FromCurrency` → `ToCurrency`, type `Spot`/`Average`/`Closing`, `EffectiveDate`, `Rate` `decimal(18,8)` = units of *to* per 1 *from* (normally base per 1 foreign). One rate per pair/type/date; recording again replaces it (audited). |
| `TaxCode` + `TaxRate` | company | Code, EN/AR names, kind `Output`/`Input`/`Withholding`, `Recoverable` (input only; never true for output), reporting box, jurisdiction. Rates are percent `decimal(9,6)` with `ValidFrom`/`ValidTo`; periods never overlap. Adding a rate after an open-ended one closes it the day before. |
| `TaxGroup` + components | company | Ordered tax codes applied together. `CompoundsOnPrevious` = base includes earlier non-withholding taxes (the first component cannot compound). |
| `TaxCategoryMapping` | company | (product tax category code, `Sales`/`Purchase`) → tax group. Products carry only the category code (e.g. `STANDARD`). |

All aggregates are `[Audited]`; all tables carry `TenantId` and are covered by row-level security.

## Tax engine (`TaxEngine.Compute`, pure and unit-tested)

* Amounts and each tax line are rounded to the currency's minor units, **half away from zero, per line** (decisions log MR-6).
* Exclusive: tax lines are computed on the net; compound components on net + earlier taxes.
* Inclusive: the net is back-calculated from the gross using the combined (compounded) rate; any rounding remainder is absorbed by the net, so **`Net + TotalTax == Gross` exactly** (verified over ~8,600 amounts in the unit tests).
* Withholding components are computed on the net and returned in `Lines`/`TotalWithholding`, but are **not** part of `TotalTax` or `Gross`: withholding reduces the amount paid at settlement (MR-2), not the document total.

## Contracts (for other modules)

* `ITaxCalculator.CalculateAsync(taxGroupId, amount, taxInclusive, currency, date)` and `CalculateForCategoryAsync(categoryCode, direction, …)` → `Result<TaxResult>`; errors `tax.group-not-found`, `tax.category-not-mapped`, `tax.rate-missing`, `tax.currency-not-found`. Requires the active company context.
* `ITaxCalculator.ResolveTaxGroupAsync(categoryCode, direction)` → group id or null.
* `IExchangeRateProvider.GetRateAsync(from, to, date, kind)` → latest rate on/before the date; same currency → 1; falls back to the inverse pair (1/rate, 8 decimals); null when none.
* `ICurrencyQueries.GetAsync(code)` → minor units and cash rounding.

## Endpoints (`/api/v1/tax`)

| Method & path | Permission | Company | Notes |
|---|---|---|---|
| `GET /currencies` | `tax.read` | — | |
| `POST /currencies` | `tax.manage` | — | `{code, nameEn, nameAr, minorUnits, cashRoundingIncrement?}` |
| `GET /exchange-rates?from&to&dateFrom&dateTo` | `tax.read` | — | Newest first, max 500. |
| `GET /exchange-rates/lookup?from&to&date&type?` | `tax.read` | — | 404 `tax.exchange-rate-not-found` when no rate on/before the date. |
| `POST /exchange-rates` | `tax.manage` | — | `{fromCurrency, toCurrency, type?, effectiveDate, rate}`; both currencies must exist. Upsert per pair/type/date. |
| `GET /tax-codes` | `tax.read` | ✔ | Includes rate history. |
| `POST /tax-codes` | `tax.manage` | ✔ | `{code, nameEn, nameAr, kind, recoverable, reportingBox?, jurisdiction?, rate, validFrom}` |
| `POST /tax-codes/{id}/rates` | `tax.manage` | ✔ | `{rate, validFrom, validTo?}`; 409 `tax.rate-period-overlap`. |
| `GET /tax-groups` · `POST /tax-groups` | `tax.read` / `tax.manage` | ✔ | `{code, nameEn, nameAr, components:[{taxCodeId, sequence, compoundsOnPrevious}]}` |
| `GET /category-mappings` · `PUT /category-mappings` | `tax.read` / `tax.manage` | ✔ | PUT upserts `{categoryCode, direction, taxGroupId}`. |
| `POST /calculate` | `tax.read` | ✔ | Preview: `{taxGroupId}` or `{categoryCode, direction}`, plus `amount, taxInclusive, currency, date?` (default today). |
| `POST /templates/egypt` | `tax.manage` | ✔ | Idempotent seed, see below. Body `{effectiveFrom?, tableTaxRate?}`. |

## Egypt template

Seeds, only where the code does not already exist (running it twice creates nothing):

* Currency **EGP** (2 minor units, cash rounding 0.01 — i.e. no cash rounding until the company decides otherwise).
* Tax codes (jurisdiction `EG`, effective **2017-07-01** unless `effectiveFrom` is given): `VAT14-OUT`, `VAT14-IN` (recoverable), `VAT0-OUT`, `VAT0-IN`, `EXEMPT-OUT`, `EXEMPT-IN`, `TABLE-OUT`, `WHT-1` (1 %), `WHT-3` (3 %).
* **Table tax is seeded at 0 %** (or `tableTaxRate`): Egyptian table-tax rates depend on the product schedule, so no single rate is correct for a company; the company sets the rate (or creates one code per schedule).
* Groups: `VAT14-SALES`, `VAT14-PURCH`, `ZERO-SALES`, `ZERO-PURCH`, `EXEMPT-SALES`, `EXEMPT-PURCH`, and the compound example `TABLE-VAT-SALES` (table tax, then VAT charged on price + table tax).
* Mappings: categories `STANDARD`, `ZERO`, `EXEMPT` × `Sales`/`Purchase`.

⚖️ Rates and reporting boxes are a starting point and must be confirmed by a tax adviser. Electronic invoicing submission (ETA) is not part of this module.
