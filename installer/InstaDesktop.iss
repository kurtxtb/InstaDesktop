#define AppName "InstaDesktop"
; Keep the installer version in sync with the executable produced by publish.
; This lets Inno Setup detect upgrades whenever a new build is installed.
#define AppVersion GetFileVersion("..\\publish\\win-x64\\InstaDesktop.exe")

[Setup]
AppId={{76CF4840-0247-4537-81ED-FB1C8081C4FA}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=InstaDesktop
DefaultDirName={localappdata}\Programs\InstaDesktop
DefaultGroupName=InstaDesktop
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\publish\installer
OutputBaseFilename=InstaDesktop-Setup
SetupIconFile=..\Assets\Icons\app.ico
UninstallDisplayIcon={app}\InstaDesktop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\InstaDesktop-Running
CloseApplications=yes
CloseApplicationsFilter=InstaDesktop.exe
RestartApplications=no
UsePreviousAppDir=yes
DisableDirPage=auto
; Re-running this setup performs an in-place upgrade (same AppId and folder).
UninstallDisplayName={#AppName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "Assets\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; Preserve user edits during an upgrade.
Source: "..\publish\win-x64\Assets\*"; DestDir: "{app}\Assets"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs

[InstallDelete]
; Emoji fonts from pre-release test builds, superseded by Twemoji.woff2.
Type: files; Name: "{app}\Assets\Fonts\NotoColorEmoji.woff2"
Type: files; Name: "{app}\Assets\Fonts\NotoColorEmoji-OFL.txt"
Type: files; Name: "{app}\Assets\Fonts\Noto-3D-Emoji.ttf"
Type: files; Name: "{app}\Assets\Fonts\Noto-3D-Emoji-OFL.txt"

[Icons]
Name: "{group}\InstaDesktop"; Filename: "{app}\InstaDesktop.exe"
Name: "{autodesktop}\InstaDesktop"; Filename: "{app}\InstaDesktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\InstaDesktop.exe"; Description: "Launch InstaDesktop"; Flags: nowait postinstall skipifsilent
; The in-app updater runs this installer with /SILENT /RELAUNCH=1: start the new version again.
Filename: "{app}\InstaDesktop.exe"; Flags: nowait skipifnotsilent; Check: ShouldRelaunch

[UninstallRun]
Filename: "{app}\InstaDesktop.exe"; Parameters: "--uninstall-notifications"; Flags: runhidden waituntilterminated

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  { The updater starts this installer just before the running app exits. Give
    it time to release its mutex instead of reporting it as still running. }
  Waited := 0;
  while ShouldRelaunch and CheckForMutexes('Local\InstaDesktop-Running') and (Waited < 30000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'InstaDesktop', StartupCommand) then
    begin
      if Pos(Lowercase(ExpandConstant('{app}\InstaDesktop.exe')), Lowercase(StartupCommand)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'InstaDesktop');
    end;
    { Silent uninstall always retains user data. No data deletion is the default. }
    if not UninstallSilent then
      if MsgBox('Also remove Instagram login data, settings and logs?' + #13#10 +
        'Choose No to keep your login for a future installation.', mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{localappdata}\InstaDesktop'), True, True, True);
  end;
end;
