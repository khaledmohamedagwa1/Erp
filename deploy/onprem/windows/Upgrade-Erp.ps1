<#
.SYNOPSIS
  Upgrades a Windows-service installation of the ERP to the binaries in -PackageDir.

.DESCRIPTION
  UNTESTED: written against the documented APIs but not yet executed on a Windows host. See README.md.

  Steps: pre-flight -> mandatory full backup (Backup-Erp.ps1) -> stop worker/api -> keep the previous binaries in
  <InstallDir>\previous -> copy new binaries -> migrator (refuses destructive changes without -AllowDestructive;
  then the previous binaries are put back) -> start -> smoke test.

.EXAMPLE
  .\Upgrade-Erp.ps1 -PackageDir D:\erp-1.5.0 -SqlServer sql01 -Database Erp -BackupDir E:\ErpBackups
#>
#Requires -Version 7.2
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDir,
    [Parameter(Mandatory)] [string] $SqlServer,
    [string] $Database = 'Erp',
    [Parameter(Mandatory)] [string] $BackupDir,
    [string] $InstallDir = 'C:\Program Files\Erp',
    [string] $DataDir = 'C:\ProgramData\Erp',
    [int] $HttpPort = 5080,
    [switch] $AllowDestructive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Write-Step([string] $Message) { Write-Host "[upgrade] $Message" }

$PackageDir = (Resolve-Path $PackageDir).Path
foreach ($required in 'api\Erp.Api.exe', 'migrator\Erp.Migrator.exe') {
    if (-not (Test-Path (Join-Path $PackageDir $required))) { throw "Package is missing $required." }
}
$services = @('ErpWorker', 'ErpApi') | Where-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue }
if (-not $services) { throw 'No ERP service is installed; run Install-Erp.ps1 first.' }
$drive = (Get-Item $InstallDir).PSDrive
if ($drive.Free -lt 2GB) { throw "Less than 2 GB free on $($drive.Name):" }

# Mandatory backup: the upgrade does not start without one.
Write-Step 'taking the pre-upgrade backup'
& (Join-Path $PSScriptRoot 'Backup-Erp.ps1') -SqlServer $SqlServer -Database $Database -BackupDir $BackupDir -DataDir $DataDir -Type Full
if (-not $?) { throw 'Backup failed; the upgrade did not start.' }

Write-Step 'stopping services'
foreach ($svc in $services) { Stop-Service -Name $svc -Force }

$previous = Join-Path $InstallDir 'previous'
Remove-Item -Recurse -Force $previous -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $previous | Out-Null
Copy-Item -Path (Join-Path $InstallDir 'app'), (Join-Path $InstallDir 'migrator') -Destination $previous -Recurse -Force

function Restore-PreviousBinaries {
    foreach ($part in 'app', 'migrator') {
        Remove-Item -Recurse -Force (Join-Path $InstallDir $part)
        Copy-Item -Path (Join-Path $previous $part) -Destination $InstallDir -Recurse -Force
    }
}

Write-Step 'copying new binaries'
foreach ($pair in @(@('api', 'app'), @('migrator', 'migrator'))) {
    $target = Join-Path $InstallDir $pair[1]
    Remove-Item -Recurse -Force $target
    Copy-Item -Path (Join-Path $PackageDir $pair[0]) -Destination $target -Recurse -Force
}

Write-Step 'applying database migrations'
$env:ERP_SECRETS_DIR = Join-Path $DataDir 'secrets'
$env:DOTNET_ENVIRONMENT = 'Production'
$migrateArgs = @('migrate')
if ($AllowDestructive) { $migrateArgs += '--allow-destructive' }
& (Join-Path $InstallDir 'migrator\Erp.Migrator.exe') @migrateArgs
$code = $LASTEXITCODE
if ($code -ne 0) {
    Restore-PreviousBinaries
    foreach ($svc in 'ErpApi', 'ErpWorker') {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) { Start-Service -Name $svc }
    }
    if ($code -eq 3) { throw 'Destructive schema changes need -AllowDestructive. Nothing was changed; the previous version is running.' }
    throw "Migration failed (exit $code). The previous binaries are running; database backup is in $BackupDir."
}

Write-Step 'starting services'
foreach ($svc in 'ErpApi', 'ErpWorker') {
    if (Get-Service -Name $svc -ErrorAction SilentlyContinue) { Start-Service -Name $svc }
}

$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 5
    try { $ready = (Invoke-WebRequest -Uri "http://127.0.0.1:$HttpPort/health/ready" -UseBasicParsing -TimeoutSec 5).Content } catch { $ready = $null }
} until ($ready -eq 'Healthy' -or (Get-Date) -gt $deadline)
if ($ready -ne 'Healthy') {
    throw "Smoke test failed. Roll back: stop the services, copy $previous\* back to $InstallDir, restore the database from $BackupDir, start the services."
}
Write-Step 'upgrade completed'
