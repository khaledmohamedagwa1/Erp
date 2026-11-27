# Parties module (customers & suppliers)

Schema `crm` · migration order 50 · module code `parties` (core, always on) · Phase 3.

## Model

| Aggregate | Notes |
|---|---|
| `PaymentTerms` | Code unique per company; installments (`percent` decimal(9,6), `days`, `basis` = `InvoiceDate` \| `EndOfMonth`) must add up to exactly 100 %. Stored as a JSON column on the row. |
| `CustomerGroup` | Defaults for customers: price list (Catalog id, no FK), payment terms, posting group. |
| `Customer` | Code unique per company, kind `Individual` \| `Business`, names EN/AR, tax registration, currency, group, price list, payment terms, posting group (default `TRADE`), credit profile (`creditLimit` decimal(19,4), `creditAction` `Warn` \| `RequireApproval` \| `Block`, `onHold`), contacts, addresses, active flag. |
| Walk-in customer | System customer (`code = WALK-IN`, `isWalkIn = true`) for cash/POS sales. Created on first request, at most one per company (filtered unique index), cannot be edited, given credit or deactivated. |
| `Supplier` | Same identity fields as customers (no credit profile). |

Contacts and addresses are JSON columns on the party row (they are never queried on their own), so they share the row's tenant security, audit and rowversion. Each has a stable `id` used to remove it. Only one default address per type (Billing/Shipping): a new default replaces the old one; removing the default promotes the oldest remaining address of that type.

**Due schedule:** each installment is rounded half away from zero to the currency's minor units; the last installment takes the remainder so the schedule always sums exactly to the amount; installments due on the same day are merged. `EndOfMonth` counts days from the last day of the invoice month.

## Contract for other modules (`Erp.Modules.Parties.Contracts`)

`IPartyQueries`: `GetCustomerAsync`, `GetSupplierAsync` (active company only), `GetDueScheduleAsync(paymentTermsId?, invoiceDate, amount, decimals)`, `GetWalkInCustomerIdAsync(companyId)` (idempotent create). `Customer.EvaluateExposure(exposure)` in the domain returns the credit action to apply (on hold ⇒ `Block`).

## Endpoints (`/api/v1/parties`, all require `X-Company-Id`)

| Method & path | Permission | Notes |
|---|---|---|
| `GET /payment-terms` · `GET /payment-terms/{id}` | `parties.setup.read` | Single GET returns `ETag`. |
| `POST /payment-terms` | `parties.setup.manage` | `{code, nameEn, nameAr, installments:[{percent, days, basis}]}`; 422 if not 100 %. |
| `PUT /payment-terms/{id}` | `parties.setup.manage` | Requires `If-Match` (428/412). |
| `GET /customer-groups` · `POST /customer-groups` | `parties.setup.read` / `.manage` | |
| `GET /customers?page&pageSize&search&includeInactive` | `parties.customer.read` | Search by code, names, tax number. Inactive hidden by default. |
| `GET /customers/{id}` | `parties.customer.read` | Detail with contacts, addresses, credit; `ETag`. |
| `POST /customers` | `parties.customer.manage` | `parties.customer-code-taken` on duplicates. |
| `POST /customers/walk-in` | `parties.customer.read` | Returns (creating if needed) the walk-in customer. |
| `PUT /customers/{id}` | `parties.customer.manage` | General details; `If-Match`. |
| `PUT /customers/{id}/credit-profile` | `parties.customer.credit` (sensitive) | `{creditLimit, creditAction, onHold}`; `If-Match`. |
| `POST /customers/{id}/deactivate` | `parties.customer.manage` | 204. |
| `POST /customers/{id}/contacts` · `DELETE /customers/{id}/contacts/{contactId}` | `parties.customer.manage` | |
| `POST /customers/{id}/addresses` · `DELETE /customers/{id}/addresses/{addressId}` | `parties.customer.manage` | `{type, line1, line2?, city, region?, postalCode?, country (ISO-2), isDefault}`. |
| `GET /suppliers…`, `POST /suppliers`, `PUT /suppliers/{id}`, `POST /suppliers/{id}/deactivate`, contacts and addresses | `parties.supplier.read` / `.manage` | Same patterns as customers. |

Errors: `parties.payment-terms-not-found`, `parties.customer-group-not-found`, `parties.customer-not-found`, `parties.supplier-not-found`, `parties.*-code-taken`, `parties.installments-not-100`, `parties.credit-limit-negative`, `parties.walk-in-locked`, `parties.contact-not-found`, `parties.address-not-found`, `concurrency-conflict` (412).

## Not yet implemented

Customer/supplier statements, balances and aging (Finance module), supplier price history (Procurement), commission plans, credit-exposure calculation (needs AR open items — Finance/Sales will call `EvaluateExposure`).
