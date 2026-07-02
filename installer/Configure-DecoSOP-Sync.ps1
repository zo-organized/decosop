<#
  DecoSOP Document Sync setup.

  Run this ON THE DECOSOP SERVER, in an interactive (RDP/console) session — the one-time
  OneDrive/SharePoint sign-in needs a browser. After setup, syncing runs headless as SYSTEM.

  It configures either:
    1) Automatic two-way OneDrive/SharePoint sync via rclone (recommended for M365), or
    2) A local folder / network share you keep updated yourself.

  Robust by design: it writes the config + creates the headless scheduled task BEFORE the
  (slow) initial download, so an interrupted first sync still leaves a working, self-healing
  setup. Safe to re-run — it reuses an existing rclone remote and overwrites its own config/task.
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
$syncScript = Join-Path $AppDir "rclone-bisync.ps1"
$taskName   = "DecoSOP Document Sync"

function Write-FolderSyncConfig($sopRoot, $docRoot, $openSop, $openDoc) {
    $cfg = [ordered]@{ FolderSync = [ordered]@{
        Enabled = $true; PollIntervalSeconds = 300; DebounceMs = 2000
        Sop = [ordered]@{ Root = "$sopRoot"; OpenBase = "$openSop" }
        Doc = [ordered]@{ Root = "$docRoot"; OpenBase = "$openDoc" }
    } }
    $path = Join-Path $AppDir "appsettings.Production.json"
    ($cfg | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "  Wrote $path" -ForegroundColor Green
}

function Restart-DecoSOP {
    Write-Host "  Restarting DecoSOP service..."
    & sc.exe stop DecoSOP  | Out-Null
    Start-Sleep -Seconds 3
    & sc.exe start DecoSOP | Out-Null
}

Write-Host "`nHow should DecoSOP get its documents?"
Write-Host "  [1] Automatic OneDrive / SharePoint sync (recommended for M365)"
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

# ================= OneDrive / SharePoint (rclone) mode =================

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

function Get-Remotes { @((& $rcloneExe listremotes --config $rcloneConf 2>$null) | ForEach-Object { $_.TrimEnd(':') } | Where-Object { $_ }) }

# 2) rclone remote — reuse if present, else run the guided config
$remoteName = $null
$remotes = Get-Remotes
if ($remotes.Count -gt 0) {
    Write-Host "`nFound existing rclone remote(s): $($remotes -join ', ')" -ForegroundColor Green
    if ((Read-Host "Reuse an existing one? [Y/n]").Trim() -notmatch '^(n|N)') {
        $remoteName = if ($remotes.Count -eq 1) { $remotes[0] } else { (Read-Host "Which remote name").Trim() }
    }
}

if (-not $remoteName) {
    Write-Host "`n==== Connect to OneDrive / SharePoint ====" -ForegroundColor Cyan
    Write-Host "IMPORTANT - which account to sign in with:" -ForegroundColor Yellow
    Write-Host "  Sign in as the account that OWNS the documents (e.g. the shared office account)."
    Write-Host "  Do NOT use the Global Admin account. If the browser is already signed into a"
    Write-Host "  different account, the sign-in can SILENTLY grab the wrong one. If that happens,"
    Write-Host "  sign that account out at portal.office.com first (or use an InPrivate window)."
    Write-Host ""
    Write-Host "rclone will ask a series of questions. Type exactly:" -ForegroundColor Cyan
    Write-Host "   New remote?            n"
    Write-Host "   name                   onedrive"
    Write-Host "   Storage                onedrive"
    Write-Host "   client_id              (blank - just press Enter)"
    Write-Host "   client_secret          (blank)"
    Write-Host "   region                 1"
    Write-Host "   tenant (if asked)      (blank)"
    Write-Host "   Edit advanced config?  n"
    Write-Host "   Use web browser?       y      <- sign in as the FILE account here"
    Write-Host "   --- after the browser says 'Success!', back in this window: ---"
    Write-Host "   config type            1      (OneDrive Personal or Business)"
    Write-Host "   which drive            0"
    Write-Host "   Is that okay?          y"
    Write-Host "   Keep this remote?      y"
    Write-Host "   then                   q      (quit)"
    Write-Host ""
    Read-Host "Press Enter to launch rclone setup"
    & $rcloneExe config --config $rcloneConf
    $remotes = Get-Remotes
    if ($remotes.Count -eq 0) { Write-Host "No remote was configured. Re-run and complete the rclone setup." -ForegroundColor Red; Done 1 }
    $remoteName = if ($remotes.Count -eq 1) { $remotes[0] } else { (Read-Host "Which remote name did you create").Trim() }
}
Write-Host "Using remote: ${remoteName}:" -ForegroundColor Green

# Show top-level folders so the user can copy exact names (spacing matters!)
Write-Host "`nTop-level folders in that account (copy the exact names for the paths below):" -ForegroundColor Cyan
try { & $rcloneExe lsd "${remoteName}:" --config $rcloneConf 2>$null } catch { }

# 3) gather paths
Write-Host "`nWhere do the SOP and Document folders live inside that remote?"
Write-Host "Example: ${remoteName}:MyFolder/SOPs   (match the exact spelling/spacing shown above)"
$sopRemote = (Read-Host "Remote path to SOPs (blank to skip)").Trim()
$docRemote = (Read-Host "Remote path to Documents (blank to skip)").Trim()
if (-not $sopRemote -and -not $docRemote) { Write-Host "Nothing to sync. Cancelled." -ForegroundColor Yellow; Done 0 }
$syncBase = (Read-Host "Local folder to sync into (default: $AppDir\sync)").Trim('"',' ')
if (-not $syncBase) { $syncBase = Join-Path $AppDir "sync" }
$sopLocal = if ($sopRemote) { Join-Path $syncBase "SOPs" } else { "" }
$docLocal = if ($docRemote) { Join-Path $syncBase "Docs" } else { "" }
$osop = if ($sopRemote) { (Read-Host "Web URL for SOPs (for 'Open in Office' links; blank to skip)").Trim() } else { "" }
$odoc = if ($docRemote) { (Read-Host "Web URL for Documents (blank to skip)").Trim() } else { "" }

# Media exclusion — big videos can't be previewed and massively slow the sync
Write-Host "`nExclude large video files from the sync? (recommended)" -ForegroundColor Cyan
Write-Host "Videos can't be previewed in DecoSOP and are often many GB, which slows every sync."
$excludeArgs = @()
if ((Read-Host "Exclude videos? [Y/n]").Trim() -notmatch '^(n|N)') {
    $excludeArgs = @("--exclude", "*.{mp4,mov,m4v,avi,mkv,wmv,mpg,mpeg,webm,flv,m2ts,mts,vob}")
}

# Interval — with guidance
Write-Host "`nSync interval = how often changes are pulled from the cloud. 5-15 min is healthy;" -ForegroundColor Cyan
Write-Host "very short intervals thrash on large folders (rclone just skips overlapping runs)."
$iv = (Read-Host "Sync interval in minutes (default 5)").Trim()
$interval = if ($iv -match '^\d+$' -and [int]$iv -ge 1) { [int]$iv } else { 5 }
if ($interval -lt 2) { Write-Host "  Note: $interval min is aggressive for large folders - runs may overlap and skip." -ForegroundColor Yellow }

# 4) flags shared by the initial sync and the recurring script
$baseFlags = @("--conflict-resolve","newer","--resilient","--recover","--max-delete","50","--create-empty-src-dirs") + $excludeArgs

# 5) write the SELF-HEALING recurring sync script (headless, run by the task).
#    bisync needs a --resync baseline; if a run fails (first run / wedged), it auto-repairs.
$exArr = if ($excludeArgs.Count) { ",'" + ($excludeArgs -join "','") + "'" } else { "" }
$lines = @(
    "# Recurring OneDrive<->local sync (generated by DecoSOP). Headless via Task Scheduler.",
    "`$ErrorActionPreference = 'Continue'",
    "`$rc = '$rcloneExe'",
    "`$flags = @('--config','$rcloneConf','--conflict-resolve','newer','--resilient','--recover','--max-delete','50','--create-empty-src-dirs'$exArr)",
    "function SyncPair(`$remote, `$local) {",
    "  New-Item -ItemType Directory -Force -Path `$local | Out-Null",
    "  & `$rc bisync `$remote `$local @flags",
    "  if (`$LASTEXITCODE -ne 0) { & `$rc bisync `$remote `$local --resync @flags }",
    "}"
)
if ($sopRemote) { $lines += "SyncPair '$sopRemote' '$sopLocal'" }
if ($docRemote) { $lines += "SyncPair '$docRemote' '$docLocal'" }
$lines | Set-Content -LiteralPath $syncScript -Encoding UTF8
Write-Host "`n  Wrote self-healing sync script." -ForegroundColor Green

# 6) DecoSOP config
Write-FolderSyncConfig $sopLocal $docLocal $osop $odoc

# 7) headless scheduled task (SYSTEM). First run delayed a few minutes so it doesn't
#    race the visible initial sync below for the bisync lock.
& schtasks /Delete /TN $taskName /F 2>$null | Out-Null
$startAt = (Get-Date).AddMinutes(4).ToString('HH:mm')
$tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$syncScript`""
& schtasks /Create /TN $taskName /TR $tr /SC MINUTE /MO $interval /ST $startAt /RU SYSTEM /RL HIGHEST /F | Out-Null
Write-Host "  Scheduled task '$taskName' created - runs every $interval min, headless." -ForegroundColor Green

# 8) restart DecoSOP so it picks up the config (indexes files as they arrive)
Restart-DecoSOP

Write-Host "`n==== Setup complete ====" -ForegroundColor Green
Write-Host "The sync is fully configured. Even if you close this window now, the scheduled task"
Write-Host "will keep OneDrive and DecoSOP in sync automatically (headless, no login required)."

# 9) initial download now — visible, but safe to interrupt (the task will finish it)
Write-Host "`nStarting the initial download. The first time can take a while." -ForegroundColor Cyan
Write-Host "You can safely close this window at any point - the scheduled task finishes it." -ForegroundColor Yellow
function InitialSync($remote, $local) {
    if (-not $remote) { return }
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    Write-Host "`nSyncing $remote  ->  $local ..." -ForegroundColor Cyan
    & $rcloneExe bisync $remote $local --resync --config $rcloneConf @baseFlags -P
}
InitialSync $sopRemote $sopLocal
InitialSync $docRemote $docLocal

Write-Host "`n==== Done! ====" -ForegroundColor Green
Write-Host "DecoSOP is now syncing with OneDrive and indexing the folders."
Write-Host "Re-run this tool any time to change folders, interval, or exclusions."
Done 0
