#Requires -RunAsAdministrator
<#
    DecoSOP Installer
    Installs the app as a Windows Service, creates a firewall rule,
    and starts the service. Run this script as Administrator.
#>

param(
    [string]$InstallDir = "C:\DecoSOP",
    [int]$Port = 5098
)

$ErrorActionPreference = "Stop"
$serviceName = "DecoSOP"

Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  DecoSOP Installer" -ForegroundColor Cyan
Write-Host "==============================" -ForegroundColor Cyan
Write-Host ""

# --- Check if already installed ---
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "An existing DecoSOP installation was found." -ForegroundColor Yellow
    Write-Host "Stopping the service..." -ForegroundColor Yellow
    Stop-Service -Name $serviceName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# --- Copy files ---
Write-Host "Installing to $InstallDir ..." -ForegroundColor White

if (!(Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceFiles = Join-Path $scriptDir "*"

# Preserve the database if upgrading
Copy-Item -Path $sourceFiles -Destination $InstallDir -Recurse -Force -Exclude @("decosop.db", "install.ps1", "uninstall.ps1")

# Don't overwrite an existing database
$dbDest = Join-Path $InstallDir "decosop.db"
$dbSource = Join-Path $scriptDir "decosop.db"
if (!(Test-Path $dbDest) -and (Test-Path $dbSource)) {
    Copy-Item -Path $dbSource -Destination $dbDest
}

Write-Host "  Files copied." -ForegroundColor Green

# --- Port configuration ---
# The app reads its listening port from port.config at startup (defaults to 5098).
# Only write it when a port was explicitly requested, so upgrades keep the current port.
if ($PSBoundParameters.ContainsKey('Port')) {
    "PORT=$Port" | Set-Content -Path (Join-Path $InstallDir "port.config") -Encoding ASCII
    Write-Host "  Listening port set to $Port." -ForegroundColor Green
}

# --- Create Windows Service ---
if (!$existingService) {
    Write-Host "Creating Windows service..." -ForegroundColor White
    $exePath = Join-Path $InstallDir "DecoSOP.exe"
    sc.exe create $serviceName binPath="$exePath" start=auto DisplayName="DecoSOP" | Out-Null
    sc.exe description $serviceName "DecoSOP - Standard Operating Procedures" | Out-Null
    Write-Host "  Service created." -ForegroundColor Green
} else {
    Write-Host "  Service already exists, skipping creation." -ForegroundColor Gray
}

# --- Firewall rule ---
$existingRule = netsh advfirewall firewall show rule name="DecoSOP" 2>$null
if ($existingRule -match "DecoSOP") {
    Write-Host "  Firewall rule already exists, skipping." -ForegroundColor Gray
} else {
    Write-Host "Creating firewall rule for port $Port ..." -ForegroundColor White
    netsh advfirewall firewall add rule name="DecoSOP" dir=in action=allow protocol=TCP localport=$Port | Out-Null
    Write-Host "  Firewall rule created." -ForegroundColor Green
}

# --- Start service ---
Write-Host "Starting service..." -ForegroundColor White
Start-Service -Name $serviceName
Start-Sleep -Seconds 2

$svc = Get-Service -Name $serviceName
if ($svc.Status -eq "Running") {
    Write-Host "  Service is running." -ForegroundColor Green
} else {
    Write-Host "  WARNING: Service status is $($svc.Status)." -ForegroundColor Yellow
    Write-Host "  Try running: C:\DecoSOP\DecoSOP.exe to see error output." -ForegroundColor Yellow
}

# --- Done ---
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notmatch "Loopback" -and $_.IPAddress -ne "127.0.0.1" } | Select-Object -First 1).IPAddress

Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host "==============================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Access DecoSOP at:" -ForegroundColor White
Write-Host "    This machine:  http://localhost:$Port" -ForegroundColor Yellow
if ($ip) {
    Write-Host "    Other machines: http://${ip}:$Port" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  Next step - point DecoSOP at your document folders:" -ForegroundColor White
$syncScript = Join-Path $InstallDir "Configure-DecoSOP-Sync.ps1"
if (Test-Path $syncScript) {
    Write-Host "    Run as Administrator:  $syncScript" -ForegroundColor Yellow
    Write-Host "    (SharePoint/OneDrive via rclone, or a local folder/share)" -ForegroundColor Gray
} else {
    Write-Host "    Set the FolderSync roots in $InstallDir\appsettings.json" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  The service starts automatically on boot." -ForegroundColor Gray
Write-Host "  To uninstall, run uninstall.ps1 as Administrator." -ForegroundColor Gray
Write-Host ""
