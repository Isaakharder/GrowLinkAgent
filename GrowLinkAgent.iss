; ============================================================
;  GrowLinkAgent — Inno Setup installer (per user, no administrator rights)
;
;  Build (on Windows; CI: .github/workflows/windows-installer.yml):
;    dotnet publish GrowLinkAgent.csproj -c Release -o publish
;    ISCC GrowLinkAgent.iss [/DAppVersion=1.1.0] [/DSourceDir=publish] [/DOutputDir=installer]
;
;  Per user: installs for the signed-in Windows user only, never asks for an
;  administrator password or UAC elevation. Settings, the encrypted upload keys
;  and logs live in %APPDATA%\GrowLinkAgent; this installer never writes, moves
;  or deletes them (not even on uninstall), and never touches watch folders or
;  their Uploaded/Failed files.
; ============================================================

#define AppName      "GrowLinkAgent"
#define AppPublisher "GrowLink"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#define AppExeName   "GrowLinkAgent.exe"
#ifndef SourceDir
  #define SourceDir  "publish"
#endif
#ifndef OutputDir
  #define OutputDir  "installer"
#endif
; Must match the AppId of every earlier release so Setup upgrades in place.
#define AppIdGuid    "{C7A4F8B2-3E5D-4A6C-9B1E-0F2D3C4E5B6A}"

[Setup]
; IMPORTANT: Keep AppId unchanged across all future releases.
AppId={{C7A4F8B2-3E5D-4A6C-9B1E-0F2D3C4E5B6A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}

; No administrator rights, ever: install for the current user only, and don't
; offer an "install for all users" choice.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; New installs: %LOCALAPPDATA%\Programs\GrowLinkAgent. An upgrade keeps the folder
; the earlier version was installed in (1.0.x used %LOCALAPPDATA%\GrowLinkAgent),
; so there is never a second copy.
DefaultDirName={userpf}\{#AppName}
UsePreviousAppDir=yes
DisableDirPage=auto
DefaultGroupName={#AppPublisher}
DisableProgramGroupPage=yes

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Assets\App.ico

; Target 64-bit Windows only (required by .NET 8 x64 publish)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; A running agent in the install folder is closed (Windows Restart Manager) before
; its files are replaced, and started again at the end (see [Run]).
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

; Uninstall entry in Apps & Features (per user)
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; Self-contained build — no .NET runtime required. Windows 10 1607 or later.
MinVersion=10.0.14393

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Start with Windows: a shortcut in this user's Startup folder (no administrator rights).
; On an upgrade, the choice made at the earlier install is kept.
Name: startup; Description: "Start {#AppName} automatically when I sign in to Windows"; GroupDescription: "Additional options:"

[Files]
; Everything from the publish folder. config.json and the encrypted key files are
; created by the app in %APPDATA%\GrowLinkAgent and are never part of the install.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";       Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
; Start the agent when setup finishes (also after a silent upgrade, so watching resumes).
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall

[UninstallRun]
; Close this user's agent before its files are removed.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /FI ""USERNAME eq {username}"" /F"; Flags: runhidden; RunOnceId: "KillBeforeUninstall"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdGuid}_is1';

{ A system-wide (administrator) installation of GrowLinkAgent, if any. }
function SystemWideInstall(): String;
var
  Location: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Location) or
     RegQueryStringValue(HKLM32, UninstallKey, 'InstallLocation', Location) then
    Result := Location
  else if RegKeyExists(HKLM64, UninstallKey) or RegKeyExists(HKLM32, UninstallKey) then
    Result := '(location not recorded)'
  else if FileExists(ExpandConstant('{commonpf64}\{#AppName}\{#AppExeName}')) then
    Result := ExpandConstant('{commonpf64}\{#AppName}')
  else if FileExists(ExpandConstant('{commonpf32}\{#AppName}\{#AppExeName}')) then
    Result := ExpandConstant('{commonpf32}\{#AppName}');
end;

{ GrowLinkAgent processes running from anywhere other than TargetDir ('' if none).
  Paths of other users' processes can't be read without administrator rights:
  those are reported as "another Windows user". }
function AgentsRunningElsewhere(TargetDir: String): String;
var
  Locator, Service, Processes, Process: Variant;
  I: Integer;
  Path: String;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''{#AppExeName}''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      if VarIsNull(Process.ExecutablePath) then
        Path := ''
      else
        Path := Process.ExecutablePath;
      if Path = '' then
        Result := Result + #13#10 + '  - running for another Windows user'
      else if CompareText(ExtractFileDir(Path), RemoveBackslashUnlessRoot(TargetDir)) <> 0 then
        Result := Result + #13#10 + '  - ' + Path;
    end;
  except
    { WMI unavailable: the agent's own single-instance check still stops a second copy. }
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  SystemWide, Elsewhere: String;
begin
  Result := '';
  SystemWide := SystemWideInstall();
  if SystemWide <> '' then
  begin
    Result := 'GrowLinkAgent is already installed for all users (with administrator rights) in:' + #13#10 +
      '  ' + SystemWide + #13#10#13#10 +
      'Installing a second, per-user copy could run two agents that upload the same files.' + #13#10 +
      'Setup has made no changes. Please follow the transition steps (stop and remove the ' +
      'system-wide copy with an administrator, keeping %APPDATA%\GrowLinkAgent), then run this setup again.';
    Exit;
  end;
  Elsewhere := AgentsRunningElsewhere(WizardDirValue());
  if Elsewhere <> '' then
    Result := 'GrowLinkAgent is running from another location:' + Elsewhere + #13#10#13#10 +
      'Close it first (right-click its icon in the notification area > Exit) so that only one agent ' +
      'runs. Setup has made no changes.';
end;
