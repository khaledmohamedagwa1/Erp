#!/usr/bin/env bash
# Build + all test projects (sequential, integration with 2 threads to limit memory). Exits non-zero on any failure.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")/../backend"
docker info >/dev/null 2>&1 || { (dockerd >/tmp/dockerd.log 2>&1 &); sleep 5; }
docker start erp-sql >/dev/null 2>&1 || true
export ERP_TEST_SQL="Server=localhost,1433;User Id=sa;Password=Dev_Passw0rd!;TrustServerCertificate=True"
out=$(dotnet build Erp.slnx 2>&1) || { echo "$out" | grep -E " error " | sort -u | head -20; echo BUILD FAILED; exit 1; }
echo "$out" | grep -E "Warning\(s\)|Error\(s\)" | tail -2
total=0
for p in Erp.UnitTests Erp.ArchitectureTests Erp.IntegrationTests; do
  extra=""; [ "$p" = Erp.IntegrationTests ] && extra="-- --max-threads 2"
  res=$(dotnet test --project tests/$p --no-build $extra 2>&1) || { echo "$res" | grep -E "^failed|Exception :|total:|failed:|error" | head -30; echo "TESTS FAILED in $p"; exit 1; }
  n=$(echo "$res" | grep -E "  total:" | tail -1 | awk '{print $2}'); echo "$p: $n passed"; total=$((total+n))
done
echo "ALL PASSED: $total tests"
