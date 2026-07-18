; ============================================================
;  GrowLinkAgent — Inno Setup Installer Script
;  App:       GrowLinkAgent 1.0.0
;  Publisher: GrowLink
;  Build:     dotnet publish -c Release -o publish\
; ============================================================

#define AppName      "GrowLinkAgent"
#define AppPublisher "GrowLink"
#define AppVersion   "1.0.0"
#define AppExeName   "GrowLinkAgent.exe"
#define SourceDir    "c:\GrowLinkAgent\publish"

[Setup]
; IMPORTANT: Keep AppId unchanged across all future releases.
; Inno Setup uses this GUID to detect and upgrade an existing installation.
AppId={{C7A4F8B2-3E5D-4A6C-9B1E-0F2D3C4E5B6A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppPublisher}
DisableProgramGroupPage=yes
OutputDir=c:\GrowLinkAgent
OutputBaseFilename=GrowLinkAgent-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Assets\App.ico

; No admin rights needed — installs under the current user only
PrivilegesRequired=lowest

; Target 64-bit Windows only (required by .NET 8 x64 publish)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Close any running instance before installing/upgrading
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

; Uninstall entry in Apps & Features
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; Version metadata embedded in the setup exe
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; Self-contained build — no .NET runtime required on the target machine.
; Windows 10 1607 (build 14393) or later required by .NET 8 WinForms APIs.
MinVersion=10.0.14393

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Startup shortcut — written to the current user's shell:Startup folder
Name: startup; Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Additional options:"
; Post-install launch
Name: launch;  Description: "Launch {#AppName} after install";                    GroupDescription: "Additional options:"; Flags: unchecked

[Files]
; Include everything from the publish folder recursively.
; config.json and secure.dat are runtime files created in %APPDATA%\GrowLinkAgent
; by the application itself — they are never present in the publish folder.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu — {group} resolves to the DefaultGroupName folder (GrowLink)
Name: "{group}\{#AppName}";       Filename: "{app}\{#AppExeName}"
; Desktop shortcut (current user only)
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"
; Per-user Startup folder — only created if the startup task is checked
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
; Offer to launch the app on the final wizard page; only if not running silently
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Tasks: launch; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Ensure the app is not running when the uninstaller removes files
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "KillBeforeUninstall"
