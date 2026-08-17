; PingBoard installer (Inno Setup 6).
;
; Build the payload first, then compile this:
;
;   dotnet publish src/PingBoard.App -c Release -r win-x64 -o dist
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\PingBoard.iss
;
; Output lands in installer\output\PingBoard-<version>-setup.exe.

#define AppName        "PingBoard"
#define AppVersion     "1.11.10"
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
; Publishing writes nothing outside {app}, but the app itself may leave a crash log beside its
; binaries if the data directory was ever unwritable.
Type: filesandordirs; Name: "{app}"

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
begin
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

