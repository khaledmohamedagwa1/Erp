# Reporting module

Schema `reporting` (migration order 160), module code `reporting`, core (always on). Owns no business data: every report reads the owning module only through its Contracts, aggregates on the server and renders JSON, CSV, Excel or printable HTML. Its own tables: `SavedReportView` (per-user saved filters), `Dashboard`, `ReportSchedule` and `ReportScheduleRun` (with the output file), and the read models `DailySalesSummary` and `StockValueSnapshot`.

## Endpoints (authenticated, `X-Company-Id`; the company is always the active one)

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/v1/reports` | Catalogue of reports the user may run (permission granted, source module installed and enabled): area, key, titles, source, filters, formats, href |
| GET | `/api/v1/reports/{area}/{report}` | Query: `from`, `to`, `branchId`, `warehouseId`, `customerId`, `supplierId`, `productId`, `categoryId` (incl. sub-categories), `groupBy` (day/month/year), `days`, `batchNumber`, `page`, `pageSize` (≤ 1000), `format=json\|csv\|xlsx\|html\|pdf` (pdf: 501 `reporting.pdf-not-configured` unless `Pdf:GotenbergUrl` is set), `lang=en\|ar` (default from Accept-Language) |
| GET | `/api/v1/reports/financial/{key}` | 302 redirect to the Accounting statement with the same query string |
| GET | `/api/v1/reports/documents/{type}/{id}` | Printable HTML of posted documents: `sales-invoice`, `credit-note`, `purchase-order`, `goods-receipt`, `voucher`; `lang=ar` renders right-to-left; `format=html` (default) or `pdf` (needs a PDF engine, else 501) |
| GET / POST | `/api/v1/reports/views` | List (`reportKey=…`) / create `{ name, reportKey, filters: {…} }` |
| DELETE | `/api/v1/reports/views/{id}` | Delete one of your own views |
| GET / POST | `/api/v1/reports/dashboards` | Dashboards I own or that are shared with one of my roles / create `{ name, description?, sharedWithRoleIds[], widgets[] }` |
| GET / PUT / DELETE | `/api/v1/reports/dashboards/{id}` | Read / replace / delete (owner only for changes: `reporting.not-owner`) |
| GET | `/api/v1/reports/dashboards/{id}/widgets/{widgetId}/data?lang` | Runs the widget's report **with the viewer's permissions** and returns `{ visualization, labelColumn, valueColumn, value, result }` |
| GET / POST | `/api/v1/reports/schedules` | My report schedules / create `{ name, reportKey, parameters{}, format: csv\|xlsx\|html\|pdf, language, cron, recipients[], isActive }` |
| PUT / DELETE | `/api/v1/reports/schedules/{id}` | Change / delete my schedule (and its runs) |
| POST | `/api/v1/reports/schedules/{id}/run` | Run now (same executor as the worker) |
| GET | `/api/v1/reports/schedules/{id}/runs`, `/runs/{runId}/file` | Run history (last 100) / download a run's output |
| POST | `/api/v1/reports/read-models/rebuild` | `{ from, to }` — recompute the daily sales summary for the range and today's stock snapshot |

Defaults: `to` = today, `from` = first day of `to`'s month. `from > to` → `reporting.invalid-range`; a range over 3 years → `reporting.range-too-long`.
JSON shape: `{ columns[], rows[{key: value}], totals, parameters[], page, pageSize, totalCount }`.

## Permissions and gating

| Permission | Requires module |
|---|---|
| `reporting.sales.read` | sales |
| `reporting.purchasing.read` | procurement |
| `reporting.inventory.read` | inventory |
| `reporting.manufacturing.read` | manufacturing |
| `reporting.document.print` | the document's own module |
| `reporting.view.manage` | — |
| `reporting.dashboard.read` / `reporting.dashboard.manage` | — (widget data still needs the widget report's own permission and module) |
| `reporting.schedule.manage` | — (the report's permission is checked for the owner at save time and at every run) |
| `reporting.read-model.rebuild` (sensitive) | — |
| `accounting.report.read` | — (financial statement links) |

Checked on every request: 403 `forbidden`, 404 `module-disabled`, 404 `reporting.report-not-found`.

**Menu gating:** `MenuItemDefinition` has an optional `RequiredModule`; `/api/v1/me/navigation` hides an entry when that module is disabled for the active company (in addition to its permission and the owning module's activation). The core Reporting module's entries name their source modules (sales, procurement, inventory, manufacturing, accounting), so e.g. "Inventory reports" disappears while inventory is off.

## Dashboards

A dashboard is a named set of up to 30 widgets on a 12-column grid. Each widget names a runnable report (`area/key`; link entries such as the financial statements cannot be used), fixed parameters (the report query-string names; dates may be absolute or relative: `today`, `yesterday`, `month-start`, `year-start`, `-30d`, `-3m`), a visualization (`Table`, `Kpi`, `Bar`, `Line`, `Pie`), optional label/value columns and a row limit (≤ 500). Dashboards are private to their owner unless shared with roles; sharing is read-only and never widens access — widget data runs the report with the viewer's own permissions (403 when the viewer may not run it). A KPI's `value` is taken from the totals row (or the first row). Charts are drawn by the client from `result`.

## Scheduled reports

A schedule runs a report on a five-field cron expression (minute hour day-of-month month day-of-week, **UTC**; `*`, lists, ranges and steps; day-of-month and day-of-week are OR-ed when both are restricted). It runs with the **owner's permissions**: at save time the owner must be able to run the report, and every run checks again — a run after the owner lost access is recorded as `Failed` with the reason. Each run stores its output (≤ 10 MB) in `ReportScheduleRun`; the recipients get an email (through `INotificationSender`, after the run is saved) with the download link, which the owner can open. The worker (`worker`/`all` role) looks for due schedules every `Reporting:ScheduleIntervalMinutes` (default 5) across tenants; it claims an occurrence by moving `nextRunAtUtc` on under the row version, so two workers never run the same occurrence. Missed occurrences while the worker was down run once, not once per missed slot.

## Read models

- `DailySalesSummary` (per company, day and branch): invoices, credit notes, sales, returns, net revenue, tax and cost of sales. Recomputed for a day from `ISalesReportQueries` whenever a sales journal of that day is posted (`JournalPosted` from the Accounting outbox), so duplicate or out-of-order deliveries converge.
- `StockValueSnapshot` (per company, day and warehouse): quantity and value captured from `IInventoryReportQueries` after every stock-moving journal (inventory, procurement, sales, POS, manufacturing). One row per warehouse per day — the last capture of the day wins, so it is a daily history of positions, not a reconstruction of past stock.
- Reports: `sales/daily-summary` and `inventory/value-history` read only these tables. `POST /read-models/rebuild` recomputes sales days in a range (≤ 3 years) and today's snapshot (e.g. after a back-dated change or a missed event).

## Reports and their sources (functional currency, base units)

**Sales** — `ISalesReportQueries.GetSalesFactsAsync`: lines of posted invoices (Posted / PartiallyPaid / Paid / Closed) and posted credit notes (negative). Net and tax at the document rate; cost of sales = line cost at issue, rounded as journalised; credit notes reverse cost only when restocked.
`sales/summary` (by day/month/year: invoices, credit notes, sales, returns, net revenue, tax, cost of sales, gross profit, margin) · `sales/by-product` · `sales/by-customer` · `sales/by-branch` · `sales/returns` · `sales/gross-profit` (per document).
Ties to the ledger (tested): net revenue = −(SalesRevenue + ServiceRevenue) − SalesReturns; cost of sales = CostOfGoodsSold. There is no salesperson on sales documents, so there is no by-salesperson report.

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

- Most reports still aggregate in the application over fact rows from the modules; only `sales/daily-summary` and `inventory/value-history` use read models. The sales summary is refreshed per posting date; documents re-dated without a new journal need a rebuild. Stock snapshots exist only for days on which stock moved (or a rebuild ran) and cannot be produced for past dates.
- Aging is FIFO regardless of the item's costing method, and transfers in count as receipts.
- Stock value uses the current average cost.
- PDF output needs the optional PDF engine (scheduled PDF runs fail without it). No salesperson dimension.
- Schedules use UTC cron times (no time zone per schedule). Recipients receive a link, not an attachment, and only the owner can download the file. Run outputs are kept until the schedule is deleted (no retention job).
- Dashboards have no drill-down, no cross-widget filters and no server-side chart rendering.
