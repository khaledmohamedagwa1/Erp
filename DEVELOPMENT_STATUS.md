# Development Status

_Last updated: 2026-11-27_

## Current phase

**Phase 0 (Business Analysis) and Phase 1 (Architecture): documentation delivered, awaiting stakeholder review.**

| Phase | Status |
|---|---|
| P0 Business analysis | ✅ Delivered (docs/business) |
| P1 Architecture | ✅ Delivered (docs/architecture, docs/domain, ADRs, API conventions) |
| Review gate | ⏳ Waiting — confirm 🔴 blocking open questions and proposed ADR-017 / ADR-019 |
| P2 Platform foundation | Not started |
| P3 – P11 | Not started |

## What exists

* Architecture & business documentation only. **No application code, no database, no build pipeline yet.**
* Repository was verified empty before this work (see docs/00-repository-assessment.md).

## Build / test status

| Item | Status |
|---|---|
| Backend build | N/A — no code yet |
| Frontend build | N/A — no code yet |
| Tests | N/A |
| Migrations | N/A |

## Decisions needed from stakeholders before Phase 2–4

1. Approve the modular-monolith / shared-transaction posting design (ADR-001, ADR-006).
2. Approve moving the accounting core ahead of inventory (ADR-019).
3. Confirm target jurisdictions for tax and e-invoicing (MR-1, OQ-TAX-1).
4. Confirm inventory valuation area and costing method scope (OQ-INV-1, OQ-INV-2).
5. Confirm back-dating and negative-stock costing policy (OQ-INV-3, OQ-INV-4).
6. Confirm COGS timing policy (OQ-SAL-1) and purchase price variance policy (OQ-PUR-1) — needed by P5/P6.

## Next phase plan — Phase 2 (Platform foundation)

* **Dependencies:** review gate items 1–2.
* **Affected modules:** BuildingBlocks, Identity, Organization, Configuration, Audit, Documents.
* **Database changes:** schemas `identity`, `organization`, `core`, `audit`; RLS policy; outbox/idempotency tables.
* **API changes:** `/auth`, `/me`, `/organization`, `/configuration`, `/identity`, `/platform/tenants`.
* **Frontend changes:** Angular workspace, layouts, auth flow, company switcher, dynamic navigation, data table, i18n AR/EN with RTL, theming.
* **Tests:** architecture tests, tenant isolation suite, auth/refresh rotation tests, numbering concurrency tests.
