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
| P2 Platform foundation | ✅ Backend (identity incl. MFA, tenancy + row-level security, organization, configuration, audit, numbering, outbox/inbox, idempotency, retry) |
| P3 Master data | ✅ Catalog & pricing, customers & suppliers, taxes & currencies |
| P7a Accounting core (moved ahead, ADR-019) | ✅ |
| P4 Inventory | ✅ |
| P5 Procurement | ✅ |
| P6 Sales + POS | ✅ |
| P8 Financial management | ✅ Receivables, payables, cash & bank, FX revaluation, statements, year-end close |
| P9 Manufacturing | ✅ Actual costing only (see limitations) |
| P10 Advanced | ✅ Approval engine, fixed assets, reporting & exports · ⏳ dashboard builder, subscription integration, commissions |
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

Platform services (audit, numbering, outbox/inbox, idempotency, retry, rendering): [platform](docs/modules/platform.md). Deployment and operations: [docs/architecture/04-deployment.md](docs/architecture/04-deployment.md), [docs/operations/runbook.md](docs/operations/runbook.md).

## Build and test status (development container, 2026-11-27)

| Check | Result |
|---|---|
| `dotnet build Erp.slnx` | ✅ 0 warnings, 0 errors |
| Unit tests | ✅ 289 passed |
| Architecture tests (layering, module boundaries, tenant filters, rowversion) | ✅ 84 passed |
| Integration tests against SQL Server 2022 | ✅ 149 passed (incl. order-to-cash, procure-to-pay, make-to-stock, POS idempotency, tenant isolation via row-level security, concurrency) |
| Pending EF model changes (17 contexts) | ✅ none |
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
3. **Gaps listed in KNOWN_LIMITATIONS.md**, in particular: data import, attachments, branch/warehouse data scopes, e-invoicing (if required by the target jurisdiction), PDF output, standard costing.
4. **Run the CI workflows on GitHub** and deploy the Helm chart to a staging cluster.
