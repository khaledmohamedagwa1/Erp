# Windows Server install (no Docker)

> **Status: UNTESTED.** These scripts parse cleanly in PowerShell 7.5. The Linux on-prem path and the API's
> `UseWindowsService()` hook are verified, but the scripts themselves have never run on a Windows host.
> Try them on a staging server first, and report anything that needs fixing.

Use this path when the customer runs Windows Server without Docker. The Linux/Docker bundle (`../scripts`) is
the primary, tested deployment.

## Requirements

- Windows Server 2019 or later, PowerShell 7.2 or later, run as Administrator.
- SQL Server 2019 or later (Express is fine for small installations).
  Install the ODBC Driver 18 and `sqlcmd` on the app server for `Backup-Erp.ps1`.
- A TLS certificate in `LocalMachine\My`, unless IIS terminates TLS.

## Build the package (on a build machine)

```powershell
dotnet publish backend/src/Host/Erp.Api/Erp.Api.csproj           -c Release -r win-x64 --self-contained -o pkg\api
dotnet publish backend/src/Host/Erp.Migrator/Erp.Migrator.csproj -c Release -r win-x64 --self-contained -o pkg\migrator
```

Self-contained means the server does not need the .NET runtime installed.

## Install

```powershell
.\Install-Erp.ps1 -PackageDir D:\pkg -PublicOrigin https://erp.contoso.local `
  -ConnectionString "Server=sql01;Database=Erp;Integrated Security=True;TrustServerCertificate=True" `
  -CertificateThumbprint <thumbprint> -HttpsPort 443
```

What the script does:

- **Secrets:** generates them on the server, in `C:\ProgramData\Erp\secrets` (key-per-file, same as the Linux
  `/run/secrets`). Only SYSTEM, Administrators and the service account can read them.
- **Services:** registers the `ErpApi` Windows Service with `Erp:Role=all`. `-SplitWorker` registers `ErpApi`
  (role `api`) and `ErpWorker` (role `worker`) instead. Both run as `NetworkService` by default; for SQL Server
  Windows authentication, grant that account (or the `-ServiceAccount` you choose) a database login.
- **Logs:** written to `C:\ProgramData\Erp\logs\erp-YYYYMMDD.json`, rolling daily and kept for 30 days.
- **First tenant:** created afterwards with the migrator; the command is printed at the end of the install.

## TLS: Kestrel or IIS/ARR

| Option | When | How |
|---|---|---|
| **Kestrel HTTPS (default)** | Dedicated server, nothing else on 443 | `-CertificateThumbprint`; Kestrel binds `https://*:443` and loads the certificate from the store. HSTS and the security headers come from the app. Renew the certificate in the store, then restart the service. |
| **IIS + ARR reverse proxy** | IIS already hosts other sites, or the customer's policy needs IIS | Install ARR 3 and URL Rewrite, then enable the proxy (*Server Proxy Settings*). Add a site with the certificate, and a rewrite rule `(.*)` → `http://127.0.0.1:5080/{R:1}` that preserves the host header. Run `Install-Erp.ps1 -TrustProxy` (no thumbprint). Kestrel then listens on 127.0.0.1 only and trusts `X-Forwarded-*` from loopback. Set the request size limit in IIS to at least 10 MB. |

Do not use the in-process IIS hosting model (`AspNetCoreModuleV2` in-process). The worker role must run as a
long-lived service, independent of IIS application-pool recycling.

## Upgrade and backup

```powershell
.\Upgrade-Erp.ps1 -PackageDir D:\pkg-1.5.0 -SqlServer sql01 -Database Erp -BackupDir \\backup01\erp
.\Backup-Erp.ps1  -SqlServer sql01 -Database Erp -BackupDir \\backup01\erp -Type Full   # nightly
.\Backup-Erp.ps1  -SqlServer sql01 -Database Erp -BackupDir \\backup01\erp -Type Log    # every 15 min
```

- **Upgrade:** always takes a full backup first, and the migrator refuses destructive schema changes without
  `-AllowDestructive`. If the migration fails, the previous binaries are restored and restarted.
- **Restore:** use SSMS or `RESTORE DATABASE ... WITH NORECOVERY` + `RESTORE LOG ...`, following
  `docs/operations/runbook.md` (*Restore*). There is no `Restore-Erp.ps1` yet.
