; Inno Setup script for Handheld Optimiser.
; Build with installer\build-installer.ps1, which publishes the app and passes AppVersion and PublishDir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Handheld Optimiser"
#define AppExeName "HandheldOptimiser.exe"

[Setup]
; Never change AppId: upgrades find the existing install by it.
AppId={{D8663A0E-9E2F-43FF-B8CE-B0AECADB50A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Zain Ali
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\HandheldOptimiser\Assets\app.ico
OutputDir=..\artifacts
OutputBaseFilename=HandheldOptimiser-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The app itself runs elevated and writes HKLM, so a per-machine install under Program Files fits.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Post-install entries run as the original, non-elevated user. The app requires administrator, which a
; plain CreateProcess cannot satisfy (error 740), so launch through the shell: it shows the UAC prompt
; exactly as the Start menu does, and the app then runs as the right user with that user's HKCU.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; Remove the full screen home app registration before its files go, and if it was the selected home
; app, clear the setting so Windows falls back to the Xbox app instead of an entry that no longer exists.
; Braces are doubled because Inno reads single braces as constants.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'HandheldOptimiser.HomeApp' | Remove-AppxPackage; $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration'; if ((Get-ItemProperty $k -ErrorAction SilentlyContinue).GamingHomeApp -eq 'HandheldOptimiser.HomeApp_n7ggsqt1rt3jm!HomeApp') {{ Remove-ItemProperty $k -Name GamingHomeApp }"""; Flags: runhidden; RunOnceId: "RemoveHomeApp"

[Code]
// Uninstalling removes the program only. Tweaks stay applied, and the undo journal in
// HKLM\SOFTWARE\HandheldOptimiser\UndoJournal is kept so a reinstall can still revert them.
// Lines must not start with '#', or the preprocessor reads them as directives; hence Gap.
function InitializeUninstall(): Boolean;
var
  Gap: String;
begin
  Result := True;
  Gap := #13#10 + #13#10;

  if not UninstallSilent then
    Result := MsgBox(
      'Uninstalling does not undo any tweaks this app applied.' + Gap +
      'To put Windows back first, cancel now, open Handheld Optimiser and use "Revert all tweaks" ' +
      'on the Dashboard. Your undo data is kept either way, so reinstalling later can still revert them.' + Gap +
      'Continue uninstalling?',
      mbConfirmation, MB_YESNO) = IDYES;
end;
