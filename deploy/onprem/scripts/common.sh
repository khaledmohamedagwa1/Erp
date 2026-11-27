#!/usr/bin/env bash
# Shared helpers for the on-prem scripts. Sourced, never executed directly.
# shellcheck shell=bash

set -Eeuo pipefail
umask 077

ERP_SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Directory that holds docker-compose.onprem.yml: the bundle root by default, or the installed ERP_HOME.
ERP_BUNDLE_DIR="$(cd "${ERP_SCRIPTS_DIR}/.." && pwd)"
ERP_HOME="${ERP_HOME:-${ERP_BUNDLE_DIR}}"
export ERP_APP_UID=1654         # non-root user of the erp images
export ERP_MSSQL_UID=10001      # mssql user of the SQL Server image
ERP_COMPOSE_FILE="docker-compose.onprem.yml"

log()  { printf '%s [%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${ERP_LOG_TAG:-erp}" "$*" >&2; }
warn() { log "WARNING: $*"; }
die()  { log "ERROR: $*"; exit 1; }

require_cmd() {
  local cmd
  for cmd in "$@"; do
    command -v "${cmd}" >/dev/null 2>&1 || die "required command not found: ${cmd}"
  done
}

require_root() {
  if [[ "${EUID}" -ne 0 && "${ERP_ALLOW_NONROOT:-0}" != "1" ]]; then
    die "run as root (secrets must be owned by uid ${ERP_APP_UID}); set ERP_ALLOW_NONROOT=1 to override"
  fi
}

# Loads ERP_HOME/.env into the environment without executing it as a script.
load_env() {
  local file="${ERP_HOME}/.env" line key value
  [[ -f "${file}" ]] || return 0
  while IFS= read -r line || [[ -n "${line}" ]]; do
    [[ -z "${line}" || "${line}" == \#* || "${line}" != *=* ]] && continue
    key="${line%%=*}"
    value="${line#*=}"
    [[ "${key}" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    # Values already set in the caller's environment win (lets operators override per run).
    if [[ -z "${!key+x}" ]]; then
      export "${key}=${value}"
    fi
  done < "${file}"
}

# Sets KEY=VALUE in ERP_HOME/.env (adds it when missing). Values must not contain newlines.
set_env_value() {
  local key="$1" value="$2" file="${ERP_HOME}/.env" tmp
  tmp="$(mktemp "${file}.XXXXXX")"
  if [[ -f "${file}" ]] && grep -q "^${key}=" "${file}"; then
    awk -v k="${key}" -v v="${value}" 'BEGIN { FS = OFS = "=" } $1 == k { print k "=" v; next } { print }' "${file}" > "${tmp}"
  else
    { [[ -f "${file}" ]] && cat "${file}"; printf '%s=%s\n' "${key}" "${value}"; } > "${tmp}"
  fi
  chmod 600 "${tmp}"
  mv -f "${tmp}" "${file}"
  # Keep this shell in step: docker compose prefers the process environment over the .env file.
  export "${key}=${value}"
}

bundled_sql() { [[ "${ERP_BUNDLED_SQL:-false}" == "true" ]]; }

compose() {
  local args=(-f "${ERP_HOME}/${ERP_COMPOSE_FILE}" --env-file "${ERP_HOME}/.env")
  if bundled_sql; then
    args+=(--profile bundled-sql)
  fi
  docker compose "${args[@]}" "$@"
}

# compose_migrator [-e NAME]... <migrator command and arguments>
compose_migrator() {
  local opts=()
  while [[ $# -gt 1 && "$1" == "-e" ]]; do
    opts+=("$1" "$2")
    shift 2
  done
  compose --profile migrate run --rm --no-deps "${opts[@]}" migrator "$@"
}

# Random alphanumeric string of the given length (default 48).
random_secret() {
  local length="${1:-48}" out=""
  while [[ ${#out} -lt ${length} ]]; do
    out+="$(openssl rand -base64 96 | tr -dc 'A-Za-z0-9')"
  done
  printf '%s' "${out:0:${length}}"
}

validate_identifier() {
  [[ "$1" =~ ^[A-Za-z][A-Za-z0-9_]{0,99}$ ]] || die "invalid database name '$1' (letters, digits, underscore)"
}

# --- SQL access ---------------------------------------------------------------------------------------------------
# sqlcmd runs inside a SQL Server container when there is one (bundled SQL, or ERP_SQLCMD_CONTAINER for an existing
# container); otherwise the host's sqlcmd connects to ERP_SQL_SERVER. Passwords travel in SQLCMDPASSWORD, never argv.

sql_container() {
  if [[ -n "${ERP_SQLCMD_CONTAINER:-}" ]]; then
    printf '%s' "${ERP_SQLCMD_CONTAINER}"
  elif bundled_sql; then
    compose ps -q sqlserver 2>/dev/null | head -n1
  fi
}

sql_password() {
  if [[ -n "${ERP_SQL_PASSWORD:-}" ]]; then
    printf '%s' "${ERP_SQL_PASSWORD}"
  elif [[ -n "${SQL_SA_PASSWORD:-}" ]]; then
    printf '%s' "${SQL_SA_PASSWORD}"
  else
    die "no SQL password: set ERP_SQL_PASSWORD (external SQL) or SQL_SA_PASSWORD in .env (bundled SQL)"
  fi
}

# sql_query "T-SQL"  -> prints rows (no headers); on a SQL error prints sqlcmd's messages to stderr and fails.
# stdin is detached so callers can use it inside `while read` loops.
sql_query() {
  local query="$1" container password output rc=0
  container="$(sql_container)"
  password="$(sql_password)"
  if [[ -n "${container}" ]]; then
    output="$(SQLCMDPASSWORD="${password}" docker exec -e SQLCMDPASSWORD "${container}" \
      /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U "${ERP_SQL_USER:-sa}" -b -h -1 -W -s '|' \
      -Q "SET NOCOUNT ON; ${query}" < /dev/null 2>&1)" || rc=$?
  else
    require_cmd sqlcmd
    output="$(SQLCMDPASSWORD="${password}" sqlcmd -C -S "${ERP_SQL_SERVER:?set ERP_SQL_SERVER for an external SQL Server}" \
      -U "${ERP_SQL_USER:-sa}" -b -h -1 -W -s '|' -Q "SET NOCOUNT ON; ${query}" < /dev/null 2>&1)" || rc=$?
  fi
  if [[ ${rc} -ne 0 ]]; then
    printf '%s\n' "${output}" | sed 's/^/  sql: /' >&2
    return "${rc}"
  fi
  [[ -z "${output}" ]] || printf '%s\n' "${output}"
}

# Path where SQL Server itself reads/writes backup files.
sql_backup_dir() { printf '%s' "${ERP_SQL_BACKUP_DIR:-/var/opt/mssql/backup/erp}"; }

# Copies a backup file written by SQL Server to the local backup directory (container case), or verifies it exists
# locally (external SQL with ERP_SQL_BACKUP_DIR pointing at a share mounted on both machines).
fetch_backup_file() {
  local server_path="$1" local_path="$2" container
  container="$(sql_container)"
  if [[ -n "${container}" ]]; then
    docker cp "${container}:${server_path}" "${local_path}" < /dev/null
    docker exec "${container}" rm -f "${server_path}"
  else
    [[ "${server_path}" == "${local_path}" ]] || cp "${server_path}" "${local_path}"
  fi
}

push_backup_file() {
  local local_path="$1" server_path="$2" container
  container="$(sql_container)"
  if [[ -n "${container}" ]]; then
    chmod 644 "${local_path}"
    docker cp "${local_path}" "${container}:${server_path}" < /dev/null
  else
    [[ "${server_path}" == "${local_path}" ]] || cp "${local_path}" "${server_path}"
  fi
}

ensure_server_dir() {
  local dir="$1" container
  container="$(sql_container)"
  if [[ -n "${container}" ]]; then
    docker exec "${container}" mkdir -p "${dir}"
  else
    mkdir -p "${dir}"
  fi
}

wait_for_healthy() {
  local service="$1" timeout="${2:-180}" waited=0 id status
  while (( waited < timeout )); do
    id="$(compose ps -q "${service}" 2>/dev/null | head -n1)"
    if [[ -n "${id}" ]]; then
      status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${id}")"
      [[ "${status}" == "healthy" ]] && return 0
    fi
    sleep 5
    waited=$(( waited + 5 ))
  done
  return 1
}
