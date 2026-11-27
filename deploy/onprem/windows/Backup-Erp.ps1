<#
.SYNOPSIS
  Backs up the ERP database (full or log) and file storage on Windows, with retention.

.DESCRIPTION
  UNTESTED: written against the documented APIs but not yet executed on a Windows host. See README.md.

  Uses sqlcmd (ODBC tools 18) with Windows authentication by default, or SQL authentication with the password in
  the SQLCMDPASSWORD environment variable (never on the command line). The SQL Server service account needs write
  access to -BackupDir (use a UNC path when SQL Server runs on another machine).

  Layout: <BackupDir>\<UTC timestamp>\{<db>_full.bak, files.zip, manifest.txt} and <BackupDir>\logs\<db>_<UTC>.trn

.EXAMPLE
  # Task Scheduler: nightly full, log every 15 minutes
  .\Backup-Erp.ps1 -SqlServer sql01 -Database Erp -BackupDir \\backup01\erp -Type Full
  .\Backup-Erp.ps1 -SqlServer sql01 -Database Erp -BackupDir \\backup01\erp -Type Log
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SqlServer,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,99}$')] [string] $Database = 'Erp',
    [Parameter(Mandatory)] [string] $BackupDir,
    [ValidateSet('Full', 'Log')] [string] $Type = 'Full',
    [string] $DataDir = 'C:\ProgramData\Erp',
    [int] $RetentionDays = 14,
    [string] $SqlUser
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Write-Step([string] $Message) { Write-Host "[backup] $Message" }

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'sqlcmd not found (install the Microsoft ODBC Driver 18 and sqlcmd tools).' }

function Invoke-Sql([string] $Query) {
    $auth = if ($SqlUser) { @('-U', $SqlUser) } else { @('-E') }
    $output = & sqlcmd -S $SqlServer @auth -C -b -h -1 -W -Q "SET NOCOUNT ON; $Query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQL failed: $($output -join [Environment]::NewLine)" }
    return $output
}

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$edition = [int]((Invoke-Sql "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);") | Select-Object -First 1)
$compression = if ($edition -eq 4) { '' } else { 'COMPRESSION, ' }   # Express cannot compress

if ($Type -eq 'Log') {
    $model = (Invoke-Sql "SELECT recovery_model_desc FROM sys.databases WHERE name = N'$Database';") | Select-Object -First 1
    if ($model -eq 'SIMPLE') { throw "$Database uses the SIMPLE recovery model; log backups need FULL." }
    $dir = Join-Path $BackupDir 'logs'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir "${Database}_$stamp.trn"
    Invoke-Sql "BACKUP LOG [$Database] TO DISK = N'$file' WITH ${compression}CHECKSUM, INIT;" | Out-Null
    Write-Step "log backup written: $file"
}
else {
    $set = Join-Path $BackupDir $stamp
    New-Item -ItemType Directory -Force -Path $set | Out-Null
    try {
        $file = Join-Path $set "${Database}_full.bak"
        Invoke-Sql "BACKUP DATABASE [$Database] TO DISK = N'$file' WITH ${compression}CHECKSUM, INIT;" | Out-Null
        Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$file' WITH CHECKSUM;" | Out-Null
        $files = Join-Path $DataDir 'files'
        if (Test-Path $files) { Compress-Archive -Path (Join-Path $files '*') -DestinationPath (Join-Path $set 'files.zip') -Force -ErrorAction SilentlyContinue }
        $manifest = @("database=$Database", "created_utc=$stamp", "sql_edition=$edition")
        $manifest += Get-ChildItem $set -File | ForEach-Object { "sha256 $((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
        Set-Content -Path (Join-Path $set 'manifest.txt') -Value $manifest -Encoding utf8
        Write-Step "full backup verified: $set"
    }
    catch {
        Remove-Item -Recurse -Force $set -ErrorAction SilentlyContinue
        throw
    }
}

# Retention (the newest full set is always kept).
if ($RetentionDays -gt 0) {
    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    $sets = Get-ChildItem $BackupDir -Directory | Where-Object Name -Match '^\d{8}T\d{6}Z$' | Sort-Object Name
    $sets | Select-Object -SkipLast 1 | Where-Object LastWriteTime -lt $cutoff | ForEach-Object {
        Remove-Item -Recurse -Force $_.FullName; Write-Step "retention: removed $($_.FullName)"
    }
    $logs = Join-Path $BackupDir 'logs'
    if (Test-Path $logs) {
        Get-ChildItem $logs -Filter '*.trn' | Where-Object LastWriteTime -lt $cutoff | Remove-Item -Force
    }
}
