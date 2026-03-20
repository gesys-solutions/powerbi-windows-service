#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Update PbiBridgeApi Windows Service to the latest build.

.DESCRIPTION
    - Stops the service
    - Publishes new binaries to a temp directory
    - Copies new files over the install directory
    - Restarts the service
    - Verifies /health returns 200

.PARAMETER ServiceName
    Windows Service name. Default: PbiBridgeApi

.PARAMETER InstallDir
    Service install directory. Default: C:\Services\PbiBridgeApi\

.PARAMETER Port
    Service port. Default: 8090.

.EXAMPLE
    .\scripts\update.ps1
    .\scripts\update.ps1 -InstallDir "D:\Services\PbiBridgeApi\"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ServiceName = "PbiBridgeApi",

    [Parameter(Mandatory = $false)]
    [string]$InstallDir = "C:\Services\PbiBridgeApi\",

    [Parameter(Mandatory = $false)]
    [int]$Port = 8090
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== PbiBridgeApi Update ===" -ForegroundColor Cyan

# --- 1. Verify .NET 8 ---
Write-Host "[1/6] Checking .NET 8..." -ForegroundColor Yellow
try {
    $dotnetVersion = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0 -or $dotnetVersion -notmatch '^8\.') {
        Write-Error ".NET 8 is required. Found: '$dotnetVersion'."
        exit 1
    }
    Write-Host "  .NET version: $dotnetVersion OK" -ForegroundColor Green
} catch {
    Write-Error ".NET is not installed or not in PATH."
    exit 1
}

# --- 2. Verify service exists ---
$ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $ExistingService) {
    Write-Error "Service '$ServiceName' not found. Run install.ps1 first."
    exit 1
}

if (-not (Test-Path $InstallDir)) {
    Write-Error "Install directory not found: $InstallDir. Run install.ps1 first."
    exit 1
}

# --- 3. Determine project root and publish to temp ---
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$CsprojPath = Join-Path $ProjectRoot "PbiBridgeApi\PbiBridgeApi.csproj"

if (-not (Test-Path $CsprojPath)) {
    Write-Error "PbiBridgeApi.csproj not found at: $CsprojPath"
    exit 1
}

$TempDir = Join-Path $env:TEMP "PbiBridgeApi-update-$(Get-Date -Format 'yyyyMMddHHmmss')"
Write-Host "[2/6] Publishing to temp dir: $TempDir..." -ForegroundColor Yellow

& dotnet publish $CsprojPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $TempDir `
    /p:PublishSingleFile=false 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed. Aborting update — service unchanged."
    exit 1
}
Write-Host "  Publish OK" -ForegroundColor Green

# --- 4. Stop service ---
Write-Host "[3/6] Stopping service '$ServiceName'..." -ForegroundColor Yellow
if ($ExistingService.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
    $Timeout = 30
    $Elapsed = 0
    while ((Get-Service -Name $ServiceName).Status -ne "Stopped" -and $Elapsed -lt $Timeout) {
        Start-Sleep -Seconds 1
        $Elapsed++
    }
    if ((Get-Service -Name $ServiceName).Status -ne "Stopped") {
        Write-Error "Service did not stop within $Timeout seconds."
        # Cleanup temp
        Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }
}
Write-Host "  Service stopped." -ForegroundColor Green

# --- 5. Copy new files ---
Write-Host "[4/6] Copying new files to $InstallDir..." -ForegroundColor Yellow
try {
    Copy-Item -Path "$TempDir\*" -Destination $InstallDir -Recurse -Force
    Write-Host "  Files copied." -ForegroundColor Green
} catch {
    Write-Error "Failed to copy files: $_. Attempting to restart old version..."
    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
} finally {
    Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 6. Restart service ---
Write-Host "[5/6] Starting service '$ServiceName'..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -ne "Running") {
    Write-Error "Service did not start after update. Status: $($svc.Status). Check Event Viewer."
    exit 1
}
Write-Host "  Service status: $($svc.Status)" -ForegroundColor Green

# --- 7. Health check ---
Write-Host "[6/6] Verifying GET http://localhost:$Port/health..." -ForegroundColor Yellow
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
    Write-Error "Health check failed after update. Service may not be healthy."
    exit 1
}

Write-Host "  Health check: 200 OK" -ForegroundColor Green
Write-Host ""
Write-Host "Update complete. Service '$ServiceName' is running the latest version." -ForegroundColor Green
