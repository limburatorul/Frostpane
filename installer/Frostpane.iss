; Inno Setup script for Frostpane. Build it through build.ps1, which publishes the app first
; and passes the version in.

#define AppName "Frostpane"
#define AppPublisher "limburatorul"
#define AppUrl "https://github.com/limburatorul/Frostpane"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{82C24B45-5C50-4728-BB6B-5B2134B44C5C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user, so the silent updater never has to raise a UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\Frostpane.exe
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Frostpane holds its own files open, so let the Restart Manager shut it down first.
CloseApplications=yes
RestartApplications=yes

OutputDir=..\dist
OutputBaseFilename=Frostpane-{#AppVersion}-setup
SetupIconFile=..\src\Frostpane.App\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "ro"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Creează o scurtătură pe desktop"; GroupDescription: "Scurtături:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Frostpane.exe"
Name: "{group}\Dezinstalează {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Frostpane.exe"; Tasks: desktopicon

[Run]
; No skipifsilent: a silent update must bring the app back up by itself.
Filename: "{app}\Frostpane.exe"; Description: "Pornește {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Frostpane"

[Code]
{ Autostart is owned by the app's own tray menu, so the uninstaller has to clear it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER,
                   'Software\Microsoft\Windows\CurrentVersion\Run', 'Frostpane');
end;
