# Testing Strategy (Part 23)

## Test pyramid

| Level | Scope | Tools | Runs |
|---|---|---|---|
| Domain unit tests | Aggregates, value objects, state machines, pricing pipeline, tax calculator, UOM conversion, WAC/FIFO engines, journal balancing, BOM explosion, MRP netting | xUnit, Shouldly, property-based tests (FsCheck) for money/UOM/costing invariants | Every build |
| Application tests | Command handlers with real domain + in-memory fakes for contracts; posting coordinators | xUnit | Every build |
| Architecture tests | Layer & module dependency rules; every decimal has precision; every tenant-scoped entity has filter + RLS; no entity type in API contracts; no `DateTime.Now` | NetArchTest / ArchUnitNET, reflection tests | Every build |
| Integration tests | EF mappings & migrations, transactions & rollback, concurrency (parallel posting on same stock), tenant isolation incl. RLS, authN/authZ, outbox/inbox, idempotency, numbering under concurrency | Testcontainers (SQL Server), Respawn, `WebApplicationFactory` | Every PR |
| API end-to-end scenarios | Business workflows through HTTP | xUnit + `WebApplicationFactory`, scenario DSL | Every PR |
| UI E2E | Critical user journeys, RTL rendering, permission-based visibility | Playwright | Main branch / nightly |
| Frontend unit | Stores, guards, interceptors, components | Angular default test runner (Vitest/Jasmine per Angular version) | Every PR |
| Non-functional | Load (posting throughput, POS bursts), security (ZAP baseline), migration upgrade tests from previous release | k6, OWASP ZAP | Release |

## Mandatory scenario suites

**S-1 Order-to-cash:** create product (base pcs, box = 24) → GRN 10 boxes @ 240 → sales invoice 30 pcs → customer receipt partial + full → assert: on-hand 210 pcs; ledger rows; COGS = 30 × 10.00; AR open item closed; journals balanced; trial balance balanced; inventory GL = valuation; income statement gross profit correct.

**S-2 Procure-to-pay:** PR → PO → partial GRN ×2 → invoice with price variance (with part of stock already sold) → payment with withholding → assert GRNI = 0, PPV correct, AP aging, supplier statement.

**S-3 Make-to-stock:** BOM (2X, 3Y, 1Z) → production order 100 → reserve → issue → report operations → partial FG receipt 60 then 40 → close → assert WIP = 0, FG cost, variance, raw material balances.

**S-4 Transfer & count:** transfer with in-transit, partial receive, discrepancy close; stock count snapshot with movements after snapshot → variance adjustment GL.

**S-5 Returns & cancellation:** sales return at original cost; invoice cancellation reversal; posted documents unchanged.

**S-6 Period control:** posting into closed period rejected; reopen with permission audited.

**S-7 Concurrency:** 50 parallel invoices for last 10 units with negative stock off → exactly 10 units sold, no negative balance, gapless numbers.

**S-8 Tenant isolation:** suite in 03-multi-tenancy.md §13.5.

**S-9 Idempotency:** same POS receipt submitted 3× → one receipt, one movement set, one journal.

**S-10 FX:** foreign invoice, receipt at different rate → realized FX; month-end revaluation and auto-reversal.

## Quality gates

* Domain projects ≥ 80 % line coverage; posting coordinators and costing engines ≥ 90 % branch coverage.
* Zero failing architecture tests; zero high/critical vulnerabilities; generated API client up to date.
