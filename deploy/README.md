# Deployment

| Folder | Purpose | Status |
|---|---|---|
| `docker/` | `Dockerfile.api`, `Dockerfile.migrator` (production images) and `docker-compose.yml` for developers / evaluation | Images built and run here (read-only root filesystem, non-root, health check) |
| `onprem/` | Production stack for customer servers: `docker-compose.onprem.yml`, nginx TLS proxy, `scripts/` (install, upgrade, backup, restore, healthcheck, offline bundle) | Verified end to end: bundle → install → upgrade → backup/restore |
| `onprem/windows/` | Windows Server without Docker: Windows Services + PowerShell scripts | Written, parse-checked, **untested on Windows** |
| `k8s/helm/erp/` | Helm chart: api (HPA, PDB), worker, migrator hook Job, ingress, existing-Secret mounts | `helm lint --strict` and `helm template` pass; not deployed to a cluster |

**Build the images** from the repository root:

```bash
docker build -f deploy/docker/Dockerfile.api -t erp-api:local .
docker build -f deploy/docker/Dockerfile.migrator -t erp-migrator:local .
```

**Build the offline bundle:** `deploy/onprem/scripts/build-offline-bundle.sh --version 1.0.0`, which writes
`deploy/onprem/dist/`.

**Operating an installation:** see `docs/operations/runbook.md`.

**Architecture:** see `docs/architecture/04-deployment.md` §15.3 *As built*.

No secret is stored in this folder; install scripts generate them on the target machine.
