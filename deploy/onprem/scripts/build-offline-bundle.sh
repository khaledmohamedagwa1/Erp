#!/usr/bin/env bash
# Builds the offline on-prem bundle: images (docker save), compose file, nginx config, scripts, Windows scripts and
# the runbook, packed as dist/erp-onprem-<version>.tar.gz with SHA256SUMS. Run from a machine with internet access.
#
#   deploy/onprem/scripts/build-offline-bundle.sh --version 1.4.0 [--output DIR] [--skip-build] [--with-sql]
#
#   --skip-build   use existing local images erp-api:<version> and erp-migrator:<version>
#   --with-sql     also bundle the SQL Server image (needed when the customer has no SQL Server and no internet)
# Builds behind a TLS-inspecting proxy: export ERP_BUILD_CA_CERT=/path/to/proxy-ca.crt (passed as a BuildKit secret).
# The bundle contains no secrets: install.sh generates them on the customer's machine.
# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
ERP_LOG_TAG=bundle
umask 022

version=""
output=""
skip_build=0
with_sql=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift ;;
    --output) output="$2"; shift ;;
    --skip-build) skip_build=1 ;;
    --with-sql) with_sql=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done
[[ "${version}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] || die "--version is required (e.g. 1.4.0)"
require_cmd docker tar gzip sha256sum

repo_root="$(cd "${ERP_SCRIPTS_DIR}/../../.." && pwd)"
output="${output:-${repo_root}/deploy/onprem/dist}"
name="erp-onprem-${version}"
stage="${output}/${name}"
nginx_image="${ERP_NGINX_IMAGE:-nginx:1.27-alpine}"
sql_image="${ERP_SQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"

if [[ "${skip_build}" == "0" ]]; then
  build_args=()
  [[ -n "${ERP_BUILD_CA_CERT:-}" ]] && build_args+=(--secret "id=cacert,src=${ERP_BUILD_CA_CERT}")
  for var in HTTPS_PROXY HTTP_PROXY NO_PROXY; do
    [[ -n "${!var:-}" ]] && build_args+=(--build-arg "${var}=${!var}")
  done
  for image in api migrator; do
    log "building erp-${image}:${version}"
    DOCKER_BUILDKIT=1 docker build "${build_args[@]}" ${ERP_BUILD_NETWORK:+--network "${ERP_BUILD_NETWORK}"} \
      --label "org.opencontainers.image.version=${version}" \
      -f "${repo_root}/deploy/docker/Dockerfile.${image}" -t "erp-${image}:${version}" "${repo_root}"
  done
fi

rm -rf "${stage}"
mkdir -p "${stage}/images" "${stage}/scripts" "${stage}/nginx" "${stage}/windows" "${stage}/docs"
images=("erp-api:${version}" "erp-migrator:${version}" "${nginx_image}")
docker image inspect "${nginx_image}" >/dev/null 2>&1 || docker pull "${nginx_image}"
if [[ "${with_sql}" == "1" ]]; then
  docker image inspect "${sql_image}" >/dev/null 2>&1 || docker pull "${sql_image}"
  images+=("${sql_image}")
fi
for image in "${images[@]}"; do
  docker image inspect "${image}" >/dev/null 2>&1 || die "image ${image} not found"
  file="$(printf '%s' "${image}" | tr '/:' '__').tar.gz"
  log "saving ${image}"
  docker save "${image}" | gzip -1 > "${stage}/images/${file}"
done

src="${repo_root}/deploy/onprem"
cp "${src}/docker-compose.onprem.yml" "${src}/env.example" "${stage}/"
cp "${src}"/nginx/* "${stage}/nginx/"
cp "${src}"/scripts/*.sh "${stage}/scripts/"
cp "${src}"/windows/* "${stage}/windows/" 2>/dev/null || true
cp "${repo_root}/docs/operations/runbook.md" "${stage}/docs/" 2>/dev/null || warn "runbook not found"
chmod 755 "${stage}"/scripts/*.sh
printf '%s\n' "${version}" > "${stage}/VERSION"
sums="$(cd "${stage}" && find . -type f -print0 | sort -z | xargs -0 sha256sum)"
printf '%s\n' "${sums}" > "${stage}/SHA256SUMS"

tar -C "${output}" -czf "${output}/${name}.tar.gz" "${name}"
(cd "${output}" && sha256sum "${name}.tar.gz" > "${name}.tar.gz.sha256")
rm -rf "${stage}"
log "bundle: ${output}/${name}.tar.gz"
printf '%s\n' "${output}/${name}.tar.gz"
