# Known Limitations

_Last updated: 2026-11-27 (after wave 5 of the limitation work: documents, security, import, finance/tax; waves 6–7 in progress; frontend not started)_

Each module document in [docs/modules](docs/modules) lists its own limitations in detail. This page is the cross-module summary — nothing here should be presented as a production feature.

## Not built

| Area | Limitation |
|---|---|
| Frontend | No Angular application yet. The UI/UX prototype is a separate claude.ai artifact with sample data and does not call the API. |
| Tax compliance | E-invoicing has a provider port, submission tracking with hash chaining and a **mock adapter only** — real Egypt ETA / KSA ZATCA adapters need the tenant's credentials and certificates/HSM and are not included; polling the authority for a later verdict (`Submitted`) is not built. Zero-rated/exempt supplies post no tax line and do not appear in the VAT return; VAT return and withholding report run per company, not per legal entity; procurement invoices do not pass a taxable amount, so their VAT base is derived from tax ÷ rate. |
| Data import | Import creates records only (no update/upsert). Variants, alternative units, BOMs, assets and price lists are not importable. Referenced setup (categories, units, price lists, groups, payment terms, warehouses) must exist first. Uploaded files are kept with the job (no retention job); import jobs are not audited. |
| Documents | Attachment read access is granted by `documents.attachments.read` alone (the owner document is not verified); no malware scanning, previews or versioning; stored files are never garbage-collected. PDF output requires the optional Gotenberg service (otherwise 501). Notifications are polled (no push), have no preferences and are not pruned. |
| Master data | No employee/salesperson master data, therefore no commissions and no by-salesperson reports. No profit centers (cost centers exist). |
| Security | Branch/warehouse data scopes apply to documents only (sales, purchasing, POS, inventory documents, fixed assets) — ledgers, balances, transfers, master data and raw-SQL reports are not scoped. OIDC sign-in links existing users by verified email (no automatic provisioning, no external-subject link). Step-up uses TOTP or recovery codes only (no WebAuthn). |
| Tenancy | Database-per-tenant routing is designed but not implemented (shared database with row-level security only). |
| POS | Offline selling is designed but not built (online submission with idempotent resend only). POS sells in the functional currency only; card/wallet shift variances are reported, not posted. |
| Reporting | No dashboard builder, no scheduled reports, no pre-aggregated read models (aggregation in the application over module facts). |
| Integrations | No subscription/billing integration, no outbound webhooks or public API clients. |

## Accounting and costing simplifications (review with an accountant — see ⚖️ items in the decisions log)

| Area | Limitation |
|---|---|
| Manufacturing | **Actual costing only.** Labour/machine/overhead are absorbed at work-center rates into "applied" accounts; clearing them against actual payroll and overhead, standard costing and split variances are not implemented. No product variants, no automatic substitution of alternative components, infinite-capacity MRP, no conversion of purchase suggestions into purchase orders. |
| Inventory | Back-dated postings are costed in posting sequence (no automatic recost). Reversing a FIFO cost adjustment on a partly consumed layer is approximate. Transfers always pass through the in-transit account and partial receipts can leave sub-cent residues. Serial-tracked items are excluded from stock counts. No bin locations. |
| Sales / POS returns | Returns restock at the first issue movement's cost and batch; a line issued from several batches/FIFO layers returns at the first one. Invoice status `Closed` is never set. |
| Procurement | Receipts are valued at the order price and rate (FX differences become price variance). Returning goods that were invoiced before receipt is not supported. |
| Finance | Reversal is whole-document only. Transfers between two currencies still post in the functional currency. Commissions and employee payments are not Finance payees. |
| Fixed assets | Single book; revaluation is simplified versus IAS 16 (no reserve transfer on disposal); no partial disposals or inter-company transfers; depreciation runs are triggered manually; the purchase-invoice link is not validated. |
| Accounting | Cash-flow categories and income-statement groups are stored per account (template defaults, editable); unclassified accounts are shown in their own cash-flow section and need classification by the accountant. |
| Workflow | Approval conditions are AND-only; escalation adds an approver rather than reassigning. |

## Operational

| Area | Limitation |
|---|---|
| Caching / rate limiting | Memory by default (per instance). Set `ErpCache:Provider=Redis` when running several API instances; the Redis path is unit-tested against an in-memory cache and has not been run against a real Redis server. |
| Reliability | Automatic retry of transient SQL errors applies to commands that own their transaction; handlers must not call external services before commit. Outbox delivery is at-least-once (inbox deduplication is available to handlers). |
| Audit | The hash chain serialises audit writes per tenant (one lock row per tenant) — fine for back-office load, to be revisited for very high POS volumes. |
| Deployment | Local attachment storage on Kubernetes with more than one API replica needs a ReadWriteMany volume (the default without a claim is emptyDir — not persistent). The Gotenberg service and the image's files folder were not exercised against real containers. Windows install scripts are untested; the Helm chart has not been deployed to a cluster; GitHub workflows have not run yet. The bundled-SQL-Server install path and the destructive-migration refusal during upgrade were not exercised. |
| Testing | No browser end-to-end tests (no frontend). Load and penetration tests have not been run. |
