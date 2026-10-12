; PerfPulse – Installer-Skript (Inno Setup 6)
; Wird von build.ps1 aufgerufen. Erwartet die veröffentlichte App in ..\publish

#define AppName "PerfPulse"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "PerfPulse"
#define AppExe "PerfPulse.exe"

[Setup]
AppId={{B7C2E3A1-6F4D-4C8E-9A51-3D2F7E8B9C10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist
OutputBaseFilename=PerfPulse-Setup-{#AppVersion}
SetupIconFile=..\PerformanceMonitor\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=assets\wizard-164.bmp,assets\wizard-205.bmp,assets\wizard-246.bmp,assets\wizard-328.bmp
WizardSmallImageFile=assets\small-55.bmp,assets\small-69.bmp,assets\small-83.bmp,assets\small-110.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UsedUserAreasWarning=no
CloseApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.AutostartTask=PerfPulse mit Windows starten (läuft im Infobereich)
english.AutostartTask=Start PerfPulse with Windows (runs in the notification area)
german.OtherTasks=Weitere Optionen:
english.OtherTasks=Other options:
german.RemoveSettings=Sollen auch deine Einstellungen und der Scan-Verlauf von PerfPulse gelöscht werden?
english.RemoveSettings=Do you also want to delete your PerfPulse settings and scan history?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:OtherTasks}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Performance Monitor & Lag-Analyse"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PerfPulse"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// PerfPulse läuft meist im Infobereich weiter – vor Installation/Deinstallation beenden
procedure KillRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Autostart-Eintrag entfernen (egal ob vom Installer oder aus der App gesetzt)
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PerfPulse');
    if not UninstallSilent then
      if MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO) = IDYES then
        DelTree(ExpandConstant('{userappdata}\PerfPulse'), True, True, True);
  end;
end;
