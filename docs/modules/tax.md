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
* `ITaxCodeQueries` → tax codes (kind, rate on a date, reporting box) and tax groups, for the tax reports.
* `IEInvoicingProvider` → the e-invoicing port (see below).

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
| `GET /e-invoicing/settings` · `PUT /e-invoicing/settings` | `tax.einvoice.read` / `tax.einvoice.manage` (sensitive) | ✔ | `{enabled, provider (default "mock"), environment (default "sandbox")}`; unknown adapters are refused (`tax.einvoice-unknown-provider`). Response includes the chain head (`lastCounter`, `lastHash`) and the installed adapters. |
| `GET /e-invoicing/submissions?status&sourceDocId&page&pageSize` · `GET /e-invoicing/submissions/{id}` | `tax.einvoice.read` | ✔ | Status, UUID, counter, hash, previous hash, provider reference, QR payload, errors, attempts. |
| `POST /e-invoicing/submissions/{id}/retry` | `tax.einvoice.manage` | ✔ | Pending (outbox gave up) or Rejected only (`tax.einvoice-not-retryable`). |

## Egypt template

Seeds, only where the code does not already exist (running it twice creates nothing):

* Currency **EGP** (2 minor units, cash rounding 0.01 — i.e. no cash rounding until the company decides otherwise).
* Tax codes (jurisdiction `EG`, effective **2017-07-01** unless `effectiveFrom` is given): `VAT14-OUT`, `VAT14-IN` (recoverable), `VAT0-OUT`, `VAT0-IN`, `EXEMPT-OUT`, `EXEMPT-IN`, `TABLE-OUT`, `WHT-1` (1 %), `WHT-3` (3 %).
* **Table tax is seeded at 0 %** (or `tableTaxRate`): Egyptian table-tax rates depend on the product schedule, so no single rate is correct for a company; the company sets the rate (or creates one code per schedule).
* Groups: `VAT14-SALES`, `VAT14-PURCH`, `ZERO-SALES`, `ZERO-PURCH`, `EXEMPT-SALES`, `EXEMPT-PURCH`, and the compound example `TABLE-VAT-SALES` (table tax, then VAT charged on price + table tax).
* Mappings: categories `STANDARD`, `ZERO`, `EXEMPT` × `Sales`/`Purchase`.

⚖️ Rates and reporting boxes are a starting point and must be confirmed by a tax adviser.

## E-invoicing (port + mock adapter)

**Only a mock adapter ships.** Real integrations with Egypt ETA or KSA ZATCA need the company's credentials, signing certificates (or an
HSM), the authority's onboarding (e.g. ZATCA CSID compliance checks) and the authority's document format/signature (UBL 2.1 XML with
XAdES for ZATCA, signed JSON for ETA). They are deliberately **not included**; they plug in as further `IEInvoicingProvider`
implementations registered in `TaxModule`.

Flow:

1. Accounting publishes `JournalPosted` (Accounting.Contracts, outbox) for every posted journal. When a **sales invoice** or **credit
   note** journal is posted (`sales` / `SalesInvoice` "invoice" / `SalesReturn` "return") and the company has e-invoicing **enabled**,
   Tax creates an `EInvoiceSubmission` (Pending) — one per document (unique index), so redelivery is harmless.
2. `EInvoiceSubmissionQueued` (Tax outbox) builds the provider-neutral `EInvoiceDocument` once from Sales.Contracts
   (`ISalesReportQueries` printable document: lines, tax breakdown, totals), the seller letterhead (legal entity name and tax
   registration number) and the buyer (`IPartyQueries`, customer tax registration number). It takes the next **counter** and links the
   **previous hash** of the company's chain (ZATCA ICV/PIH style; the chain head lives on `EInvoicingSettings` and is advanced under
   optimistic concurrency), seals the document (`Hash` = SHA-256/Base64 of the canonical JSON without the hash) and stores the payload.
3. The adapter is called. **Transient failures** (exceptions) are recorded on the submission and rethrown, so the outbox retries with the
   *same* payload (up to `Outbox:MaxAttempts`, then a user can retry). **Rejections** are final until a user corrects the master data and
   retries; a retried rejected submission is rebuilt with a new UUID and the next counter (the rejected document stays in the chain).
   `Submitted` is available for asynchronous authorities, but polling for their later verdict is not implemented.

Mock adapter behaviour: rejects documents without lines or whose totals do not add up; outside the `sandbox` environment, rejects a
missing seller tax registration number; rejects buyers named `MOCK-REJECT…` and fails transiently once for buyers named
`MOCK-TRANSIENT…` (test hooks). Accepted documents get a reference `MOCK-{counter}-…` and a Base64 QR payload (seller, tax number, date,
total, tax, hash).

Not covered: POS receipts (simplified invoices), debit notes, cancellation messages to the authority, and authority-specific code lists
(unit, tax category and exemption codes).

## Tax reports (Reporting module, area `tax`)

* `GET /api/v1/reports/tax/vat-return?from&to` — output and input tax per reporting box and tax code from the **general ledger**: every
  OutputTax/InputTax journal line now stores its posting role, tax code and taxable amount (Sales, POS and Finance expenses pass the taxable
  amount; for other lines — e.g. procurement invoices — it is derived from the tax and the code's rate and marked `Derived`). Parameters:
  output tax, input tax, net payable, the net movement on the accounts mapped to the tax roles and the **unreconciled difference**
  (lines on tax accounts without a tax role, typically manual journals). Zero-rated and exempt supplies post no tax line and therefore
  do not appear in the boxes yet.
* `GET /api/v1/reports/tax/withholding?from&to&customerId&supplierId` — tax withheld on receipts and payments with certificate numbers.
