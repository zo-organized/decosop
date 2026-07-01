#Requires -RunAsAdministrator
<#
    DecoSOP Uninstaller
    Stops and removes the service, removes the firewall rule,
    and optionally deletes application files.
#>

param(
    [string]$InstallDir = "C:\DecoSOP",
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$serviceName = "DecoSOP"

Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  DecoSOP Uninstaller" -ForegroundColor Cyan
Write-Host "==============================" -ForegroundColor Cyan
Write-Host ""

# --- Stop and remove service ---
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping service..." -ForegroundColor White
    Stop-Service -Name $serviceName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "Removing service..." -ForegroundColor White
    sc.exe delete $serviceName | Out-Null
    Write-Host "  Service removed." -ForegroundColor Green
} else {
    Write-Host "  Service not found, skipping." -ForegroundColor Gray
}

# --- Remove firewall rule ---
$existingRule = netsh advfirewall firewall show rule name="DecoSOP" 2>$null
if ($existingRule -match "DecoSOP") {
    Write-Host "Removing firewall rule..." -ForegroundColor White
    netsh advfirewall firewall delete rule name="DecoSOP" | Out-Null
    Write-Host "  Firewall rule removed." -ForegroundColor Green
} else {
    Write-Host "  Firewall rule not found, skipping." -ForegroundColor Gray
}

# --- Remove document-sync scheduled task (created by Configure-DecoSOP-Sync) ---
$taskName = "DecoSOP Document Sync"
if (schtasks /Query /TN $taskName 2>$null) {
    Write-Host "Removing document-sync scheduled task..." -ForegroundColor White
    schtasks /Delete /TN $taskName /F 2>$null | Out-Null
    Write-Host "  Scheduled task removed (your synced folders are left untouched)." -ForegroundColor Green
}

# --- Remove files ---
if (Test-Path $InstallDir) {
    if ($KeepData) {
        Write-Host "Removing application files (keeping database)..." -ForegroundColor White
        Get-ChildItem -Path $InstallDir -Exclude "decosop.db" | Remove-Item -Recurse -Force
        Write-Host "  Application files removed. Database preserved at $InstallDir\decosop.db" -ForegroundColor Green
    } else {
        Write-Host "Removing all files from $InstallDir ..." -ForegroundColor White
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-Host "  All files removed." -ForegroundColor Green
    }
} else {
    Write-Host "  Install directory not found, skipping." -ForegroundColor Gray
}

Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  Uninstall complete." -ForegroundColor Green
Write-Host "==============================" -ForegroundColor Cyan
Write-Host ""
