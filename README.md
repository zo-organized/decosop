# DecoSOP

A fast, polished web catalog for your Standard Operating Procedures and office documents. DecoSOP indexes a folder you already have — a OneDrive-synced folder, a network share, or a plain local folder — and turns it into a searchable, browsable library that everyone on your network can reach from a browser.

The **folder is the source of truth.** DecoSOP watches it and keeps itself in sync automatically: add, edit, rename, or remove a file in that folder (in File Explorer, in OneDrive, in Word/Excel) and the change shows up in DecoSOP on its own. There's no separate copy to keep up to date and no in-app editor to fight with — you edit documents in real Word and Excel.

Built with Blazor Server (.NET 10) and SQLite. Runs as a Windows Service on a single PC or server — no cloud account required.

## How it works

- You point DecoSOP at one folder for **SOPs** and one for **Documents** (either is optional).
- DecoSOP mirrors each folder **1:1** — every subfolder becomes a category, every file becomes an entry. It reads file metadata only, so it never forces OneDrive to download files just to index them.
- A background watcher plus a periodic rescan keep the catalog current within seconds of a change.
- Editing happens in the real apps: open a document in Word/Excel (or click **Open in Word/Excel**), save, and OneDrive/your share propagates it. You can also **Replace** a file or **Upload** a new one through the web UI — those write straight back into the watched folder.
- **Deleting is done in the folder, not in the app.** Remove a file or folder on disk and DecoSOP drops it on the next sync. This keeps the folder authoritative and prevents accidental data loss from the browser.

## Features

- 1:1 mirror of your folder tree — nested categories, every file type
- Full-text search across titles and file names
- Inline **PDF/image preview** and one-click **print** (Office files are rendered to PDF on demand via LibreOffice, if installed)
- **Open in Word/Excel/PowerPoint** via the Office URI scheme (when configured for a SharePoint/share path)
- **Favorites, pins, and folder colors** — stored per machine, so each workstation has its own quick-access set
- Create folders, upload files, rename folders, and replace a file — all written back into the watched folder
- Runs as a Windows Service — starts on boot, always available on the LAN

## Installation

Download the latest `DecoSOP-Setup-<version>.exe` from the [Releases](https://github.com/zo-organized/decosop/releases) page and run it **as Administrator**. The installer:

- Installs to `C:\DecoSOP` (default)
- Registers DecoSOP as a Windows Service that starts on boot
- Opens the chosen port in Windows Firewall so other machines can connect
- Starts the service and shows the URL

After installation, run the **Configure Document Sync** tool (a Start-menu shortcut, also offered at the end of setup) to point DecoSOP at your folders. It supports two modes:

1. **SharePoint / OneDrive (headless server)** — sets up a two-way sync of just your SOP and Document libraries using [rclone](https://rclone.org/), authenticated during setup, running as a scheduled task under the SYSTEM account. Use this when the server has no interactive OneDrive session.
2. **Local folder or network share** — point DecoSOP directly at a UNC path or a folder that's already kept in sync some other way.

The tool writes the folder paths into `appsettings.Production.json` and restarts the service.

## Configuring the watched folders

Sync settings live in `appsettings.json` (or `appsettings.Production.json`) next to the executable:

```json
"FolderSync": {
  "Enabled": true,
  "PollIntervalSeconds": 300,
  "DebounceMs": 2000,
  "Sop": { "Root": "C:\\Docs\\SOPs",      "OpenBase": "" },
  "Doc": { "Root": "C:\\Docs\\Documents", "OpenBase": "" }
}
```

- **`Root`** — the folder to index for that module. Leave blank to turn the module off. Can be a OneDrive-synced folder, a mapped/UNC share, or a plain local folder — DecoSOP treats them all the same.
- **`OpenBase`** — the client-reachable base (a SharePoint/OneDrive web URL, or a UNC path like `\\SERVER\Share`) used to build **Open in Word/Excel** links. Leave blank if clients can't reach the files directly; the button is simply hidden.
- **`PollIntervalSeconds` / `DebounceMs`** — how often the fallback rescan runs, and how long changes are batched before reconciling.

**OneDrive note:** use OneDrive's *"Choose folders"* so only the SOP and Document libraries sync to the machine, and set those folders to **"Always keep on this device."** Indexing works on cloud-only placeholders, but downloads and previews need the actual files present. The OneDrive client only runs in a signed-in session, so the account must stay signed in on the host.

**Network share note:** point the roots at the UNC/mapped path and ensure the service account can read it. Back up the folder yourself — it's the single source of truth.

## Keeping documents synced to the server

DecoSOP only ever indexes a **folder** — it has no SharePoint/OneDrive connector of its own. So "syncing from the cloud" means: something mirrors your OneDrive/SharePoint library down to a local folder, and DecoSOP watches that folder. This section covers how to set that up.

### Where to run DecoSOP

Run it on a **small always-on PC or a lightweight VM** — **not** on a Hyper-V host or domain controller. The app is tiny (Blazor + SQLite), but the machine choice matters because of how the documents get there (below).

### Which sync method to use

Pick based on one question: **can that machine stay logged in?**

| | Machine can stay logged in | Machine must run headless (no login) |
|---|---|---|
| **Method** | OneDrive desktop client | rclone (SYSTEM scheduled task) |
| **Admin consent needed?** | No | Yes, once |
| **Survives reboot unattended?** | Only if it auto-logs-in | Yes |

Both are offered by the **`Configure-DecoSOP-Sync`** tool (Start-menu shortcut / end of installer). If you already share one Microsoft 365 account across the office via OneDrive, the DecoSOP machine is just "one more office PC," and either method uses that same account.

#### Method A — OneDrive desktop client (simplest, for a machine that stays logged in)

1. Install the **OneDrive** app and sign in with the account whose OneDrive holds the documents (e.g. the shared office account).
2. Use **"Choose folders"** (selective sync) to sync **only** the SOP and Document folders — not the whole OneDrive.
3. Right-click those folders → **"Always keep on this device."** Indexing works on cloud-only placeholders, but **downloads and previews need the real files present**.
4. Run `Configure-DecoSOP-Sync` → **Local folder / network share**, and point the SOP and Doc roots at the synced local paths (e.g. `C:\Users\<user>\OneDrive - <Org>\...\SOPs`).

**Caveat:** the OneDrive client only syncs **while a user is signed in**. Disconnect RDP (leaves the session running) rather than signing out. If the machine reboots with nobody logged in, syncing pauses until someone logs in.

#### Method B — rclone, headless (for a machine that must run with no login)

rclone mirrors the library to a local folder and re-syncs on a **SYSTEM scheduled task**, so no one needs to stay logged in and it survives reboots. Run `Configure-DecoSOP-Sync` → **OneDrive / SharePoint (rclone)** and it walks you through it (it prints the exact answers for rclone's prompts). Two things to know going in:

**1. One-time Entra (Azure AD) admin consent.** Most tenants require an administrator to approve third-party apps, so rclone's sign-in hits a **"Need admin approval"** wall. A **Global Administrator** clears it once:
- Sign in at **entra.microsoft.com**, use the **search bar** at the top for **"Enterprise applications"** (the left-menu path varies by version), open **rclone → Security → Permissions → "Grant admin consent for &lt;org&gt;"**.
- Or approve live: at the "Need admin approval" screen click **"Have an admin account? Sign in with that account,"** sign in as the Global Admin, tick **"Consent on behalf of your organization."**

**2. Sign in as the *file-owning* account, not the admin.** rclone's sign-in uses whatever account the browser is currently logged into — and it can **silently grab the wrong one**. Sign in as the account whose OneDrive holds the documents (e.g. the shared office account); if the browser is on the Global Admin, sign that out at portal.office.com first (or use an InPrivate window). The tool warns you about this at the prompt.

After consent, the tool: shows your top-level folders (copy the exact names), asks for the SOP/Doc paths, offers to **exclude large video files** (recommended — they can't be previewed and bloat the sync), and asks a **sync interval** (5–15 min is healthy). It then writes the config, creates the **"DecoSOP Document Sync"** task, and starts the first download.

**It's robust to interruptions:** the config + task are created *before* the (slow) first download, and the recurring sync **self-heals** (re-establishes its baseline automatically), so you can safely close the window mid-download — the task finishes it in the background. Re-run the tool any time to change folders, interval, or exclusions.

### Notes for both methods

- **Sync only the SOP/Doc folders**, not the entire OneDrive — the rest is usually large and irrelevant.
- Content should be **non-sensitive** — DecoSOP has no login and is reachable by anyone on the LAN.
- **Latency:** a change shows up in DecoSOP within roughly the sync interval (OneDrive's own sync + the rclone poll); DecoSOP itself reflects a landed file within seconds.
- If you use the **OneDrive client** (Method A), previews/downloads need the bytes present (**"Always keep on this device"**); rclone (Method B) copies real files, so that doesn't apply.

## Accessing the app

- **On the host:** `http://localhost:<port>` (default `5098`)
- **From other machines:** `http://<host-ip>:<port>` — find the host IP with `ipconfig` (usually `192.168.x.x`)

## Updating

Re-run the installer for the new version; it stops the service, updates the files (preserving your database and configuration), and restarts. DecoSOP can also check for and install updates itself — see **Settings → Updates**.

## Uninstalling

Uninstall from **Add or Remove Programs**, or run the uninstaller in the install folder. This removes the service, the firewall rule, and the application files. **Your documents are not touched** — they live in the watched folder, not in DecoSOP. Only the local index database (favorites, pins, colors) is removed.

## Development

```powershell
dotnet run
```

Runs at `http://localhost:5098` with hot reload. The SQLite index database is created automatically on first run. To index a folder while developing, set `FolderSync:Enabled` and the roots in `appsettings.Development.json`, or via environment variables:

```powershell
$env:FolderSync__Enabled = "true"
$env:FolderSync__Sop__Root = "C:\path\to\sops"
$env:FolderSync__Doc__Root = "C:\path\to\docs"
dotnet run
```

### Project structure

```
Components/
  Layout/   - MainLayout, NavMenu (sidebar with SOPs/Documents toggle)
  Pages/    - SopHome/DocHome, category views, viewers, upload, Settings
  Shared/   - CategoryContextMenu, FolderIcon, cards, banners
Data/       - EF Core DbContext (category + file index, user preferences)
Models/     - SopCategory/SopFile, DocumentCategory/OfficeDocument, UserPreference
Services/   - FolderReconciler + FolderSyncBackgroundService (the sync engine)
              SopFileService/DocumentService (folder-backed CRUD)
              PdfConversionService, OfficeProtocol, UpdateService, caches
wwwroot/js/ - PDF preview, print, sidebar resize, context menu
installer/  - Inno Setup script + Configure-DecoSOP-Sync tool
```

## Troubleshooting

**Files show up but won't open or preview**
- The watched folder is likely OneDrive cloud-only. Set it to **"Always keep on this device"** so the bytes are present.

**Documents don't appear or don't update**
- Check **Settings → Document Sync**: confirm the folder path and that sync is enabled, and click **Sync now** to force a reconcile.
- Confirm the service account can read the folder (network share) and that OneDrive is signed in (OneDrive).

**"Can't reach the page" from another computer**
- Verify the firewall rule: `netsh advfirewall firewall show rule name="DecoSOP"`
- Verify the service is running: `Get-Service DecoSOP`
- Confirm the host IP with `ipconfig`

**Service starts but immediately stops**
- Run the exe directly to see the error: `C:\DecoSOP\DecoSOP.exe`
- Common cause: the port is already in use. Change it in `port.config` / `appsettings.json` and restart.

**"Open in Word/Excel" button is missing**
- It only appears when `OpenBase` is configured to a path/URL the client machine can reach. Set it in `appsettings.json`.
</content>
</invoke>
