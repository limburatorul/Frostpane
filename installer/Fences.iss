; Inno Setup script for Fences. Build it through build.ps1, which publishes the app first
; and passes the version in.

#define AppName "Fences"
#define AppPublisher "limburatorul"
#define AppUrl "https://github.com/limburatorul/Fences"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{A6B11999-0E83-4EA5-AC57-E892ECD4A6AE}
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
UninstallDisplayIcon={app}\Fences.exe
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Fences holds its own files open, so let the Restart Manager shut it down first.
CloseApplications=yes
RestartApplications=yes

OutputDir=..\dist
OutputBaseFilename=Fences-{#AppVersion}-setup
SetupIconFile=..\src\Fences.App\app.ico
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
Name: "{group}\{#AppName}"; Filename: "{app}\Fences.exe"
Name: "{group}\Dezinstalează {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Fences.exe"; Tasks: desktopicon

[Run]
; No skipifsilent: a silent update must bring the app back up by itself.
Filename: "{app}\Fences.exe"; Description: "Pornește {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Fences"

[Code]
{ Autostart is owned by the app's own tray menu, so the uninstaller has to clear it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER,
                   'Software\Microsoft\Windows\CurrentVersion\Run', 'Fences');
end;
