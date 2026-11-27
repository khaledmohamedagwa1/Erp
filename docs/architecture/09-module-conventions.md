# Module Implementation Conventions

Every business module follows the Phase 2 pattern (see `backend/src/Modules/Organization` as the reference).

## Projects

`Erp.Modules.<X>.Domain` (aggregates, value objects, domain events — SharedKernel only) ·
`Erp.Modules.<X>.Application` (commands/queries/handlers/validators, `I<X>DbContext`, contract implementations) ·
`Erp.Modules.<X>.Infrastructure` (DbContext, EF mappings, migrations, design-time factory, endpoints, `<X>Module : IErpModule`) ·
`Erp.Modules.<X>.Contracts` (DTOs/interfaces other modules may use — SharedKernel only).

A module references other modules **only** through their `Contracts` project. Cross-module effects inside a posting go through
contract services called in the same transaction (ADR-006); everything else through outbox events.

## Registration checklist

1. Add the four projects to `backend/Erp.slnx`.
2. Reference the Infrastructure project from `Erp.Api`, `Erp.Migrator`, and add the module instance to both `modules` arrays.
3. Add the module name to `Modules` in `tests/Erp.ArchitectureTests/ArchitectureTests.cs` and its design-time factory to `ContextFactories`.
4. Register the DbContext with `AddErpDbContext<T>(schema, order)` using the order below; add an initial migration.
5. Permissions: `<module>.<resource>.<action>`, declared in the manifest with EN/AR descriptions; mark posting/cancel/approve as sensitive.
6. Endpoints under `/api/v1/<module>`; `.RequirePermission(...)`; `.RequireCompany()` for company data; `.RequireModule(code)` for optional modules.
7. Money `decimal(19,4)`, quantity `decimal(18,6)`, unit cost `decimal(19,6)`, rates `decimal(18,8)`, percentages `decimal(9,6)` — explicit precision is mandatory (the base DbContext throws otherwise).
8. Unit tests for domain rules; integration tests for the HTTP flow and posting effects.
9. Document endpoints in `docs/modules/<module>.md` (the API_CONTRACTS.md summary is consolidated from these).

## Schemas and migration order

| Order | Module | Schema | Module code |
|---|---|---|---|
| 0 | Platform | core / audit | — |
| 10 | Organization | organization | organization |
| 20 | Identity | identity | identity |
| 30 | Configuration | configuration | configuration |
| 40 | Catalog | catalog | catalog |
| 50 | Parties | crm | parties |
| 60 | Tax & currency | tax | tax |
| 70 | Accounting | accounting | accounting |
| 80 | Inventory | inventory | inventory |
| 90 | Finance | finance | finance |
| 100 | Procurement | procurement | procurement |
| 110 | Sales | sales | sales |
| 120 | POS | pos | pos |
| 130 | Manufacturing | manufacturing | manufacturing |
| 140 | Workflow | workflow | workflow |
| 150 | Fixed assets | assets | fixed-assets |
| 160 | Reporting | reporting | reporting |
| 170 | Documents (attachments, notifications) | documents | documents |
