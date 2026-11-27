# API Contracts and Conventions (Deliverable 17)

> Status: **Phase 2 endpoints implemented** (see §6). This file is updated in every phase with the endpoints actually implemented.

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

## 6. Implemented endpoints (Phase 2)

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
