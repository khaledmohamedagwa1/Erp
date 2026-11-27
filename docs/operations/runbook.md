# ERP operations runbook (on-premises, Linux + Docker)

How to run a customer installation day to day: install, upgrade, back up, restore, monitor and troubleshoot.

**Paths and status:**

- Commands assume the default `ERP_HOME=/opt/erp` and are run as root.
- Windows installs are covered in `deploy/onprem/windows/README.md` (untested).
- Kubernetes installs are covered in `deploy/k8s/helm/erp` (`helm show values`).

All scripts live in `$ERP_HOME/scripts`, print `-h` help, are idempotent and exit non-zero on failure.

## 1. Layout of an installation

| Path | Contents | Mode |
|---|---|---|
| `.env` | Version, public origin, ports, subnet, database name, bundled-SQL flag, `SQL_SA_PASSWORD` (bundled SQL only) | 0600 root |
| `secrets/` | `Auth__SigningKeyPem`, `ConnectionStrings__Erp`, `Platform__ApiKey`, mounted read-only at `/run/secrets` | dir 0500, files 0400, uid 1654 |
| `certs/tls.crt`, `certs/tls.key` | Customer TLS certificate (full chain) and key | 0644 / 0640 |
| `nginx/` | Proxy configuration (replaced on upgrade) | |
| `data/files/` | File storage: `attachments/` holds uploaded attachments (content-addressed per tenant). Included in full backups (`files.tar.gz`) | uid 1654 |
| `data/sql/` | Bundled SQL Server data (only with `ERP_BUNDLED_SQL=true`) | uid 10001 |
| `backups/` | `<UTC timestamp>/` full sets, `logs/` transaction-log backups | 0700 root |
| `.previous/<version>/` | Compose file and nginx config of the version before the last upgrade | |

**The secrets are generated on the customer's machine at install time and exist nowhere else.**

- Keep an offline copy of `secrets/` and `.env`. Full backups include them in `config.tar.gz`, so treat backups
  as secret material.
- Losing `Auth__SigningKeyPem` signs every user out; it does not lose data.
- Losing the Data Protection keys (stored in the database) invalidates MFA secrets and pending password-reset
  tokens.

## 2. Install

Prerequisites:

- Linux x86-64 with Docker Engine 24 or later and the Compose v2 plugin;
- 4 vCPU and 8 GB RAM (16 GB with bundled SQL);
- ports 80 and 443 free;
- a DNS name for the server and its TLS certificate.

```bash
tar -xzf erp-onprem-1.4.0.tar.gz && cd erp-onprem-1.4.0
sha256sum -c SHA256SUMS
sudo ./scripts/install.sh --origin https://erp.customer.local \
     --tls-cert /path/fullchain.pem --tls-key /path/privkey.pem --init-tenant
```

To use the customer's SQL Server instead of the bundled container (recommended for production), add:

```bash
--external-sql 'Server=sql01,1433;Database=Erp;User Id=erp_app;Password=...;Encrypt=True;TrustServerCertificate=False'
```

- **Login:** the login needs `db_owner` on the database, because the migrator creates schemas and the
  row-level-security policy.
- **Connection string:** it is written to `secrets/ConnectionStrings__Erp`, never to `.env`.

What install does:

1. Loads the images from the bundle.
2. Creates the layout and generates the signing key (RSA 3072), the platform key and (bundled SQL only) the sa password.
3. Starts SQL Server if bundled and runs the migrator.
4. Starts proxy, api and worker, and waits for health.
5. Optionally creates the first tenant (prompts, or the `ERP_INIT_*` variables; the admin password is read
   without echo).

Re-running install is safe: nothing that exists is regenerated.

Without `--tls-cert`, a self-signed certificate is created so that the system starts. Replace it as described in
§8.

## 3. Daily operations

```bash
cd /opt/erp
./scripts/healthcheck.sh                     # containers, /health/ready of api and worker, HTTPS path; exit 0/1
docker compose -f docker-compose.onprem.yml --env-file .env ps
docker compose -f docker-compose.onprem.yml --env-file .env logs -f --since 15m api worker
```

Logs are JSON, one object per line. Every request line carries `TenantId`, `UserId`, `CorrelationId` and
`ClientIp`. A user's error dialog shows the correlation id (the `X-Correlation-Id` response header). Search for it:

```bash
docker compose -f docker-compose.onprem.yml --env-file .env logs api worker | grep '<correlation-id>'
```

The json-file driver rotates logs at 5 × 20 MB per container. Ship them to the customer's log system with any
Docker logging driver if required.

### Attachments and PDF output

- **Attachments** are stored under `data/files/attachments/<tenant>/<aa>/<sha256>` (setting
  `Documents__Attachments__LocalRoot`, preset in the compose file). The database row holds the name, type and hash;
  deleting an attachment is a soft delete and never removes the file (the same content may be attached elsewhere).
  Uploads are limited to 20 MB (`Documents__Attachments__MaxUploadBytes`; the proxy allows 25 MB on
  `/api/v1/documents/attachments`). Keep `data/files` and the database backups together: a restore of one without
  the other leaves attachments pointing at missing files (downloads answer `documents.file-missing`).
- To store attachments in S3 or an S3-compatible server (MinIO) instead, set `Documents__Attachments__Storage=S3` and
  `Documents__Attachments__S3__Bucket` / `__ServiceUrl` / `__Region` in a `docker-compose.override.yml`, and put
  `Documents__Attachments__S3__AccessKey` and `Documents__Attachments__S3__SecretKey` as files in `secrets/`.
- **PDF output** (`?format=pdf` on printable documents and reports) needs the optional Gotenberg container. Enable it:

  ```bash
  echo 'ERP_PDF_GOTENBERG_URL=http://gotenberg:3000' >> .env
  docker compose -f docker-compose.onprem.yml --env-file .env --profile pdf up -d
  ```

  Without it, PDF requests answer `501 reporting.pdf-not-configured` and HTML output keeps working. Gotenberg is
  reachable only on the internal `erp` network and runs with JavaScript disabled; for offline installs add the
  `gotenberg/gotenberg:8` image to the bundle (`ERP_GOTENBERG_IMAGE` overrides the image name).

### Monitoring

Point the monitoring system at:

- `scripts/healthcheck.sh` (exit code), or
- `https://<host>/health/ready` from inside the LAN. `/health/` is allowed only from private address ranges.

| Signal | Meaning | Action |
|---|---|---|
| `database` check Unhealthy | SQL Server unreachable or login failing | Check SQL Server service, credentials in `secrets/ConnectionStrings__Erp`, network |
| `migrations` check Unhealthy (lists `Context: Migration`) | Images are newer than the schema | Run the migrator (§4); never start a new version without it |
| `outbox` Degraded (> 5 min) / Unhealthy (> 30 min) | Integration events are not being delivered (worker stopped, poison message, SQL contention) | `logs worker`; restart the worker. A message that failed 10 times (`Outbox:MaxAttempts`) stays in `[schema].[OutboxMessage]` with `LastError` and is no longer retried: fix the cause, then reset its `Attempts` to 0 |
| Metric `erp.auth.failed_logins` spike | Password guessing | The proxy limits `/api/v1/auth/` to 10 req/s per IP and the app locks out accounts; consider blocking the source |

OpenTelemetry metrics (meter `Erp` plus the ASP.NET Core instrumentation) are exported when an OTLP endpoint is
configured. Set `OTEL_EXPORTER_OTLP_ENDPOINT` in the api/worker environment, for example through a
`docker-compose.override.yml`.

## 4. Upgrade

```bash
tar -xzf erp-onprem-1.5.0.tar.gz && cd erp-onprem-1.5.0 && sha256sum -c SHA256SUMS
sudo ./scripts/upgrade.sh --home /opt/erp
```

The script performs these steps in order:

1. **Pre-flight:**
   - secrets present;
   - at least 4 GB free (`ERP_MIN_FREE_MB`);
   - current health (asks before continuing if unhealthy);
   - SQL reachable.
2. **Mandatory full backup:** `backup.sh --full`, verified with `RESTORE VERIFYONLY`. The upgrade stops if it fails.
3. **New files and images:** loads the new images and keeps the old compose file and nginx config in `.previous/<old version>`.
4. **Stop the worker:** no background processing while the schema changes; the API keeps serving.
5. **Migrate:** runs the migrator with the new image. If the release contains destructive changes, the migrator
   refuses (exit 3). The script then puts the old version back, restarts the worker and stops: nothing was
   changed. Read the release notes, then re-run with `--allow-destructive`.
6. **Start:** starts the new api, worker and proxy, then runs the smoke test (`healthcheck.sh --wait 180`).

**Rollback** (the smoke test failed, or a defect is found after the upgrade):

```bash
cd /opt/erp
./scripts/restore.sh --from backups/<pre-upgrade timestamp>   # stops api/worker, restores DB + files, restarts
sed -i 's/^ERP_VERSION=.*/ERP_VERSION=<old version>/' .env
cp -a .previous/<old version>/docker-compose.onprem.yml . && cp -a .previous/<old version>/nginx/. nginx/
docker compose -f docker-compose.onprem.yml --env-file .env up -d
./scripts/healthcheck.sh --wait 180
```

A database restore discards everything entered since the backup. If the new schema is already in use and the
defect is small, prefer rolling forward with a fixed release.

## 5. Backup

```bash
./scripts/backup.sh --full          # DB full (COMPRESSION except Express, CHECKSUM, VERIFYONLY) + data/files + config
./scripts/backup.sh --log           # transaction-log backup (needs the FULL recovery model)
```

Recommended schedule (root crontab):

```cron
15 1 * * *    /opt/erp/scripts/backup.sh --full  >> /var/log/erp-backup.log 2>&1
*/15 * * * *  /opt/erp/scripts/backup.sh --log   >> /var/log/erp-backup.log 2>&1
```

- **Recovery model:** log backups need the FULL recovery model. Switch once with
  `ALTER DATABASE [Erp] SET RECOVERY FULL;`, then take a full backup. In SIMPLE mode the RPO is the last full
  backup, and `--log` fails with an explanatory message.
- **Retention:** `ERP_BACKUP_RETENTION_DAYS` (default 14) removes older sets and logs. The newest full set is always kept.
- **Run safety:** one backup runs at a time (lock file), and a failed full backup removes its partial directory.
- **Off-site copy:** copy `backups/` off the machine (rsync, NAS, tape). A backup on the same disk is not a backup.
- **External SQL Server:**
  - set `ERP_SQL_SERVER`, `ERP_SQL_USER` and `ERP_SQL_PASSWORD`, and install `sqlcmd` on the host;
  - point `ERP_SQL_BACKUP_DIR` at a share that both SQL Server and this host can reach;
  - or let the DBA's own backup jobs cover the database, and run `backup.sh` only for files and config.
- **Existing SQL container:** `ERP_SQLCMD_CONTAINER=<name>` runs sqlcmd inside that container.

## 6. Restore

```bash
# Side by side (verification, no impact on the running system):
./scripts/restore.sh --from backups/20260131T011500Z --target-db Erp_verify --no-files
# Over the live database (stops api/worker, restores DB and data/files, restarts):
./scripts/restore.sh --from backups/20260131T011500Z
# Point in time: full + log chain up to a UTC moment:
./scripts/restore.sh --from backups/20260131T011500Z --with-logs --stop-at 2026-01-31T13:45:00
```

- **Before restoring:** the manifest checksums are verified, and the operator must type the database name
  (`--yes` skips the prompt).
- **During the restore:** the files are moved to the server's default data and log paths under the target name.
  Logs taken after the full backup are applied in order.
- **After the restore:** `data/files` is restored only when overwriting the live database; the previous content
  is kept in `data/files.previous`.
- **Configuration:** to restore `.env`, `secrets/` and `certs/` on a new machine, unpack `config.tar.gz` from the
  set into `ERP_HOME` before running `install.sh`.

**Restore test (quarterly, required for audits):**

1. Restore the latest full backup side by side (`--target-db Erp_restoretest`).
2. Compare row counts of key tables, for example `SELECT COUNT(*) FROM accounting.JournalEntry`, and the trial
   balance total.
3. Drop the test database and record the result.

## 7. Disaster recovery (new server)

1. Install Docker and copy the latest backup set, the log backups and the release bundle.
2. Unpack `config.tar.gz` from the backup set into `/opt/erp`. This restores `.env`, `secrets/` and `certs/`, so
   tokens and MFA keep working.
3. Run `install.sh --no-init-tenant` from the bundle of the same version. It keeps the restored secrets and
   creates an empty schema.
4. Run `restore.sh --from <set> [--with-logs]`.
5. Run `healthcheck.sh`, then point DNS at the new server.

## 8. Certificates and secrets

- **TLS certificate renewal:**
  1. Replace `certs/tls.crt` (full chain) and `certs/tls.key`.
  2. Run `docker compose ... restart proxy`.
  3. Check with `openssl s_client -connect <host>:443 -servername <host> </dev/null | openssl x509 -noout -dates`.
- **Signing-key rotation:**
  1. Generate a new key: `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out secrets/Auth__SigningKeyPem.new`.
  2. Move it over the old file, then `chown 1654:1654` and `chmod 400` it.
  3. Restart api and worker. All sessions end; users sign in again.
- **SQL password change:**
  1. Change it in SQL Server.
  2. Update `secrets/ConnectionStrings__Erp`, and `SQL_SA_PASSWORD` in `.env` for bundled SQL.
  3. Restart.
- **Platform key / operators:** `Platform__ApiKey` now only bootstraps the **first** platform operator
  (`POST /api/platform/operators/bootstrap`); once an operator exists the key is refused. Operators sign in with password +
  TOTP (`POST /api/platform/auth/login`) and provision tenants with their token. Store the TOTP secret returned at creation
  in an authenticator app; remove the key file after bootstrap. Operator actions are listed in `identity.PlatformAuditEntry`.
  The public proxy still blocks `/api/platform`.
- **Several API instances → Redis:** set `ERP_CACHE_PROVIDER=Redis` in `.env` and start the `redis` profile
  (`docker compose ... --profile redis up -d`); Helm: `cache.provider=Redis` and either `cache.bundledRedis.enabled=true`
  or the secret key `ErpCache__RedisConnection` for a managed Redis. Without it, a revoked session can stay usable on
  another instance for up to 60 s and sign-in rate limits are per instance. Redis holds cache data only (no persistence
  needed); if it is unreachable, requests needing a session check fail — keep it next to the API.
- **External sign-in secrets:** a provider configured with `clientSecretName = X` reads `secrets/Auth__Oidc__Secrets__X`
  (owner 1654, mode 0400). Register `https://<host>/api/v1/auth/oidc/callback` as the redirect URI with the provider.

## 9. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| api exits at start with "Refusing to start: the configuration is not production-ready" | Read the listed problems (signing key, secure cookies, CORS origin, AutoMigrate, platform key length) and fix `.env` or secrets. For a throw-away evaluation only: `Hosting__SkipProductionChecks=true`. |
| `UnauthorizedAccessException: Access to the path '/run/secrets'` | Secret ownership was changed. Run `chown -R 1654:1654 secrets && chmod 500 secrets && chmod 400 secrets/*`. |
| Proxy returns 502 | api is not ready yet, or is crash-looping (`logs api`). During an upgrade, wait for `healthcheck.sh`. |
| `/health/ready` is Unhealthy with `migrations` | Run `docker compose ... --profile migrate run --rm migrator migrate` (or `upgrade.sh`). |
| `403 identity.step-up-required` on administration screens | Expected: the user must confirm a TOTP code (`/auth/step-up`), or enroll in two-step verification first (`mfaEnrollmentRequired: true`). Evaluation installs may set `Auth__StepUp__AllowWithoutMfa=true`. |
| `403 data-scope.denied` / documents "missing" for a user | The user's company access lists branches/warehouses; the document belongs to another one. Widen the access or grant `identity.scope.all`. |
| api exits with "ErpCache:RedisConnection is required" | `ErpCache__Provider=Redis` without a connection string. Set `ERP_REDIS_CONNECTION` / the secret, or switch back to `Memory`. |
| Migrator exit code 3 | Destructive schema change refused. Review, back up, then run `migrate --allow-destructive`. |
| Users are signed out after every request through the proxy | `ERP_PUBLIC_ORIGIN` does not match the URL users browse to, or a proxy in front of nginx strips `X-Forwarded-Proto`. |
| Wrong client IPs in logs | Another proxy sits in front of nginx: add its network to `Hosting__KnownNetworks__1` and raise `Hosting__ForwardLimit`. |
| Disk full | Clean `backups/` (keep the newest full set plus its logs) and old images (`docker image prune`); check `docker system df`. |

## 10. Security checklist (per installation)

- [ ] Customer certificate installed (not self-signed); HTTP redirects to HTTPS; HSTS present.
- [ ] `.env` is 0600 and `secrets/` is 0500/0400; an offline copy of both is stored securely.
- [ ] Bundled SQL is not published on the host (default). External SQL uses a dedicated login with TLS
      (`Encrypt=True`) and a trusted certificate.
- [ ] Full and log backups are scheduled, an off-site copy exists, and a restore test is recorded.
- [ ] The host firewall allows only 80/443 (and SSH from admin networks).
- [ ] OS and Docker are patched; images are upgraded with each release (images are scanned in CI).
