# Development Status

_Last updated: 2026-11-27_

## Summary

**The backend for every module in the specification is implemented and tested (Phases 2–11, backend only). The Angular frontend has not been started.**

Open business-policy questions were resolved with the recommended defaults at the project owner's request — see [docs/business/05-decisions-log.md](docs/business/05-decisions-log.md). Items marked ⚖️ there must be confirmed by a qualified accountant / tax adviser before production use.

| Phase | Status |
|---|---|
| P0 Business analysis | ✅ |
| P1 Architecture | ✅ |
| UI/UX prototype | ✅ Clickable prototype (claude.ai artifact, sample data, not connected to the API) |
| P2 Platform foundation | ✅ Backend (identity incl. MFA, step-up MFA, OIDC federation, platform operators, branch/warehouse data scopes, tenancy + row-level security, organization, configuration, audit, numbering, outbox/inbox, idempotency, retry, optional Redis cache) |
| P3 Master data | ✅ Catalog & pricing, customers & suppliers, taxes & currencies |
| P7a Accounting core (moved ahead, ADR-019) | ✅ |
| P4 Inventory | ✅ |
| P5 Procurement | ✅ |
| P6 Sales + POS | ✅ |
| P8 Financial management | ✅ Receivables, payables, cash & bank (GL reconciliation), reversals, cross-currency settlement, FX revaluation, withholding certificates, VAT return, e-invoicing port (mock adapter), statements, year-end close |
| P9 Manufacturing | ✅ Actual and standard costing, variances, absorption clearing, variants, substitution, capacity load, rework |
| P10 Advanced | ✅ Approval engine (OR groups, reassign), fixed assets (multi-book, IAS 16 reserve, impairment, scheduled depreciation), reporting (dashboards, schedules, read models), commissions · ⏳ subscription integration (wave 7) |
| P11 Deployment | ✅ Images, on-prem bundle and scripts, Helm chart, hardening, CI/CD · ⚠️ Windows scripts, Helm deployment and GitHub workflows not executed |
| Frontend (Angular) | ⏳ Not started |

## Modules (backend/src/Modules)

| Module | Schema | Doc | HTTP endpoints |
|---|---|---|---|
| Organization (tenants, companies, legal entities, branches, warehouses) | organization | — (see API_CONTRACTS.md) | 10 |
| Identity (users, roles, MFA, sessions) | identity | [platform](docs/modules/platform.md) | 21 |
| Configuration (modules, policies) | configuration | — | 4 |
| Catalog & pricing | catalog | [catalog](docs/modules/catalog.md) | 24 |
| Parties (customers, suppliers) | crm | [parties](docs/modules/parties.md) | 22 |
| Tax & currency | tax | [tax](docs/modules/tax.md) | 14 |
| Accounting | accounting | [accounting](docs/modules/accounting.md) | 36 |
| Finance | finance | [finance](docs/modules/finance.md) | 26 |
| Inventory | inventory | [inventory](docs/modules/inventory.md) | 40 |
| Procurement | procurement | [procurement](docs/modules/procurement.md) | 40 |
| Sales | sales | [sales](docs/modules/sales.md) | 33 |
| POS | pos | [pos](docs/modules/pos.md) | 20 |
| Manufacturing | manufacturing | [manufacturing](docs/modules/manufacturing.md) | 31 |
| Workflow (approvals) | workflow | [workflow](docs/modules/workflow.md) | 17 |
| Fixed assets | assets | [fixed-assets](docs/modules/fixed-assets.md) | 20 |
| Reporting & exports | reporting | [reporting](docs/modules/reporting.md) | 6 (+ every report key) |
| Documents (attachments, notifications, PDF port) | documents | [documents](docs/modules/documents.md) | 8 |
| Data import | import | [import](docs/modules/import.md) | 7 |
| People (employees, salespersons, commissions) | people | [people](docs/modules/people.md) | 13 |

Platform services (audit, numbering, outbox/inbox, idempotency, retry, rendering): [platform](docs/modules/platform.md). Deployment and operations: [docs/architecture/04-deployment.md](docs/architecture/04-deployment.md), [docs/operations/runbook.md](docs/operations/runbook.md).

## Build and test status (development container, 2026-11-27)

| Check | Result |
|---|---|
| `dotnet build Erp.slnx` | ✅ 0 warnings, 0 errors |
| Unit tests | ✅ 447 passed |
| Architecture tests (layering, module boundaries, tenant filters, rowversion) | ✅ 99 passed |
| Integration tests against SQL Server 2022 | ✅ 212 passed (incl. order-to-cash, procure-to-pay, make-to-stock, POS idempotency, tenant isolation via row-level security, concurrency) |
| Pending EF model changes (20 contexts) | ✅ none |
| Docker images (API, migrator) | ✅ built and run (read-only filesystem, non-root) |
| On-prem install / upgrade / backup / restore scripts (Linux) | ✅ executed end to end against SQL Server |
| Helm chart | ✅ `helm lint --strict`, `helm template` · ⚠️ not deployed to a cluster |
| Windows PowerShell scripts | ⚠️ parse-checked only |
| GitHub Actions workflows | ⚠️ linted (actionlint) · not yet run on GitHub |

## How it was built

Waves of parallel agents, each in its own git worktree, merged and re-verified on this branch after every merge. Merge-time review found and fixed: key-range deadlocks between concurrent postings (account balances, stock balances, numbering, audit chain, inbox, idempotency), unchecked expense accounts, a walk-in customer creation race, an order-dependent FEFO test, and a corrupted solution file during one merge (fixed in the next commit).

## Next steps

1. **Frontend (Angular):** workspace, layouts, sign-in/MFA/refresh, company switcher, navigation from `/api/v1/me/navigation`, data table, Arabic/English RTL, then the module screens following the UI/UX prototype.
2. **Professional review** of the ⚖️ decisions (revenue timing, tax, FX, year-end, costing, revaluation) and of the chart-of-accounts template and posting rules.
3. **Limitation work in waves** — wave 5 (attachments/PDF/notifications, security, data import, finance/tax) is merged; wave 6 (salespersons/commissions/profit centers, standard costing, inventory/procurement/POS gaps, fixed assets/reporting/workflow) is merged; wave 7 (database-per-tenant, webhooks/API clients/subscription port, residual accounting gaps, load/security testing) was stopped before completion and nothing from it is merged — specs in [HANDOFF.md](HANDOFF.md). Real e-invoicing adapters need the target jurisdiction's credentials.
4. **Run the CI workflows on GitHub** and deploy the Helm chart to a staging cluster.
