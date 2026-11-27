# People module (employees, salespersons, commissions)

Schema `people`, migration order 190, module code `people` (core, always on). Commission plans, runs and the commission statement are gated by the
optional catalog module `commissions` (depends on `sales`; switched on by the `distribution` preset or `PUT /api/v1/configuration/modules/commissions`).

The module covers:

- **Employees:** code, English/Arabic names, branch, department, job title, an optional linked user (one employee per user), the *salesperson* flag and
  a default commission plan.
- **Salespersons on sales documents:** quotations, orders, invoices, credit notes (Sales) and POS receipts/refunds (POS) carry an optional
  `SalespersonId`. Customers carry a default salesperson (Parties).
- **Commission plans and monthly commission runs** posted through `IJournalPostingService`.

The module never writes another module's tables: sales data is read through `ISalesReportQueries` and `IPosSalesQueries`, categories through
`ICatalogReportQueries`, journals go through the Accounting contract.

## Contracts (`Erp.Modules.People.Contracts`)

```csharp
PeoplePermissions: people.employee.read, people.employee.manage, people.commission.read, people.commission.manage,
                   people.commission.approve (sensitive), people.commission.post (sensitive)
PeopleDocumentTypes.CommissionRun = "CommissionRun"     // journal source document type (source module "people")
PeoplePartyTypes.Employee = "Employee"                  // journal-line party type of commission lines
EmployeeInfo(Id, Code, NameEn, NameAr, BranchId, IsActive, IsSalesperson, UserId, CommissionPlanId) { CanSell => IsActive && IsSalesperson }
IEmployeeQueries { GetEmployeeAsync, GetEmployeesAsync, FindByUserAsync }
ICommissionReportQueries { GetStatementAsync(from, to, salespersonId) }   // lines of posted (and reversed, flagged) runs
```

## Salesperson defaulting (Sales, POS)

| Document | Salesperson |
|---|---|
| Quotation, order, free invoice | `salespersonId` in the request (must be an active salesperson, else `sales.salesperson-invalid`), otherwise the customer's default salesperson while that employee can still sell, otherwise none |
| Order from quotation, delivery note | Copied from the source |
| Invoice from order / delivery note | Request value, otherwise the source document's, otherwise the customer default |
| Credit note | Always the invoice's |
| POS sale | Request value (`pos.salesperson-invalid`), otherwise the customer default, otherwise the cashier's own employee record (linked user) when it is a salesperson |
| POS refund | Always the sale's |

Drafts can be reassigned: `PUT /api/v1/sales/{quotations|orders|invoices}/{id}/salesperson` (quotations/orders in Draft, invoices before posting).
Customer default: `PUT /api/v1/parties/customers/{id}/salesperson`.

## Commission plans

- **Basis:** `NetSales` (net amount excluding tax) or `GrossMargin` (net − the cost of goods sold recorded on the document line).
- **Base rate**, plus optional **tiers** `{threshold, ratePercent}`: the highest tier whose threshold is reached by the salesperson's *period basis
  total* applies to the whole period (slab, not marginal).
- **Category overrides** `{categoryId, ratePercent}`: lines of products in the category use that rate instead of the plan/tier rate.
- **Clawback on returns:** when on, credit notes and POS refunds in the period reduce the basis and the commission (negative commission is possible
  and posts the other way round); when off they are ignored.
- Rates 0–100 % (6 decimals); thresholds ≥ 0 and unique; one override per category.

## Commission runs

One active run (Draft, Approved or Posted) per legal entity and calendar month (filtered unique index).

| Step | Endpoint | Effect |
|---|---|---|
| Calculate | `POST /commission-runs {legalEntityId, period}` | Draft run for the month of `period`; lines per salesperson with a plan, details per document and rate; salespersons with sales but no active plan are listed in `skippedSalespersons` |
| Recalculate | `POST /commission-runs/{id}/recalculate` | Draft only |
| Approve | `POST /commission-runs/{id}/approve` | Needs a non-zero commission; records approver |
| Post | `POST /commission-runs/{id}/post {date?}` | Number `COM-…` (gapless, per legal entity and year); journal dated the month end (or `date`): per salesperson Dr `CommissionExpense` / Cr `CommissionPayable`, party = the employee |
| Reverse | `POST /commission-runs/{id}/reverse {date?}` | Reversal journal; the month is free for a new run |
| Cancel | `POST /commission-runs/{id}/cancel` | Draft or Approved |

Commission is rounded half away from zero per document and rate; a run line equals the sum of its details. Sources are posted sales invoices and
credit notes of the legal entity (document date in the month) and POS receipts/refunds (business date in the month) that carry a salesperson.

## Endpoints (`/api/v1/people`, `X-Company-Id` required)

| Method | Path | Permission |
|---|---|---|
| GET | `/employees?salespersons&active&branchId&search`, `/employees/{id}` | `people.employee.read` |
| POST / PUT | `/employees`, `/employees/{id}` (`{details, isActive}`) | `people.employee.manage` |
| GET | `/commission-plans`, `/commission-runs?legalEntityId&status`, `/commission-runs/{id}` | `people.commission.read` |
| POST / PUT | `/commission-plans`, `/commission-plans/{id}`, `/commission-runs`, `/commission-runs/{id}/recalculate`, `/cancel` | `people.commission.manage` |
| POST | `/commission-runs/{id}/approve` | `people.commission.approve` |
| POST | `/commission-runs/{id}/post`, `/reverse` (Idempotency-Key) | `people.commission.post` |

Reports: `GET /api/v1/reports/sales/by-salesperson` (sales area, `reporting.sales.read`; invoices, credit notes and POS) and
`GET /api/v1/reports/people/commission-statement` (`people.commission.read`, module `commissions`); both accept `salespersonId`.

## Known limitations

- **Paying commissions is not built:** the payable sits on `CommissionPayable` with the employee as party; Finance payments are supplier payments
  only, so settle it with a manual journal / payroll outside the system.
- No payroll, attendance, leave or HR records beyond the employee master; no employee import.
- One salesperson per document (no split commissions); one plan per salesperson (no effective dating of plans — recalculating an old draft run uses
  the plan as it is now).
- Tiers are slab rates on the monthly total; quarterly/annual targets and marginal tiers are not supported.
- Commission lines carry no profit center (the posting has no single branch).
- POS facts assume the functional currency (POS sells in the functional currency today).
