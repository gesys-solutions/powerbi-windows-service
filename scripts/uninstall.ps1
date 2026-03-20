#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstall the PbiBridgeApi Windows Service.

.PARAMETER ServiceName
    Windows Service name. Default: PbiBridgeApi

.PARAMETER RemoveFiles
    If specified, also removes the deploy directory (use with caution).

.PARAMETER InstallDir
    Directory to remove if -RemoveFiles is specified. Default: C:\Services\PbiBridgeApi\

.EXAMPLE
    .\scripts\uninstall.ps1
    .\scripts\uninstall.ps1 -RemoveFiles -InstallDir "D:\Services\PbiBridgeApi\"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ServiceName = "PbiBridgeApi",

    [Parameter(Mandatory = $false)]
    [switch]$RemoveFiles,

    [Parameter(Mandatory = $false)]
    [string]$InstallDir = "C:\Services\PbiBridgeApi\"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== PbiBridgeApi Uninstall ===" -ForegroundColor Cyan

# --- 1. Check service exists ---
$ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $ExistingService) {
    Write-Host "Service '$ServiceName' not found — nothing to uninstall." -ForegroundColor Yellow
    exit 0
}

# --- 2. Stop service ---
Write-Host "[1/3] Stopping service '$ServiceName'..." -ForegroundColor Yellow
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
        exit 1
    }
}
Write-Host "  Service stopped." -ForegroundColor Green

# --- 3. Remove service ---
Write-Host "[2/3] Removing service '$ServiceName'..." -ForegroundColor Yellow
$NssmPath = Get-Command "nssm" -ErrorAction SilentlyContinue

if ($NssmPath) {
    & nssm remove $ServiceName confirm 2>&1 | Out-Null
} else {
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "sc.exe delete failed (exit $LASTEXITCODE)"
        exit 1
    }
}
Write-Host "  Service removed." -ForegroundColor Green

# --- 4. Optionally remove files ---
Write-Host "[3/3] Cleanup..." -ForegroundColor Yellow
if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-Host "  Removed deploy directory: $InstallDir" -ForegroundColor Green
    } else {
        Write-Host "  Deploy directory not found, skipping: $InstallDir" -ForegroundColor DarkGray
    }
} else {
    Write-Host "  Deploy files kept at: $InstallDir (use -RemoveFiles to delete)." -ForegroundColor Gray
}

Write-Host ""
Write-Host "Service '$ServiceName' uninstalled successfully." -ForegroundColor Green
