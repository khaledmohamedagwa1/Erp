# Requirements Traceability Matrix

Backend update (2026-11-27, after waves 1–4): rows marked **Tested** are implemented in `backend/` and covered by automated tests (522 passing). **Partial**, **Deferred** and **Not started** are explained in the row and in KNOWN_LIMITATIONS.md. The Angular frontend has not been started.

Status legend: **Designed** = covered by architecture docs, not implemented · **Designed (OQ)** = designed with a recommended default awaiting confirmation · **Deferred** = intentionally later phase/out of v1 · **Implemented** / **Tested** (used from Phase 2 on).

Phase numbers follow [docs/business/04-implementation-roadmap.md](docs/business/04-implementation-roadmap.md) (P7a = accounting core moved earlier, ADR-019 proposed).

| ID | Requirement | Spec | Module | Phase | Design reference | Status |
|---|---|---|---|---|---|---|
| **Architecture** |||||||
| R-ARC-01 | Modular monolith, Clean Architecture, DDD bounded contexts | 2.1 | All | P2 | ADR-001/002, 01-solution-architecture | Tested |
| R-ARC-02 | CQRS where valuable | 2.1 | All | P2 | ADR-016 | Tested |
| R-ARC-03 | Domain events, integration events, transactional outbox | 2.1, 22 | BuildingBlocks | P2 | ADR-007, 07-transaction-consistency §19.7 | Tested |
| R-ARC-04 | Optimistic concurrency, explicit transactions | 2.1, 21 | All | P2 | 07-transaction-consistency §19.3–19.5 | Tested |
| R-ARC-05 | Modules extractable to services later | 2.1 | All | — | ADR-005/006 | Designed (modules communicate only through Contracts; enforced by architecture tests) |
| R-ARC-06 | No dependencies without architectural reason | 3 | All | All | 01-solution-architecture §2.9 | Tested (dependency choices documented in ADRs and module docs) |
| **Tenancy & organization** |||||||
| R-TEN-01 | Distinguish Tenant / Company / Legal Entity / Branch / Warehouse | 4 | Organization | P2 | 03-multi-tenancy §13.1 | Tested |
| R-TEN-02 | Scenarios: 1 tenant–N companies, N branches, N warehouses, N legal entities with separate books | 4 | Organization, Accounting | P2/P7a | 03-multi-tenancy, ADR-017 | Tested |
| R-TEN-03 | Shared DB with TenantId | 4 | BuildingBlocks | P2 | ADR-003/004 | Tested |
| R-TEN-04 | Database per tenant | 4, 20.1 | Platform | P11 | ADR-003 | Deferred (designed, not implemented) |
| R-TEN-05 | Dedicated on-prem DB | 4, 20.2 | Platform | P2 | 03-multi-tenancy §13.2 | Tested |
| R-TEN-06 | Tenant never trusted from frontend | 4 | Identity | P2 | 03-multi-tenancy §13.3 | Tested |
| R-TEN-07 | Cross-tenant isolation tests | 4, 23 | Tests | P2 | 03-multi-tenancy §13.5 | Tested |
| R-TEN-08 | Tenant provisioning | 20.1 | Platform | P2/P11 | 03-multi-tenancy §13.6 | Tested |
| **Configuration** |||||||
| R-CFG-01 | Module activation per company with dependency validation | 1, 5 | Configuration | P2 | 01-business-capability-map §3.3 | Tested |
| R-CFG-02 | Business policies (negative stock, approvals, POS, mfg, batch/serial/expiry, credit limits, reservations, commissions, multi-currency) | 5 | Configuration | P2 | ADR-020 | Tested |
| R-CFG-03 | No scattered business-type if/else | 5 | All | All | ADR-020 | Tested |
| **Identity & security** |||||||
| R-SEC-01 | ASP.NET Identity, JWT, refresh token rotation | 3, 15 | Identity | P2 | 06-security-model §18.1, ADR-013 | Tested |
| R-SEC-02 | RBAC + fine-grained permissions | 15 | Identity | P2 | 06-security-model §18.2 | Tested |
| R-SEC-03 | Branch & warehouse restrictions | 15 | Identity | P2 | 06-security-model §18.2 | Partial (scopes stored per user, not yet applied to data queries) |
| R-SEC-04 | Approval & sensitive operation permissions | 15 | Identity, Workflow | P2/P5 | 06-security-model §18.2 | Tested (approval engine + sensitive permissions; step-up MFA re-authentication not implemented) |
| R-SEC-05 | Audit of financial, inventory, price, accounting-config, permission, posting, cancellation changes | 15 | Audit | P2 | 06-security-model §18.3 | Tested |
| R-SEC-06 | Tamper-protected audit history | 15 | Audit | P2 | 06-security-model §18.3 | Tested |
| R-SEC-07 | Secure secrets management, none in source control | 15, 20.3 | Platform | P2 | 06-security-model §18.4, 04-deployment §14.1 | Tested (secret files / env; generated at install) |
| **Platform services** |||||||
| R-PLT-01 | Localization AR/EN | 2.2, 19 | Core | P2 | 05-frontend-architecture §16.7 | Tested |
| R-PLT-02 | Notifications | 2.2 | Core | P2/P10 | 04-deployment §14.2 | Partial (email notifications via outbox and SMTP/log sender; no in-app/real-time channel) |
| R-PLT-03 | Concurrency-safe document numbering per tenant/company/LE/branch/doc type/fiscal year | 17 | Documents | P2 | 07-transaction-consistency §19.8, ADR-021 | Tested |
| R-PLT-04 | File management / attachments | 2.2, 17 | Documents | P2 | 02-database-erd §6.9 | Partial (attachments storage not implemented) |
| R-PLT-05 | Document templates, PDF, printing | 17 | Documents | P6 | API_CONTRACTS §2 | Partial (printable HTML documents; no PDF engine) |
| R-PLT-06 | Document status history & historical snapshots | 17 | Documents | P2 | 03-document-lifecycles | Tested |
| R-PLT-07 | Logging (Serilog), health checks, OpenTelemetry | 3 | Platform | P2 | 04-deployment | Tested |
| **Master data** |||||||
| R-MD-01 | Product, variant, attribute, category, brand, manufacturer | 6.1 | Catalog | P3 | 01-aggregates §5.3 | Tested |
| R-MD-02 | Product types (stockable, consumable, service, bundle, manufactured, raw, semi-finished, finished) | 6.1 | Catalog | P3 | 01-aggregates §5.3 | Tested |
| R-MD-03 | Multiple units, barcodes, variants, price lists, tax configs per product | 6.1 | Catalog | P3 | 02-database-erd §6.3 | Tested |
| R-MD-04 | UOM domain with deterministic conversions | 6.2 | Catalog | P3 | 01-aggregates §5.3 (conversion rule) | Tested |
| R-MD-05 | Customers: groups, addresses, contacts, credit profile, price list, payment terms, account; individual/business | 6.3 | Parties | P3 | 01-aggregates §5.4 | Tested |
| R-MD-06 | Suppliers: contacts, addresses, terms, account, price history | 6.4 | Parties | P3 | 01-aggregates §5.4 | Tested |
| R-MD-07 | Employees, salespersons, departments | 6.5 | Organization | P3 | 01-aggregates §5.2 | Partial (employees/salespersons master data not implemented) |
| R-MD-08 | Currencies, tax definitions | 2.2, 13 | Tax | P3 | 01-aggregates §5.5 | Tested |
| R-MD-09 | Data import (opening data) | — (MR-3) | Platform | P3 | 03-open-questions MR-3 | Not started (import framework) |
| **Inventory** |||||||
| R-INV-01 | On-hand, reserved, available, incoming, outgoing, in-transit | 7.1 | Inventory | P4 | glossary; OQ-INV-5 | Tested |
| R-INV-02 | Immutable inventory ledger with required fields | 7.2 | Inventory | P4 | 02-database-erd §6.6, ADR-010 | Tested |
| R-INV-03 | WAC and FIFO behind valuation strategy; traceable valuation | 7.3 | Inventory | P4 | 01-aggregates §5.9 | Tested |
| R-INV-04 | Historical cost (never current purchase price) for issues/returns/transfers/mfg | 7.3 | Inventory | P4 | 04-accounting-posting J-1, J-8 | Tested |
| R-INV-05 | Batch, serial, expiry tracking; FEFO | 7.4 | Inventory | P4 | 03-document-lifecycles §7.6 | Tested |
| R-INV-06 | Opening stock workflow | 7.5 | Inventory | P4 | 03-document-lifecycles §7.1 | Tested |
| R-INV-07 | Stock transfer workflow with in-transit | 7.5 | Inventory | P4 | 03-document-lifecycles §7.2 | Tested |
| R-INV-08 | Stock count workflow (freeze/snapshot) | 7.5 | Inventory | P4 | 03-document-lifecycles §7.3 | Tested |
| R-INV-09 | Adjustments & reservations | 7 | Inventory | P4 | 03-document-lifecycles §7.4–7.5 | Tested |
| R-INV-10 | Atomic operations, ledger + balance consistent | 7.5, 22 | Inventory | P4 | 07-transaction-consistency §19.4 | Tested |
| **Sales** |||||||
| R-SAL-01 | Quotation, SO, invoice lifecycles; no arbitrary status changes | 8.1 | Sales | P6 | 03-document-lifecycles §8 | Tested |
| R-SAL-02 | Cancellation through reversal | 8.1 | Sales | P6 | J-13 | Tested |
| R-SAL-03 | Delivery notes, partial/multiple deliveries | 8.2 | Sales | P6 | §8.4, OQ-SAL-1 | Tested |
| R-SAL-04 | Cash & credit sales, customer pricing, price lists, volume & promo discounts | 8.2 | Sales, Catalog | P6 | MR-7 | Partial (price lists with quantity tiers, customer price lists, permissioned line discounts; no promotions engine) |
| R-SAL-05 | Returns, credit notes, refunds | 8.2 | Sales, Finance | P6/P8 | §8.5, J-8 | Tested |
| R-SAL-06 | Salesperson commissions | 8.2 | Sales | P6/P10 | OQ-SAL-5 | Deferred (module switch exists) |
| R-SAL-07 | Atomic invoice posting (inventory, COGS, revenue, tax, AR) | 8.3, 22 | Sales | P6 | 04-accounting-posting §11.2 | Tested |
| R-SAL-08 | Frontend never determines financial values | 8.3, 25.9 | Sales | P6 | API_CONTRACTS §1 | Tested |
| R-SAL-09 | Credit limits | 5, 12.5 | Sales, Finance | P6 | MR-8 | Tested |
| **Procurement** |||||||
| R-PUR-01 | PR, PO, GRN, invoice, return lifecycles | 9.1 | Procurement | P5 | 03-document-lifecycles §9 | Tested |
| R-PUR-02 | Purchase quotations & supplier comparison | 9.2 | Procurement | P5 | 01-aggregates §5.8 | Tested |
| R-PUR-03 | Partial/multiple receipts, quality inspection | 9.2 | Procurement | P5 | §9.3 | Tested |
| R-PUR-04 | Landed costs | 9.2 | Procurement, Inventory | P5 | OQ-PUR-2 | Tested |
| R-PUR-05 | GRNI at receipt; AP + input tax at invoice; no double counting | 9.3, 22 | Procurement | P5 | §11.3, J-3/J-4 | Tested |
| R-PUR-06 | Invoice-before-receipt and receipt-before-invoice policies | 9.3 | Procurement | P5 | §11.3 | Tested |
| R-PUR-07 | Debit notes, supplier payments | 9.2 | Procurement, Finance | P5/P8 | §9.5, J-6 | Tested |
| **POS** |||||||
| R-POS-01 | Barcode, fast lookup, cart, split/multiple payments, discounts, tax, receipts, refunds, exchanges | 10 | POS | P6 | 05-frontend §16.8–16.9 | Tested |
| R-POS-02 | Shift open/close, cash drawer, reconciliation, terminal management | 10 | POS | P6 | POS shift lifecycle, J-11 | Tested |
| R-POS-03 | Offline-ready design (idempotency, sync queue, conflicts, price versioning) | 10 | POS | Future | 07-transaction-consistency §19.9 | Deferred (idempotent resend and price versioning implemented; offline sync designed only) |
| **Manufacturing** |||||||
| R-MFG-01 | BOM with versions, scrap %, yield, alternatives, effective dates, multi-level | 11.1 | Manufacturing | P9 | 01-aggregates §5.10 | Tested (alternative components stored, not auto-substituted) |
| R-MFG-02 | Work centers, routings, operations, costs | 11.2 | Manufacturing | P9 | 02-database-erd §6.7 | Tested |
| R-MFG-03 | Production order lifecycle, issues, returns, FG receipt, scrap, rework, variance | 11.3 | Manufacturing | P9 | §10.1–10.2 | Tested (rework not modelled separately) |
| R-MFG-04 | WIP accounting | 11.4 | Manufacturing, Accounting | P9 | J-9 | Tested (actual costing) |
| R-MFG-05 | Actual & standard costing extensible | 11.4 | Manufacturing | P9 | OQ-MFG-1 | Partial (actual costing only; standard costing and absorption-variance clearing not implemented) |
| R-MFG-06 | Basic MRP | 11.5 | Manufacturing | P9 | §10.4 | Tested |
| R-MFG-07 | Finite capacity scheduling | 11.5 | Manufacturing | Future | — | Deferred |
| **Accounting** |||||||
| R-ACC-01 | COA hierarchy, AR/EN names, control vs posting accounts | 12.2 | Accounting | P7a | 01-aggregates §5.11 | Tested |
| R-ACC-02 | Journal entries with required header/line fields | 12.3 | Accounting | P7a | 02-database-erd §6.8 | Tested |
| R-ACC-03 | Debit = credit, decimal, immutable, reversals, closed periods | 12.4 | Accounting | P7a | ADR-009/010 | Tested |
| R-ACC-04 | Period open/close, period-end adjustments, opening balances | 12.4 | Accounting | P7a/P8 | OQ-ACC-3/4 | Tested |
| R-ACC-05 | Foreign currency transactions | 12.4, 13 | Accounting, Finance | P8 | J-10, OQ-ACC-5 | Tested |
| R-ACC-06 | Configurable posting rules; no hardcoded account IDs | 12.8, 25.15 | Accounting | P7a | ADR-011 | Tested |
| R-ACC-07 | Cost centers, profit centers | 12.1 | Accounting | P7a | 01-aggregates §5.11 | Partial (cost centers implemented; profit centers not) |
| R-ACC-08 | Financial statements from posted entries only | 12.9, 18 | Reporting | P7a/P8 | ADR-016 | Tested |
| **Finance** |||||||
| R-FIN-01 | AR: subledger, receipts, credit notes, refunds, advances, allocation (N:M), unallocated, aging, statements | 12.5 | Finance | P8 | 01-aggregates §5.12 | Tested |
| R-FIN-02 | AP: subledger, payments, debit notes, advances, allocation, aging, statements | 12.6 | Finance | P8 | 01-aggregates §5.12 | Tested |
| R-FIN-03 | Cash & bank accounts, deposits, withdrawals, transfers, reconciliation | 12.7 | Finance | P8 | 01-aggregates §5.12 | Tested |
| R-FIN-04 | Expense management | 2.2 | Finance | P8 | 01-aggregates §5.12 | Tested |
| R-FIN-05 | Withholding tax | — (MR-2) | Finance, Tax | P8 | J-6 | Tested (withholding certificates/reports not implemented) |
| **Tax & currency** |||||||
| R-TAX-01 | Tax codes, rates, inclusive/exclusive, components, effective dates, exemptions, registrations, input/output, reporting | 13 | Tax | P3/P8 | 01-aggregates §5.5 | Tested |
| R-TAX-02 | No single-country tax assumptions in core | 13 | Tax | P3 | ADR-011 | Tested |
| R-TAX-03 | Multi-currency: base/transaction currency, effective rates, realized/unrealized FX | 13 | Tax, Finance | P8 | J-10 | Tested |
| R-TAX-04 | E-invoicing compliance | — (MR-1) | Compliance | P6+ | MR-1 | Not started (out of scope by decision: no e-invoicing submission) |
| **Fixed assets** |||||||
| R-FA-01 | Register, categories, acquisition, location, assignment, depreciation methods/schedules, transfer, disposal, revaluation, history; GL entries | 14 | FixedAssets | P10 | 01-aggregates §5.13, J-12 | Tested |
| **Workflow** |||||||
| R-WF-01 | Reusable approval engine: definitions, steps, rules, approvers, delegation, history, rejection reasons, escalation, notifications | 16 | Workflow | P5/P10 | ADR-022 | Tested |
| R-WF-02 | Approval separate from posting | 16 | Workflow | P5 | ADR-022 | Tested |
| **Reporting** |||||||
| R-RPT-01 | Sales, inventory, purchasing, manufacturing, accounting reports | 18 | Reporting | per phase | API_CONTRACTS §4 | Tested |
| R-RPT-02 | Filters, Excel/PDF export, print, server-side paging/aggregation | 18 | Reporting | per phase | 05-frontend §16.5 | Tested (CSV/XLSX/HTML; no PDF) |
| R-RPT-03 | Dashboard builder | 2.2, 18 | Reporting | P10 | — | Deferred |
| **Frontend** |||||||
| R-UI-01 | Angular structure core/shared/layouts/features with pages/components/data-access/models/state/forms | 19 | Frontend | P2 | 05-frontend §16.2 | Not started (frontend) |
| R-UI-02 | RTL/LTR, light/dark, responsive, keyboard, barcode | 19 | Frontend | P2 | 05-frontend §16.7–16.8 | Not started (frontend) |
| R-UI-03 | Sidebar from enabled modules + permissions, not hardcoded | 19 | Frontend | P2 | 05-frontend §16.4 | Partial (server navigation API done; UI not started) |
| R-UI-04 | Reusable data table (paging, filtering, sorting, columns, export, saved views, permission-aware actions) | 19 | Frontend | P2 | 05-frontend §16.5 | Not started (frontend; server paging/filtering/export in place) |
| R-UI-05 | Reusable forms & lookups | 19 | Frontend | P2/P3 | 05-frontend §16.6 | Not started (frontend) |
| **Deployment** |||||||
| R-DEP-01 | Same codebase cloud & on-prem; environment-based config | 20 | Platform | P2/P11 | 04-deployment §14.1 | Tested |
| R-DEP-02 | Abstractions for storage, secrets, cache, jobs, notifications | 20.4 | Platform | P2 | 04-deployment §14.2 | Tested |
| R-DEP-03 | On-prem: offline package, local auth, SMTP, backups, migration tooling, no internet dependency | 20.2 | Platform | P11 | 04-deployment §15 | Partial (Linux offline bundle, install/upgrade/backup/restore verified; Windows scripts untested) |
| R-DEP-04 | Versioned migrations, backup before upgrade, logging, approval for destructive changes | 20.5 | Platform | P2/P11 | 04-deployment §15.1, ADR-015 | Tested |
| R-DEP-05 | Subscription billing as integration-ready module | 20.1 | Platform | P10 | 04-deployment §14.2 | Deferred |
| R-DEP-06 | Docker, Compose, CI/CD, monitoring | 3, 24 | Platform | P2/P11 | 04-deployment §15.2 | Partial (images build and run; Compose validated; Helm linted; CI workflows not yet run on GitHub) |
| **Testing** |||||||
| R-TST-01 | Domain, application, integration, E2E tests incl. complete workflows | 23 | Tests | all | 08-testing-strategy | Tested (unit, architecture, integration incl. order-to-cash, procure-to-pay, make-to-stock; no UI end-to-end tests) |
