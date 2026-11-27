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
