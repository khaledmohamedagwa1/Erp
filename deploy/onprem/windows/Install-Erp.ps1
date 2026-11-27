<#
.SYNOPSIS
  Installs the ERP on Windows Server as Windows Services (no Docker).

.DESCRIPTION
  UNTESTED: written against the documented APIs but not yet executed on a Windows host. Review before use and
  report problems. See README.md in this folder.

  Expects a package produced by:
    dotnet publish backend/src/Host/Erp.Api/Erp.Api.csproj           -c Release -r win-x64 --self-contained -o pkg\api
    dotnet publish backend/src/Host/Erp.Migrator/Erp.Migrator.csproj -c Release -r win-x64 --self-contained -o pkg\migrator

  What it does (idempotent; existing secrets are kept):
    1. Copies the binaries to <InstallDir>\app and <InstallDir>\migrator.
    2. Generates the token signing key and platform API key into <DataDir>\secrets (ACL: SYSTEM, Administrators,
       and the service account only) and stores the connection string there. Nothing secret goes on a command line
       or into the registry.
    3. Runs the migrator (explicit schema step, ADR-015).
    4. Registers and starts the ErpApi service (Erp:Role=all, or api + a separate ErpWorker service with -SplitWorker).

.EXAMPLE
  .\Install-Erp.ps1 -PackageDir D:\erp-1.4.0 -ConnectionString "Server=sql01;Database=Erp;Integrated Security=True;TrustServerCertificate=True" `
                    -PublicOrigin https://erp.contoso.local -CertificateThumbprint 3A1B... -HttpsPort 443
#>
#Requires -Version 7.2
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $PackageDir,
    [Parameter(Mandatory)] [string] $ConnectionString,
    [Parameter(Mandatory)] [ValidatePattern('^https://[^/]+$')] [string] $PublicOrigin,
    [string] $InstallDir = 'C:\Program Files\Erp',
    [string] $DataDir = 'C:\ProgramData\Erp',
    # Kestrel serves HTTPS itself with a certificate from LocalMachine\My. Omit when IIS/ARR terminates TLS (then
    # Kestrel listens on http://127.0.0.1:<HttpPort> only and -TrustProxy is required).
    [string] $CertificateThumbprint,
    [int] $HttpsPort = 443,
    [int] $HttpPort = 5080,
    [switch] $TrustProxy,
    [string] $ServiceAccount = 'NT AUTHORITY\NetworkService',
    [switch] $SplitWorker
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Message) { Write-Host "[install] $Message" }

function New-RandomSecret([int] $Length = 64) {
    $alphabet = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    -join (1..$Length | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })
}

function Protect-Path([string] $Path, [string] $Account) {
    # Owner-only ACL: no inheritance, full control for SYSTEM and Administrators, read for the service account.
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($id in 'NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Account, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $Path -AclObject $acl
}

function Set-ServiceEnvironment([string] $Name, [hashtable] $Values) {
    # Per-service environment (REG_MULTI_SZ). Non-secret settings only; secrets are read from ERP_SECRETS_DIR files.
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $lines = $Values.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }
    New-ItemProperty -Path $key -Name Environment -PropertyType MultiString -Value ([string[]]$lines) -Force | Out-Null
}

function Install-ErpService([string] $Name, [string] $DisplayName, [string] $Role, [hashtable] $Environment) {
    $exe = Join-Path $InstallDir 'app\Erp.Api.exe'
    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name $Name -Force }
    }
    else {
        Write-Step "registering service $Name"
        New-Service -Name $Name -DisplayName $DisplayName -BinaryPathName "`"$exe`"" -StartupType Automatic `
            -Description "Modular ERP ($Role)" | Out-Null
    }
    & sc.exe config $Name obj= $ServiceAccount | Out-Null
    # Restart on failure: 5 s, 30 s, then every 2 minutes; reset the counter after a day.
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/30000/restart/120000 | Out-Null
    $envValues = $Environment.Clone()
    $envValues['Erp__Role'] = $Role
    Set-ServiceEnvironment -Name $Name -Values $envValues
}

$PackageDir = (Resolve-Path $PackageDir).Path
foreach ($required in 'api\Erp.Api.exe', 'migrator\Erp.Migrator.exe') {
    if (-not (Test-Path (Join-Path $PackageDir $required))) { throw "Package is missing $required (see the publish commands in the help)." }
}
if (-not $CertificateThumbprint -and -not $TrustProxy) {
    throw 'Give -CertificateThumbprint (Kestrel serves HTTPS) or -TrustProxy (IIS/ARR terminates TLS in front of Kestrel).'
}
if ($CertificateThumbprint -and -not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) {
    throw "Certificate $CertificateThumbprint not found in LocalMachine\My."
}

# 1. Binaries (services are stopped first so files are not locked).
foreach ($svc in 'ErpApi', 'ErpWorker') {
    if (Get-Service -Name $svc -ErrorAction SilentlyContinue) { Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue }
}
Write-Step "copying binaries to $InstallDir"
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir 'app'), (Join-Path $InstallDir 'migrator') | Out-Null
Copy-Item -Path (Join-Path $PackageDir 'api\*') -Destination (Join-Path $InstallDir 'app') -Recurse -Force
Copy-Item -Path (Join-Path $PackageDir 'migrator\*') -Destination (Join-Path $InstallDir 'migrator') -Recurse -Force

# 2. Secrets (key-per-file: file name = configuration key with ':' written as '__').
$secrets = Join-Path $DataDir 'secrets'
New-Item -ItemType Directory -Force -Path $secrets, (Join-Path $DataDir 'files'), (Join-Path $DataDir 'logs'), (Join-Path $DataDir 'backups') | Out-Null
Protect-Path -Path $secrets -Account $ServiceAccount
foreach ($writable in 'files', 'logs') {
    $path = Join-Path $DataDir $writable
    $acl = Get-Acl $path
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($ServiceAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $path -AclObject $acl
}
$signingKey = Join-Path $secrets 'Auth__SigningKeyPem'
if (-not (Test-Path $signingKey)) {
    Write-Step 'generating the token signing key (RSA 3072)'
    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    try { Set-Content -Path $signingKey -Value $rsa.ExportPkcs8PrivateKeyPem() -NoNewline -Encoding ascii } finally { $rsa.Dispose() }
}
$apiKey = Join-Path $secrets 'Platform__ApiKey'
if (-not (Test-Path $apiKey)) {
    Write-Step 'generating the platform API key'
    Set-Content -Path $apiKey -Value (New-RandomSecret 64) -NoNewline -Encoding ascii
}
Set-Content -Path (Join-Path $secrets 'ConnectionStrings__Erp') -Value $ConnectionString -NoNewline -Encoding utf8

# 3. Schema.
Write-Step 'applying database migrations'
$env:ERP_SECRETS_DIR = $secrets
$env:DOTNET_ENVIRONMENT = 'Production'
& (Join-Path $InstallDir 'migrator\Erp.Migrator.exe') migrate
if ($LASTEXITCODE -eq 3) { throw 'The release contains destructive schema changes; review and run Upgrade-Erp.ps1 -AllowDestructive.' }
if ($LASTEXITCODE -ne 0) { throw "Migration failed (exit $LASTEXITCODE). Nothing was started." }

# 4. Services.
$common = @{
    ASPNETCORE_ENVIRONMENT          = 'Production'
    ERP_SECRETS_DIR                 = $secrets
    Auth__SecureCookies             = 'true'
    Database__AutoMigrate           = 'false'
    Hosting__CorsAllowedOrigins__0  = $PublicOrigin
    # A service has no console: add a rolling JSON log file (Serilog.Sinks.File ships with the app).
    Serilog__Using__0                     = 'Serilog.Sinks.File'
    Serilog__WriteTo__0__Name             = 'File'
    Serilog__WriteTo__0__Args__path       = (Join-Path $DataDir 'logs\erp-.json')
    Serilog__WriteTo__0__Args__rollingInterval = 'Day'
    Serilog__WriteTo__0__Args__retainedFileCountLimit = '30'
    Serilog__WriteTo__0__Args__formatter  = 'Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact'
}
if ($CertificateThumbprint) {
    $common['Kestrel__Endpoints__Https__Url'] = "https://*:$HttpsPort"
    $common['Kestrel__Endpoints__Https__Certificate__Subject'] = (Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint").GetNameInfo('SimpleName', $false)
    $common['Kestrel__Endpoints__Https__Certificate__Store'] = 'My'
    $common['Kestrel__Endpoints__Https__Certificate__Location'] = 'LocalMachine'
    $common['Kestrel__Endpoints__Http__Url'] = "http://127.0.0.1:$HttpPort"
}
else {
    $common['Kestrel__Endpoints__Http__Url'] = "http://127.0.0.1:$HttpPort"
}
if ($TrustProxy) {
    $common['Hosting__TrustForwardedHeaders'] = 'true'
    $common['Hosting__KnownProxies__0'] = '127.0.0.1'
    $common['Hosting__KnownProxies__1'] = '::1'
}

if ($SplitWorker) {
    Install-ErpService -Name 'ErpApi' -DisplayName 'ERP API' -Role 'api' -Environment $common
    $workerEnv = $common.Clone()
    $workerEnv.Remove('Kestrel__Endpoints__Https__Url'); $workerEnv.Remove('Kestrel__Endpoints__Https__Certificate__Subject')
    $workerEnv.Remove('Kestrel__Endpoints__Https__Certificate__Store'); $workerEnv.Remove('Kestrel__Endpoints__Https__Certificate__Location')
    $workerEnv['Kestrel__Endpoints__Http__Url'] = "http://127.0.0.1:$($HttpPort + 1)"
    Install-ErpService -Name 'ErpWorker' -DisplayName 'ERP background worker' -Role 'worker' -Environment $workerEnv
    Start-Service ErpApi, ErpWorker
}
else {
    Install-ErpService -Name 'ErpApi' -DisplayName 'ERP' -Role 'all' -Environment $common
    Start-Service ErpApi
}

# Firewall for direct Kestrel HTTPS.
if ($CertificateThumbprint -and -not (Get-NetFirewallRule -DisplayName 'ERP HTTPS' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'ERP HTTPS' -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Action Allow | Out-Null
}

# Smoke test against the local HTTP listener.
$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 5
    try { $ready = (Invoke-WebRequest -Uri "http://127.0.0.1:$HttpPort/health/ready" -UseBasicParsing -TimeoutSec 5).Content } catch { $ready = $null }
} until ($ready -eq 'Healthy' -or (Get-Date) -gt $deadline)
if ($ready -ne 'Healthy') { throw 'The service did not become healthy; check the Windows event log (source Erp.Api) and the service status.' }

Write-Step "installed. Open $PublicOrigin"
Write-Step "first tenant: `$env:ERP_SECRETS_DIR='$secrets'; `$env:ERP_INIT_ADMIN_PASSWORD='<password>'; & '$InstallDir\migrator\Erp.Migrator.exe' init-tenant --code ACME ..."
Write-Step "schedule Backup-Erp.ps1 (Task Scheduler) and keep a copy of $secrets off this machine."
