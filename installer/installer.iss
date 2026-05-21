; -----------------------------------------------------------------------------
; Windows Audio Switcher — Inno Setup installer
;
; This script is invoked by build/release.ps1 with:
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=...\publish /DOutputDir=...\dist installer.iss
;
; It produces a per-user setup.exe that installs to %LOCALAPPDATA%\Programs.
; -----------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#ifndef SourceDir
  #define SourceDir "..\src\WindowsAudioSwitcher\bin\Release\net8.0-windows\win-x64\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName        "Windows Audio Switcher"
#define AppPublisher   "WindowsAudioSwitcher"
#define AppExeName     "WindowsAudioSwitcher.exe"

[Setup]
; AppId is the upgrade key — keep it constant across versions.
; The leading {{ is Inno's escape for a literal {, so the AppId value is {F8B3...}
AppId={{F8B3A0D2-7C4E-4B9A-8E5D-9F8A6B7C3D2E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\WindowsAudioSwitcher
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=WindowsAudioSwitcher-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\WindowsAudioSwitcher\Assets\app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=force
CloseApplicationsFilter=*.exe
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Shortcuts"
Name: "desktopicon"; Description: "Create a Desktop shortcut"; GroupDescription: "Shortcuts"; Flags: unchecked
Name: "autostart"; Description: "Launch automatically on Windows sign-in"; GroupDescription: "Startup"

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Pick up any sibling files the publish step emits (PDBs, runtime configs, native deps not bundled).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "{#AppExeName}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; HKCU Run key — task-gated so the user controls it from the installer wizard.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "WindowsAudioSwitcher"; \
    ValueData: """{app}\{#AppExeName}"" --tray"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
; postinstall = show as a "Launch app" checkbox on the Finished page (interactive
; installs). No skipifsilent: the entry ALSO runs after a /SILENT install, which
; is what the in-app auto-update path needs to relaunch the new version.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall

[UninstallDelete]
; Leave the user's settings.json alone by default — uncomment to wipe on uninstall.
; Type: filesandordirs; Name: "{userappdata}\WindowsAudioSwitcher"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
