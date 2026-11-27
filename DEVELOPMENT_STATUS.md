# Development Status

_Last updated: 2026-11-27_

## Current phase

**Phase 2 — Platform foundation: backend implemented and tested. Frontend (Angular) not started.**

| Phase | Status |
|---|---|
| P0 Business analysis | ✅ Delivered (docs/business) |
| P1 Architecture | ✅ Delivered (docs/architecture, docs/domain, ADRs) |
| UI/UX prototype | ✅ Clickable prototype published as a claude.ai artifact (not in the repository) |
| P2 Platform foundation — backend | ✅ Implemented, 66 automated tests passing |
| P2 Platform foundation — frontend | ⏳ Not started |
| Review gate for P3/P4 | ⏳ Blocking open questions still unanswered (see below) |
| P3 – P11 | Not started |

## What exists (backend/)

| Area | Delivered |
|---|---|
| Solution | `backend/Erp.slnx`, .NET 10 LTS, central package management, warnings as errors |
| Building blocks | Shared kernel (entities, aggregates, domain events, Result/Error, SQL-Server-sequential GUIDs); in-house dispatcher with logging → validation → transaction pipeline; shared connection/transaction per request across module DbContexts (ADR-006); tenant + company query filters by convention; tenant write guard; rowversion and audit stamps; transactional outbox per module with background dispatcher; tamper-evident, hash-chained audit log; concurrency-safe gapless document numbering; migrator with destructive-change guard |
| Multi-tenancy | Shared database with `TenantId`; tenant only from the signed token; SQL Server row-level security policy on every tenant table (45 predicates over 15 tables), read-only `SESSION_CONTEXT`; company selection via `X-Company-Id` validated against user access |
| Organization module | Tenants, companies, legal entities, branches, warehouses (automatic transit warehouse per legal entity); tenant provisioning (platform key) in one transaction across three modules |
| Identity module | Users, roles with permission codes, per-company access with branch/warehouse scopes, sign-in with lockout, RS256 access tokens, rotating refresh tokens in an HttpOnly cookie with reuse detection, logout/revocation, cached permission and session checks |
| Configuration module | Module catalog with dependency rules, business presets, typed versioned policies (inventory, sales, procurement) with ETag/If-Match concurrency |
| Web platform | Problem-details errors with stable codes and Arabic/English titles, permission/company/module endpoint filters, navigation built from module manifests, health checks, rate limiting on auth, Serilog JSON logs, optional OpenTelemetry, OpenAPI + Swagger UI (Development) |
| Hosts | `Erp.Api` (roles api/worker/all), `Erp.Migrator` (`migrate`, `init-tenant`) |
| Deployment | Dockerfiles for API and migrator, Docker Compose dev stack, GitHub Actions CI with SQL Server service |

## Build / test status (2026-11-27, in the development container)

| Item | Result |
|---|---|
| `dotnet build Erp.slnx` | ✅ 0 warnings, 0 errors |
| Unit tests (`tests/Erp.UnitTests`) | ✅ 25 passed |
| Architecture tests (`tests/Erp.ArchitectureTests`) | ✅ 19 passed |
| Integration tests against SQL Server 2022 (`tests/Erp.IntegrationTests`) | ✅ 22 passed |
| Migrations apply to an empty database | ✅ (all four contexts + RLS policy) |
| Pending model changes | ✅ none |
| Docker images | ⚠️ Dockerfiles written, **not built** in this environment |

## Decisions still needed before Phase 3/4

1. Approve moving the accounting core ahead of inventory (ADR-019).
2. Target jurisdictions for tax and e-invoicing (MR-1, OQ-TAX-1).
3. Inventory valuation area and costing method scope (OQ-INV-1, OQ-INV-2).
4. Back-dating and negative-stock costing (OQ-INV-3, OQ-INV-4).
5. COGS timing (OQ-SAL-1) and purchase price variance (OQ-PUR-1).

## Next steps

* **Phase 2 frontend:** Angular workspace, layouts, sign-in and refresh flow, company switcher, navigation from `/api/v1/me/navigation`, reusable data table, Arabic/English with RTL, light/dark themes.
* **Phase 2 remainder (backend):** MFA (TOTP), password reset, user deactivation endpoint, idempotency keys (needed before the first posting endpoint), OpenAPI bearer security scheme.
* **Phase 3:** master data (catalog, units, customers, suppliers, tax, currencies, price lists).
