# Reporting module

Schema `reporting` (migration order 160), module code `reporting`, core (always on). Owns no business data: every report reads the owning module only through its Contracts, aggregates on the server and renders JSON, CSV, Excel or printable HTML. The only table is `reporting.SavedReportView` (per-user saved filters).

## Endpoints (authenticated, `X-Company-Id`; the company is always the active one)

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/v1/reports` | Catalogue of reports the user may run (permission granted, source module installed and enabled): area, key, titles, source, filters, formats, href |
| GET | `/api/v1/reports/{area}/{report}` | Query: `from`, `to`, `branchId`, `warehouseId`, `customerId`, `supplierId`, `productId`, `categoryId` (incl. sub-categories), `groupBy` (day/month/year), `days`, `batchNumber`, `page`, `pageSize` (≤ 1000), `format=json\|csv\|xlsx\|html\|pdf` (pdf: 501 `reporting.pdf-not-configured` unless `Pdf:GotenbergUrl` is set), `lang=en\|ar` (default from Accept-Language) |
| GET | `/api/v1/reports/financial/{key}` | 302 redirect to the Accounting statement with the same query string |
| GET | `/api/v1/reports/documents/{type}/{id}` | Printable HTML of posted documents: `sales-invoice`, `credit-note`, `purchase-order`, `goods-receipt`, `voucher`, `withholding-certificate`; `lang=ar` renders right-to-left; `format=html` (default) or `pdf` (needs a PDF engine, else 501) |
| GET / POST | `/api/v1/reports/views` | List (`reportKey=…`) / create `{ name, reportKey, filters: {…} }` |
| DELETE | `/api/v1/reports/views/{id}` | Delete one of your own views |

Defaults: `to` = today, `from` = first day of `to`'s month. `from > to` → `reporting.invalid-range`; a range over 3 years → `reporting.range-too-long`.
JSON shape: `{ columns[], rows[{key: value}], totals, parameters[], page, pageSize, totalCount }`.

## Permissions and gating

| Permission | Requires module |
|---|---|
| `reporting.sales.read` | sales |
| `reporting.purchasing.read` | procurement |
| `reporting.inventory.read` | inventory |
| `reporting.manufacturing.read` | manufacturing |
| `reporting.tax.read` | — (area `tax`: `vat-return` built from the ledger with its difference to the tax GL accounts, `withholding`) |
| `reporting.document.print` | the document's own module |
| `reporting.view.manage` | — |
| `accounting.report.read` | — (financial statement links) |

Checked on every request: 403 `forbidden`, 404 `module-disabled`, 404 `reporting.report-not-found`.

## Reports and their sources (functional currency, base units)

**Sales** — `ISalesReportQueries.GetSalesFactsAsync`: lines of posted invoices (Posted / PartiallyPaid / Paid / Closed) and posted credit notes (negative). Net and tax at the document rate; cost of sales = line cost at issue, rounded as journalised; credit notes reverse cost only when restocked.
`sales/summary` (by day/month/year: invoices, credit notes, sales, returns, net revenue, tax, cost of sales, gross profit, margin) · `sales/by-product` · `sales/by-customer` · `sales/by-branch` · `sales/returns` · `sales/gross-profit` (per document) · `sales/by-salesperson` (also POS receipts and refunds through `IPosSalesQueries`; filter `salespersonId`).
Ties to the ledger (tested): net revenue = −(SalesRevenue + ServiceRevenue) − SalesReturns; cost of sales = CostOfGoodsSold.

**People** (module `commissions`, permission `people.commission.read`) — `ICommissionReportQueries`: `people/commission-statement` (commissioned documents of posted runs per salesperson; reversed runs flagged with zero commission; filter `salespersonId`).

**Purchasing** — `IProcurementReportQueries`: `purchasing/by-supplier`, `purchasing/by-product` (quantity, value, average unit cost), `purchasing/price-history` (posted supplier invoices, dated by posting date, else invoice date), `purchasing/receipt-analysis` (posted goods receipts: received / accepted / rejected / invoiced / returned / not-invoiced quantity and value — the GRNI view).

**Inventory** — `IInventoryReportQueries`: `inventory/stock-on-hand` (balances × valuation-area average cost), `inventory/ledger` (paged in the database, opening and closing balances as parameters), `inventory/valuation-by-category`, `inventory/aging` (0–30 / 31–60 / 61–90 / 91–180 / 181+ days by receipt date, FIFO over inbound movements; stock that cannot be explained goes to the oldest bucket), `inventory/expiry` (batches expiring within `days`, default 30, including already expired), `inventory/batch-trace` (ledger by batch number). Stock value equals the inventory GL up to per-row rounding (tested).

**Manufacturing** — `IManufacturingReportQueries.GetProductionFactsAsync` (orders released or later, not cancelled, with a start date or WIP transaction in range): `manufacturing/production-summary`, `manufacturing/material-consumption` (standard = BOM quantity × (completed + scrapped); cost variance at actual unit cost), `manufacturing/variance` (charged to WIP vs received, scrap, remaining WIP, posted variance), `manufacturing/wip` (open WIP), `manufacturing/scrap`.

**Financial** — catalogue links to `/api/v1/accounting/reports/{trial-balance, income-statement, balance-sheet, cash-flow}`; not duplicated.

## Exports

- **CSV:** RFC 4180, UTF-8 with BOM, invariant numbers, ISO dates, localised headers, totals row, streamed.
- **XLSX:** ClosedXML; title, company and parameters, typed cells, bold totals, frozen header row, right-to-left sheet for Arabic.
- **HTML:** `IDocumentRenderer` (Erp.Application.Reporting) with `HtmlDocumentRenderer` (Erp.Infrastructure.Rendering): `dir`/`lang`, letterhead, repeating table headers, rows that avoid page breaks, A4.
- **Row cap:** `Reporting:MaxExportRows` (default 50,000) → 422 `reporting.export-too-large`. JSON is never capped.

**Why ClosedXML:** version 0.105.0, MIT licence, no Office or COM dependency. Its dependencies are MIT (DocumentFormat.OpenXml, ClosedXML.Parser, ExcelNumberFormat, RBush) and Apache-2.0 (SixLabors.Fonts 1.0.0). Rejected: EPPlus (PolyForm Noncommercial licence), NPOI (heavier), raw OpenXML SDK (much more code).

## Printable documents

Modules publish a `PrintableDocument` (SharedKernel) through their contracts (sales invoice and credit note, purchase order and goods receipt, finance receipt/payment voucher). Reporting adds the party (`IPartyReportQueries`), unit codes (`ICatalogReportQueries`) and letterhead (`IOrganizationReportQueries`), then renders HTML. Only posted documents can be printed (purchase orders: approved or later). PDF generation is not included (see decisions log, OQ-LIC-1).

## Incoming and projected stock

`GET /api/v1/inventory/stock` now returns `incoming` = open purchase-order supply (`IProcurementQueries`) + inbound transfers in transit, `outgoing` = open sales-order demand (`ISalesQueries`), and `projected` = available + incoming − outgoing. Both sources are optional, so the inquiry still works when Procurement or Sales is not installed.

## Limitations

- Aggregation happens in the application over fact rows from the modules; pre-aggregated read models are future work.
- Aging is FIFO regardless of the item's costing method, and transfers in count as receipts.
- Stock value uses the current average cost.
- No PDF output, no scheduled reports, no salesperson dimension.
- Reporting menu items follow permissions only, so a reporting menu entry can appear while its source module is disabled; the API still returns 404 in that case.
