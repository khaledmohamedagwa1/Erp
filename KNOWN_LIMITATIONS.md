# Known Limitations

_Last updated: 2026-11-27 (backend complete for all modules; frontend not started)_

Each module document in [docs/modules](docs/modules) lists its own limitations in detail. This page is the cross-module summary — nothing here should be presented as a production feature.

## Not built

| Area | Limitation |
|---|---|
| Frontend | No Angular application yet. The UI/UX prototype is a separate claude.ai artifact with sample data and does not call the API. |
| Tax compliance | **No electronic invoicing submission** (e.g. Egypt ETA, KSA ZATCA) and no withholding certificates/reports. The tax engine is jurisdiction-neutral; Egypt is only the first seeded template. |
| Data import | No import framework for opening data (products, customers, open invoices). Opening balances exist only as GL, inventory and finance documents. |
| Documents | No file attachments and no PDF engine — printable documents are HTML (OQ-LIC-1). |
| Master data | No employee/salesperson master data, therefore no commissions and no by-salesperson reports. No profit centers (cost centers exist). |
| Security | Branch/warehouse data scopes are stored per user but not applied to queries. No step-up MFA re-authentication for sensitive actions. No external OIDC sign-in. Platform-operator endpoints use a static key. |
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
| Finance | Posted receipts/payments cannot be reversed yet; cross-currency allocation is not supported; bank reconciliation only sees Finance cash movements; GL lines of Finance journals carry base amounts only. |
| Fixed assets | Single book; revaluation is simplified versus IAS 16 (no reserve transfer on disposal); no partial disposals or inter-company transfers; depreciation runs are triggered manually; the purchase-invoice link is not validated. |
| Accounting | Cash-flow classification defaults unclassified balance-sheet accounts to operating; income-statement grouping follows the *current* posting rules. |
| Workflow | Approval conditions are AND-only; escalation adds an approver rather than reassigning. |

## Operational

| Area | Limitation |
|---|---|
| Caching / rate limiting | In-memory per instance: with several API instances a revoked session may remain usable on another instance for up to 60 s, and rate limits are per instance (use a distributed cache / the reverse proxy). |
| Reliability | Automatic retry of transient SQL errors applies to commands that own their transaction; handlers must not call external services before commit. Outbox delivery is at-least-once (inbox deduplication is available to handlers). |
| Audit | The hash chain serialises audit writes per tenant (one lock row per tenant) — fine for back-office load, to be revisited for very high POS volumes. |
| Deployment | Windows install scripts are untested; the Helm chart has not been deployed to a cluster; GitHub workflows have not run yet. The bundled-SQL-Server install path and the destructive-migration refusal during upgrade were not exercised. |
| Testing | No browser end-to-end tests (no frontend). Load and penetration tests have not been run. |
