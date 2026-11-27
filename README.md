# Modular Enterprise ERP Platform

Angular + ASP.NET Core + SQL Server · Modular Monolith · Clean Architecture · DDD · Double-Entry Accounting · Cloud SaaS & On-Premises

> **Status: backend implemented and tested for every module (650 automated tests); Angular frontend not started.**
> See [DEVELOPMENT_STATUS.md](DEVELOPMENT_STATUS.md), [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) and [backend/README.md](backend/README.md).
> Business-policy decisions were taken with the recommended defaults — [docs/business/05-decisions-log.md](docs/business/05-decisions-log.md);
> items marked ⚖️ need review by an accountant / tax adviser before production use.

## Document map

| # | Deliverable (Part 26) | Document |
|---|---|---|
| 1 | Current repository assessment | [docs/00-repository-assessment.md](docs/00-repository-assessment.md) |
| 2 | Proposed solution architecture | [docs/architecture/01-solution-architecture.md](docs/architecture/01-solution-architecture.md) |
| 3 | Business capability map | [docs/business/01-business-capability-map.md](docs/business/01-business-capability-map.md) |
| 4 | Bounded context diagram | [docs/architecture/02-bounded-contexts.md](docs/architecture/02-bounded-contexts.md) |
| 5 | Domain entities & aggregate roots | [docs/domain/01-aggregates-and-entities.md](docs/domain/01-aggregates-and-entities.md) |
| 6 | Database ERD | [docs/domain/02-database-erd.md](docs/domain/02-database-erd.md) |
| 7–10 | Inventory / Sales / Procurement / Manufacturing lifecycles | [docs/domain/03-document-lifecycles.md](docs/domain/03-document-lifecycles.md) |
| 11–12 | Accounting posting workflows & journal examples | [docs/domain/04-accounting-posting.md](docs/domain/04-accounting-posting.md) |
| 13 | Multi-tenancy strategy | [docs/architecture/03-multi-tenancy.md](docs/architecture/03-multi-tenancy.md) |
| 14–15 | Cloud & on-premises deployment | [docs/architecture/04-deployment.md](docs/architecture/04-deployment.md) |
| 16 | Angular feature architecture | [docs/architecture/05-frontend-architecture.md](docs/architecture/05-frontend-architecture.md) |
| 17 | API structure | [API_CONTRACTS.md](API_CONTRACTS.md) |
| 18 | Security model | [docs/architecture/06-security-model.md](docs/architecture/06-security-model.md) |
| 19 | Transaction consistency strategy | [docs/architecture/07-transaction-consistency.md](docs/architecture/07-transaction-consistency.md) |
| 20 | Implementation roadmap | [docs/business/04-implementation-roadmap.md](docs/business/04-implementation-roadmap.md) |
| — | Business requirements, glossary, NFRs | [docs/business/02-requirements-glossary-nfr.md](docs/business/02-requirements-glossary-nfr.md) |
| — | Missing requirements & open questions | [docs/business/03-open-questions-and-missing-requirements.md](docs/business/03-open-questions-and-missing-requirements.md) |
| — | Testing strategy | [docs/architecture/08-testing-strategy.md](docs/architecture/08-testing-strategy.md) |
| — | Decisions log (resolved open questions) | [docs/business/05-decisions-log.md](docs/business/05-decisions-log.md) |
| — | Module implementation conventions | [docs/architecture/09-module-conventions.md](docs/architecture/09-module-conventions.md) |
| — | Module reference (as built) | [docs/modules/](docs/modules/) |
| — | Operations runbook | [docs/operations/runbook.md](docs/operations/runbook.md) |

## Living governance documents (Part 25)

- [DEVELOPMENT_STATUS.md](DEVELOPMENT_STATUS.md)
- [REQUIREMENTS_TRACEABILITY.md](REQUIREMENTS_TRACEABILITY.md)
- [ARCHITECTURE_DECISIONS.md](ARCHITECTURE_DECISIONS.md)
- [API_CONTRACTS.md](API_CONTRACTS.md)
- [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md)

Diagrams are written in Mermaid and render natively on GitHub.
