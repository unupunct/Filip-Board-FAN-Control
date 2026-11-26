; Inno Setup script for Filip Board FAN Control
#define AppName "Filip Board FAN Control"
#define AppVersion "1.0.1"
#define AppExe "FilipBoardFanControl.exe"

[Setup]
AppId={{6F1B7C52-3C1E-4C0A-9E57-FB0A4F3A1C01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=unupunct
AppPublisherURL=https://github.com/unupunct/Filip-Board-FAN-Control
AppSupportURL=https://github.com/unupunct/Filip-Board-FAN-Control/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=FilipBoardFanControl-Setup-{#AppVersion}
SetupIconFile=..\src\FilipBoard.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the optional elevated start-up task and tell the user's fans go back to BIOS control on exit.
Filename: "schtasks.exe"; Parameters: "/Delete /TN FilipBoardFanControl /F"; Flags: runhidden; RunOnceId: "DelTask"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\FilipBoardFanControl"; Check: ShouldRemoveData

[Code]
function ShouldRemoveData: Boolean;
begin
  Result := MsgBox('Also delete your settings, profiles and history?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;
