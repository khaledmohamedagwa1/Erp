#!/usr/bin/env bash
# Checks that the on-prem stack is up: container state, api/worker readiness and the HTTPS proxy.
#
#   healthcheck.sh [--wait SECONDS] [--quiet]
#
# Exit code 0 = healthy, 1 = something is down (details on stderr). Suitable for monitoring agents and cron.
# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ERP_LOG_TAG=health

wait_seconds=0
quiet=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --wait) wait_seconds="$2"; shift ;;
    --quiet) quiet=1 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done
[[ "${wait_seconds}" =~ ^[0-9]+$ ]] || die "--wait must be a number of seconds"

load_env
require_cmd docker
[[ -f "${ERP_HOME}/.env" ]] || die "${ERP_HOME}/.env not found (is ERP_HOME right?)"

check_once() {
  local failures=0 service id state
  local services=(api worker proxy)
  bundled_sql && services+=(sqlserver)
  for service in "${services[@]}"; do
    id="$(compose ps -q "${service}" 2>/dev/null | head -n1)"
    if [[ -z "${id}" ]]; then
      [[ "${quiet}" == "1" ]] || log "${service}: not running"
      failures=$(( failures + 1 ))
      continue
    fi
    state="$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "${id}")"
    case "${state}" in
      running/healthy|running/none) [[ "${quiet}" == "1" ]] || log "${service}: ${state}" ;;
      *) [[ "${quiet}" == "1" ]] || log "${service}: ${state}"; failures=$(( failures + 1 )) ;;
    esac
  done

  # Readiness as the application sees it (database reachable, schema current, outbox lag for the worker).
  for service in api worker; do
    id="$(compose ps -q "${service}" 2>/dev/null | head -n1)"
    [[ -n "${id}" ]] || continue
    if ! docker exec "${id}" dotnet Erp.Api.dll --healthcheck >/dev/null 2>&1; then
      [[ "${quiet}" == "1" ]] || log "${service}: /health/ready is not Healthy"
      failures=$(( failures + 1 ))
    fi
  done

  # End-to-end through TLS (self-signed certificates are accepted here: this checks the path, not the chain).
  if command -v curl >/dev/null 2>&1; then
    if ! curl -fsk --max-time 10 -o /dev/null "https://127.0.0.1:${ERP_HTTPS_PORT:-443}/health/live"; then
      [[ "${quiet}" == "1" ]] || log "proxy: https://127.0.0.1:${ERP_HTTPS_PORT:-443}/health/live failed"
      failures=$(( failures + 1 ))
    fi
  fi
  return "${failures}"
}

deadline=$(( SECONDS + wait_seconds ))
requested_quiet="${quiet}"
# While waiting, retry silently; report details only for the final attempt.
(( wait_seconds > 0 )) && quiet=1
while true; do
  if check_once; then
    if [[ "${requested_quiet}" != "1" ]]; then
      quiet=0
      check_once || true
      log "healthy"
    fi
    exit 0
  fi
  if (( SECONDS >= deadline )); then
    quiet="${requested_quiet}"
    check_once || true
    log "UNHEALTHY"
    exit 1
  fi
  sleep 5
done
