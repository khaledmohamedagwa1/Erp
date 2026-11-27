#!/usr/bin/env bash
# Upgrades an installed ERP to the version of the bundle this script belongs to.
#
#   sudo ./scripts/upgrade.sh [--home DIR] [--allow-destructive] [--yes]
#
# Steps: pre-flight checks -> mandatory full backup -> load images -> stop worker -> migrator -> start -> smoke test.
# The migrator refuses migrations that drop or narrow data unless --allow-destructive is given (ADR-015); in that
# case nothing is changed and the previous version keeps running.
# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ERP_LOG_TAG=upgrade

home="${ERP_HOME_OVERRIDE:-/opt/erp}"
allow_destructive=0
assume_yes=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --home) home="$2"; shift ;;
    --allow-destructive) allow_destructive=1 ;;
    --yes|-y) assume_yes=1 ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done

require_root
require_cmd docker df awk
ERP_HOME="$(cd "${home}" && pwd)" || die "${home} does not exist"
export ERP_HOME
[[ -f "${ERP_HOME}/.env" ]] || die "${ERP_HOME}/.env not found: install first (scripts/install.sh)"
load_env
current="${ERP_VERSION:-unknown}"
target="$(cat "${ERP_BUNDLE_DIR}/VERSION" 2>/dev/null || true)"
[[ -n "${target}" ]] || die "VERSION file not found in the bundle"

# --- Pre-flight ---------------------------------------------------------------------------------------------------
log "pre-flight: ${current} -> ${target}"
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required"
for f in Auth__SigningKeyPem ConnectionStrings__Erp; do
  [[ -s "${ERP_HOME}/secrets/${f}" ]] || die "secret ${f} is missing"
done
free_mb="$(df -Pm "${ERP_HOME}" | awk 'NR == 2 { print $4 }')"
(( free_mb >= ${ERP_MIN_FREE_MB:-4096} )) || die "only ${free_mb} MB free in ${ERP_HOME}; need ${ERP_MIN_FREE_MB:-4096} MB for the backup and images"
if ! "${ERP_SCRIPTS_DIR}/healthcheck.sh" --quiet; then
  warn "the current system is not fully healthy"
  [[ "${assume_yes}" == "1" ]] || { read -r -p "Continue anyway? [y/N] " reply; [[ "${reply}" =~ ^[Yy] ]] || die "aborted"; }
fi
sql_query "SELECT 1;" >/dev/null || die "cannot reach SQL Server"

if [[ "${assume_yes}" != "1" ]]; then
  read -r -p "Upgrade ERP ${current} to ${target}? A full backup is taken first. [y/N] " reply
  [[ "${reply}" =~ ^[Yy] ]] || die "aborted"
fi

# --- Mandatory backup ---------------------------------------------------------------------------------------------
log "taking the pre-upgrade backup"
backup_set="$("${ERP_SCRIPTS_DIR}/backup.sh" --full)" || die "backup failed; the upgrade did not start"
log "backup: ${backup_set}"

# --- New images and files -----------------------------------------------------------------------------------------
if compgen -G "${ERP_BUNDLE_DIR}/images/*.tar.gz" >/dev/null; then
  for image in "${ERP_BUNDLE_DIR}"/images/*.tar.gz; do
    log "loading $(basename "${image}")"
    gzip -dc "${image}" | docker load >/dev/null
  done
fi
for image in erp-api erp-migrator; do
  docker image inspect "${ERP_REGISTRY:-}${image}:${target}" >/dev/null 2>&1 || die "image ${ERP_REGISTRY:-}${image}:${target} is not available"
done

previous_dir="${ERP_HOME}/.previous/${current}"
mkdir -p "${previous_dir}"
cp -a "${ERP_HOME}/${ERP_COMPOSE_FILE}" "${ERP_HOME}/nginx" "${previous_dir}/"
if [[ "${ERP_BUNDLE_DIR}" != "${ERP_HOME}" ]]; then
  cp -f "${ERP_BUNDLE_DIR}/${ERP_COMPOSE_FILE}" "${ERP_BUNDLE_DIR}/VERSION" "${ERP_BUNDLE_DIR}/env.example" "${ERP_HOME}/"
  cp -f "${ERP_BUNDLE_DIR}"/nginx/* "${ERP_HOME}/nginx/"
  cp -f "${ERP_BUNDLE_DIR}"/scripts/*.sh "${ERP_HOME}/scripts/"
  chmod 755 "${ERP_HOME}"/scripts/*.sh
fi

rollback_files() {
  cp -a "${previous_dir}/${ERP_COMPOSE_FILE}" "${ERP_HOME}/"
  cp -a "${previous_dir}/nginx/." "${ERP_HOME}/nginx/"
  set_env_value ERP_VERSION "${current}"
}

# --- Migrate ------------------------------------------------------------------------------------------------------
log "stopping the worker (no background processing during the migration)"
compose stop worker >/dev/null
set_env_value ERP_VERSION "${target}"
migrate_args=(migrate)
[[ "${allow_destructive}" == "1" ]] && migrate_args+=(--allow-destructive)
rc=0
compose_migrator "${migrate_args[@]}" || rc=$?
if [[ ${rc} -ne 0 ]]; then
  rollback_files
  compose start worker >/dev/null || true
  if [[ ${rc} -eq 3 ]]; then
    die "this upgrade contains destructive schema changes. Nothing was changed. Review the release notes, then re-run with --allow-destructive"
  fi
  die "migration failed (exit ${rc}); the previous version is running again. Database backup: ${backup_set}"
fi

# --- Start and smoke test -----------------------------------------------------------------------------------------
log "starting ${target}"
compose up -d --remove-orphans api worker proxy
if ! "${ERP_SCRIPTS_DIR}/healthcheck.sh" --wait 180; then
  log "the new version is not healthy. To roll back:"
  log "  1. ${ERP_SCRIPTS_DIR}/restore.sh --from ${backup_set}"
  log "  2. set ERP_VERSION=${current} in ${ERP_HOME}/.env, restore ${previous_dir}, then docker compose up -d"
  die "smoke test failed"
fi
log "upgraded ${current} -> ${target}. Pre-upgrade backup: ${backup_set}"
