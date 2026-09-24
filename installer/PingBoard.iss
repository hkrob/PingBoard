; PingBoard installer (Inno Setup 6).
;
; Build the payload first, then compile this:
;
;   dotnet publish src/PingBoard.App -c Release -r win-x64 -o dist
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\PingBoard.iss
;
; Output lands in installer\output\PingBoard-<version>-setup.exe.

#define AppName        "PingBoard"
#define AppVersion     "1.11.17"
#define AppPublisher   "hkrob"
#define AppExeName     "PingBoard.App.exe"
#define AppUrl         "https://github.com/hkrob/PingBoard"
#define PayloadDir     "..\dist"

[Setup]
AppId={{8F2B9C41-7D3E-4A56-9B18-2E7C5D0A4F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

; Per-user install into %LocalAppData%\Programs. This is the single most useful choice here:
; it needs no elevation, so there is no UAC prompt to install or to update, and the autostart
; entry it writes is a per-user HKCU value that matches. A tray utility does not need to be
; installed for every account on the machine, and asking for admin to get one is a poor trade.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Upgrades reuse whatever the previous install chose, rather than asking again. Both default to
; yes; stated explicitly because the whole point of the [Code] section below is that a re-run
; should behave as an update, and that only holds if these do.
UsePreviousAppDir=yes
UsePreviousTasks=yes

; Skipped on an upgrade only - see ShouldSkipPage. A first install still gets to confirm.
DisableReadyPage=no

; The payload is ~220 MB of self-contained .NET and Windows App SDK runtime. LZMA2/max roughly
; halves it, at the cost of a slower compile — worth it for something people download.
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

OutputDir=output
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\src\PingBoard.App\Assets\pingboard.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern

; The app is x64-only (Platforms=x64, RuntimeIdentifier=win-x64) and WinUI 3 needs 10.0.19041
; to match TargetPlatformMinVersion. Refusing early beats failing at InitializeComponent.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostart";  Description: "Start {#AppName} automatically when I sign in"; GroupDescription: "Startup:"

[Files]
; The whole publish tree. resources.pri must be among it — the app dies at InitializeComponent
; without it, which is why the csproj has an explicit copy target guarded by an Error.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";              Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}";    Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";        Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Matches Autostart.cs exactly — same key, same value name, same --minimized switch — so the
; in-app "Start with Windows" toggle and this checkbox control one setting rather than two that
; disagree. uninsdeletevalue removes it on uninstall; without that a deleted app keeps trying to
; launch at every login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "PingBoard"; \
    ValueData: """{app}\{#AppExeName}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Deliberately not "filesandordirs {app}". The directory page lets the user type any folder, and
; typing an existing one (C:\Tools, say) installs straight into it - after which a wholesale delete
; of {app} on uninstall would take every unrelated file in that folder with it. The app writes
; nothing into its own folder at runtime (crash.log, config and state all live under %AppData%), so
; the uninstaller's own record of what it installed is already complete; this only removes the
; folder itself once it is empty.
Type: dirifempty; Name: "{app}"

[Code]

{ Inno records its own uninstall entry under this key. Reading it back is how we tell an upgrade
  from a first install - there is no built-in "is this an update" flag. HKCU first because this is
  a per-user install; HKLM as a fallback in case someone once elevated through the
  PrivilegesRequiredOverridesAllowed dialog. }

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F2B9C41-7D3E-4A56-9B18-2E7C5D0A4F63}_is1';

function InstalledVersion(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result) then
      Result := '';
end;

function IsUpgrade(): Boolean;
begin
  Result := InstalledVersion() <> '';
end;

{ The Nth dot-separated component of a "1.11.14"-style version string, or 0 past the end - so
  comparing "1.9" against "1.10.0" still reads as component-wise, not as a string. }
function VersionPart(const S: String; Index: Integer): Integer;
var
  Work, Piece: String;
  DotPos, I: Integer;
begin
  Work := S;
  Piece := '';
  for I := 0 to Index do
  begin
    DotPos := Pos('.', Work);
    if DotPos = 0 then
    begin
      Piece := Work;
      Work := '';
    end
    else
    begin
      Piece := Copy(Work, 1, DotPos - 1);
      Work := Copy(Work, DotPos + 1, Length(Work));
    end;
  end;
  Result := StrToIntDef(Piece, 0);
end;

{ >0 when A is newer than B, 0 when equal, <0 when A is older. }
function CompareVersions(const A, B: String): Integer;
var
  I: Integer;
begin
  Result := 0;
  for I := 0 to 2 do
  begin
    Result := VersionPart(A, I) - VersionPart(B, I);
    if Result <> 0 then Exit;
  end;
end;

{ An upgrade should not re-ask questions that were answered the last time. UsePreviousTasks already
  carries the answers forward, so showing the page again only invites the user to change something
  by accident - and makes a routine update look like a fresh install, which is exactly the
  confusion this removes. A first install still sees every page. }

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsUpgrade() and ((PageID = wpSelectTasks) or (PageID = wpReady));
end;

procedure InitializeWizard();
begin
  if IsUpgrade() then
  begin
    WizardForm.Caption := 'Update {#AppName} to {#AppVersion}';
    WizardForm.WelcomeLabel1.Caption := 'Updating {#AppName}';
    WizardForm.WelcomeLabel2.Caption :=
      'Version ' + InstalledVersion() + ' is installed. This will replace it with {#AppVersion}.' + #13#10 + #13#10 +
      'Your targets, statistics and settings are kept - they live in %AppData%\{#AppName}, which Setup does not touch.';
  end;
end;

{ The app hides to the tray rather than exiting, so an upgrade will usually find the previous
  version still running and holding its files open. Close it first — silently, since the user
  already agreed to install. User data in %AppData%\PingBoard is deliberately left alone. }

{ Unconditional rather than gated on CheckForMutexes: single-instance here is handled by
  AppInstance.FindOrRegisterForKey, not a named mutex, so there is nothing for Inno to detect.
  taskkill on a process that is not running is a harmless no-op. }

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
  Installed: String;
begin
  Installed := InstalledVersion();

  { IsUpgrade() only asks "is anything installed", not "which is newer" - so a stale installer
    run over a newer install (an old download, an old link) would present as a routine update and
    silently downgrade. Ask first; the uninstall registry entry has no notion of direction. }
  if (Installed <> '') and (CompareVersions(Installed, '{#AppVersion}') > 0) then
  begin
    if MsgBox('Version ' + Installed + ' is already installed, which is newer than this installer ' +
              '({#AppVersion}). Continue and downgrade anyway?', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;

  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

