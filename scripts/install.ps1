#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install PbiBridgeApi as a Windows Service.

.DESCRIPTION
    - Verifies .NET 8 is installed
    - Publishes the ASP.NET Core app
    - Creates the Windows Service via NSSM (preferred) or sc.exe
    - Configures ADMIN_API_KEY environment variable on the service
    - Starts the service and verifies /health returns 200

.PARAMETER AdminApiKey
    Required. The admin API key for managing client keys.

.PARAMETER Port
    Service port. Default: 8090. DA-012: DO NOT CHANGE.

.PARAMETER InstallDir
    Publish output directory. Default: C:\Services\PbiBridgeApi\

.PARAMETER ServiceName
    Windows Service name. Default: PbiBridgeApi

.EXAMPLE
    .\scripts\install.ps1 -AdminApiKey "your-secret-key"
    .\scripts\install.ps1 -AdminApiKey "your-secret-key" -InstallDir "D:\Services\PbiBridgeApi\"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AdminApiKey,

    [Parameter(Mandatory = $false)]
    [int]$Port = 8090,

    [Parameter(Mandatory = $false)]
    [string]$InstallDir = "C:\Services\PbiBridgeApi\",

    [Parameter(Mandatory = $false)]
    [string]$ServiceName = "PbiBridgeApi"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== PbiBridgeApi Install ===" -ForegroundColor Cyan

# --- 1. Verify .NET 8 ---
Write-Host "[1/6] Checking .NET 8..." -ForegroundColor Yellow
try {
    $dotnetVersion = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0 -or $dotnetVersion -notmatch '^8\.') {
        Write-Error ".NET 8 SDK/Runtime is required. Found: '$dotnetVersion'. Install from https://dotnet.microsoft.com/download/dotnet/8.0"
        exit 1
    }
    Write-Host "  .NET version: $dotnetVersion OK" -ForegroundColor Green
} catch {
    Write-Error ".NET is not installed or not in PATH. Install .NET 8 from https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

# --- 2. Determine project root ---
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$CsprojPath = Join-Path $ProjectRoot "PbiBridgeApi\PbiBridgeApi.csproj"

if (-not (Test-Path $CsprojPath)) {
    Write-Error "PbiBridgeApi.csproj not found at: $CsprojPath"
    exit 1
}

# --- 3. Publish ---
Write-Host "[2/6] Publishing to $InstallDir..." -ForegroundColor Yellow
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

& dotnet publish $CsprojPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $InstallDir `
    /p:PublishSingleFile=false 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit $LASTEXITCODE)"
    exit 1
}
Write-Host "  Published OK" -ForegroundColor Green

# --- 4. Install service ---
Write-Host "[3/6] Installing Windows Service '$ServiceName'..." -ForegroundColor Yellow
$ExePath = Join-Path $InstallDir "PbiBridgeApi.exe"

if (-not (Test-Path $ExePath)) {
    Write-Error "Published executable not found at: $ExePath"
    exit 1
}

# Check if NSSM is available
$NssmPath = Get-Command "nssm" -ErrorAction SilentlyContinue
$UseNssm = $null -ne $NssmPath

# Remove existing service if present
$ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($ExistingService) {
    Write-Host "  Removing existing service '$ServiceName'..." -ForegroundColor DarkYellow
    if ($UseNssm) {
        & nssm stop $ServiceName 2>&1 | Out-Null
        & nssm remove $ServiceName confirm 2>&1 | Out-Null
    } else {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    Start-Sleep -Seconds 2
}

if ($UseNssm) {
    Write-Host "  Using NSSM to create service..." -ForegroundColor Gray
    & nssm install $ServiceName $ExePath
    & nssm set $ServiceName AppDirectory $InstallDir
    & nssm set $ServiceName DisplayName "PBI Bridge API"
    & nssm set $ServiceName Description "Power BI Bridge API - ASP.NET Core .NET 8 Windows Service (ADR-002)"
    & nssm set $ServiceName Start SERVICE_AUTO_START
    & nssm set $ServiceName ObjectName LocalSystem ""
    # DA-017: ADMIN_API_KEY via env var — never hardcode in code
    & nssm set $ServiceName AppEnvironmentExtra "ADMIN_API_KEY=$AdminApiKey" "ASPNETCORE_ENVIRONMENT=Production"
} else {
    Write-Host "  Using sc.exe to create service (NSSM not found)..." -ForegroundColor Gray
    & sc.exe create $ServiceName `
        binPath= "`"$ExePath`"" `
        start= auto `
        DisplayName= "PBI Bridge API" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "sc.exe create failed"
        exit 1
    }
    & sc.exe description $ServiceName "Power BI Bridge API - ASP.NET Core .NET 8 Windows Service (ADR-002)" | Out-Null

    # Set env vars via registry for non-NSSM installs
    # DA-017: set ADMIN_API_KEY in the service's registry environment
    $RegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $EnvValue = "ADMIN_API_KEY=$AdminApiKey`0ASPNETCORE_ENVIRONMENT=Production`0"
    Set-ItemProperty -Path $RegPath -Name "Environment" -Value ([System.Text.Encoding]::Unicode.GetBytes($EnvValue)) -Type MultiString
    Write-Host "  Environment variables set in registry." -ForegroundColor Gray
}

Write-Host "  Service created OK" -ForegroundColor Green

# --- 5. Start service ---
Write-Host "[4/6] Starting service '$ServiceName'..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -ne "Running") {
    Write-Error "Service did not start. Status: $($svc.Status). Check Event Viewer for details."
    exit 1
}
Write-Host "  Service status: $($svc.Status)" -ForegroundColor Green

# --- 6. Health check ---
Write-Host "[5/6] Verifying GET http://localhost:$Port/health..." -ForegroundColor Yellow
$MaxRetries = 10
$Delay = 2
$Success = $false

for ($i = 1; $i -le $MaxRetries; $i++) {
    try {
        $Response = Invoke-WebRequest -Uri "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 5
        if ($Response.StatusCode -eq 200) {
            $Success = $true
            break
        }
    } catch {
        Write-Host "  Attempt $i/$MaxRetries — waiting ${Delay}s..." -ForegroundColor DarkGray
    }
    Start-Sleep -Seconds $Delay
}

if (-not $Success) {
    Write-Error "Health check failed after $MaxRetries attempts. Service may not be running correctly."
    exit 1
}
Write-Host "  Health check: 200 OK" -ForegroundColor Green

Write-Host "[6/6] Done!" -ForegroundColor Cyan
Write-Host ""
Write-Host "Service '$ServiceName' is running on port $Port." -ForegroundColor Green
Write-Host "Admin API Key is configured via environment variable ADMIN_API_KEY." -ForegroundColor Gray
Write-Host "NEVER share this key. It controls client API key management." -ForegroundColor Yellow
