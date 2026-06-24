; DecoSOP Inno Setup Installer Script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
;
; Wizard pages:
;   1. Welcome
;   2. License Agreement
;   3. Install Directory
;   4. Port Configuration
;   5. Database Setup (Empty / Demo / Import backup / Scan folders)
;   6. Import Database file      (only if Import selected)
;   7. Import SOP files dir      (only if Import selected, optional)
;   8. Import Documents dir      (only if Import selected, optional)
;   9. Scan SOP source dir       (only if Scan selected)
;  10. Scan Documents source dir (only if Scan selected)
;  11. Auto-Update Preference (checks, auto-install, time picker)
;  12. LibreOffice (optional download + install for Office doc previews)
;  13. Shortcuts (desktop icon)
;  14. Ready to Install
;  15. Installing...
;  16. Finish (open in browser)

#define MyAppName "DecoSOP"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Tyler Sweeney"
#define MyAppURL "https://github.com/Susguine/decosop"
#define MyAppExeName "DecoSOP.exe"
#define LibreOfficeVersion "25.8.5"
#define LibreOfficeFileName "LibreOffice_25.8.5_Win_x86-64.msi"
#define LibreOfficeURL "https://download.documentfoundation.org/libreoffice/stable/25.8.5/win/x86_64/LibreOffice_25.8.5_Win_x86-64.msi"

[Setup]
AppId={{D3C0-50F1-4A2B-B8E9-DecoSOP-1000}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName=C:\DecoSOP
DefaultGroupName={#MyAppName}
LicenseFile=license.txt
OutputDir=..\installer-output
OutputBaseFilename=DecoSOP-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=force

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Published app files — excludes DB and upload dirs (user data)
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "decosop.db,doc-uploads,sop-uploads"
; Document-sync setup script (SharePoint/OneDrive via rclone, or a local folder/share)
Source: "Configure-DecoSOP-Sync.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\doc-uploads"
Name: "{app}\sop-uploads"

[Icons]
Name: "{group}\DecoSOP"; Filename: "http://localhost:{code:GetPort}"
Name: "{group}\Configure Document Sync"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Configure-DecoSOP-Sync.ps1"" -AppDir ""{app}"""; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall DecoSOP"; Filename: "{uninstallexe}"
Name: "{commondesktop}\DecoSOP"; Filename: "http://localhost:{code:GetPort}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
; Stop existing service if upgrading
Filename: "sc.exe"; Parameters: "stop DecoSOP"; Flags: runhidden; StatusMsg: "Stopping existing service..."; Check: ServiceExists
; Wait for service to stop
Filename: "cmd.exe"; Parameters: "/c timeout /t 3 /nobreak >nul"; Flags: runhidden; Check: ServiceExists
; Delete old service before re-creating (handles path changes)
Filename: "sc.exe"; Parameters: "delete DecoSOP"; Flags: runhidden; Check: ServiceExists
; Wait for deletion
Filename: "cmd.exe"; Parameters: "/c timeout /t 2 /nobreak >nul"; Flags: runhidden; Check: ServiceExists

; Register Windows Service
Filename: "sc.exe"; Parameters: "create DecoSOP binPath=""{app}\{#MyAppExeName}"" start=auto displayname=""DecoSOP Document Manager"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering Windows Service..."
Filename: "sc.exe"; Parameters: "description DecoSOP ""DecoSOP - SOP & Document Management System"""; Flags: runhidden waituntilterminated

; Write configuration files
Filename: "cmd.exe"; Parameters: "/c echo {code:GetPortConfigContent}> ""{app}\port.config"""; Flags: runhidden; Check: IsCustomPort; StatusMsg: "Writing port configuration..."
Filename: "cmd.exe"; Parameters: "/c echo {code:GetUpdateConfigContent}> ""{app}\update-config.json"""; Flags: runhidden; StatusMsg: "Writing update configuration..."

; Copy imported database if user chose "Import backup"
Filename: "cmd.exe"; Parameters: "/c copy /y ""{code:GetImportDbPath}"" ""{app}\decosop.db"""; Flags: runhidden; Check: ShouldImportDb; StatusMsg: "Importing database backup..."

; Copy SOP upload files if directory was selected
Filename: "robocopy.exe"; Parameters: """{code:GetImportSopDirPath}"" ""{app}\sop-uploads"" /E /NFL /NDL /NJH /NJS"; Flags: runhidden; Check: ShouldImportSopDir; StatusMsg: "Importing SOP files..."

; Copy Document upload files if directory was selected
Filename: "robocopy.exe"; Parameters: """{code:GetImportDocDirPath}"" ""{app}\doc-uploads"" /E /NFL /NDL /NJH /NJS"; Flags: runhidden; Check: ShouldImportDocDir; StatusMsg: "Importing document files..."

; Add firewall rule
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DecoSOP"""; Flags: runhidden; StatusMsg: "Updating firewall rules..."
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""DecoSOP"" dir=in action=allow protocol=TCP localport={code:GetPort}"; Flags: runhidden waituntilterminated

; Seed demo data if selected
Filename: "{app}\{#MyAppExeName}"; Parameters: "--seed-demo"; Flags: runhidden waituntilterminated; StatusMsg: "Loading demo data..."; Check: ShouldSeedDemo

; NOTE: SOP/Doc scan imports are now handled in CurStepChanged with progress polling

; Start the service
Filename: "sc.exe"; Parameters: "start DecoSOP"; Flags: runhidden waituntilterminated; StatusMsg: "Starting DecoSOP..."

; Offer to configure document sync now (SharePoint/OneDrive via rclone, or a local folder/share)
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Configure-DecoSOP-Sync.ps1"" -AppDir ""{app}"""; Flags: postinstall shellexec nowait; Description: "Configure document sync (SharePoint / OneDrive or a folder) now"

; Open browser
Filename: "http://localhost:{code:GetPort}"; Flags: postinstall shellexec nowait unchecked; Description: "Open DecoSOP in browser"

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop DecoSOP"; Flags: runhidden waituntilterminated
Filename: "cmd.exe"; Parameters: "/c timeout /t 5 /nobreak >nul"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete DecoSOP"; Flags: runhidden waituntilterminated
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DecoSOP"""; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}\wwwroot"
Type: files; Name: "{app}\{#MyAppExeName}"
Type: files; Name: "{app}\port.config"
Type: files; Name: "{app}\update-config.json"
; NOTE: database and uploads are intentionally preserved

[Code]
var
  PortPage: TInputQueryWizardPage;
  DatabasePage: TInputOptionWizardPage;
  ImportDbPage: TInputFileWizardPage;
  ImportSopDirPage: TWizardPage;
  ImportSopDirEdit: TNewEdit;
  ImportDocDirPage: TWizardPage;
  ImportDocDirEdit: TNewEdit;
  ScanSopDirPage: TInputDirWizardPage;
  ScanDocDirPage: TWizardPage;
  ScanDocDirEdit: TNewEdit;
  UpdatePage: TWizardPage;
  UpdateEnableRadio: TNewRadioButton;
  UpdateDisableRadio: TNewRadioButton;
  AutoInstallCheckbox: TNewCheckBox;
  AutoInstallTimeLabel: TNewStaticText;
  AutoInstallTimeCombo: TNewComboBox;
  IsUpgradeInstall: Boolean;
  LibreOfficePage: TWizardPage;
  LibreOfficeCheckbox: TNewCheckBox;
  LibreOfficeStatusLabel: TNewStaticText;
  LibreOfficeDetected: Boolean;

// ---- Port helpers ----

function GetPort(Param: String): String;
begin
  if PortPage <> nil then
    Result := PortPage.Values[0]
  else
    Result := '5098';
  if Result = '' then
    Result := '5098';
end;

function IsCustomPort: Boolean;
begin
  Result := GetPort('') <> '5098';
end;

function GetPortConfigContent(Param: String): String;
begin
  Result := 'PORT=' + GetPort('');
end;

// ---- Database helpers ----

function ShouldSeedDemo: Boolean;
begin
  if DatabasePage <> nil then
    Result := DatabasePage.SelectedValueIndex = 1
  else
    Result := False;
end;

function ShouldImportDb: Boolean;
begin
  if DatabasePage <> nil then
    Result := DatabasePage.SelectedValueIndex = 2
  else
    Result := False;
end;

function GetImportDbPath(Param: String): String;
begin
  if ImportDbPage <> nil then
    Result := ImportDbPage.Values[0]
  else
    Result := '';
end;

// ---- Import directory helpers ----

function GetImportSopDirPath(Param: String): String;
begin
  if ImportSopDirEdit <> nil then
    Result := ImportSopDirEdit.Text
  else
    Result := '';
end;

function ShouldImportSopDir: Boolean;
begin
  Result := ShouldImportDb and (GetImportSopDirPath('') <> '') and DirExists(GetImportSopDirPath(''));
end;

function GetImportDocDirPath(Param: String): String;
begin
  if ImportDocDirEdit <> nil then
    Result := ImportDocDirEdit.Text
  else
    Result := '';
end;

function ShouldImportDocDir: Boolean;
begin
  Result := ShouldImportDb and (GetImportDocDirPath('') <> '') and DirExists(GetImportDocDirPath(''));
end;

// ---- Scan directory helpers ----

function ShouldScanFiles: Boolean;
begin
  if DatabasePage <> nil then
    Result := DatabasePage.SelectedValueIndex = 3
  else
    Result := False;
end;

function GetScanSopDirPath(Param: String): String;
begin
  if ScanSopDirPage <> nil then
    Result := ScanSopDirPage.Values[0]
  else
    Result := '';
end;

function ShouldScanSops: Boolean;
begin
  Result := ShouldScanFiles and (GetScanSopDirPath('') <> '') and DirExists(GetScanSopDirPath(''));
end;

function GetScanDocDirPath(Param: String): String;
begin
  if ScanDocDirEdit <> nil then
    Result := ScanDocDirEdit.Text
  else
    Result := '';
end;

function ShouldScanDocs: Boolean;
begin
  Result := ShouldScanFiles and (GetScanDocDirPath('') <> '') and DirExists(GetScanDocDirPath(''));
end;

// ---- Update helpers ----

function IsAutoUpdateEnabled: Boolean;
begin
  if UpdateEnableRadio <> nil then
    Result := UpdateEnableRadio.Checked
  else
    Result := True;
end;

function IsAutoInstallEnabled: Boolean;
begin
  if AutoInstallCheckbox <> nil then
    Result := AutoInstallCheckbox.Checked and IsAutoUpdateEnabled
  else
    Result := False;
end;

function GetAutoInstallTime: String;
var
  Idx: Integer;
begin
  Result := '02:00';
  if AutoInstallTimeCombo <> nil then
  begin
    Idx := AutoInstallTimeCombo.ItemIndex;
    if Idx >= 0 then
      Result := Format('%.2d:00', [Idx]);
  end;
end;

function GetUpdateConfigContent(Param: String): String;
begin
  if IsAutoUpdateEnabled then
  begin
    if IsAutoInstallEnabled then
      Result := '{ "enabled": true, "repoOwner": "Susguine", "repoName": "DecoSOP", "checkIntervalHours": 24, "autoInstall": true, "autoInstallTime": "' + GetAutoInstallTime + '" }'
    else
      Result := '{ "enabled": true, "repoOwner": "Susguine", "repoName": "DecoSOP", "checkIntervalHours": 24, "autoInstall": false, "autoInstallTime": "02:00" }';
  end
  else
    Result := '{ "enabled": false, "repoOwner": "Susguine", "repoName": "DecoSOP", "checkIntervalHours": 24, "autoInstall": false, "autoInstallTime": "02:00" }';
end;

// ---- Service detection ----

function ServiceExists: Boolean;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'query DecoSOP', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// ---- LibreOffice detection ----

function IsLibreOfficeInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf}\LibreOffice\program\soffice.exe'))
         or FileExists(ExpandConstant('{commonpf32}\LibreOffice\program\soffice.exe'));
end;

function ShouldInstallLibreOffice: Boolean;
begin
  Result := (LibreOfficeCheckbox <> nil) and LibreOfficeCheckbox.Checked and (not LibreOfficeDetected);
end;

// ---- LibreOffice download progress callback ----

function LibreOfficeDownloadProgress(const Url, FileName: string; const Progress, ProgressMax: Int64): Boolean;
var
  Pct: Integer;
begin
  if ProgressMax > 0 then
  begin
    Pct := (Progress * 100) div ProgressMax;
    WizardForm.StatusLabel.Caption := Format('Downloading LibreOffice... %d%%', [Pct]);
  end
  else
    WizardForm.StatusLabel.Caption := 'Downloading LibreOffice...';
  Result := True;
end;

// ---- Existing install detection ----

function GetUninstallString: String;
var
  UninstallKey: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D3C0-50F1-4A2B-B8E9-DecoSOP-1000}}_is1';
  Result := '';
  RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', Result);
end;

function GetInstalledVersion: String;
var
  UninstallKey: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D3C0-50F1-4A2B-B8E9-DecoSOP-1000}}_is1';
  Result := '';
  RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result);
end;

function InitializeSetup: Boolean;
var
  InstalledVersion: String;
  UninstallString: String;
  Msg: String;
  Choice: Integer;
  ResultCode: Integer;
begin
  Result := True;

  InstalledVersion := GetInstalledVersion;
  if InstalledVersion <> '' then
  begin
    if InstalledVersion = '{#MyAppVersion}' then
      Msg := 'DecoSOP v' + InstalledVersion + ' is already installed.' + #13#10 + #13#10 +
             'Yes = Reinstall (keep database and files)' + #13#10 +
             'No = Uninstall (remove application)' + #13#10 +
             'Cancel = Exit setup'
    else
      Msg := 'DecoSOP v' + InstalledVersion + ' is already installed.' + #13#10 + #13#10 +
             'Yes = Upgrade to v{#MyAppVersion} (keep database and files)' + #13#10 +
             'No = Uninstall v' + InstalledVersion + ' (remove application)' + #13#10 +
             'Cancel = Exit setup';

    Choice := MsgBox(Msg, mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1);

    if Choice = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;

    UninstallString := GetUninstallString;

    if Choice = IDNO then
    begin
      // Uninstall: run uninstaller visibly, then exit
      if UninstallString <> '' then
      begin
        if (Length(UninstallString) > 1) and (UninstallString[1] = '"') then
          UninstallString := Copy(UninstallString, 2, Length(UninstallString) - 2);

        Exec(UninstallString, '/NORESTART', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
      end;
      Result := False;
      Exit;
    end;

    // Yes = Upgrade/Reinstall: silently remove old service/firewall, then continue
    IsUpgradeInstall := True;
    if UninstallString <> '' then
    begin
      if (Length(UninstallString) > 1) and (UninstallString[1] = '"') then
        UninstallString := Copy(UninstallString, 2, Length(UninstallString) - 2);

      Exec(UninstallString, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

// ---- Browse button handlers for optional directory pages ----

procedure BrowseImportSopDir(Sender: TObject);
var
  Dir: String;
begin
  Dir := ImportSopDirEdit.Text;
  if BrowseForFolder('Select the SOP uploads folder:', Dir, False) then
    ImportSopDirEdit.Text := Dir;
end;

procedure BrowseImportDocDir(Sender: TObject);
var
  Dir: String;
begin
  Dir := ImportDocDirEdit.Text;
  if BrowseForFolder('Select the Document uploads folder:', Dir, False) then
    ImportDocDirEdit.Text := Dir;
end;

procedure BrowseScanDocDir(Sender: TObject);
var
  Dir: String;
begin
  Dir := ScanDocDirEdit.Text;
  if BrowseForFolder('Select the Document source folder:', Dir, False) then
    ScanDocDirEdit.Text := Dir;
end;

// ---- Update page event handlers ----

procedure UpdateRadioClick(Sender: TObject);
var
  Enabled: Boolean;
begin
  Enabled := UpdateEnableRadio.Checked;
  AutoInstallCheckbox.Enabled := Enabled;
  if not Enabled then
  begin
    AutoInstallCheckbox.Checked := False;
    AutoInstallTimeLabel.Enabled := False;
    AutoInstallTimeCombo.Enabled := False;
  end;
end;

procedure AutoInstallCheckboxClick(Sender: TObject);
var
  Enabled: Boolean;
begin
  Enabled := AutoInstallCheckbox.Checked;
  AutoInstallTimeLabel.Enabled := Enabled;
  AutoInstallTimeCombo.Enabled := Enabled;
end;

// ---- Wizard pages ----

procedure InitializeWizard;
begin
  // Page 1: Port configuration (after directory selection)
  PortPage := CreateInputQueryPage(
    wpSelectDir,
    'Port Configuration',
    'Choose the port DecoSOP will listen on.',
    'Enter the TCP port number (default: 5098).' + #13#10 +
    'Make sure this port is not already in use by another application.' + #13#10 + #13#10 +
    'Other computers on your network will access DecoSOP at:' + #13#10 +
    '  http://your-computer-name:PORT');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '5098';

  // Page 2: Database setup (after port)
  DatabasePage := CreateInputOptionPage(
    PortPage.ID,
    'Database Setup',
    'Choose how to initialize the database.',
    'DecoSOP stores all SOPs, documents, and categories in a local database.' + #13#10 +
    'Select how you would like to start:',
    True, False);
  DatabasePage.Add('Empty database (start fresh — add your own content)');
  DatabasePage.Add('Demo database (sample SOPs and documents to explore the app)');
  DatabasePage.Add('Import a database backup (.db file from a previous installation)');
  DatabasePage.SelectedValueIndex := 0;

  // Page 3: Import file picker (after database — only shown if Import selected)
  ImportDbPage := CreateInputFilePage(
    DatabasePage.ID,
    'Import Database',
    'Select a database backup file to restore.',
    'Choose the .db file from a previous DecoSOP installation or backup.' + #13#10 +
    'You can export a backup from Settings in DecoSOP at any time.');
  ImportDbPage.Add('Database file:', '*.db|*.db', '.db');

  // Page 4: SOP files directory (after db import — only shown if Import selected)
  ImportSopDirPage := CreateCustomPage(
    ImportDbPage.ID,
    'Import SOP Files',
    'Optionally import your SOP upload files.');
  with TNewStaticText.Create(ImportSopDirPage) do
  begin
    Parent := ImportSopDirPage.Surface;
    Caption := 'If you have a previous DecoSOP installation, select the sop-uploads folder' + #13#10 +
               'to restore your uploaded SOP files.' + #13#10 + #13#10 +
               'Leave blank to skip this step.';
    Left := 0;
    Top := 0;
    Width := ImportSopDirPage.SurfaceWidth;
    WordWrap := True;
    AutoSize := True;
  end;
  with TNewStaticText.Create(ImportSopDirPage) do
  begin
    Parent := ImportSopDirPage.Surface;
    Caption := 'SOP uploads folder (e.g. C:\DecoSOP\sop-uploads):';
    Left := 0;
    Top := 76;
  end;
  ImportSopDirEdit := TNewEdit.Create(ImportSopDirPage);
  with ImportSopDirEdit do
  begin
    Parent := ImportSopDirPage.Surface;
    Left := 0;
    Top := 96;
    Width := ImportSopDirPage.SurfaceWidth - 90;
    Text := '';
  end;
  with TNewButton.Create(ImportSopDirPage) do
  begin
    Parent := ImportSopDirPage.Surface;
    Caption := 'Browse...';
    Left := ImportSopDirPage.SurfaceWidth - 85;
    Top := 94;
    Width := 85;
    Height := 25;
    OnClick := @BrowseImportSopDir;
  end;

  // Page 5: Documents directory (after SOP dir — only shown if Import selected)
  ImportDocDirPage := CreateCustomPage(
    ImportSopDirPage.ID,
    'Import Document Files',
    'Optionally import your Document upload files.');
  with TNewStaticText.Create(ImportDocDirPage) do
  begin
    Parent := ImportDocDirPage.Surface;
    Caption := 'If you have a previous DecoSOP installation, select the doc-uploads folder' + #13#10 +
               'to restore your uploaded document files.' + #13#10 + #13#10 +
               'Leave blank to skip this step.';
    Left := 0;
    Top := 0;
    Width := ImportDocDirPage.SurfaceWidth;
    WordWrap := True;
    AutoSize := True;
  end;
  with TNewStaticText.Create(ImportDocDirPage) do
  begin
    Parent := ImportDocDirPage.Surface;
    Caption := 'Document uploads folder (e.g. C:\DecoSOP\doc-uploads):';
    Left := 0;
    Top := 76;
  end;
  ImportDocDirEdit := TNewEdit.Create(ImportDocDirPage);
  with ImportDocDirEdit do
  begin
    Parent := ImportDocDirPage.Surface;
    Left := 0;
    Top := 96;
    Width := ImportDocDirPage.SurfaceWidth - 90;
    Text := '';
  end;
  with TNewButton.Create(ImportDocDirPage) do
  begin
    Parent := ImportDocDirPage.Surface;
    Caption := 'Browse...';
    Left := ImportDocDirPage.SurfaceWidth - 85;
    Top := 94;
    Width := 85;
    Height := 25;
    OnClick := @BrowseImportDocDir;
  end;

  // Page 6: Scan SOP source dir (after import dirs — only shown if Scan selected)
  ScanSopDirPage := CreateInputDirPage(
    ImportDocDirPage.ID,
    'Scan SOP Files',
    'Select a folder containing your SOP files.',
    'DecoSOP will scan the selected folder and its subfolders.' + #13#10 +
    'Subfolders become categories, and matching files (PDF, Word, Excel, etc.)' + #13#10 +
    'are imported as SOPs.' + #13#10 + #13#10 +
    'Archive and temporary folders are automatically skipped.',
    False, '');
  ScanSopDirPage.Add('SOP source folder (e.g. S:\SOPs):');
  ScanSopDirPage.Values[0] := '';

  // Page 7: Scan Documents source dir (after scan SOP — only shown if Scan selected)
  ScanDocDirPage := CreateCustomPage(
    ScanSopDirPage.ID,
    'Scan Document Files',
    'Optionally select a folder containing your document files.');
  with TNewStaticText.Create(ScanDocDirPage) do
  begin
    Parent := ScanDocDirPage.Surface;
    Caption := 'DecoSOP will scan the selected folder and its subfolders.' + #13#10 +
               'Subfolders become categories, and matching files are imported as Documents.' + #13#10 + #13#10 +
               'Leave blank to skip document scanning.';
    Left := 0;
    Top := 0;
    Width := ScanDocDirPage.SurfaceWidth;
    WordWrap := True;
    AutoSize := True;
  end;
  with TNewStaticText.Create(ScanDocDirPage) do
  begin
    Parent := ScanDocDirPage.Surface;
    Caption := 'Document source folder (e.g. S:\Documents):';
    Left := 0;
    Top := 76;
  end;
  ScanDocDirEdit := TNewEdit.Create(ScanDocDirPage);
  with ScanDocDirEdit do
  begin
    Parent := ScanDocDirPage.Surface;
    Left := 0;
    Top := 96;
    Width := ScanDocDirPage.SurfaceWidth - 90;
    Text := '';
  end;
  with TNewButton.Create(ScanDocDirPage) do
  begin
    Parent := ScanDocDirPage.Surface;
    Caption := 'Browse...';
    Left := ScanDocDirPage.SurfaceWidth - 85;
    Top := 94;
    Width := 85;
    Height := 25;
    OnClick := @BrowseScanDocDir;
  end;

  // Page 8: Auto-update preference (after scan dirs)
  UpdatePage := CreateCustomPage(
    ScanDocDirPage.ID,
    'Automatic Updates',
    'Choose whether DecoSOP should check for updates.');

  with TNewStaticText.Create(UpdatePage) do
  begin
    Parent := UpdatePage.Surface;
    Caption := 'When enabled, DecoSOP will periodically check GitHub for new releases' + #13#10 +
               'and show a notification in the app when an update is available.' + #13#10 + #13#10 +
               'No data is sent — it only checks the public release page.' + #13#10 +
               'You can change these settings later in the app.';
    Left := 0;
    Top := 0;
    Width := UpdatePage.SurfaceWidth;
    WordWrap := True;
    AutoSize := True;
  end;

  UpdateEnableRadio := TNewRadioButton.Create(UpdatePage);
  with UpdateEnableRadio do
  begin
    Parent := UpdatePage.Surface;
    Caption := 'Enable automatic update checks (recommended)';
    Left := 0;
    Top := 80;
    Width := UpdatePage.SurfaceWidth;
    Checked := True;
    OnClick := @UpdateRadioClick;
  end;

  UpdateDisableRadio := TNewRadioButton.Create(UpdatePage);
  with UpdateDisableRadio do
  begin
    Parent := UpdatePage.Surface;
    Caption := 'Disable automatic update checks';
    Left := 0;
    Top := 102;
    Width := UpdatePage.SurfaceWidth;
    OnClick := @UpdateRadioClick;
  end;

  AutoInstallCheckbox := TNewCheckBox.Create(UpdatePage);
  with AutoInstallCheckbox do
  begin
    Parent := UpdatePage.Surface;
    Caption := 'Automatically install updates when available';
    Left := 20;
    Top := 136;
    Width := UpdatePage.SurfaceWidth - 20;
    Checked := False;
    OnClick := @AutoInstallCheckboxClick;
  end;

  AutoInstallTimeLabel := TNewStaticText.Create(UpdatePage);
  with AutoInstallTimeLabel do
  begin
    Parent := UpdatePage.Surface;
    Caption := 'Install time:';
    Left := 40;
    Top := 164;
    Enabled := False;
  end;

  AutoInstallTimeCombo := TNewComboBox.Create(UpdatePage);
  with AutoInstallTimeCombo do
  begin
    Parent := UpdatePage.Surface;
    Left := 110;
    Top := 160;
    Width := 120;
    Style := csDropDownList;
    Items.Add('12:00 AM');
    Items.Add('1:00 AM');
    Items.Add('2:00 AM');
    Items.Add('3:00 AM');
    Items.Add('4:00 AM');
    Items.Add('5:00 AM');
    Items.Add('6:00 AM');
    Items.Add('7:00 AM');
    Items.Add('8:00 AM');
    Items.Add('9:00 AM');
    Items.Add('10:00 AM');
    Items.Add('11:00 AM');
    Items.Add('12:00 PM');
    Items.Add('1:00 PM');
    Items.Add('2:00 PM');
    Items.Add('3:00 PM');
    Items.Add('4:00 PM');
    Items.Add('5:00 PM');
    Items.Add('6:00 PM');
    Items.Add('7:00 PM');
    Items.Add('8:00 PM');
    Items.Add('9:00 PM');
    Items.Add('10:00 PM');
    Items.Add('11:00 PM');
    ItemIndex := 2;  // Default: 2:00 AM
    Enabled := False;
  end;

  // Page 9: LibreOffice (after auto-update — optional download for Office doc previews)
  LibreOfficeDetected := IsLibreOfficeInstalled;

  LibreOfficePage := CreateCustomPage(
    UpdatePage.ID,
    'Office Document Previews',
    'LibreOffice enables inline previews of Word, Excel, and PowerPoint files.');

  if LibreOfficeDetected then
  begin
    with TNewStaticText.Create(LibreOfficePage) do
    begin
      Parent := LibreOfficePage.Surface;
      Caption := 'LibreOffice is already installed on this computer.' + #13#10 + #13#10 +
                 'Office documents (Word, Excel, PowerPoint) will be converted to PDF' + #13#10 +
                 'automatically for inline preview in the browser.' + #13#10 + #13#10 +
                 'No additional action is needed.';
      Left := 0;
      Top := 0;
      Width := LibreOfficePage.SurfaceWidth;
      WordWrap := True;
      AutoSize := True;
    end;
  end
  else
  begin
    with TNewStaticText.Create(LibreOfficePage) do
    begin
      Parent := LibreOfficePage.Surface;
      Caption := 'DecoSOP can show inline previews of Office documents (Word, Excel,' + #13#10 +
                 'PowerPoint) by converting them to PDF using LibreOffice.' + #13#10 + #13#10 +
                 'Without LibreOffice, Office documents will still be available for' + #13#10 +
                 'download but cannot be previewed in the browser.' + #13#10 + #13#10 +
                 'LibreOffice is free and open-source (approx. 350 MB download).';
      Left := 0;
      Top := 0;
      Width := LibreOfficePage.SurfaceWidth;
      WordWrap := True;
      AutoSize := True;
    end;

    LibreOfficeCheckbox := TNewCheckBox.Create(LibreOfficePage);
    with LibreOfficeCheckbox do
    begin
      Parent := LibreOfficePage.Surface;
      Caption := 'Download and install LibreOffice (recommended)';
      Left := 0;
      Top := 120;
      Width := LibreOfficePage.SurfaceWidth;
      Checked := True;
    end;

    LibreOfficeStatusLabel := TNewStaticText.Create(LibreOfficePage);
    with LibreOfficeStatusLabel do
    begin
      Parent := LibreOfficePage.Surface;
      Caption := '';
      Left := 0;
      Top := 150;
      Width := LibreOfficePage.SurfaceWidth;
      WordWrap := True;
      AutoSize := True;
    end;
  end;
end;

// ---- Import with real-time progress polling ----

procedure RunImportWithProgress(ExeParams, InitialMsg: String);
var
  StatusFile: String;
  StatusText: AnsiString;
  ResultCode: Integer;
  ElapsedMs: Integer;
begin
  StatusFile := ExpandConstant('{app}\import-status.txt');
  DeleteFile(StatusFile);

  WizardForm.StatusLabel.Caption := InitialMsg;
  WizardForm.FilenameLabel.Caption := '';
  WizardForm.Refresh;

  // Launch import process in background
  Exec(ExpandConstant('{app}\{#MyAppExeName}'), ExeParams, '', SW_HIDE, ewNoWait, ResultCode);

  // Poll status file until COMPLETE or timeout (60 min)
  ElapsedMs := 0;
  while ElapsedMs < 3600000 do
  begin
    Sleep(500);
    ElapsedMs := ElapsedMs + 500;

    if LoadStringFromFile(StatusFile, StatusText) then
    begin
      if Pos('COMPLETE', String(StatusText)) > 0 then
        Break;
      if Length(StatusText) > 0 then
      begin
        WizardForm.StatusLabel.Caption := InitialMsg + ' (' + String(StatusText) + ')';
        WizardForm.Refresh;
      end;
    end;
  end;

  DeleteFile(StatusFile);
end;

// ---- Post-install: imports + LibreOffice ----

procedure CurStepChanged(CurStep: TSetupStep);
var
  MsiPath: String;
  ResultCode: Integer;
  DownloadSize: Int64;
begin
  if CurStep = ssPostInstall then
  begin
    // Download and install LibreOffice if selected
    if ShouldInstallLibreOffice then
    begin
      WizardForm.StatusLabel.Caption := 'Downloading LibreOffice (this may take several minutes)...';
      WizardForm.FilenameLabel.Caption := '{#LibreOfficeURL}';

      try
        DownloadSize := DownloadTemporaryFile('{#LibreOfficeURL}', '{#LibreOfficeFileName}', '', @LibreOfficeDownloadProgress);
        if DownloadSize = 0 then
        begin
          MsgBox('LibreOffice download failed. You can install LibreOffice manually later from https://www.libreoffice.org', mbError, MB_OK);
          Exit;
        end;
      except
        MsgBox('LibreOffice download failed. You can install LibreOffice manually later from https://www.libreoffice.org', mbError, MB_OK);
        Exit;
      end;

      MsiPath := ExpandConstant('{tmp}\{#LibreOfficeFileName}');
      WizardForm.StatusLabel.Caption := 'Installing LibreOffice (this may take a few minutes)...';
      WizardForm.FilenameLabel.Caption := MsiPath;

      if not Exec('msiexec.exe', '/i "' + MsiPath + '" /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        MsgBox('LibreOffice installation failed. You can install it manually later from https://www.libreoffice.org', mbError, MB_OK);
      end
      else if ResultCode <> 0 then
      begin
        MsgBox('LibreOffice installation returned an error (code ' + IntToStr(ResultCode) + '). You can install it manually later from https://www.libreoffice.org', mbError, MB_OK);
      end;

      DeleteFile(MsiPath);
    end;
  end;
end;

// ---- Page visibility + validation ----

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  // On upgrade, skip database setup and all import/scan pages (DB already exists)
  if IsUpgradeInstall then
  begin
    if (DatabasePage <> nil) and (PageID = DatabasePage.ID) then
      Result := True;
    if (ImportDbPage <> nil) and (PageID = ImportDbPage.ID) then
      Result := True;
    if (ImportSopDirPage <> nil) and (PageID = ImportSopDirPage.ID) then
      Result := True;
    if (ImportDocDirPage <> nil) and (PageID = ImportDocDirPage.ID) then
      Result := True;
    if (ScanSopDirPage <> nil) and (PageID = ScanSopDirPage.ID) then
      Result := True;
    if (ScanDocDirPage <> nil) and (PageID = ScanDocDirPage.ID) then
      Result := True;
    Exit;
  end;

  // Skip import backup pages unless "Import backup" is selected
  if (ImportDbPage <> nil) and (PageID = ImportDbPage.ID) then
    Result := not ShouldImportDb;

  if (ImportSopDirPage <> nil) and (PageID = ImportSopDirPage.ID) then
    Result := not ShouldImportDb;

  if (ImportDocDirPage <> nil) and (PageID = ImportDocDirPage.ID) then
    Result := not ShouldImportDb;

  // Skip scan directory pages unless "Import from file directories" is selected
  if (ScanSopDirPage <> nil) and (PageID = ScanSopDirPage.ID) then
    Result := not ShouldScanFiles;

  if (ScanDocDirPage <> nil) and (PageID = ScanDocDirPage.ID) then
    Result := not ShouldScanFiles;

  // Skip LibreOffice page if already installed
  if (LibreOfficePage <> nil) and (PageID = LibreOfficePage.ID) then
    Result := LibreOfficeDetected;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: String;
  PortNum: Integer;
  DbFile: String;
begin
  Result := True;

  // Validate port number
  if CurPageID = PortPage.ID then
  begin
    Port := PortPage.Values[0];
    if Port = '' then
    begin
      PortPage.Values[0] := '5098';
      Exit;
    end;

    PortNum := StrToIntDef(Port, 0);
    if (PortNum < 1) or (PortNum > 65535) then
    begin
      MsgBox('Please enter a valid port number between 1 and 65535.', mbError, MB_OK);
      Result := False;
    end;
  end;

  // Validate import file selection
  if (ImportDbPage <> nil) and (CurPageID = ImportDbPage.ID) then
  begin
    DbFile := ImportDbPage.Values[0];
    if DbFile = '' then
    begin
      MsgBox('Please select a database file to import.', mbError, MB_OK);
      Result := False;
    end
    else if not FileExists(DbFile) then
    begin
      MsgBox('The selected file does not exist. Please choose a valid .db file.', mbError, MB_OK);
      Result := False;
    end;
  end;

  // Validate optional import SOP dir (non-empty must exist)
  if (ImportSopDirPage <> nil) and (CurPageID = ImportSopDirPage.ID) then
  begin
    if (ImportSopDirEdit.Text <> '') and not DirExists(ImportSopDirEdit.Text) then
    begin
      MsgBox('The selected folder does not exist. Please choose a valid directory or leave blank to skip.', mbError, MB_OK);
      Result := False;
    end;
  end;

  // Validate optional import doc dir (non-empty must exist)
  if (ImportDocDirPage <> nil) and (CurPageID = ImportDocDirPage.ID) then
  begin
    if (ImportDocDirEdit.Text <> '') and not DirExists(ImportDocDirEdit.Text) then
    begin
      MsgBox('The selected folder does not exist. Please choose a valid directory or leave blank to skip.', mbError, MB_OK);
      Result := False;
    end;
  end;

  // Validate scan SOP directory (required when scan option selected)
  if (ScanSopDirPage <> nil) and (CurPageID = ScanSopDirPage.ID) then
  begin
    if (ScanSopDirPage.Values[0] = '') then
    begin
      MsgBox('Please select a folder containing your SOP files to scan.', mbError, MB_OK);
      Result := False;
    end
    else if not DirExists(ScanSopDirPage.Values[0]) then
    begin
      MsgBox('The selected folder does not exist. Please choose a valid directory.', mbError, MB_OK);
      Result := False;
    end;
  end;

  // Validate optional scan doc dir (non-empty must exist, must not overlap with SOP dir)
  if (ScanDocDirPage <> nil) and (CurPageID = ScanDocDirPage.ID) then
  begin
    if (ScanDocDirEdit.Text <> '') then
    begin
      if not DirExists(ScanDocDirEdit.Text) then
      begin
        MsgBox('The selected folder does not exist. Please choose a valid directory or leave blank to skip.', mbError, MB_OK);
        Result := False;
      end
      else if (CompareText(ScanDocDirEdit.Text, GetScanSopDirPath('')) = 0) then
      begin
        MsgBox('The Document folder cannot be the same as the SOP folder. Please choose a different directory or leave blank to skip.', mbError, MB_OK);
        Result := False;
      end
      else if (CompareText(Copy(ScanDocDirEdit.Text, 1, Length(GetScanSopDirPath(''))), GetScanSopDirPath('')) = 0) or
              (CompareText(Copy(GetScanSopDirPath(''), 1, Length(ScanDocDirEdit.Text)), ScanDocDirEdit.Text) = 0) then
      begin
        MsgBox('The Document folder overlaps with the SOP folder (one is inside the other). Please choose a non-overlapping directory or leave blank to skip.', mbError, MB_OK);
        Result := False;
      end;
    end;
  end;
end;
