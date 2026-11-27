# Known Limitations

_Last updated: 2026-11-27 (after waves 5–6 of the limitation work; wave 7 — database-per-tenant, integrations, residual gaps, operational testing — in progress; frontend not started)_

Each module document in [docs/modules](docs/modules) lists its own limitations in detail. This page is the cross-module summary — nothing here should be presented as a production feature.

## Not built

| Area | Limitation |
|---|---|
| Frontend | No Angular application yet. The UI/UX prototype is a separate claude.ai artifact with sample data and does not call the API. |
| Tax compliance | E-invoicing has a provider port, submission tracking with hash chaining and a **mock adapter only** — real Egypt ETA / KSA ZATCA adapters need the tenant's credentials and certificates/HSM and are not included; polling the authority for a later verdict (`Submitted`) is not built. Zero-rated/exempt supplies post no tax line and do not appear in the VAT return; VAT return and withholding report run per company, not per legal entity; procurement invoices do not pass a taxable amount, so their VAT base is derived from tax ÷ rate. |
| Data import | Import creates records only (no update/upsert). Variants, alternative units, BOMs, assets and price lists are not importable. Referenced setup (categories, units, price lists, groups, payment terms, warehouses) must exist first. Uploaded files are kept with the job (no retention job); import jobs are not audited. |
| Documents | Attachment read access is granted by `documents.attachments.read` alone (the owner document is not verified); no malware scanning, previews or versioning; stored files are never garbage-collected. PDF output requires the optional Gotenberg service (otherwise 501). Notifications are polled (no push), have no preferences and are not pruned. |
| Master data | Employees/salespersons, commission plans and monthly runs, sales-by-salesperson and commission statements, and profit centers exist. No payroll/HR; one salesperson per document; commission plans are not effective-dated; split commissions are not supported. |
| Security | Branch/warehouse data scopes apply to documents only (sales, purchasing, POS, inventory documents, fixed assets) — ledgers, balances, transfers, master data and raw-SQL reports are not scoped. OIDC sign-in links existing users by verified email (no automatic provisioning, no external-subject link). Step-up uses TOTP or recovery codes only (no WebAuthn). |
| Tenancy | Database-per-tenant routing is designed but not implemented (shared database with row-level security only). |
| POS | Offline selling is server-side only (reserved number ranges per terminal, batch sync with per-receipt results) — no device application or signed offline queue; offline receipts cannot join a closed shift. Foreign cash is accepted at the day rate but not revalued later; non-cash variances are posted only for counted methods. |
| Reporting | Most reports aggregate on request; read models exist for daily sales and stock value history only (snapshots only for days stock moved; a re-dated document needs a rebuild). Schedules use UTC cron times and deliver a download link only the owner can open; outputs are kept until the schedule is deleted. Dashboards have no drill-down or cross-widget filters. |
| Integrations | No subscription/billing integration, no outbound webhooks or public API clients. |

## Accounting and costing simplifications (review with an accountant — see ⚖️ items in the decisions log)

| Area | Limitation |
|---|---|
| Manufacturing | Actual or standard costing (cost versions, roll-up, activation with stock revaluation, price/usage/conversion variance split), absorption clearing per cost pool, variant BOM lines, alternative substitution, capacity load report, rework orders and suggestion→PO conversion are built. Remaining: standards per product, not per variant; purchased stock stays on average/FIFO (difference shows as material price variance at issue); MRP plans at infinite capacity and does not substitute; the capacity load proposes dates but does not reschedule or sequence operations; substitution is 1:1 in base units; absorption covers operation reports recorded after the change; rework has no quality-inspection link. ⚖️ variance and revaluation conventions. |
| Inventory | Recost corrects weighted-average sales issues and adjustment losses only (no cascade into production, transfers or returns; FIFO items are not recosted); automatic recost is opt-in and asynchronous. Bins are not part of stock counts, reservations or expiry-first picking. |
| Sales / POS returns | Returns are split by the inventory engine across all batches/layers the sale issued. `Closed` means settled and nothing more can happen (fully returned or closed manually after payment) — confirm this reading of the status with the business. |
| Procurement | Receipts are valued at the receipt-date rate (policy) with realised FX at invoice; goods invoiced before receipt are valued at the invoiced value (no FX split). |
| Finance | Reversal is whole-document only. Transfers between two currencies still post in the functional currency. Commissions payable cannot be paid through Finance (employees are not payees); profit centers are defaulted from the branch only on sales, POS and inventory postings. |
| Fixed assets | Additional depreciation books are reporting-only (straight line / declining balance; revaluations and impairments affect the posting book only). IAS 16 revaluation uses the NBV method without restating accumulated depreciation, and the reserve moves to retained earnings only on (partial) disposal ⚖️. Transfers between legal entities need the same functional currency and no revaluation reserve; transfers between companies and asset splits/merges are not supported; functional currency only. |
| Accounting | Cash-flow categories and income-statement groups are stored per account (template defaults, editable); unclassified accounts are shown in their own cash-flow section and need classification by the accountant. |
| Workflow | Conditions support one level of OR-of-AND groups (no nesting/NOT); escalation happens once per step and never auto-approves. |

## Operational

| Area | Limitation |
|---|---|
| Caching / rate limiting | Memory by default (per instance). Set `ErpCache:Provider=Redis` when running several API instances; the Redis path is unit-tested against an in-memory cache and has not been run against a real Redis server. |
| Reliability | Automatic retry of transient SQL errors applies to commands that own their transaction; handlers must not call external services before commit. Outbox delivery is at-least-once (inbox deduplication is available to handlers). |
| Audit | The hash chain serialises audit writes per tenant (one lock row per tenant) — fine for back-office load, to be revisited for very high POS volumes. |
| Deployment | Local attachment storage on Kubernetes with more than one API replica needs a ReadWriteMany volume (the default without a claim is emptyDir — not persistent). The Gotenberg service and the image's files folder were not exercised against real containers. Windows install scripts are untested; the Helm chart has not been deployed to a cluster; GitHub workflows have not run yet. The bundled-SQL-Server install path and the destructive-migration refusal during upgrade were not exercised. |
| Testing | No browser end-to-end tests (no frontend). Load and penetration tests have not been run. |
