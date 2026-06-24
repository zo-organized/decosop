<#
  DecoSOP Document Sync setup.

  Run this ON THE DECOSOP SERVER, in your interactive (RDP/console) session — the one-time
  SharePoint sign-in needs a browser. After setup, syncing runs headless as SYSTEM forever.

  It configures either:
    1) Automatic two-way SharePoint/OneDrive sync via rclone (recommended for M365), or
    2) A local folder / network share you keep updated yourself,
  then writes DecoSOP's FolderSync config and restarts the service.
#>
[CmdletBinding()]
param([string]$AppDir = "C:\DecoSOP")

# --- self-elevate ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -AppDir `"$AppDir`"" -Verb RunAs
    return
}

$ErrorActionPreference = "Stop"
function Done($code = 0) { Read-Host "`nPress Enter to close"; exit $code }

Write-Host "==== DecoSOP Document Sync Setup ====" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $AppDir)) { Write-Host "DecoSOP not found at $AppDir" -ForegroundColor Red; Done 1 }

$rcloneConf = Join-Path $AppDir "rclone.conf"
$rcloneExe  = Join-Path $AppDir "rclone.exe"

function Write-FolderSyncConfig($sopRoot, $docRoot, $openSop, $openDoc) {
    $cfg = [ordered]@{ FolderSync = [ordered]@{
        Enabled = $true; PollIntervalSeconds = 300; DebounceMs = 2000
        Sop = [ordered]@{ Root = "$sopRoot"; OpenBase = "$openSop" }
        Doc = [ordered]@{ Root = "$docRoot"; OpenBase = "$openDoc" }
    } }
    $path = Join-Path $AppDir "appsettings.Production.json"
    ($cfg | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Wrote $path" -ForegroundColor Green
}

function Restart-DecoSOP {
    Write-Host "Restarting DecoSOP service..."
    & sc.exe stop DecoSOP  | Out-Null
    Start-Sleep -Seconds 3
    & sc.exe start DecoSOP | Out-Null
}

Write-Host "`nHow should DecoSOP get its documents?"
Write-Host "  [1] Automatic SharePoint / OneDrive sync (recommended for M365)"
Write-Host "  [2] A local folder or network share you keep updated yourself"
Write-Host "  [3] Cancel"
$mode = (Read-Host "Choose 1, 2, or 3").Trim()

if ($mode -eq "2") {
    $sop  = (Read-Host "Local/UNC path to the SOPs folder (blank to skip)").Trim('"',' ')
    $doc  = (Read-Host "Local/UNC path to the Documents folder (blank to skip)").Trim('"',' ')
    $osop = (Read-Host "OpenBase for SOPs ('Open in Office' base: SharePoint URL or UNC; blank if same machine)").Trim()
    $odoc = (Read-Host "OpenBase for Documents (blank if same machine)").Trim()
    Write-FolderSyncConfig $sop $doc $osop $odoc
    Restart-DecoSOP
    Write-Host "`nDone. DecoSOP will index those folders." -ForegroundColor Green
    Done 0
}
elseif ($mode -ne "1") { Write-Host "Cancelled."; Done 0 }

# ================= SharePoint mode =================

# 1) ensure rclone
if (-not (Test-Path -LiteralPath $rcloneExe)) {
    Write-Host "`nDownloading rclone..." -ForegroundColor Cyan
    $zip = Join-Path $env:TEMP "rclone-dl.zip"
    Invoke-WebRequest "https://downloads.rclone.org/rclone-current-windows-amd64.zip" -OutFile $zip
    $tmp = Join-Path $env:TEMP "rclone-extract"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
    $found = Get-ChildItem $tmp -Recurse -Filter rclone.exe | Select-Object -First 1
    Copy-Item $found.FullName $rcloneExe -Force
    Write-Host "rclone ready." -ForegroundColor Green
}

# 2) interactive rclone config — this is where the browser sign-in happens (once)
Write-Host "`n==== Connect to SharePoint ====" -ForegroundColor Cyan
Write-Host "rclone's configurator will open. Steps:"
Write-Host "  n) New remote  ->  name it e.g. 'sharepoint'"
Write-Host "  Storage: type 'onedrive'"
Write-Host "  Leave client_id/secret blank; a browser opens -> sign in with your M365 account"
Write-Host "  Choose the SharePoint SITE / document library that holds your SOP & Doc folders"
Write-Host "  Accept the defaults, then 'q' to quit when back at the main menu.`n"
Read-Host "Press Enter to launch rclone config"
& $rcloneExe config --config $rcloneConf

$remotes = (& $rcloneExe listremotes --config $rcloneConf) 2>$null
Write-Host "`nConfigured remotes: $($remotes -join ', ')" -ForegroundColor Green

# 3) gather paths
Write-Host "`nTell DecoSOP where the SOP and Document folders live inside that remote."
$sopRemote = (Read-Host "Remote path to SOPs (e.g. sharepoint:z PROTOCOLS) - blank to skip").Trim()
$docRemote = (Read-Host "Remote path to Documents (e.g. sharepoint:DOCUMENTS) - blank to skip").Trim()
$syncBase  = (Read-Host "Local folder to sync into (default: $AppDir\sync)").Trim('"',' ')
if (-not $syncBase) { $syncBase = Join-Path $AppDir "sync" }
$sopLocal = if ($sopRemote) { Join-Path $syncBase "SOPs" } else { "" }
$docLocal = if ($docRemote) { Join-Path $syncBase "Docs" } else { "" }
$osop = (Read-Host "SharePoint web URL for SOPs (for 'Open in Office' links; blank to skip)").Trim()
$odoc = (Read-Host "SharePoint web URL for Documents (blank to skip)").Trim()
$iv = (Read-Host "Sync interval in minutes (default 5)").Trim()
$interval = if ($iv -match '^\d+$') { [int]$iv } else { 5 }

# 4) initial two-way baseline (resync), and define the recurring command
$common = @("--config", $rcloneConf, "--conflict-resolve", "newer", "--resilient", "--recover", "--max-delete", "50", "--create-empty-src-dirs")
function Bisync($remote, $local, [switch]$Resync) {
    if (-not $remote) { return }
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    $a = @("bisync", "$remote", "$local") + $common
    if ($Resync) { $a += "--resync" }
    Write-Host "`nSyncing $remote  ->  $local  (this can take a while the first time)..." -ForegroundColor Cyan
    & $rcloneExe @a
}
Bisync $sopRemote $sopLocal -Resync
Bisync $docRemote $docLocal -Resync

# 5) recurring bisync script + headless scheduled task (runs as SYSTEM, no login needed)
$syncScript = Join-Path $AppDir "rclone-bisync.ps1"
$lines = @('# Recurring two-way sync (generated by DecoSOP). Runs headless as SYSTEM.', '$ErrorActionPreference = "Continue"')
$flags = "--config '$rcloneConf' --conflict-resolve newer --resilient --recover --max-delete 50 --create-empty-src-dirs"
if ($sopRemote) { $lines += "& '$rcloneExe' bisync `"$sopRemote`" `"$sopLocal`" $flags" }
if ($docRemote) { $lines += "& '$rcloneExe' bisync `"$docRemote`" `"$docLocal`" $flags" }
$lines | Set-Content -LiteralPath $syncScript -Encoding UTF8

$taskName = "DecoSOP Document Sync"
& schtasks /Delete /TN $taskName /F 2>$null | Out-Null
$tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$syncScript`""
& schtasks /Create /TN $taskName /TR $tr /SC MINUTE /MO $interval /RU SYSTEM /RL HIGHEST /F | Out-Null
Write-Host "`nScheduled task '$taskName' created - runs every $interval min, headless." -ForegroundColor Green

# 6) DecoSOP config + restart
Write-FolderSyncConfig $sopLocal $docLocal $osop $odoc
Restart-DecoSOP
Write-Host "`n==== Done! ====" -ForegroundColor Green
Write-Host "DecoSOP is now two-way syncing with SharePoint and indexing the folders."
Write-Host "Edits in the app (Replace / Open in Office) flow back up; SharePoint changes flow down."
Done 0
