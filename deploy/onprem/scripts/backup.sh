#!/usr/bin/env bash
# Backs up the ERP database (full or transaction log) and the file storage, then applies retention.
#
#   backup.sh [--full | --log] [--database NAME] [--dest DIR] [--retention-days N] [--no-files]
#
# Full backup  -> DEST/<UTC timestamp>/{<db>_full.bak, files.tar.gz, config.tar.gz, manifest.txt}
# Log backup   -> DEST/logs/<db>_<UTC timestamp>.trn   (requires the FULL recovery model; restore.sh replays them)
# Schedule (example, root crontab):  0 1 * * * /opt/erp/scripts/backup.sh --full
#                                    */15 * * * * /opt/erp/scripts/backup.sh --log
# Environment: ERP_HOME, ERP_SQLCMD_CONTAINER (use an existing SQL container), ERP_SQL_SERVER/ERP_SQL_USER/
# ERP_SQL_PASSWORD (external SQL with host sqlcmd), ERP_SQL_BACKUP_DIR (path as seen by SQL Server).
# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ERP_LOG_TAG=backup

mode=full
database=""
dest=""
retention=""
with_files=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --full) mode=full ;;
    --log) mode=log ;;
    --database) database="$2"; shift ;;
    --dest) dest="$2"; shift ;;
    --retention-days) retention="$2"; shift ;;
    --no-files) with_files=0 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done

load_env
database="${database:-${ERP_DATABASE:-Erp}}"
dest="${dest:-${ERP_BACKUP_DIR:-${ERP_HOME}/backups}}"
retention="${retention:-${ERP_BACKUP_RETENTION_DAYS:-14}}"
validate_identifier "${database}"
[[ "${retention}" =~ ^[0-9]+$ ]] || die "--retention-days must be a number"
require_cmd docker tar gzip

# One backup at a time (cron overlap, manual run during upgrade).
mkdir -p "${dest}"
exec 9> "${dest}/.backup.lock"
flock -w 600 9 || die "another backup is still running"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
server_dir="$(sql_backup_dir)"
ensure_server_dir "${server_dir}"

edition="$(sql_query "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);" | tr -d '[:space:]')"
# Express (4) cannot compress backups.
compression="COMPRESSION,"
[[ "${edition}" == "4" ]] && compression=""

if [[ "${mode}" == "log" ]]; then
  recovery="$(sql_query "SELECT recovery_model_desc FROM sys.databases WHERE name = N'${database}';" | tr -d '[:space:]')"
  [[ -n "${recovery}" ]] || die "database ${database} not found"
  [[ "${recovery}" != "SIMPLE" ]] || die "database ${database} uses the SIMPLE recovery model; log backups need FULL (ALTER DATABASE [${database}] SET RECOVERY FULL, then take a full backup)"
  mkdir -p "${dest}/logs"
  file="${database}_${stamp}.trn"
  sql_query "BACKUP LOG [${database}] TO DISK = N'${server_dir}/${file}' WITH ${compression} CHECKSUM, INIT, NAME = N'${database} log ${stamp}';" >/dev/null
  fetch_backup_file "${server_dir}/${file}" "${dest}/logs/${file}"
  log "log backup written: ${dest}/logs/${file}"
else
  set_dir="${dest}/${stamp}"
  mkdir -p "${set_dir}"
  # A failed run must not leave a half-written set that retention or restore could mistake for a good one.
  complete=0
  discard_incomplete() { [[ "${complete}" == "1" ]] || rm -rf -- "${set_dir}"; }
  trap discard_incomplete EXIT
  file="${database}_full.bak"
  log "full backup of ${database} -> ${set_dir}"
  sql_query "BACKUP DATABASE [${database}] TO DISK = N'${server_dir}/${stamp}_${file}' WITH ${compression} CHECKSUM, INIT, NAME = N'${database} full ${stamp}';" >/dev/null
  fetch_backup_file "${server_dir}/${stamp}_${file}" "${set_dir}/${file}"

  if [[ "${with_files}" == "1" && -d "${ERP_HOME}/data/files" ]]; then
    tar -C "${ERP_HOME}/data" -czf "${set_dir}/files.tar.gz" files
  fi
  # Configuration needed to bring the system back (secrets included: the backup directory is root-only).
  config_items=()
  for item in .env secrets certs; do
    [[ -e "${ERP_HOME}/${item}" ]] && config_items+=("${item}")
  done
  if [[ ${#config_items[@]} -gt 0 ]]; then
    tar -C "${ERP_HOME}" -czf "${set_dir}/config.tar.gz" "${config_items[@]}"
  fi

  {
    echo "database=${database}"
    echo "created_utc=${stamp}"
    echo "erp_version=${ERP_VERSION:-unknown}"
    echo "sql_edition=${edition}"
    (cd "${set_dir}" && sha256sum -- *.bak *.tar.gz 2>/dev/null) | sed 's/^/sha256 /'
  } > "${set_dir}/manifest.txt"
  # Verify the backup is readable before declaring success.
  push_backup_file "${set_dir}/${file}" "${server_dir}/verify_${stamp}.bak"
  sql_query "RESTORE VERIFYONLY FROM DISK = N'${server_dir}/verify_${stamp}.bak' WITH CHECKSUM;" >/dev/null
  container="$(sql_container)"
  if [[ -n "${container}" ]]; then
    docker exec "${container}" rm -f "${server_dir}/verify_${stamp}.bak"
  fi
  complete=1
  log "full backup verified: ${set_dir}"
  printf '%s\n' "${set_dir}"
fi

# Retention: whole backup sets and log files older than N days (the newest full set is always kept).
if (( retention > 0 )); then
  newest="$(find "${dest}" -mindepth 1 -maxdepth 1 -type d -name '20*Z' | sort | tail -n1)"
  while IFS= read -r old; do
    [[ -n "${old}" && "${old}" != "${newest}" ]] || continue
    rm -rf -- "${old}"
    log "retention: removed ${old}"
  done < <(find "${dest}" -mindepth 1 -maxdepth 1 -type d -name '20*Z' -mtime "+${retention}")
  if [[ -d "${dest}/logs" ]]; then
    find "${dest}/logs" -type f -name '*.trn' -mtime "+${retention}" -print -delete | sed 's/^/retention: removed /' >&2 || true
  fi
fi
