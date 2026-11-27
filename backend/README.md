# ERP backend (.NET 10)

Module reference: [docs/modules](../docs/modules) · deployment and operations: [docs/operations/runbook.md](../docs/operations/runbook.md).

Modular monolith (ADR-001). Each module has `Domain`, `Application`, `Infrastructure` and `Contracts` projects; modules talk to each other only through `Contracts`, which the architecture tests enforce.

```
src/BuildingBlocks/  Erp.SharedKernel · Erp.Application · Erp.Infrastructure · Erp.Web
src/Modules/         Organization · Identity · Configuration · Catalog · Parties · Tax · Accounting · Finance
                     Inventory · Procurement · Sales · Pos · Manufacturing · Workflow · FixedAssets · Reporting
src/Host/            Erp.Api (roles api|worker|all) · Erp.Migrator (migrate | init-tenant)
tests/               Erp.UnitTests · Erp.ArchitectureTests · Erp.IntegrationTests (real SQL Server)
```

## Prerequisites

* .NET SDK 10.0.1xx (`global.json`)
* SQL Server 2019+ (2022 recommended). For local work: `docker run -d -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong password>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`

## Configuration (never commit secrets)

| Setting | Env var | Notes |
|---|---|---|
| Connection string | `ConnectionStrings__Erp` | Required. |
| Platform key | `Platform__ApiKey` | Enables `POST /api/platform/tenants`. Empty = disabled. |
| Signing key | `Auth__SigningKeyPem` | PEM RSA private key. Required outside Development (Development uses an ephemeral key). |
| Process role | `Erp__Role` | `api`, `worker` or `all` (default). Only worker/all deliver the outbox. |
| Secure cookies | `Auth__SecureCookies` | `true` in production (HTTPS). |
| Single-tenant sign-in | `Auth__DefaultTenantCode` | On-prem: lets users sign in without typing the tenant code. |
| Telemetry | `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional. |

For local development use `dotnet user-secrets` in `src/Host/Erp.Api` or environment variables.

## Run

```bash
export ConnectionStrings__Erp="Server=localhost,1433;Database=Erp;User Id=sa;Password=<pwd>;TrustServerCertificate=True"
dotnet run --project src/Host/Erp.Migrator -- migrate          # migrations + row-level security policy
dotnet run --project src/Host/Erp.Migrator -- init-tenant --code ACME --name "Acme" --company-code ACME \
  --company-name-en "Acme Trading" --company-name-ar "أكمي للتجارة" --currency EGP --preset trading \
  --admin-email admin@acme.example --admin-name "Admin" --admin-password '<password>'
dotnet run --project src/Host/Erp.Api                          # http://localhost:5080, Swagger at /swagger (Development)
```

Sign in: `POST /api/v1/auth/login {"tenantCode":"ACME","email":"admin@acme.example","password":"..."}`, then send `Authorization: Bearer <accessToken>` and, for company-scoped endpoints, `X-Company-Id`.

Migrations are **not** applied at startup in production; run `Erp.Migrator migrate` as a deployment step. It refuses destructive migrations unless `--allow-destructive` is passed after a backup.

## Test

```bash
dotnet test --project tests/Erp.UnitTests
dotnet test --project tests/Erp.ArchitectureTests
# Integration tests: point at a SQL Server (a fresh database is created and dropped), or leave unset to use Testcontainers.
export ERP_TEST_SQL="Server=localhost,1433;User Id=sa;Password=<pwd>;TrustServerCertificate=True"
dotnet test --project tests/Erp.IntegrationTests
```

## Adding a migration

```bash
dotnet ef migrations add <Name> -p src/Modules/<Module>/Erp.Modules.<Module>.Infrastructure -s src/Host/Erp.Migrator -c <Module>DbContext -o Migrations
```

New tables with a `TenantId` column are added to the row-level security policy automatically the next time the migrator runs.
