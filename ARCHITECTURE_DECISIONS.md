# Architecture Decision Records

Status values: **Accepted** (working decision for implementation), **Proposed** (needs stakeholder approval), **Superseded**.
Format per ADR: Decision · Why · Alternatives considered · Trade-offs · Future implications.

---

## ADR-001 Modular monolith as initial architecture — Accepted

* **Decision:** One deployable backend (API + worker roles from the same code), internally split into bounded-context modules with enforced boundaries.
* **Why:** ERP posting needs strong consistency across Sales/Inventory/Accounting; domain still being discovered; on-prem customers need simple operations; spec Part 2.1.
* **Alternatives:** Microservices (rejected: distributed transactions/sagas for every posting, high ops cost on-prem); classic layered monolith (rejected: boundaries erode, no extraction path).
* **Trade-offs:** Single deployment unit and scaling unit; discipline needed to keep boundaries (mitigated by architecture tests).
* **Future:** Modules extractable via Contracts + outbox (ADR-006, ADR-007).

## ADR-002 Clean Architecture inside each module (Domain / Application / Infrastructure / Contracts) — Accepted

* **Decision:** Four projects per module; only `Contracts` is referenceable by other modules; endpoints live in the module's Infrastructure project.
* **Why:** Keeps domain pure and testable; explicit public surface per module.
* **Alternatives:** Single project per module with folders (weaker enforcement); separate Presentation project (more projects, little gain).
* **Trade-offs:** ~70 projects at full scope; build time mitigated by incremental builds.
* **Future:** A module's four projects can move to its own solution/service unchanged.

## ADR-003 Shared-database multi-tenancy first; database-per-tenant designed, implemented later — Accepted

* **Decision:** `TenantId` on every tenant-scoped row; tenant catalog and connection resolver interfaces exist from day one but route all tenants to the shared DB until Phase 11.
* **Why:** Spec Part 4 (do not implement DB-per-tenant before shared is stable); lowest cost per tenant.
* **Alternatives:** DB-per-tenant only (high ops cost, migrations × N); schema-per-tenant (poor SQL Server tooling, migration complexity).
* **Trade-offs:** Noisy-neighbor risk; isolation depends on correct filtering (mitigated by ADR-004).
* **Future:** Large tenants moved to dedicated DBs without code change (TenantId kept).

## ADR-004 SQL Server Row-Level Security as second isolation layer — Accepted

* **Decision:** EF global filters (primary) + RLS filter/block predicates on `SESSION_CONTEXT('TenantId')` set read-only on connection open.
* **Why:** Protects against raw SQL, report queries and accidental `IgnoreQueryFilters`; "never allow cross-tenant data access" (Part 25.14).
* **Alternatives:** EF filters only (single point of failure).
* **Trade-offs:** Small query overhead; connection pooling requires setting context on every open (interceptor); platform jobs need an explicit bypass role.
* **Future:** Same mechanism works in dedicated DBs.

## ADR-005 One schema and one DbContext per module; no cross-module foreign keys (except tenancy tables) — Accepted

* **Decision:** Schemas per Part 21; references across modules by ID only; FKs to `organization` tenant/company/legal-entity tables allowed.
* **Why:** Ownership clarity; extraction possible; migrations per module.
* **Alternatives:** Single DbContext (easy joins, boundary erosion).
* **Trade-offs:** Cross-module referential integrity checked by application + reconciliation jobs; reporting needs published views.
* **Future:** Moving a module to another database is a data migration, not a redesign.

## ADR-006 Atomic posting via a Posting Coordinator using one local transaction across module DbContexts — Accepted

* **Decision:** Source-document posting runs in one SQL transaction (shared connection); other modules are called through idempotent, reversible posting contracts keyed by `SourceDocumentReference`.
* **Why:** Part 8.3/22 atomicity; COGS depends on inventory cost; avoids exposing half-posted documents.
* **Alternatives:** Choreographed events/saga (eventual consistency, complex compensations, temporary ledger mismatch); 2PC/MSDTC (forbidden by spec, not portable).
* **Trade-offs:** Modules coupled at runtime to one database; long transactions must be chunked.
* **Future:** On extraction, coordinator becomes a durable process manager with compensations already defined (Reverse operations).

## ADR-007 Transactional outbox + inbox, no message broker in v1 — Accepted

* **Decision:** Integration events stored in per-schema outbox tables in the business transaction; worker dispatches to in-process handlers; inbox dedupes.
* **Why:** Reliable eventual side effects without extra infrastructure on-prem.
* **Alternatives:** In-memory events after commit (lost on crash); broker from day one (ops cost; MassTransit v9 licensing).
* **Trade-offs:** Polling latency (≈1 s); dispatcher must scale with volume.
* **Future:** Broker adapter (RabbitMQ / Azure Service Bus) plugs into the dispatcher.

## ADR-008 GUID primary keys generated by the application in SQL-Server-sequential order — Accepted

* **Decision:** `uniqueidentifier` PKs from an EF Core sequential GUID generator (SQL Server byte-order aware).
* **Why:** IDs known before insert (aggregates, offline POS, outbox payloads); globally unique for tenant moves and service extraction; avoids page splits.
* **Alternatives:** `bigint IDENTITY` (not globally unique, insert round-trip); UUIDv7 (`Guid.CreateVersion7`) — rejected for clustered keys because SQL Server sorts GUIDs by the last 6 bytes first, so v7 values are not sequential in SQL Server.
* **Trade-offs:** 16-byte keys (bigger indexes).
* **Future:** Same IDs valid in any store.

## ADR-009 Decimal precision and deterministic rounding — Accepted

* **Decision:** money `decimal(19,4)`, quantity `decimal(18,6)`, unit cost `decimal(19,6)`, FX rate `decimal(18,8)`, percentages `decimal(9,6)`; rounding through `IRoundingPolicy` (currency minor units, half-away-from-zero default, configurable per legal entity); tax rounding mode (line vs document) configurable (MR-6).
* **Why:** Part 21 database rules; reproducible totals.
* **Alternatives:** `money` SQL type (imprecise in division), float (forbidden).
* **Trade-offs:** Rounding differences on FX journals require an automatic `FxRoundingDifference` line.
* **Future:** Currencies with 3 minor units (KWD, BHD, OMR) supported by 4-dp storage.

## ADR-010 Immutable ledgers; corrections only via reversal/adjustment — Accepted

* **Decision:** Posted inventory movements, journal entries and audit rows are never updated or deleted (DB grants + triggers + domain).
* **Why:** Part 7.2, 12.4, 25.11–12; auditability.
* **Trade-offs:** More rows; reports must handle reversal pairs.

## ADR-011 Semantic posting roles + configurable account determination — Accepted

* **Decision:** Modules emit journal requests with posting roles and context; Accounting resolves accounts via PostingRuleSet (most-specific rule wins); readiness check before module enablement.
* **Why:** Part 12.8 and rule 15 (no hardcoded account mappings); supports jurisdiction-specific COAs.
* **Alternatives:** Account IDs on product/customer records only (rigid); hardcoded mappings (forbidden).
* **Trade-offs:** Setup complexity — mitigated by COA + rule templates.

## ADR-012 In-house command/query dispatcher instead of MediatR; manual mapping instead of AutoMapper — Accepted

* **Decision:** Small `ICommandHandler`/`IQueryHandler` abstractions with decorators (validation, transaction, audit, logging).
* **Why:** MediatR and AutoMapper moved to commercial licensing (2025); needed functionality is small; explicit mapping is safer for financial DTOs.
* **Alternatives:** MediatR (licensing), source-generated `Mediator` library (acceptable fallback).
* **Trade-offs:** We own ~200 lines of infrastructure code.

## ADR-013 Own JWT issuer on ASP.NET Core Identity with rotating refresh tokens; permissions resolved server-side — Accepted

* **Decision:** See security model. Access token small (sub, tid, sid); permissions cached server-side per (user, company).
* **Why:** Works offline on-prem; immediate permission changes; small tokens.
* **Alternatives:** External IdP mandatory (Keycloak/Entra) — breaks offline on-prem simplicity; permissions in JWT (stale, large).
* **Trade-offs:** We operate token issuance securely (key rotation, reuse detection).
* **Future:** OIDC federation per tenant; could swap issuer for an external IdP.

## ADR-014 Redis optional; HybridCache with in-memory fallback — Accepted

* **Why:** Part 3 "Redis when needed"; on-prem small sites shouldn't need Redis.
* **Trade-offs:** Multi-instance deployments must configure Redis (validated at startup when replicas > 1 flag set).

## ADR-015 Migrations run by a dedicated migrator step, never at production startup — Accepted

* **Why:** Part 20.5 (backup before upgrade, approval for destructive changes, logging).
* **Alternatives:** `Database.Migrate()` on startup (race conditions with replicas, no approval gate).

## ADR-016 CQRS-lite: commands through aggregates, queries as projections; no separate read store initially — Accepted

* **Why:** Value where needed without operational overhead; reporting summary tables added per measured need; accounting reports always from posted journals (Part 18).
* **Future:** Read replicas / reporting DB for heavy SaaS tenants.

## ADR-017 Books per legal entity; master data per company; inventory valuation area default = legal entity — Accepted 2026-11-27 (decisions log)

* **Decision:** Journals, periods, open items and inventory valuation scoped to legal entity; products/customers/suppliers shared at company level.
* **Why:** Matches Part 4 definitions; transfers within a legal entity keep cost without revaluation.
* **Alternatives:** Valuation per warehouse (needed by some businesses with different landed costs per location) — supported as configuration.
* **Trade-offs:** Branch-level P&L uses profit-center/branch dimension, not separate books.

## ADR-018 Angular: standalone + Signals stores, Angular Material/CDK, Transloco runtime i18n — Accepted

* **Why:** Signals per spec; Material gives accessible components with RTL support; Transloco allows runtime language switching in one build (Angular's built-in i18n produces one build per locale and cannot switch at runtime, which conflicts with on-prem single-artifact deployment).
* **Alternatives:** NgRx global store (heavier than needed); PrimeNG (richer grids, but mixed RTL quality and theming consistency); built-in `@angular/localize`.
* **Trade-offs:** A data grid with advanced features must be built on CDK table (effort) — revisit if requirements exceed it.

## ADR-019 Build the accounting core before Inventory/Procurement/Sales — Accepted 2026-11-27 (decisions log)

* **Decision:** Move COA, periods, journals and posting engine from Phase 7 to directly after master data.
* **Why:** Every posting in Phases 4–6 must produce balanced journals (Part 22); avoids retrofitting.
* **Alternatives:** Follow spec order and stub accounting (risk of "posted" documents without journals — violates rule 16).

## ADR-020 Business models are setup presets only; behavior from module activation + typed policies — Accepted

* **Why:** Part 5 (no scattered if/else on business type).
* **Implementation:** `IModuleRegistry.IsEnabled(module)`, `IPolicyProvider<InventoryPolicy>`, endpoint `RequireModule` filters; strategies selected by policy values (e.g. `IInventoryIssueTiming` = InvoiceDriven | DeliveryDriven).

## ADR-021 Legal document numbers allocated at posting, gapless, inside the posting transaction — Accepted (confirm OQ-ACC-2)

* **Why:** Many tax regimes require sequential gapless numbering of issued invoices; allocation in-transaction makes rollback restore the counter.
* **Trade-offs:** Sequence row is a hot spot → per-branch/terminal sequences for high volume.

## ADR-022 Approval workflow is a separate generic engine; approval never posts — Accepted

* **Why:** Part 16. Modules ask `IApprovalService.Evaluate(documentFacts)`; completion only changes status to Approved; posting remains a separate permissioned action (optionally auto-post policy executed as the posting user/system with audit).

## ADR-023 Minimal APIs with route groups per module; REST resource + action endpoints for transitions — Accepted

* **Why:** Low overhead, endpoint filters for module/permission/idempotency, OpenAPI generation. State transitions as explicit action endpoints (`POST /invoices/{id}/post`) rather than PATCHing status.

---

## ADR-024 Module application layers use EF Core through a module DbContext interface — Accepted (Phase 2)

* **Decision:** Each module's Application project depends on `Microsoft.EntityFrameworkCore` (not the SQL Server provider) and talks to persistence through a narrow interface (`IOrganizationDbContext`, `IIdentityDbContext`, `IConfigurationDbContext`) exposing only that module's `DbSet`s. Domain projects stay free of EF (enforced by architecture tests).
* **Why:** Removes a repository-per-aggregate layer that would only forward calls, while keeping LINQ projections for queries (CQRS-lite, ADR-016). The interface still stops one module from reaching another module's tables.
* **Alternatives:** Repository interfaces per aggregate (more code, little extra isolation); handlers in Infrastructure (blurs layers).
* **Trade-offs:** Application code is coupled to EF Core's query API. Acceptable: EF is the chosen ORM, and swapping it would be a rewrite anyway.

## ADR-025 One host with process roles instead of a separate Worker project — Accepted (Phase 2)

* **Decision:** `Erp.Api` runs as `api`, `worker` or `all` (`Erp:Role`). The outbox dispatcher only runs for `worker`/`all`. `Erp.Migrator` stays a separate executable.
* **Why:** Same image and code paths everywhere; small on-prem installs run one process. Replaces the separate `Erp.Worker` project in the solution sketch.
* **Trade-offs:** The worker carries the HTTP stack it does not use (small cost).

## ADR-026 User store: own `User` aggregate with ASP.NET Core Identity's password hasher — Accepted (Phase 2)

* **Decision:** Users, roles, sessions and refresh tokens are domain aggregates in the Identity module. Password hashing uses ASP.NET Core Identity's `PasswordHasher` (PBKDF2, versioned format). The full `UserManager`/`IdentityDbContext` stack is not used.
* **Why:** Identity's stores assume globally unique user names and its own table layout; we need tenant-scoped uniqueness, row-level security, rotating refresh tokens with reuse detection, and the shared transaction model. This refines ADR-013.
* **Trade-offs:** MFA (TOTP), password reset and email confirmation must be built by us (scheduled; see KNOWN_LIMITATIONS.md).

## ADR-027 Configuration module schema is `configuration`; `core` holds platform tables — Accepted (Phase 2)

* **Decision:** Module activation and policies live in schema `configuration`. Schema `core` holds platform tables (number sequences); `audit` holds the audit log.
* **Why:** Each DbContext keeps its own `__EFMigrationsHistory` in its own schema; two contexts sharing `core` would collide.

## ADR-028 Open business-policy questions resolved with the recommended defaults — Accepted 2026-11-27

* **Decision:** Every open question in docs/business/03 is resolved with its recommended default, recorded in [docs/business/05-decisions-log.md](docs/business/05-decisions-log.md).
* **Why:** The project owner instructed the team to decide and proceed.
* **Trade-offs:** Items marked ⚖️ there (revenue timing, tax, FX, year-end, costing) must be confirmed by a qualified accountant/tax adviser before production use; they are configurable so the confirmation does not require rework. E-invoicing submission remains out of scope.

## ADR-029 Row locks and guarded inserts instead of UPDLOCK+HOLDLOCK on rows that may not exist — Accepted 2026-11-27

* **Decision:** Upserts of running totals (account balances, stock balances, item valuation), number sequences, audit chain heads, inbox claims and idempotency keys create a missing row with a guarded INSERT (a duplicate-key error from a concurrent creator is caught and ignored, the insert having waited on the unique index), then serialise with an explicit `UPDLOCK, ROWLOCK` on the existing row. Idempotency claims the key with a placeholder row at the start of the request.
* **Why:** `HOLDLOCK` on an absent key takes key-range locks; two transactions inserting *different* new rows into the same gap deadlocked (observed in parallel integration tests across tenants).
* **Trade-offs:** Slightly more SQL per upsert; the duplicate-key path costs one failed statement under contention. Per-row serialisation (needed for exact stock checks) is unchanged.

## ADR-030 Child collections inside an aggregate stored as JSON columns where they are never queried on their own — Accepted 2026-11-27

* **Decision:** Contacts/addresses (Parties), document lines of inventory documents, production-order components/operations/transactions, approval conditions/steps/actions, fixed-asset schedules and similar are JSON columns on the aggregate row. Data that other modules or reports query relationally (journal lines, inventory movements, invoice lines, open items) stays in tables.
* **Why:** One row = one tenant/RLS check, one audit entry and one rowversion for the whole aggregate; fewer tables and joins.
* **Trade-offs:** JSON content is not covered by row-level security predicates of its own (it lives inside a protected row) and cannot be indexed relationally; reporting reads it through module contracts.

## ADR-031 Reporting composes module data only through Contracts; exports via ClosedXML and an HTML renderer — Accepted 2026-11-27

* **Decision:** The Reporting module owns no business data. Each module publishes report fact queries in its Contracts; Reporting aggregates, pages and renders JSON, CSV, XLSX (ClosedXML, MIT) or printable HTML (`IDocumentRenderer`). PDF is out of scope until a PDF library is chosen (OQ-LIC-1).
* **Trade-offs:** Aggregation in the application over fact rows; read models can be added per report when volume requires.

## ADR-032 Deployment: one image with process roles, secrets as files, explicit migrations — Accepted 2026-11-27

* **Decision:** The same API image runs as `api`, `worker` or `all`; secrets are read from files (`/run/secrets`, the same mechanism for Docker, Kubernetes and Windows) and generated only at install time; migrations run only as an explicit step (Compose profile, Helm hook disabled by default, `upgrade.sh` with mandatory backup); readiness reports unhealthy while migrations are pending. Production startup refuses weak configuration (missing signing key, insecure cookies, short platform key, implicit CORS).
* **Why:** Implements Part 20 and ADR-015/025 identically for cloud and on-premises.
