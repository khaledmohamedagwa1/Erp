# Handoff — where to start next

_Saved 2026-11-27. Branch `claude/erp-platform-architecture-tiz96e`. Read this first, then DEVELOPMENT_STATUS.md and KNOWN_LIMITATIONS.md._

## State at handoff

- **Backend:** every module is implemented. Waves 1–6 are merged and pushed.
  - Last verified run: 758 tests passing (447 unit, 99 architecture, 212 integration), 0 build warnings, no pending EF model changes in any of the 20 DbContexts.
- **Frontend:** the Angular application has **not been started**. A clickable UI/UX prototype (sample data, not connected to the API) exists as a claude.ai artifact: https://claude.ai/artifact/UvcbgcEiiNebvvCWj4dJTc. It predates waves 5–7, so it has no screens for attachments, import, commissions, standard costing, dashboards and the other features added since.
- **Wave 7** was started and then **stopped on the owner's request before finishing**. None of its work was merged; the partial worktree changes were not reviewed and are discarded. Wave 7 must be redone from scratch — see the specs below.
- **CI:** the GitHub workflows have never run. They trigger only on `main` and on pull requests, and no pull request has been opened (the owner has not asked for one).

## How the work was done (repeat this)

1. **Machine setup.**
   - Use the .NET 10 SDK.
   - Run SQL Server 2022 in docker as a container named `erp-sql` on localhost:1433 (sa / `Dev_Passw0rd!`):
     `docker run -d --name erp-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Dev_Passw0rd!' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`
   - If the docker daemon is down: `nohup dockerd >/tmp/dockerd.log 2>&1 &` then `docker start erp-sql`.
2. **Verify.** Run `tools/verify.sh`. It builds, then runs the test projects one after another, with 2 threads for the integration tests to keep memory usage down. It prints `ALL PASSED: N` and exits non-zero on any failure. Run it after every merge.
3. **Parallel work.**
   - Background agents, each in its own git worktree, at most 4 at a time because of memory.
   - Each agent was told: do not edit the governance docs; finish with a "Governance notes" section; commit in its worktree without pushing.
   - The orchestrator then:
     - merges each finished branch;
     - resolves conflicts in the registration files by keeping both sides (`tools/union_merge.py` helps; check by hand afterwards);
     - runs `tools/verify.sh` and checks `dotnet ef migrations has-pending-model-changes` for every changed context;
     - pushes, then updates DEVELOPMENT_STATUS, KNOWN_LIMITATIONS, REQUIREMENTS_TRACEABILITY, API_CONTRACTS and the ADRs.
4. **Conventions that must hold:**
   - [docs/architecture/09-module-conventions.md](docs/architecture/09-module-conventions.md). Migration order is taken up to 190; the next free value is **200**.
   - ADR-029: guarded INSERT plus `UPDLOCK, ROWLOCK`; never `HOLDLOCK` on rows that may not exist.
   - A new module must be registered in both `Program.cs` files (Api, Migrator), in `Erp.slnx`, and in the architecture tests' `Modules` and `ContextFactories` lists.
   - Sensitive actions get the step-up filter; branch- or warehouse-bound documents get the data-scope marker.
   - Posting roles go in `PostingRoles` with a template account in `ChartTemplate.RoleAccounts`. Adding a role means updating the role count asserted in `AccountingDomainTests`.

## Remaining work, in suggested order

### Wave 7 (redo; specs as briefed)
1. **Database-per-tenant** (Tenancy row):
   - The tenant catalog records an isolation mode (Shared | Dedicated) and a connection-string *secret reference*.
   - The connection is resolved per tenant in ErpDbSession; row-level security stays on in dedicated databases too.
   - Every background worker (outbox, inbox, schedulers, depreciation, report schedules) iterates over every database through one catalog abstraction.
   - An operator endpoint provisions a dedicated tenant; the migrator gets a `--all-tenants` option; write a runbook for moving a tenant from Shared to Dedicated.
   - Integration test against a second database on the same server.
2. **Integrations module** (schema `integrations`, order 200):
   - **Outbound webhooks:** HMAC signature with timestamp, retries with backoff then dead-letter, manual redeliver, SSRF protection (block private, loopback and link-local addresses after DNS resolution; no redirects).
   - **Machine API clients:** OAuth2 client-credentials token endpoint `POST /api/v1/oauth/token`, scopes limited to a subset of permissions, company restriction, hashed secret, rotate and revoke, per-client rate limit, actions audited as the client.
   - **Subscriptions:** plans with included modules and user/company limits; tenant subscription status; enforcement, where write endpoints return 402 `subscription.inactive` when past due beyond the grace period; an `IBillingProvider` port with a mock provider and a signed inbound webhook; an Unlimited plan for on-prem.
3. **Remaining accounting gaps:**
   - Employee payee payments, so commissions payable can be paid.
   - Transfers between two currencies with the realised FX difference.
   - VAT: zero-rated and exempt lines in the return; procurement invoices pass the taxable amount; per-legal-entity filter.
   - Exact reversal of a weighted-average cost adjustment.
   - Profit-center defaulting for Procurement, Finance, Manufacturing, Fixed Assets and commission postings.
   - Optional yearly transfer of realised revaluation surplus to retained earnings (IAS 16.41).
   - E-invoice status polling for `Submitted`.
   - Import upsert mode for products, customers and suppliers, plus price-list import.
4. **Operations and testing:**
   - Opt-in tests against real Redis (`ERP_TEST_REDIS`) and Gotenberg (`ERP_TEST_GOTENBERG`); build the API image and check attachment upload with the volume.
   - Partition the audit hash chain for throughput.
   - Load-test harness in `tests/Erp.LoadTests`, with results recorded in `docs/operations/performance.md`.
   - An endpoint test asserting that every route is either on an anonymous allowlist or requires authentication and a permission.
   - ZAP baseline scan.
   - pwsh smoke tests of the Windows scripts.

### After wave 7
5. **Angular frontend** following [docs/architecture/05-frontend-architecture.md](docs/architecture/05-frontend-architecture.md) and the prototype:
   - workspace and layouts; sign-in with MFA, step-up and refresh; company switcher; navigation from `/api/v1/me/navigation`;
   - a reusable data table and forms; Arabic/English RTL;
   - then the module screens.
   - Optionally update the prototype artifact first with the wave 5–6 features.
6. **Professional review** of the ⚖️ items in [docs/business/05-decisions-log.md](docs/business/05-decisions-log.md), the chart-of-accounts template and the posting rules. Also confirm the meaning of the `Closed` sales-invoice status (implemented as "settled and nothing more can happen").
7. **Real e-invoicing adapters** (ETA/ZATCA) once the target jurisdiction's credentials and certificates are available.
8. **CI and deployment:** run the workflows (needs a pull request or a merge to `main` — ask the owner), deploy the Helm chart to staging, test the Windows install on a real Windows host.

## Process rules from the owner and environment

- Develop and push only on `claude/erp-platform-architecture-tiz96e`. Do not open a pull request unless explicitly asked.
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and the session link. Never put model identifiers in code or docs.
- Never commit secrets. Business-policy questions were delegated ("take a decision"): record each decision in the decisions log, and mark accounting or tax items with ⚖️.
