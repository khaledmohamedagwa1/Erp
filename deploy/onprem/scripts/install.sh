#!/usr/bin/env bash
# First-time installation of the ERP on a Linux host with Docker (online or from the offline bundle).
# Safe to re-run: existing secrets, .env values and data are kept; only missing pieces are created.
#
#   sudo ./scripts/install.sh --origin https://erp.customer.local [options]
#
#   --home DIR              install directory (default /opt/erp)
#   --origin URL            public HTTPS origin users browse to (required on first install)
#   --external-sql CS       use an existing SQL Server; CS is the full ADO.NET connection string (stored as a secret
#                           file, never in .env). Without it, a SQL Server container is bundled (Express edition).
#   --tls-cert FILE --tls-key FILE
#                           customer certificate (PEM, full chain) and key. Without them a self-signed certificate is
#                           generated so the system starts; replace certs/tls.crt + tls.key and restart the proxy.
#   --http-port N --https-port N   host ports for the proxy (default 80/443)
#   --init-tenant           create the first tenant after installing (prompts unless the ERP_INIT_* variables are set)
#   --no-init-tenant        never prompt for the first tenant
#   --yes                   non-interactive
# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ERP_LOG_TAG=install

home="/opt/erp"
origin=""
external_cs=""
tls_cert=""
tls_key=""
http_port=""
https_port=""
init_tenant="ask"
assume_yes=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --home) home="$2"; shift ;;
    --origin) origin="$2"; shift ;;
    --external-sql) external_cs="$2"; shift ;;
    --tls-cert) tls_cert="$2"; shift ;;
    --tls-key) tls_key="$2"; shift ;;
    --http-port) http_port="$2"; shift ;;
    --https-port) https_port="$2"; shift ;;
    --init-tenant) init_tenant="yes" ;;
    --no-init-tenant) init_tenant="no" ;;
    --yes|-y) assume_yes=1 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done

require_root
require_cmd docker openssl awk sed tar
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 (docker compose) is required"
docker info >/dev/null 2>&1 || die "cannot talk to the Docker daemon"

ERP_HOME="$(mkdir -p "${home}" && cd "${home}" && pwd)"
export ERP_HOME
version="$(cat "${ERP_BUNDLE_DIR}/VERSION" 2>/dev/null || true)"
[[ -n "${version}" ]] || die "VERSION file not found next to the scripts (run from an extracted bundle)"
log "installing ERP ${version} into ${ERP_HOME}"

# 1. Files from the bundle (configuration templates and scripts; never overwrites operator data).
if [[ "${ERP_BUNDLE_DIR}" != "${ERP_HOME}" ]]; then
  cp -f "${ERP_BUNDLE_DIR}/${ERP_COMPOSE_FILE}" "${ERP_BUNDLE_DIR}/VERSION" "${ERP_BUNDLE_DIR}/env.example" "${ERP_HOME}/"
  mkdir -p "${ERP_HOME}/nginx" "${ERP_HOME}/scripts"
  cp -f "${ERP_BUNDLE_DIR}"/nginx/* "${ERP_HOME}/nginx/"
  cp -f "${ERP_BUNDLE_DIR}"/scripts/*.sh "${ERP_HOME}/scripts/"
  chmod 755 "${ERP_HOME}"/scripts/*.sh
fi
chmod 644 "${ERP_HOME}/${ERP_COMPOSE_FILE}" "${ERP_HOME}"/nginx/*

# 2. Images from the offline bundle (no registry access needed).
if compgen -G "${ERP_BUNDLE_DIR}/images/*.tar.gz" >/dev/null; then
  for image in "${ERP_BUNDLE_DIR}"/images/*.tar.gz; do
    log "loading $(basename "${image}")"
    gzip -dc "${image}" | docker load >/dev/null
  done
fi

# 3. Directories and permissions.
mkdir -p "${ERP_HOME}/secrets" "${ERP_HOME}/certs" "${ERP_HOME}/data/files" "${ERP_HOME}/backups"
chmod 700 "${ERP_HOME}/secrets" "${ERP_HOME}/backups"
chmod 755 "${ERP_HOME}/certs" "${ERP_HOME}/data"

# 4. .env (mode 0600). Existing values win; only missing keys are added.
touch "${ERP_HOME}/.env"
chmod 600 "${ERP_HOME}/.env"
load_env
set_env_value ERP_VERSION "${version}"
if [[ -n "${origin}" ]]; then
  [[ "${origin}" =~ ^https://[^/]+$ ]] || die "--origin must be https://host[:port] without a path"
  set_env_value ERP_PUBLIC_ORIGIN "${origin}"
elif [[ -z "${ERP_PUBLIC_ORIGIN:-}" ]]; then
  die "--origin is required on first install (e.g. https://erp.customer.local)"
fi
[[ -n "${http_port}" ]] && set_env_value ERP_HTTP_PORT "${http_port}"
[[ -n "${https_port}" ]] && set_env_value ERP_HTTPS_PORT "${https_port}"
[[ -n "${ERP_SUBNET:-}" ]] || set_env_value ERP_SUBNET "172.28.0.0/24"
[[ -n "${ERP_DATABASE:-}" ]] || set_env_value ERP_DATABASE "Erp"
[[ -n "${ERP_BACKUP_RETENTION_DAYS:-}" ]] || set_env_value ERP_BACKUP_RETENTION_DAYS "14"

if [[ -n "${external_cs}" ]]; then
  set_env_value ERP_BUNDLED_SQL false
  # Backups and restores address the database by name: take it from the connection string.
  external_db="$(printf '%s' "${external_cs}" | tr ';' '\n' | awk -F= 'tolower($1) ~ /^ *(database|initial catalog) *$/ { gsub(/ /, "", $2); print $2; exit }')"
  [[ -n "${external_db}" ]] || die "--external-sql must name the database (Database=...)"
  validate_identifier "${external_db}"
  set_env_value ERP_DATABASE "${external_db}"
elif [[ -z "${ERP_BUNDLED_SQL:-}" ]]; then
  set_env_value ERP_BUNDLED_SQL true
fi
# Re-read so the values written above are visible.
unset ERP_BUNDLED_SQL ERP_DATABASE ERP_HTTPS_PORT ERP_HTTP_PORT
load_env

# 5. Secrets: generated here, on the customer's machine, once. Never shipped in the bundle.
secret() { printf '%s' "${ERP_HOME}/secrets/$1"; }
if [[ ! -s "$(secret Auth__SigningKeyPem)" ]]; then
  log "generating the token signing key (RSA 3072)"
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$(secret Auth__SigningKeyPem)" 2>/dev/null
fi
if [[ ! -s "$(secret Platform__ApiKey)" ]]; then
  log "generating the platform API key"
  random_secret 64 > "$(secret Platform__ApiKey)"
fi
if bundled_sql; then
  if [[ -z "${SQL_SA_PASSWORD:-}" ]]; then
    log "generating the SQL Server sa password"
    set_env_value SQL_SA_PASSWORD "$(random_secret 32)Aa1"
    unset SQL_SA_PASSWORD
    load_env
  fi
  set_env_value MSSQL_PID "${MSSQL_PID:-Express}"
  printf 'Server=sqlserver,1433;Database=%s;User Id=sa;Password=%s;TrustServerCertificate=True;Encrypt=True' \
    "${ERP_DATABASE}" "${SQL_SA_PASSWORD}" > "$(secret ConnectionStrings__Erp)"
  mkdir -p "${ERP_HOME}/data/sql"
  chown -R "${ERP_MSSQL_UID}:0" "${ERP_HOME}/data/sql"
  chmod 770 "${ERP_HOME}/data/sql"
elif [[ -n "${external_cs}" ]]; then
  printf '%s' "${external_cs}" > "$(secret ConnectionStrings__Erp)"
fi
[[ -s "$(secret ConnectionStrings__Erp)" ]] || die "no connection string: pass --external-sql or use the bundled SQL Server"
# The containers run as uid ${ERP_APP_UID}; only that user (and root) can read the secrets.
chown "${ERP_APP_UID}:${ERP_APP_UID}" "${ERP_HOME}/secrets" "${ERP_HOME}"/secrets/*
chmod 500 "${ERP_HOME}/secrets"
chmod 400 "${ERP_HOME}"/secrets/*
chown -R "${ERP_APP_UID}:${ERP_APP_UID}" "${ERP_HOME}/data/files"

# 6. TLS certificate.
if [[ -n "${tls_cert}" || -n "${tls_key}" ]]; then
  [[ -f "${tls_cert}" && -f "${tls_key}" ]] || die "--tls-cert and --tls-key must both be readable files"
  openssl x509 -in "${tls_cert}" -noout >/dev/null 2>&1 || die "${tls_cert} is not a PEM certificate"
  install -m 644 "${tls_cert}" "${ERP_HOME}/certs/tls.crt"
  install -m 640 "${tls_key}" "${ERP_HOME}/certs/tls.key"
elif [[ ! -s "${ERP_HOME}/certs/tls.crt" ]]; then
  host="${ERP_PUBLIC_ORIGIN#https://}"
  host="${host%%:*}"
  warn "no certificate given: generating a self-signed one for ${host}. Replace certs/tls.crt and certs/tls.key with the customer's certificate."
  openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=${host}" -addext "subjectAltName=DNS:${host}" \
    -keyout "${ERP_HOME}/certs/tls.key" -out "${ERP_HOME}/certs/tls.crt" 2>/dev/null
  chmod 644 "${ERP_HOME}/certs/tls.crt"
fi
# nginx workers read the key after the master (root) has opened it; keep it unreadable for others.
chmod 640 "${ERP_HOME}/certs/tls.key"
chown 0:101 "${ERP_HOME}/certs/tls.key" 2>/dev/null || true

# 7. Database and schema.
if bundled_sql; then
  log "starting SQL Server"
  compose up -d sqlserver
  wait_for_healthy sqlserver 300 || die "SQL Server did not become healthy (docker compose logs sqlserver)"
fi
log "applying database migrations"
compose_migrator migrate || die "migration failed; nothing else was started"

# 8. Application.
log "starting api, worker and proxy"
compose up -d api worker proxy
"${ERP_SCRIPTS_DIR}/healthcheck.sh" --wait 180 --quiet || die "the stack did not become healthy (scripts/healthcheck.sh for details)"

# 9. First tenant.
if [[ "${init_tenant}" == "ask" && "${assume_yes}" == "0" && -t 0 ]]; then
  read -r -p "Create the first tenant now? [y/N] " reply
  [[ "${reply}" =~ ^[Yy] ]] && init_tenant="yes"
fi
if [[ "${init_tenant}" == "yes" ]]; then
  prompt() { local var="$1" text="$2" def="${3:-}"; if [[ -z "${!var:-}" ]]; then read -r -p "${text}${def:+ [${def}]}: " "${var?}"; printf -v "${var}" '%s' "${!var:-${def}}"; fi; }
  prompt ERP_INIT_TENANT_CODE "Tenant code (e.g. ACME)"
  prompt ERP_INIT_TENANT_NAME "Tenant name"
  prompt ERP_INIT_COMPANY_CODE "Company code" "${ERP_INIT_TENANT_CODE:-}"
  prompt ERP_INIT_COMPANY_NAME_EN "Company name (English)" "${ERP_INIT_TENANT_NAME:-}"
  prompt ERP_INIT_COMPANY_NAME_AR "Company name (Arabic)" "${ERP_INIT_COMPANY_NAME_EN:-}"
  prompt ERP_INIT_CURRENCY "Base currency" "EGP"
  prompt ERP_INIT_PRESET "Preset (trading, manufacturing, services)" "trading"
  prompt ERP_INIT_ADMIN_EMAIL "Administrator e-mail"
  prompt ERP_INIT_ADMIN_NAME "Administrator name" "Administrator"
  if [[ -z "${ERP_INIT_ADMIN_PASSWORD:-}" ]]; then
    read -r -s -p "Administrator password: " ERP_INIT_ADMIN_PASSWORD; echo >&2
  fi
  export ERP_INIT_ADMIN_PASSWORD
  # The password reaches the migrator through the environment, not the command line.
  compose_migrator -e ERP_INIT_ADMIN_PASSWORD init-tenant \
    --code "${ERP_INIT_TENANT_CODE}" --name "${ERP_INIT_TENANT_NAME}" \
    --company-code "${ERP_INIT_COMPANY_CODE}" --company-name-en "${ERP_INIT_COMPANY_NAME_EN}" \
    --company-name-ar "${ERP_INIT_COMPANY_NAME_AR}" --currency "${ERP_INIT_CURRENCY}" --preset "${ERP_INIT_PRESET}" \
    --admin-email "${ERP_INIT_ADMIN_EMAIL}" --admin-name "${ERP_INIT_ADMIN_NAME}" \
    || die "tenant creation failed (the system itself is installed; retry with scripts/install.sh --init-tenant)"
fi

log "installed ERP ${version}. Open ${ERP_PUBLIC_ORIGIN}"
log "next: schedule ${ERP_HOME}/scripts/backup.sh (see docs/operations/runbook.md) and keep a copy of ${ERP_HOME}/secrets off this machine"
