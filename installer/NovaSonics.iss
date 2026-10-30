#ifndef MyAppVersion
#define MyAppVersion "1.1.2"
#endif

[Setup]
AppId={{9F6C8A42-5D12-4B7E-971E-A37F55CE8D92}
AppName=NovaSonics
AppVersion={#MyAppVersion}
AppVerName=NovaSonics v{#MyAppVersion}
AppPublisher=NovaSonics Open Source
AppPublisherURL=https://github.com/brazaoo4/novasonics
AppSupportURL=https://github.com/brazaoo4/novasonics/issues
AppUpdatesURL=https://github.com/brazaoo4/novasonics/releases
DefaultDirName={autopf}\NovaSonics
DefaultGroupName=NovaSonics
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\NovaSonics.exe
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=NovaSonics-Setup-v{#MyAppVersion}
SetupIconFile=..\NovaSonics\Resources\app.ico
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=force
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startupentry"; Description: "Start NovaSonics automatically with Windows (Minimized to Tray)"; GroupDescription: "Startup options:"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NovaSonics"; Filename: "{app}\NovaSonics.exe"; IconFilename: "{app}\NovaSonics.exe"
Name: "{group}\{cm:UninstallProgram,NovaSonics}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NovaSonics"; Filename: "{app}\NovaSonics.exe"; Tasks: desktopicon

[Registry]
; Autorun entry when startup task is selected
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NovaSonics"; ValueData: """{app}\NovaSonics.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startupentry

[Run]
Filename: "{app}\NovaSonics.exe"; Description: "Launch NovaSonics"; Flags: nowait postinstall skipifsilent

[Code]
// Helper function to kill running instances before install or uninstall so locked binaries never block updates
procedure StopNovaSonicsProcess();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM NovaSonics.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(250);
end;

function InitializeSetup(): Boolean;
begin
  StopNovaSonicsProcess();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  StopNovaSonicsProcess();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Clean up autorun registry entry on uninstall if it exists
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'NovaSonics');
  end;
end;
