# Platform services (Phase 2 completion)

## Idempotency keys

Endpoints marked `.RequireIdempotency()` (OpenAPI shows the header) need `Idempotency-Key: <1–100 chars, e.g. a UUID>`.

| Situation | Response |
|---|---|
| Header missing | 422 `idempotency.key-required` |
| First request | Executed normally; a 2xx response is stored in `core.IdempotencyRecord` **in the same transaction** as the effect |
| Same key + same method/path/query/body | Stored status and body replayed, header `Idempotency-Replayed: true`, no second effect |
| Same key + different body | 422 `idempotency.key-reused` |
| Concurrent duplicate | Waits on the record's lock, then replays the first response (rare race fallback: 409 `idempotency.in-progress`) |
| Non-2xx result | Rolled back and not stored — retry with the same key is allowed |

Records expire after 24 hours (expired records are ignored and replaced). Replays return the stored body and status, not other
headers such as `Location`. Demonstrated on `POST /api/v1/organization/branches`; every posting/payment/POS endpoint must use it.

How it works: the filter opens the request's database transaction before the handler runs (the dispatcher's transaction
behaviour joins it), reads the record with `UPDLOCK, HOLDLOCK`, executes the endpoint result into memory, stores the record and
commits — only then is the response written to the client.

## Consumer inbox

Outbox delivery is at-least-once. Handlers that must not run twice derive from `InboxIntegrationEventHandler<TEvent>` and implement
`HandleOnceAsync`. The base class opens a transaction, claims `(messageId, consumer)` in `core.InboxMessage`, runs the work and
commits; a redelivered message finds the claim and is skipped. The current message id is available from `IOutboxMessageContext`.

## Sign-in with two-step verification (TOTP)

1. `POST /api/v1/auth/mfa/enroll` (signed in) → `{ secret, otpAuthUri }`; show the URI as a QR code.
2. `POST /api/v1/auth/mfa/confirm { code }` → `{ recoveryCodes: [10 codes] }` — shown once, stored only as SHA-256 hashes.
3. From then on `POST /api/v1/auth/login` returns `200 { mfaRequired: true, mfaToken, expiresInSeconds: 300 }` and **no** tokens
   or cookie.
4. `POST /api/v1/auth/mfa/verify { mfaToken, code }` or `{ mfaToken, recoveryCode }` → the normal sign-in response + refresh cookie.
5. `POST /api/v1/auth/mfa/disable { password, code }` (code may be a recovery code).

Rules: RFC 6238 (HMAC-SHA1, 6 digits, 30 s, ±1 step); each time step is accepted once per user (replay protection);
recovery codes are single use; failed second factors count towards the 5-attempt lockout. The TOTP secret is encrypted with
ASP.NET Core Data Protection; the key ring is stored in `core.DataProtectionKey` so all instances share it.
**Production:** protect the key ring at rest (certificate via `ProtectKeysWithCertificate`, or SQL Server TDE).

## Passwords

| Flow | Endpoint | Behaviour |
|---|---|---|
| Change own password | `POST /api/v1/auth/password { currentPassword, newPassword }` | Current password required; every **other** session of the user is revoked. |
| Administrator reset | `POST /api/v1/identity/users/{id}/reset-password { temporaryPassword? }` (`identity.user.manage`) | Sets a temporary password (generated and returned once when omitted), unlocks the account, revokes all sessions, sends a notification. |
| Forced change | `POST /api/v1/auth/login` with the temporary password → `403 { code: "auth.password-change-required", passwordChangeToken }`; then `POST /api/v1/auth/password/complete-change { passwordChangeToken, newPassword }` → 204 | The token is valid 10 minutes and single-purpose; afterwards the user signs in normally. |

Minimum length 10. Password hashes, MFA secrets and recovery codes are marked `[Sensitive]`: the audit log records that they
changed (`"***"`) but never their values.

## Deactivation

`POST /api/v1/identity/users/{id}/deactivate` / `/reactivate` (`identity.user.manage`). Deactivation revokes every session at once
(the user's access token stops working on the next request) and sign-in reports invalid credentials. Users cannot deactivate
themselves.

## Notifications

`INotificationSender.SendEmailAsync`. Default implementation logs recipient and subject only. Enable SMTP with
`Notifications:Smtp:Enabled=true` plus `Host`, `Port`, `EnableSsl`, `UserName`, `Password`, `From` (secrets via environment
variables or a secret store). Sent today (from the outbox after commit, via inbox consumers, so at most once per event):
password reset by an administrator, MFA enabled/disabled.

## Transient database errors

`TransientRetryBehavior` (between validation and the transaction) re-runs a command up to 3 times when SQL Server reports a
transient error (deadlock victim 1205, lock timeout 1222, snapshot conflict, connection/timeouts). Conditions that keep it safe:
the command must own its transaction (not commands with `IManualTransaction`, not inside an idempotency filter's transaction),
the failed transaction is rolled back first, and every tracked entity is discarded so the handler reloads fresh state.
Handlers must therefore not cause non-transactional side effects (emails, external calls) before commit — use the outbox.
Queries are not retried.

## OpenAPI

The document declares the `Bearer` (JWT) security scheme, marks authorized operations, and documents `X-Company-Id` and
`Idempotency-Key` on the endpoints that require them. Served at `/openapi/v1.json` (+ Swagger UI) in Development or when
`OpenApi:Enabled=true`.

## Step-up re-authentication (sensitive actions)

Endpoints marked `.RequireStepUp()` need a second-factor verification from the last `Auth:StepUp:MaxAgeMinutes` (default 10).
The access token carries `mfa_at` (Unix seconds of the last TOTP/recovery-code check in this session), `mfa_enrolled` and
`amr` (`pwd`/`ext` + `otp`). The session stores `LastMfaAtUtc`, so a refreshed token keeps the verification until it ages out.

* `POST /api/v1/auth/step-up { code }` or `{ recoveryCode }` → `{ accessToken, accessTokenExpiresAtUtc, mfaVerifiedAtUtc }`
  (same session, new access token). Wrong codes count towards the lockout; `identity.mfa-not-enabled` if MFA is off.
* Otherwise the endpoint answers `403 identity.step-up-required` with `mfaEnrollmentRequired` (true = the user has no MFA
  and must enroll first) and `maxAgeMinutes`.
* Policy for users **without** MFA: `Auth:StepUp:AllowWithoutMfa` (default **false** = they must enroll before sensitive
  actions; true = password suffices, for evaluation installs). `Auth:StepUp:Enabled=false` switches the check off.

Applied to: creating users and changing their roles/company access/password/activation; creating roles and changing role
permissions; external identity provider configuration; accounting period status changes, fiscal-year close and reopen,
journal and opening-balance reversal; enabling/disabling modules and changing policies.

## Branch / warehouse data scopes

A user's company access (`PUT /api/v1/identity/users/{id}/companies/{companyId} { granted, branchIds, warehouseIds }`) may
list branches and warehouses; empty lists mean all. For each request with `X-Company-Id` the middleware loads that access
into the scoped `IDataScope` unless the user holds `identity.scope.all` (the Administrator role has every permission).

* **Reads:** entities marked `IBranchScoped` (property `BranchId`) or `IWarehouseScoped` (property `WarehouseId`) get an
  extra global query filter — out-of-scope documents are invisible (lists omit them, GET returns 404). A null branch is
  company-wide and stays visible.
* **Writes:** `SaveChanges` refuses to add such a document, or to change its branch/warehouse, outside the scope:
  `403 data-scope.denied`.
* Marked documents: sales documents (branch + warehouse), purchase requests/orders (warehouse; orders also branch), goods
  receipts, purchase returns, POS terminals and receipts (branch + warehouse), opening stock, stock adjustments and stock
  counts (warehouse), fixed assets (branch).
* Not scoped (by design): master data (warehouses, branches, products, parties), ledgers and balances
  (`InventoryMovement`, `InventoryBalance`, reservations, journals), transfers (they span two warehouses) and reports built
  from raw SQL facts. Background jobs and outbox handlers run unrestricted.

## External sign-in (OpenID Connect)

Per-tenant providers (`identity.ExternalIdentityProvider`, permission `identity.idp.manage`, step-up on changes):
`GET|POST /api/v1/identity/identity-providers`, `PUT /api/v1/identity/identity-providers/{id}` with
`{ key, displayName, authority (https), clientId, clientSecretName?, scopes?, emailClaim?, requireVerifiedEmail?, isEnabled? }`.
The secret itself is never stored: `clientSecretName = "entra"` reads `Auth:Oidc:Secrets:entra` (e.g. the file
`/run/secrets/Auth__Oidc__Secrets__entra`). Public clients (no secret) rely on PKCE alone.

Flow (authorization code + PKCE S256, `response_mode=query`):

1. `GET /api/v1/auth/oidc/providers?tenantCode=ACME` → `[{ key, displayName }]` for the sign-in page.
2. Browser navigates to `GET /api/v1/auth/oidc/{key}/start?tenantCode=ACME&returnUrl=/dashboard` → `302` to the provider.
   `returnUrl` must be a local path or an origin listed in `Hosting:CorsAllowedOrigins` (`422 auth.invalid-return-url`).
   The `state` (tenant, provider, PKCE verifier, nonce, return URL) is Data-Protection encrypted for 10 minutes and bound to
   the browser with an `HttpOnly; SameSite=Lax` cookie `erp_oidc` (login-CSRF defence).
3. The provider redirects to `GET /api/v1/auth/oidc/callback?code&state`. The API redeems the code (client_secret_post +
   code_verifier), validates the ID token (signature from JWKS, issuer, audience = client id, lifetime, nonce), requires
   `email_verified=true` unless the provider is configured otherwise, and matches an **existing active** user by email.
   No user is created automatically.
4. Success: refresh cookie set, `302 returnUrl`; the SPA calls `POST /api/v1/auth/refresh` (the access token never travels in a
   URL). Users with ERP MFA enabled get `302 returnUrl?mfaToken=…` and finish with `POST /api/v1/auth/mfa/verify`.
   Failures: `302 returnUrl?error=<code>` (`auth.external-user-not-found`, `auth.external-email-not-verified`,
   `auth.external-sign-in-failed`, `auth.locked-out`); an invalid or foreign state returns `401 auth.external-state-invalid`.

The callback URL is derived from the request (behind a proxy enable `Hosting:TrustForwardedHeaders`) or fixed with
`Auth:Oidc:CallbackUrl`; register it with the provider. Back-channel calls use the named HttpClient `erp-oidc` (15 s timeout);
discovery documents and keys are cached and refreshed by `ConfigurationManager`. Sessions record `AuthenticationMethod`
(`pwd` or `ext:{key}`).

## Platform operators

SaaS operators are separate from tenant users (`identity.PlatformOperator`, no tenant, no row-level security):

* `POST /api/platform/operators/bootstrap` with header `X-Platform-Key` creates the **first** operator only (a unique filtered
  index makes this race-safe); afterwards it answers `409 platform.bootstrap-closed`. The response returns the TOTP secret
  and `otpauth://` URI once.
* `POST /api/platform/auth/login { email, password, code }` — the TOTP code is always required. Returns a token for the
  audience `Auth:PlatformAudience` (`erp-platform`, lifetime `Auth:PlatformTokenMinutes`, default 15, no refresh). Tenant
  APIs reject it and platform endpoints reject tenant tokens (`401`). Failed attempts lock the operator like users.
* With an operator token (`Authorization: Bearer`): `POST /api/platform/tenants` (provision), `GET|POST /api/platform/operators`,
  `POST /api/platform/operators/{id}/deactivate` (takes effect on the next call — the operator's status is checked per request).
* Every operator call, sign-in, failed sign-in and the bootstrap are appended to `identity.PlatformAuditEntry`
  (time, operator, action, status, IP).

## Shared cache and rate limiting

`ErpCache:Provider` = `Memory` (default, one instance) or `Redis` (`ErpCache:RedisConnection`, `ErpCache:InstanceName` key
prefix, default `erp:`). Registered as `IDistributedCache`.

* Session status (60 s) and the per-user / per-tenant permission **versions** live in the distributed cache: logout,
  revocation, deactivation, role or permission changes take effect on every instance immediately. Versions are random
  values (not counters), so concurrent invalidations on two instances cannot lose an update.
* The larger access/permission lists are cached per instance under keys that include those versions.
* The `auth` rate-limit policy (`RateLimiting:AuthPerMinute`, default 20 per client IP) counts in a fixed one-minute window
  through `IFixedWindowCounter` — Redis `INCR` + `PEXPIRE` (Lua, atomic) with Redis, in-process otherwise — so the limit
  holds across instances.
