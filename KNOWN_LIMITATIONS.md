# Known Limitations

_Last updated: 2026-11-27 (Phase 2 backend)_

## Current state

* Only the **Phase 2 platform foundation backend** exists (organization, identity, configuration, tenancy, audit, numbering, outbox). No business module (catalog, inventory, sales, purchasing, accounting…) is implemented yet.
* There is **no frontend** in the repository. The UI/UX prototype is a separate claude.ai artifact with sample data; it does not call the API.

## Phase 2 gaps (planned, not yet built)

| Area | Limitation |
|---|---|
| Identity | No MFA (TOTP), password reset, email confirmation, user deactivation endpoint or external OIDC sign-in yet. |
| Identity | Branch/warehouse data scopes are stored per user but not yet applied to queries (no branch- or warehouse-scoped business data exists yet). |
| API | Idempotency keys (`Idempotency-Key`) are not implemented yet; they are required before the first posting/POS/payment endpoint (Phase 4+). |
| API | OpenAPI document does not yet declare the bearer security scheme. |
| Reliability | No automatic retry of transient SQL errors (deadlocks) around a command; the client receives a 500 and may retry. |
| Outbox | Delivery is at-least-once to in-process handlers; the consumer inbox (dedupe table) is designed but not yet built. Domain events are not dispatched in-process before commit. |
| Audit | The hash chain serialises audit writes per tenant (one lock row per tenant). Fine for back-office load; to be revisited for high-volume POS. |
| Rate limiting | In-memory, per instance. Multi-instance SaaS needs a distributed limiter or the reverse proxy's. |
| Caching | In-memory caches (60 s) for sessions/permissions; with several API instances a revoked session can stay usable on another instance for up to 60 s until Redis-backed invalidation is added. |
| Deployment | Dockerfiles and the Compose stack were written but not built in the development environment. |
| Tenancy | Platform-operator endpoints use a static API key; a proper operator identity is future work. |

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
