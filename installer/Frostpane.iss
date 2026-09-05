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

; Frostpane keeps its own files open. "force" lets the Restart Manager terminate it when it will
; not close on request, which builds before 1.0.1 did not.
CloseApplications=force
RestartApplications=yes

OutputDir=..\dist
OutputBaseFilename=Frostpane-{#AppVersion}-setup
SetupIconFile=..\src\Frostpane.App\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Frostpane.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Frostpane.exe"; Tasks: desktopicon

[Run]
; No skipifsilent: a silent update must bring the app back up by itself.
Filename: "{app}\Frostpane.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Frostpane"

[Code]
const
  WM_CLOSE = $0010;

function PostMessage(Wnd: HWND; Msg: UINT; WParam, LParam: Longint): BOOL;
  external 'PostMessageW@user32.dll stdcall';

{ Setup cannot replace files the running app holds open, so stop it first. Ask politely, then
  insist: builds before 1.0.1 have no window that answers a close request. }
procedure StopFrostpane();
var
  Window: HWND;
  Waited, ResultCode: Integer;
begin
  Window := FindWindowByWindowName('Frostpane.Command');
  if Window <> 0 then
  begin
    PostMessage(Window, WM_CLOSE, 0, 0);
    for Waited := 1 to 40 do
    begin
      Sleep(100);
      if FindWindowByWindowName('Frostpane.Command') = 0 then
        break;
    end;
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Frostpane.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopFrostpane();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopFrostpane();
  Result := True;
end;

{ Autostart and the desktop menu entries are written by the app itself, so the uninstaller, not
  the installer, is what has to clear them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Roots: array[0..1] of String;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;

  RegDeleteValue(HKEY_CURRENT_USER,
                 'Software\Microsoft\Windows\CurrentVersion\Run', 'Frostpane');

  Roots[0] := 'Software\Classes\DesktopBackground\Shell\';
  Roots[1] := 'Software\Classes\Directory\Background\shell\';
  for I := 0 to 1 do
  begin
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, Roots[I] + 'Frostpane.NewPane');
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, Roots[I] + 'Frostpane.NewPortal');
  end;
end;
