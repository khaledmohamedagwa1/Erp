#!/usr/bin/env bash
# Restores a backup set made by backup.sh.
#
#   restore.sh --from DIR [--target-db NAME] [--with-logs [--stop-at "YYYY-MM-DDTHH:MM:SS"]] [--no-files]
#              [--no-stop] [--yes]
#
#   --from        a full backup set directory (backups/<timestamp>)
#   --target-db   database to restore into (default: the one in the manifest). A different name restores side by
#                 side (useful for verifying backups) and leaves the running system alone.
#   --with-logs   replay backups/logs/*.trn taken after the full backup (point-in-time with --stop-at, UTC)
#   --no-files    do not restore data/files
#   --no-stop     do not stop api/worker when overwriting the live database (you stopped them yourself)
# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ERP_LOG_TAG=restore

from=""
target=""
with_logs=0
stop_at=""
with_files=1
stop_app=1
assume_yes=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from) from="$2"; shift ;;
    --target-db) target="$2"; shift ;;
    --with-logs) with_logs=1 ;;
    --stop-at) stop_at="$2"; shift ;;
    --no-files) with_files=0 ;;
    --no-stop) stop_app=0 ;;
    --yes|-y) assume_yes=1 ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done

load_env
[[ -n "${from}" && -f "${from}/manifest.txt" ]] || die "--from must be a backup set directory containing manifest.txt"
from="$(cd "${from}" && pwd)"
source_db="$(sed -n 's/^database=//p' "${from}/manifest.txt")"
created="$(sed -n 's/^created_utc=//p' "${from}/manifest.txt")"
target="${target:-${source_db}}"
validate_identifier "${source_db}"
validate_identifier "${target}"
if [[ -n "${stop_at}" ]]; then
  [[ "${stop_at}" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$ ]] || die "--stop-at must look like 2026-01-31T13:45:00"
  [[ "${with_logs}" == "1" ]] || die "--stop-at needs --with-logs"
fi
bak="${from}/${source_db}_full.bak"
[[ -f "${bak}" ]] || die "missing ${bak}"

# Integrity: the manifest's checksums must match before anything is touched.
if grep -q '^sha256 ' "${from}/manifest.txt"; then
  (cd "${from}" && sed -n 's/^sha256 //p' manifest.txt | sha256sum --quiet -c -) || die "checksum mismatch in ${from}"
fi

live_db="${ERP_DATABASE:-Erp}"
overwriting_live=0
[[ "${target}" == "${live_db}" ]] && overwriting_live=1

if [[ "${assume_yes}" != "1" ]]; then
  printf 'Restore %s (taken %s) into database [%s]%s. Existing data in [%s] is REPLACED. Type the database name to continue: ' \
    "${source_db}" "${created}" "${target}" "$([[ ${with_logs} == 1 ]] && echo ' with log replay')" "${target}" >&2
  read -r answer
  [[ "${answer}" == "${target}" ]] || die "aborted"
fi

stopped=0
restart_app() {
  if [[ "${stopped}" == "1" ]]; then
    log "starting api and worker"
    compose start api worker >/dev/null || warn "could not restart api/worker; run: docker compose ... up -d"
  fi
}
trap restart_app EXIT

if [[ "${overwriting_live}" == "1" && "${stop_app}" == "1" && -f "${ERP_HOME}/${ERP_COMPOSE_FILE}" && -f "${ERP_HOME}/.env" ]]; then
  log "stopping api and worker"
  compose stop api worker >/dev/null
  stopped=1
fi

server_dir="$(sql_backup_dir)/restore_$(date -u +%Y%m%dT%H%M%SZ)"
ensure_server_dir "${server_dir}"
push_backup_file "${bak}" "${server_dir}/full.bak"

# MOVE every file of the backup to the server's default data/log paths under the target name.
data_path="$(sql_query "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400));" | tr -d '\r' | sed 's:/*$::')"
log_path="$(sql_query "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(400));" | tr -d '\r' | sed 's:/*$::')"
moves=""
index=0
while IFS='|' read -r logical type; do
  [[ -n "${logical}" ]] || continue
  case "${type}" in
    L) path="${log_path}/${target}_log${index}.ldf" ;;
    *) path="${data_path}/${target}_${index}.mdf"; [[ ${index} -gt 0 ]] && path="${data_path}/${target}_${index}.ndf" ;;
  esac
  moves+="MOVE N'${logical//\'/\'\'}' TO N'${path}', "
  index=$(( index + 1 ))
done < <(sql_query "DECLARE @f TABLE (LogicalName nvarchar(128), PhysicalName nvarchar(260), Type char(1), FileGroupName nvarchar(128), Size numeric(20,0), MaxSize numeric(20,0), FileId bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0), UniqueId uniqueidentifier, ReadOnlyLSN numeric(25,0), ReadWriteLSN numeric(25,0), BackupSizeInBytes bigint, SourceBlockSize int, FileGroupId int, LogGroupGUID uniqueidentifier, DifferentialBaseLSN numeric(25,0), DifferentialBaseGUID uniqueidentifier, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32), SnapshotUrl nvarchar(360));
INSERT INTO @f EXEC (N'RESTORE FILELISTONLY FROM DISK = N''${server_dir}/full.bak''');
SELECT LogicalName, Type FROM @f ORDER BY FileId;" | tr -d '\r')
[[ -n "${moves}" ]] || die "could not read the file list of ${bak}"

recovery="RECOVERY"
[[ "${with_logs}" == "1" ]] && recovery="NORECOVERY"

log "restoring [${target}] from ${bak}"
sql_query "IF DB_ID(N'${target}') IS NOT NULL ALTER DATABASE [${target}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [${target}] FROM DISK = N'${server_dir}/full.bak' WITH ${moves} REPLACE, CHECKSUM, ${recovery}, STATS = 25;" >/dev/null

if [[ "${with_logs}" == "1" ]]; then
  logs_dir="$(dirname "${from}")/logs"
  applied=0
  stop_clause=""
  [[ -n "${stop_at}" ]] && stop_clause=", STOPAT = N'${stop_at}'"
  if [[ -d "${logs_dir}" ]]; then
    while IFS= read -r trn; do
      name="$(basename "${trn}")"
      stamp="${name#"${source_db}"_}"
      stamp="${stamp%.trn}"
      # Only logs taken after the full backup belong to this chain.
      [[ "${stamp}" > "${created}" ]] || continue
      push_backup_file "${trn}" "${server_dir}/${name}"
      log "applying ${name}"
      sql_query "RESTORE LOG [${target}] FROM DISK = N'${server_dir}/${name}' WITH NORECOVERY${stop_clause};" >/dev/null
      applied=$(( applied + 1 ))
      # The first log taken after the stop point contains it; later logs are past the point in time.
      if [[ -n "${stop_at}" ]]; then
        compact="${stop_at//[-:]/}Z"
        [[ "${stamp}" > "${compact}" ]] && break
      fi
    done < <(find "${logs_dir}" -maxdepth 1 -type f -name "${source_db}_*.trn" | sort)
  fi
  sql_query "RESTORE DATABASE [${target}] WITH RECOVERY;" >/dev/null
  log "applied ${applied} log backup(s)"
fi

sql_query "ALTER DATABASE [${target}] SET MULTI_USER;" >/dev/null
container="$(sql_container)"
if [[ -n "${container}" ]]; then
  docker exec "${container}" rm -rf "${server_dir}"
fi

if [[ "${with_files}" == "1" && "${overwriting_live}" == "1" && -f "${from}/files.tar.gz" ]]; then
  log "restoring data/files"
  mkdir -p "${ERP_HOME}/data"
  rm -rf "${ERP_HOME}/data/files.previous"
  [[ -d "${ERP_HOME}/data/files" ]] && mv "${ERP_HOME}/data/files" "${ERP_HOME}/data/files.previous"
  tar -C "${ERP_HOME}/data" -xzf "${from}/files.tar.gz"
  chown -R "${ERP_APP_UID}:${ERP_APP_UID}" "${ERP_HOME}/data/files" 2>/dev/null || warn "could not chown data/files to ${ERP_APP_UID}"
fi

log "restore of [${target}] completed"
