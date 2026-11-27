# 18. Security Model

## 18.1 Authentication

| Aspect | Design |
|---|---|
| User store | ASP.NET Core Identity (PBKDF2/ASP.NET hasher, lockout, email confirmation, TOTP MFA, password policy per tenant) |
| Tokens | Short-lived **access JWT** (10–15 min, asymmetric signing ES256/RS256, `kid` rotation) with claims `sub`, `tid` (tenant), `sid` (session), `amr`; **no permission list in the token** (kept small, permissions resolved server-side and cached) |
| Refresh | Opaque refresh token (256-bit random, stored **hashed**), in an `HttpOnly; Secure; SameSite=Strict` cookie scoped to `/api/v1/auth/refresh`. **Rotation on every use**; reuse of an already-rotated token ⇒ revoke the whole token family and the session (theft detection). Absolute session lifetime + idle timeout per tenant policy |
| Logout / revoke | Session `sid` revoked → access token rejected at next request (session check is cached ≤ 60 s) |
| External IdP | Optional OIDC federation per tenant (auth-code + PKCE); external identities linked to local users; tenant membership always checked locally |
| Service-to-service / integrations | Per-tenant API clients (client credentials) with scoped permissions (Phase 10) |
| POS devices | Terminal registered with a device key; cashier logs in on a registered terminal; shift bound to terminal + user |

## 18.2 Authorization

**RBAC with fine-grained permissions + data scopes.**

* Permission codes: `<module>.<resource>.<action>`, e.g. `sales.invoice.read`, `sales.invoice.create`, `sales.invoice.approve`, `sales.invoice.post`, `sales.invoice.cancel`, `inventory.adjustment.post`, `accounting.period.close`, `accounting.journal.post-manual`, `catalog.product.change-price`, `identity.role.manage`.
* Modules declare permissions in their manifest; roles are tenant-defined collections; built-in role templates (Admin, Accountant, Sales Clerk, Storekeeper, Cashier, Purchasing Officer, Production Planner, Auditor-read-only).
* **Sensitive operations** (posting, cancellation, period reopen, price change, permission change, negative stock override, credit limit override) have separate permissions and may require **step-up re-authentication** (MFA) per tenant policy.
* **Data scopes**: per user per company — allowed branches and warehouses. Applied in queries (`IDataScope`) and in commands (e.g. cannot post a transfer from a warehouse outside scope).
* **Approval permissions** are evaluated by the workflow engine (approver role/user), separate from posting permissions — segregation of duties: policy option to forbid the creator from approving/posting their own document.
* Enforcement: endpoint filter `RequirePermission("sales.invoice.post")` **and** handler-level checks for data scope/ownership. The frontend only hides; the API decides (Part 25 rule 10).
* Disabled module ⇒ all its endpoints return 404 `module-disabled` (filter `RequireModule("manufacturing")`).

## 18.3 Audit

| Event | Captured |
|---|---|
| Entity changes on audited aggregates (financial, inventory, price, accounting config, permissions, policies) | before/after JSON of changed properties, user, tenant, company, IP, user agent, correlation id, UTC time |
| Document lifecycle | status transitions with reason (DocumentStatusHistory) |
| Postings & cancellations | source document, journal id, movement ids |
| Security | logins, failures, lockouts, MFA changes, token reuse detection, permission/role changes, cross-tenant operator actions |

Protection: audit tables are insert-only (no UPDATE/DELETE grants for the app login, triggers block modification), each record stores `PrevHash`/`Hash` (SHA-256 chain per tenant) so tampering is detectable by a verification job; retention configurable; audit viewer requires `audit.log.read`.

## 18.4 Data protection & secrets

* TLS 1.2+ everywhere; HSTS; secure cookies.
* Secrets only via `ISecretProvider` (key vault / K8s secrets / Docker secrets / encrypted on-prem file). Repository contains only `.env.template`. Secret scanning in CI (gitleaks / GitHub secret scanning).
* SQL Server TDE recommended (cloud default); column encryption for highly sensitive fields (bank account numbers, national IDs) via ASP.NET Data Protection or Always Encrypted (decision per field).
* Least-privilege DB logins: `erp_app` (DML, no DDL, no UPDATE/DELETE on ledgers & audit), `erp_migrator` (DDL, used only by migrator), `erp_report` (read-only, RLS-bound).
* PII minimization in logs (Serilog destructuring policies mask emails/phones/tokens).

## 18.5 Application security controls (OWASP ASVS L2 baseline)

| Threat | Control |
|---|---|
| Broken access control / IDOR | Tenant filters + RLS; permission + data-scope checks; 404 for foreign ids |
| Injection | EF Core parameterization; raw SQL only via `FormattableString`/parameters; code review rule |
| Mass assignment | Explicit request DTOs; entities never bound or returned (Part 25 rule 8) |
| CSRF | Access token in `Authorization` header (not cookie); refresh cookie `SameSite=Strict` + path-scoped + anti-forgery header on refresh |
| XSS | Angular sanitization; strict CSP (`script-src 'self'`); no `innerHTML` bypasses without review |
| Brute force / abuse | Rate limiting (ASP.NET Core rate limiter) per IP/user/tenant on auth and expensive endpoints; lockout |
| Replay / double submit | Idempotency keys on posting/POS/payment endpoints |
| Concurrency abuse | rowversion + `If-Match`; posting locks |
| File upload | size/type allow-list, content sniffing, storage outside web root, download via authorized endpoint, optional AV scan hook |
| Dependency risk | Central package management, vulnerability scanning, pinned versions, SBOM |
| Financial fraud | segregation of duties, approval rules, immutable postings, audit hash chain, reconciliation controls |
