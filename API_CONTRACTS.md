# API Contracts and Conventions (Deliverable 17)

> Status: **all backend modules implemented** (≈ 365 endpoints). §6 details the platform endpoints; §7 indexes every module's endpoints, documented in `docs/modules/`. The live OpenAPI document is served at `/openapi/v1.json` (Development, or `OpenApi:Enabled=true`).

## 1. General conventions

| Topic | Convention |
|---|---|
| Base path | `/api/v{major}/{module}/{resource}` — e.g. `/api/v1/sales/invoices` |
| Versioning | URL major version; additive changes within a version; breaking changes → new version, old version supported for ≥ 1 release (on-prem upgrade window) |
| Format | JSON, camelCase, UTF-8; enums as strings; IDs as GUID strings |
| Dates | Business dates `YYYY-MM-DD`; timestamps ISO-8601 UTC with `Z` |
| Money | JSON **string** decimals (`"1140.0000"`) plus `currency` — avoids JS floating point loss; quantities likewise |
| Localized text | `{ "ar": "…", "en": "…" }` for master data names; UI chooses |
| Tenant | **Never** in URL/body; from token. Company via `X-Company-Id` header (verified) |
| Auth | `Authorization: Bearer <access token>` |
| Concurrency | Responses carry `ETag` (rowversion, base64); updates and actions require `If-Match` → `412` on mismatch |
| Idempotency | `Idempotency-Key: <uuid>` **required** on post/cancel/payment/receipt/POS endpoints; optional on creates |
| Correlation | `X-Correlation-Id` accepted/returned; logged everywhere |
| Language | `Accept-Language: ar|en` for error messages and server-generated documents |
| Entities | Never exposed — request/response DTOs only (Part 25.8) |
| Calculations | Server computes all totals, taxes, costs; client-sent totals are ignored. `POST …/preview` endpoints return computed drafts without saving |

## 2. Resource and action pattern

| Operation | Method & path | Notes |
|---|---|---|
| List (paged) | `GET /api/v1/sales/invoices?page=1&pageSize=50&sort=-documentDate,number&filter[status]=Posted&filter[customerId]=…&filter[documentDate][gte]=2026-01-01&search=INV-0012` | max pageSize 200 |
| Get | `GET /api/v1/sales/invoices/{id}` | `ETag` returned |
| Create draft | `POST /api/v1/sales/invoices` | returns `201` + `Location` |
| Update draft | `PUT /api/v1/sales/invoices/{id}` | `If-Match` |
| Delete draft | `DELETE /api/v1/sales/invoices/{id}` | only drafts; posted → `409` |
| Preview totals | `POST /api/v1/sales/invoices/preview` | no persistence |
| Transition | `POST /api/v1/sales/invoices/{id}/submit` · `/approve` · `/reject` · `/post` · `/cancel` | explicit actions; body may carry reason |
| Sub-resources | `GET /api/v1/sales/invoices/{id}/journal`, `/history`, `/attachments`, `/approvals` | |
| Export | `GET /api/v1/sales/invoices/export?format=xlsx|pdf&<same filters>` | async job for large exports → `202` + job URL |
| Print | `GET /api/v1/sales/invoices/{id}/document?template=default&lang=ar` | PDF |
| Lookup | `GET /api/v1/catalog/products/lookup?search=…&warehouseId=…` | lightweight DTO for pickers |

### Paged response

```json
{
  "items": [ { "id": "…", "number": "INV-2026-000123", "status": "Posted", "grandTotal": "1140.0000", "currency": "EGP" } ],
  "page": 1, "pageSize": 50, "totalCount": 1234,
  "aggregates": { "grandTotal": "152340.5000" }
}
```

## 3. Errors — RFC 9457 ProblemDetails

```json
{
  "type": "https://errors.erp.local/inventory/insufficient-stock",
  "title": "Insufficient stock",
  "status": 409,
  "detail": "Product SKU-100 has 4 available in WH-MAIN; 10 requested.",
  "code": "inventory.insufficient-stock",
  "traceId": "00-…",
  "errors": { "lines[2].quantity": ["inventory.insufficient-stock"] },
  "meta": { "productId": "…", "warehouseId": "…", "available": "4.000000" }
}
```

| Status | Use |
|---|---|
| 400 | Malformed request |
| 401 / 403 | Unauthenticated / missing permission |
| 404 | Not found **or belongs to another tenant/out of data scope**; disabled module (`code: module-disabled`) |
| 409 | Business rule / invalid state transition (`invalid-state-transition`, `period-closed`, `posting-rule-missing`, `credit-limit-exceeded`) |
| 412 | ETag mismatch |
| 422 | Validation errors (FluentValidation) / idempotency key reused with different body |
| 429 | Rate limited |

Error `code`s are stable and translated client-side.

## 4. Module route map (planned)

| Module | Base | Main resources |
|---|---|---|
| Auth | `/api/v1/auth` | `login`, `refresh`, `logout`, `mfa/*`, `password/*` |
| Me | `/api/v1/me` | profile, companies, `navigation`, `permissions`, `views/{tableKey}` |
| Platform (operators) | `/api/platform` | `tenants`, `tenants/{id}/entitlements` |
| Organization | `/api/v1/organization` | `companies`, `legal-entities`, `branches`, `departments`, `warehouses`, `employees`, `salespersons` |
| Configuration | `/api/v1/configuration` | `modules`, `policies/{section}`, `number-sequences` |
| Identity | `/api/v1/identity` | `users`, `roles`, `permissions` |
| Catalog | `/api/v1/catalog` | `products`, `products/{id}/variants`, `categories`, `brands`, `uoms`, `barcodes/{code}`, `price-lists`, `discount-rules`, `price-quote` |
| Parties | `/api/v1/parties` | `customers`, `customer-groups`, `suppliers`, `payment-terms` |
| Tax | `/api/v1/tax` | `tax-codes`, `tax-groups`, `currencies`, `exchange-rates` |
| Inventory | `/api/v1/inventory` | `stock` (inquiry), `movements`, `opening-stock`, `transfers`, `adjustments`, `counts`, `reservations`, `batches`, `serials`, `valuation` |
| Procurement | `/api/v1/procurement` | `requests`, `rfqs`, `quotations`, `orders`, `receipts`, `invoices`, `returns`, `landed-costs` |
| Sales | `/api/v1/sales` | `quotations`, `orders`, `deliveries`, `invoices`, `returns`, `commission-plans` |
| POS | `/api/v1/pos` | `terminals`, `shifts`, `shifts/{id}/close`, `receipts`, `receipts/{id}/refund`, `catalog-snapshot` |
| Manufacturing | `/api/v1/manufacturing` | `work-centers`, `routings`, `boms`, `boms/{id}/versions`, `production-orders`, `production-orders/{id}/issues`, `/operations`, `/receipts`, `mrp-runs` |
| Accounting | `/api/v1/accounting` | `accounts`, `fiscal-years`, `periods/{id}/close`, `journals`, `posting-rules`, `cost-centers`, `profit-centers` |
| Finance | `/api/v1/finance` | `open-items`, `receipts`, `payments`, `allocations`, `cash-accounts`, `bank-accounts`, `bank-reconciliations`, `expenses`, `transfers`, `fx-revaluations` |
| Fixed assets | `/api/v1/assets` | `assets`, `categories`, `depreciation-runs`, `disposals` |
| Workflow | `/api/v1/workflow` | `definitions`, `inbox` (my approvals), `instances/{id}/approve|reject|delegate` |
| Reports | `/api/v1/reports` | `financial/trial-balance`, `financial/general-ledger`, `financial/income-statement`, `financial/balance-sheet`, `financial/cash-flow`, `ar/aging`, `ap/aging`, `inventory/stock-on-hand`, `inventory/valuation`, `sales/summary`, … |
| Audit | `/api/v1/audit` | `logs`, `documents/{type}/{id}/history` |
| Files | `/api/v1/files` | upload, download (authorized) |

## 5. Example contract — post sales invoice

```
POST /api/v1/sales/invoices/{id}/post
Authorization: Bearer …
X-Company-Id: 6f1c…
If-Match: "AAAAAAAAB9E="
Idempotency-Key: 0b9b2c1e-…
```

`200 OK`
```json
{
  "id": "…",
  "number": "INV-CAI-2026-000123",
  "status": "Posted",
  "postingDate": "2026-11-27",
  "totals": { "net": "1000.0000", "tax": "140.0000", "grand": "1140.0000", "currency": "EGP" },
  "journalEntry": { "id": "…", "number": "JV-2026-004512" },
  "inventoryMovementCount": 1
}
```

Errors: `409 period-closed`, `409 inventory.insufficient-stock`, `409 posting-rule-missing`, `409 credit-limit-exceeded`, `409 invalid-state-transition`, `412`, `422`.

## 6. Platform endpoints (Phase 2)

All `/api/v1` endpoints except sign-in require `Authorization: Bearer`. "Company" means the `X-Company-Id` header is required and validated.

| Method & path | Permission | Company | Notes |
|---|---|---|---|
| `POST /api/platform/tenants` | `X-Platform-Key` | — | Provisions tenant, first company (legal entity, HQ branch, main + transit warehouse), administrator, default modules and policies in one transaction. Disabled (404) when no key is configured. |
| `POST /api/v1/auth/login` | anonymous, rate-limited | — | Body `{tenantCode?, email, password}`. Returns `{accessToken, accessTokenExpiresAtUtc, userId, tenantId, displayName}`; sets `erp_refresh` HttpOnly cookie (path `/api/v1/auth`). Errors: `auth.invalid-credentials`, `auth.locked-out`. |
| `POST /api/v1/auth/refresh` | cookie + header `X-ERP-Request` | — | Rotates the refresh token. Reusing an old token revokes the session (`auth.refresh-token-reused`). |
| `POST /api/v1/auth/logout` | authenticated | — | Revokes the session; access token stops working immediately. 204. |
| `GET /api/v1/me` | authenticated | optional | User, tenant, accessible companies, effective permissions. |
| `GET /api/v1/me/navigation` | authenticated | optional | Menu groups filtered by enabled modules and permissions. |
| `GET /api/v1/organization/companies` | `organization.company.read` | — | Companies the user can access. |
| `GET /api/v1/organization/companies/{id}` | `organization.company.read` | — | 404 for companies the user cannot access. |
| `POST /api/v1/organization/companies` | `organization.company.manage` | — | Creates company with default structure; grants the creator access. 201. |
| `GET/POST /api/v1/organization/legal-entities` | `organization.structure.read` / `.manage` | ✔ | POST also creates the legal entity's transit warehouse. |
| `GET/POST /api/v1/organization/branches` | `organization.structure.read` / `.manage` | ✔ | Codes unique per company (`organization.branch-code-taken`). |
| `GET /api/v1/organization/warehouses?page&pageSize` | `organization.structure.read` | ✔ | Paged. |
| `POST /api/v1/organization/warehouses` | `organization.structure.manage` | ✔ | Type `Physical`, `Production` or `Virtual`. |
| `GET /api/v1/identity/users?page&pageSize&search` | `identity.user.read` | — | Paged. |
| `POST /api/v1/identity/users` | `identity.user.manage` | — | Can only grant companies the caller can access. |
| `PUT /api/v1/identity/users/{id}/roles` | `identity.user.manage` | — | Replaces roles. |
| `PUT /api/v1/identity/users/{id}/companies/{companyId}` | `identity.user.manage` | — | `{granted, branchIds?, warehouseIds?}`; empty scopes = all. |
| `GET/POST /api/v1/identity/roles` | `identity.role.read` / `.manage` | — | Unknown permission codes rejected. |
| `PUT /api/v1/identity/roles/{id}/permissions` | `identity.role.manage` | — | System role "Administrator" is locked. |
| `GET /api/v1/identity/permissions` | `identity.role.read` | — | Catalog from installed module manifests (EN/AR descriptions, sensitive flag). |
| `GET /api/v1/configuration/modules` | `configuration.read` | ✔ | Catalog with enabled state and dependencies. |
| `PUT /api/v1/configuration/modules/{code}` | `configuration.module.manage` | ✔ | `{enabled}`; dependency errors `configuration.module-dependency-missing`, `configuration.module-in-use`, `configuration.core-module`. |
| `GET /api/v1/configuration/policies/{section}` | `configuration.read` | ✔ | `inventory`, `sales`, `procurement`; returns `ETag`. |
| `PUT /api/v1/configuration/policies/{section}` | `configuration.policy.manage` | ✔ | Requires `If-Match` (428 if missing, 412 if stale); validated (422). |
| `GET /health/live`, `GET /health/ready` | anonymous | — | Readiness checks the database. |
| `GET /openapi/v1.json`, `/swagger` | Development only | — | |


Additional platform endpoints added later: `POST /api/v1/auth/mfa/verify`, MFA enrolment/confirm/disable, password change and admin reset, `POST /api/v1/auth/password/complete-change`, user deactivate/reactivate — see [docs/modules/platform.md](docs/modules/platform.md). State-changing posting endpoints across modules require `Idempotency-Key` (422 `idempotency.key-required` when missing, replay with `Idempotency-Replayed: true`).

## 7. Module endpoint index

| Module | Base path | Module code (switchable?) | Endpoints | Reference |
|---|---|---|---|---|
| Organization | `/api/v1/organization`, `/api/platform/tenants`, `/api/platform/operators`, `/api/platform/auth` | organization (core) | 15 | §6; tenant provisioning needs a platform-operator token (password + TOTP, audience `erp-platform`); the static key only bootstraps the first operator |
| Identity | `/api/v1/auth` (incl. `POST step-up`, `GET oidc/providers`, `GET oidc/{key}/start`, `GET oidc/callback`), `/api/v1/me`, `/api/v1/identity` (incl. `identity-providers`) | identity (core) | 28 | §6, [platform.md](docs/modules/platform.md) |
| Configuration | `/api/v1/configuration` | configuration (core) | 4 | §6 |
| Catalog & pricing | `/api/v1/catalog` | catalog (core) | 24 | [catalog.md](docs/modules/catalog.md) |
| Parties | `/api/v1/parties` | parties (core) | 22 | [parties.md](docs/modules/parties.md) |
| Tax & currency | `/api/v1/tax` | tax (core) | 14 | [tax.md](docs/modules/tax.md) |
| Accounting | `/api/v1/accounting` | accounting (core) | 36 | [accounting.md](docs/modules/accounting.md) |
| Finance | `/api/v1/finance` | finance (core) | 26 | [finance.md](docs/modules/finance.md) |
| Inventory | `/api/v1/inventory` | inventory (optional) | 40 | [inventory.md](docs/modules/inventory.md) |
| Procurement | `/api/v1/procurement` | procurement (optional) | 40 | [procurement.md](docs/modules/procurement.md) |
| Sales | `/api/v1/sales` | sales (optional) | 33 | [sales.md](docs/modules/sales.md) |
| POS | `/api/v1/pos` | pos (optional; needs sales + inventory) | 20 | [pos.md](docs/modules/pos.md) |
| Manufacturing | `/api/v1/manufacturing` | manufacturing (optional; needs inventory + procurement) | 31 | [manufacturing.md](docs/modules/manufacturing.md) |
| Workflow | `/api/v1/workflow` | workflow (core) | 17 | [workflow.md](docs/modules/workflow.md) |
| Fixed assets | `/api/v1/assets` | fixed-assets (optional) | 20 | [fixed-assets.md](docs/modules/fixed-assets.md) |
| People (employees, commissions) | `/api/v1/people` | people (core; commission features need optional `commissions`) | 13 | [people.md](docs/modules/people.md) |
| Reporting | `/api/v1/reports` (incl. `tax/vat-return`, `tax/withholding`; `format=pdf` on reports and printable documents — 501 `reporting.pdf-not-configured` without a PDF engine) | reporting (core) | 6 + report keys | [reporting.md](docs/modules/reporting.md) |
| Documents | `/api/v1/documents/attachments` (POST multipart, GET `?ownerType&ownerId`, GET `/{id}/content`, DELETE `/{id}`), `/api/v1/notifications` (GET, `unread-count`, POST `/{id}/read`, POST `read-all`) | documents (core) | 8 | [documents.md](docs/modules/documents.md) |
| Data import | `/api/v1/import` (`types`, `types/{type}/template`, `jobs`, POST `jobs` multipart, POST `jobs/{id}/validate`, POST `jobs/{id}/commit` with Idempotency-Key) | import (core) | 7 | [import.md](docs/modules/import.md) |

Wave-5 additions inside existing modules: Finance `POST /finance/{receipts|payments}/{id}/reverse`, `POST /finance/bank-reconciliations/{id}/reopen`, `POST /finance/cash-bank-accounts/{id}/sync-gl`, `/finance/withholding-certificates` (list, get, create, cancel); Accounting `PUT /accounting/accounts/{id}/report-group`; Tax `GET|PUT /tax/e-invoicing/settings`, `GET /tax/e-invoicing/submissions[/{id}]`, `POST /tax/e-invoicing/submissions/{id}/retry`. New error codes: `identity.step-up-required`, `data-scope.denied`, `platform.bootstrap-closed`, `platform.invalid-credentials`, `auth.external-*`, `auth.invalid-return-url`, `import.commit-failed`, `import.already-committed`.

Wave-6 additions: **People** `/api/v1/people/employees` (GET, POST, PUT `{id}`), `/commission-plans` (GET, POST, PUT `{id}`), `/commission-runs` (GET, POST, GET `{id}`, POST `{id}/recalculate|approve|cancel|post|reverse`). **Accounting** `/profit-centers` (GET, POST, PUT `{id}`), `/reports/profit-center-pnl`, `income-statement?profitCenterId=`. **Sales** `PUT /{quotations|orders|invoices}/{id}/salesperson`, `POST /invoices/{id}/close`. **Parties** `PUT /customers/{id}/salesperson`. **Catalog** `POST /products/{id}/variants/generate`. **Manufacturing** `GET|PUT /settings`, `/cost-versions` (GET, POST, GET `{id}`, PUT `{id}/items`, POST `{id}/roll-up|approve|reopen|activate`), `/cost-pools` (GET, POST, PUT `{id}`), `/absorption-runs` (GET, GET `preview`, POST), `GET /capacity-load`, `POST /rework-orders`, `POST /production-orders/{id}/substitute`, `POST /mrp-runs/{id}/convert-purchases`. **Inventory** `GET returnable-issues`, `GET|POST warehouses/{id}/bins`, `PUT bins/{id}`, `GET warehouses/{id}/bin-stock`, `POST bin-transfers`, `POST recost`, `GET recost-runs[/{id}]`; transfers accept `direct`, counts accept `serials`. **POS** `POST terminals/{id}/offline-range`, `POST terminals/{id}/offline-sync`; payments accept `currency`. **Fixed assets** `POST /assets/{id}/impair|dispose-partial|transfer-legal-entity`, `GET|POST /books`, `PUT /books/{id}`, `GET /assets/{id}/books`, `PUT|DELETE /assets/{id}/books/{bookId}`, `GET|PUT /settings`, `POST /depreciation-runs/scheduled/run`, `GET /reports/book-register`. **Reporting** `/dashboards` (GET, POST, GET|PUT|DELETE `{id}`, GET `{id}/widgets/{widgetId}/data`), `/schedules` (GET, POST, PUT|DELETE `{id}`, POST `{id}/run`, GET `{id}/runs`, GET `{id}/runs/{runId}/file`), `POST /read-models/rebuild`; report keys `sales/by-salesperson`, `sales/daily-summary`, `inventory/value-history`, `people/commission-statement`. **Workflow** condition `group`, step `escalationMode` (`Add`|`Reassign`).

Optional modules return 404 `module-disabled` when switched off for the active company. Endpoint counts are the number of mapped routes per module at the time of writing.
