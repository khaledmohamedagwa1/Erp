# Known Limitations

_Last updated: 2026-11-27 (Phase 0/1)_

## Current state

* **Nothing is implemented.** The repository contains architecture and business documentation only. No feature should be considered available.

## Design-level limitations (intentional for v1, documented so they are not mistaken for production features)

| Area | Limitation | Planned resolution |
|---|---|---|
| Tenancy | Database-per-tenant routing designed but not implemented | Phase 11 |
| POS | Offline operation not supported (idempotency and price versioning designed in) | Future phase |
| Inventory | Back-dated postings costed in posting sequence; no automatic recost of later issues (pending OQ-INV-3) | Recost utility later |
| Inventory | No bin/location management inside warehouses | Future (MR-15) |
| Inventory | Cross-legal-entity transfers blocked (no intercompany) | MR-4 |
| Manufacturing | Actual costing only; standard costing & absorption-variance clearing not implemented — **full manufacturing cost accounting is not claimed** | Later than Phase 9 |
| Manufacturing | MRP is infinite-capacity; no finite scheduling; no forecasts; no subcontracting | Future |
| Accounting | No consolidation, intercompany, budgeting | MR-4, MR-5, MR-17 |
| Accounting | Deferred revenue / IFRS 15 multi-element arrangements not supported | Future |
| Tax | No jurisdiction e-invoicing adapter until target jurisdictions are confirmed | MR-1 |
| Reporting | Dashboard builder deferred | Phase 10 |
| Distribution | Routes, van sales, delivery planning not covered | MR-12 |
| HR | Employees are master data only; no payroll | Out of scope |
