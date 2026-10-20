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
#define LibraryName "Game Library"
#define LauncherExeName "HandheldOptimiser.HomeLauncher.exe"
#define ServiceExeName "HandheldOptimiser.PowerService.exe"
; Must match AutoTdpProtocol.ServiceName.
#define ServiceName "HandheldOptimiserAutoTdp"

[Setup]
; Never change AppId: upgrades find the existing install by it.
AppId={{D8663A0E-9E2F-43FF-B8CE-B0AECADB50A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Zain Ali
DefaultDirName={autopf}\{#AppName}
; The TDP Engine service runs as SYSTEM from the program folder, so that folder must be one only
; administrators can write: always Program Files, never a folder picked by hand.
DisableDirPage=yes
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
; Opens the game library directly, whatever the chosen full screen home app. The launcher carries the
; library's own icon, and does not need administrator, so this starts without a UAC prompt.
Name: "{group}\{#LibraryName}"; Filename: "{app}\{#LauncherExeName}"; Parameters: "--library"; Comment: "Open the Handheld Optimiser game library"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Post-install entries run as the original, non-elevated user. The app requires administrator, which a
; plain CreateProcess cannot satisfy (error 740), so launch through the shell: it shows the UAC prompt
; exactly as the Start menu does, and the app then runs as the right user with that user's HKCU.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec
; In-app updates run Setup silently with /UPDATE=1 from the already elevated app, so Setup is elevated
; too and can start the app directly. This reopens it once the update is in place.
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: IsInAppUpdate

[UninstallRun]
; The TDP Engine service is stopped and deleted in [Code] (CurUninstallStepChanged) before this runs,
; as stopping has to be waited for, which these entries cannot do.
; Remove the full screen home app registration before its files go, and if it was the selected home
; app, clear the setting so Windows falls back to the Xbox app instead of an entry that no longer exists.
; Braces are doubled because Inno reads single braces as constants. The uninstaller runs elevated, so
; PowerShell is started by full path and only loads modules from its own folder, never from anywhere
; the user can write to.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$env:PSModulePath = $PSHOME + '\Modules'; Get-AppxPackage -Name 'HandheldOptimiser.GameBar' | Remove-AppxPackage"""; Flags: runhidden; RunOnceId: "RemoveGameBarWidget"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$env:PSModulePath = $PSHOME + '\Modules'; Get-AppxPackage -Name 'HandheldOptimiser.HomeApp' | Remove-AppxPackage; $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration'; if ((Get-ItemProperty $k -ErrorAction SilentlyContinue).GamingHomeApp -in 'HandheldOptimiser.HomeApp_pgc7cpy78bqbw!HomeApp','HandheldOptimiser.HomeApp_n7ggsqt1rt3jm!HomeApp') {{ Remove-ItemProperty $k -Name GamingHomeApp }"""; Flags: runhidden; RunOnceId: "RemoveHomeApp"

[Code]
// Stops the TDP Engine service and waits, up to about 15 seconds, until Windows reports it STOPPED.
// sc stop only asks; the exe stays locked until the service has put the operating mode back and ended.
// sc query exits with 1060 when the service is not installed at all, which counts as stopped.
procedure StopAutoTdp();
var
  Sc, Output: String;
  Lines: TArrayOfString;
  Code, I, Tries: Integer;
  Stopped: Boolean;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Output := ExpandConstant('{tmp}\autotdp-state.txt');
  Exec(Sc, 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);

  for Tries := 1 to 30 do
  begin
    Stopped := True;
    if Exec(ExpandConstant('{cmd}'), '/C ""' + Sc + '" query {#ServiceName} > "' + Output + '""', '', SW_HIDE,
      ewWaitUntilTerminated, Code) and (Code = 0) and LoadStringsFromFile(Output, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if (Pos('STATE', Lines[I]) > 0) and (Pos('STOPPED', Lines[I]) = 0) then
          Stopped := False;

    if Stopped then
      Exit;
    Sleep(500);
  end;

  Log('The TDP Engine service had not stopped after waiting for it.');
end;

// The TDP Engine service's exe is replaced by an update, so a running one is stopped first.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAutoTdp();
  Result := '';
end;

// The TDP Engine service's log folder, ProgramData\HandheldOptimiser\logs. The service writes there as
// SYSTEM, and ProgramData lets any user make folders, so one already there is not trusted: a link in its
// place is removed (which removes the link only), and both folders are made Administrators' with access
// only Administrators and SYSTEM can write and users can read. The service checks the same before writing.
procedure SecureFolder(Dir: String);
var
  Icacls: String;
  FindRec: TFindRec;
  Code: Integer;
begin
  if FindFirst(Dir, FindRec) then
  begin
    // FILE_ATTRIBUTE_REPARSE_POINT: a junction or symbolic link.
    if (FindRec.Attributes and $400) <> 0 then
      RemoveDir(Dir);
    FindClose(FindRec);
  end;

  ForceDirectories(Dir);

  // /L works on the folder itself even if it were swapped for a link meanwhile, never on what a link
  // points at, and nothing recurses (/T), so no folder outside this one can be touched. Owner first, then
  // any access a user added is cleared (/reset), then only the three entries below are left.
  Icacls := ExpandConstant('{sys}\icacls.exe');
  Exec(Icacls, '"' + Dir + '" /setowner *S-1-5-32-544 /L', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Icacls, '"' + Dir + '" /reset /L', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Icacls, '"' + Dir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /L',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

// An install from before the folder page was removed may be outside Program Files, where folders
// usually let any signed-in user change files. The TDP Engine service runs as SYSTEM from here, so such
// a folder gets Program Files' kind of access: only Administrators and SYSTEM may change it.
procedure SecureAppFolderOutsideProgramFiles();
var
  App, ProgramFiles: String;
  Code: Integer;
begin
  App := AddBackslash(ExpandConstant('{app}'));
  ProgramFiles := AddBackslash(ExpandConstant('{autopf}'));
  if Pos(Lowercase(ProgramFiles), Lowercase(App)) = 1 then
    exit;

  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + RemoveBackslash(App) + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /T /L /C',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure SecureLogFolder();
begin
  SecureFolder(ExpandConstant('{commonappdata}\HandheldOptimiser'));
  SecureFolder(ExpandConstant('{commonappdata}\HandheldOptimiser\logs'));
end;

// True while TDP Engine replaces Armoury Crate: the main app's Replace Armoury Crate tweak sets HKLM
// SOFTWARE\HandheldOptimiser\AllyControls Replacing to 1. Any value but 0 counts, as it does for the
// service (AllyControlsStore.Replacing), so the two never disagree. A missing value means it does not.
function IsReplacingArmouryCrate(): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\HandheldOptimiser\AllyControls', 'Replacing', Value) and (Value <> 0);
end;

// Registers the TDP Engine service, which counts a game's frames (that needs administrator, which the
// game library never has) and sets the power limit. It only starts on demand: the library starts it
// when a game starts with TDP Engine on, and it ends itself once the game closes.
//
// While it replaces Armoury Crate it also runs the Ally's buttons and starts with Windows, so a
// reinstall or an update keeps it starting with Windows. Set back to demand, the buttons would stop
// working after the next restart.
//
// Its permissions are Windows' defaults for a service plus one: the signed-in player (IU) may start
// it (RP). They cannot stop, change or delete it. The exe path is quoted because it has spaces. App
// packages (AC) may start it too, for the Game Bar widget, which runs as one; the service itself only
// takes requests from the widget's own package.
procedure RegisterAutoTdp();
var
  Sc, Path, Start: String;
  Code: Integer;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Path := ExpandConstant('{app}\{#ServiceExeName}');
  if IsReplacingArmouryCrate() then
  begin
    Start := 'auto';
    Log('TDP Engine replaces Armoury Crate, so it is kept starting with Windows.');
  end
  else
    Start := 'demand';

  if not Exec(Sc, 'create {#ServiceName} binPath= "\"' + Path + '\"" start= ' + Start + ' DisplayName= "Handheld Optimiser TDP Engine"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    // Already there from an earlier version: brought up to date instead.
    Exec(Sc, 'config {#ServiceName} binPath= "\"' + Path + '\"" start= ' + Start + ' DisplayName= "Handheld Optimiser TDP Engine"',
      '', SW_HIDE, ewWaitUntilTerminated, Code);

  Exec(Sc, 'description {#ServiceName} "Holds a game at the frame rate chosen in the Handheld Optimiser game library with the lowest power limit, and runs the ROG Ally''s buttons and controller mode when it replaces Armoury Crate."',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Sc, 'sdset {#ServiceName} D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;RP;;;AC)',
    '', SW_HIDE, ewWaitUntilTerminated, Code);

  // If it crashes mid-game, Windows starts it again after 5 seconds, and on starting it puts back the
  // operating mode it saved, so the game's power limit is not left set. The count resets after a day.
  //
  // While it replaces Armoury Crate, the actions are the ones Replace Armoury Crate sets, so a reinstall
  // or an update does not undo them, and failureflag 1 makes Windows start it again when it ends with an
  // error as well as when it crashes: it ends that way when it fails while running the buttons.
  if IsReplacingArmouryCrate() then
  begin
    Exec(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(Sc, 'failureflag {#ServiceName} 1',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
  end
  else
    Exec(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000',
      '', SW_HIDE, ewWaitUntilTerminated, Code);

  // PrepareToInstall stopped it to replace its exe. While it replaces Armoury Crate, ASUS's services
  // are off and it is what runs the Ally's buttons, so it is started again now rather than at the next
  // restart. 1056 means it was already running.
  if IsReplacingArmouryCrate() then
  begin
    if Exec(Sc, 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 1056)) then
      Log('Started TDP Engine again (exit ' + IntToStr(Code) + ').')
    else
      Log('Could not start TDP Engine again (exit ' + IntToStr(Code) + ').');
  end;
end;

// Adds the TDP Engine widget to the player's Game Bar, when this build has it (it needs Visual Studio's
// UWP tools to build). See GameBar\RegisterWidget.ps1 for why it takes three steps. A widget that fails
// to add leaves the rest of the install as it is.
procedure RegisterGameBarWidget();
var
  PowerShell, Params: String;
  Code: Integer;
  TrustedHere: Boolean;
begin
  if not FileExists(ExpandConstant('{app}\GameBar\GameBar.msix')) then
    Exit;

  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\GameBar\RegisterWidget.ps1') + '" -Step ';

  if not Exec(PowerShell, Params + 'Trust', '', SW_HIDE, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 2)) then
  begin
    Log('Could not trust the Game Bar widget''s certificate, so the widget was not added.');
    Exit;
  end;
  TrustedHere := Code = 0;

  if not ExecAsOriginalUser(PowerShell, Params + 'Add', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Log('The Game Bar widget could not be added.');

  if TrustedHere then
    Exec(PowerShell, Params + 'Untrust', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SecureLogFolder();
    SecureAppFolderOutsideProgramFiles();
    RegisterAutoTdp();
    RegisterGameBarWidget();
  end;
end;

// Sets one ASUS service's start type with sc config, and logs how it went. The name is quoted, since
// some have spaces.
procedure SetAsusStart(Name, Start: String);
var
  Code: Integer;
begin
  if Exec(ExpandConstant('{sys}\sc.exe'), 'config "' + Name + '" start= ' + Start, '', SW_HIDE,
    ewWaitUntilTerminated, Code) and (Code = 0) then
    Log('Set ' + Name + ' to start ' + Start + '.')
  else
    Log('Could not set ' + Name + ' to start ' + Start + ' (exit ' + IntToStr(Code) + ').');
end;

// Starts one ASUS service with sc start, and logs how it went. 1056 means it was already running.
procedure StartAsus(Name: String);
var
  Code: Integer;
begin
  if Exec(ExpandConstant('{sys}\sc.exe'), 'start "' + Name + '"', '', SW_HIDE, ewWaitUntilTerminated, Code)
    and ((Code = 0) or (Code = 1056)) then
    Log('Started ' + Name + '.')
  else
    Log('Could not start ' + Name + ' (exit ' + IntToStr(Code) + ').');
end;

// Gives the Ally its ASUS services back when TDP Engine was replacing Armoury Crate, since nothing
// will run the Ally's buttons once TDP Engine is gone. The main app's journal is not available to the
// uninstaller, so these are the stock values recorded on this Ally on 2026-10-18: every service
// Automatic except asusm, which is Manual. The lists repeat ServicesInStopOrder and UpdateServices in
// src\HandheldOptimiser\TweakDefinitions\ArmouryCrateTweaks.cs, which is their source, and so do the
// task and the startup entry; a change there is made here too. The services that start are started
// as the tweak's revert starts them: the update service first and ArmouryCrateSEService last.
procedure PutBackArmouryCrate();
var
  Code: Integer;
begin
  if not IsReplacingArmouryCrate() then
    Exit;

  Log('TDP Engine was replacing Armoury Crate, so the ASUS services are put back.');

  // UpdateServices.
  SetAsusStart('asus', 'auto');
  SetAsusStart('asusm', 'demand');

  // ServicesInStopOrder.
  SetAsusStart('AsusAppService', 'auto');
  SetAsusStart('ASUSOptimization', 'auto');
  SetAsusStart('ASUSSoftwareManager', 'auto');
  SetAsusStart('ASUSSwitch', 'auto');
  SetAsusStart('ASUSSystemAnalysis', 'auto');
  SetAsusStart('ASUSSystemDiagnosis', 'auto');
  SetAsusStart('ArmouryCrateControlInterface', 'auto');
  SetAsusStart('ROG Live Service', 'auto');
  SetAsusStart('GameSDK Service', 'auto');
  SetAsusStart('LightingService', 'auto');
  SetAsusStart('ArmouryCrateSEService', 'auto');

  // FrameworkTask, which starts asus_framework at sign-in.
  if Exec(ExpandConstant('{sys}\schtasks.exe'), '/Change /TN "\ASUS\Framework Service" /ENABLE', '', SW_HIDE,
    ewWaitUntilTerminated, Code) and (Code = 0) then
    Log('Enabled the scheduled task \ASUS\Framework Service.')
  else
    Log('Could not enable the scheduled task \ASUS\Framework Service (exit ' + IntToStr(Code) + ').');

  // ScreenManagerEntry: the tweak turns it off under Run32 as Task Manager does. With no flag there,
  // Windows runs the entry again.
  if RegDeleteValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32', 'ROG Screen Manager') then
    Log('Turned the ROG Screen Manager startup entry back on.')
  else
    Log('There was no ROG Screen Manager startup flag to remove.');

  StartAsus('asus');
  StartAsus('AsusAppService');
  StartAsus('ASUSOptimization');
  StartAsus('ASUSSoftwareManager');
  StartAsus('ASUSSwitch');
  StartAsus('ASUSSystemAnalysis');
  StartAsus('ASUSSystemDiagnosis');
  StartAsus('ArmouryCrateControlInterface');
  StartAsus('ROG Live Service');
  StartAsus('GameSDK Service');
  StartAsus('LightingService');
  StartAsus('ArmouryCrateSEService');

  // Nothing is left to read the flag, and a stale 1 would make a later install start TDP Engine with
  // Windows again.
  if RegWriteDWordValue(HKLM64, 'SOFTWARE\HandheldOptimiser\AllyControls', 'Replacing', 0) then
    Log('Set AllyControls Replacing to 0.')
  else
    Log('Could not set AllyControls Replacing to 0.');
end;

// The TDP Engine service goes first, so its exe is free to delete. Stopping it puts Armoury Crate's
// operating mode back if a game was being held. While it replaced Armoury Crate, the ASUS services
// are put back once it has stopped and let go of the controller, and before it is deleted.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopAutoTdp();
    PutBackArmouryCrate();
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    // Where the service notes the operating mode to put back while a game is held; nothing reads it now.
    RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\HandheldOptimiser\TdpEngine');
  end;
end;

// Set by the app's updater; see the [Run] entry that reopens the app.
function IsInAppUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

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
      'To put Windows back first, cancel now, open Handheld Optimiser and use "Undo all changes" ' +
      'on the Dashboard. Your undo data is kept either way, so reinstalling later can still revert them.' + Gap +
      'Continue uninstalling?',
      mbConfirmation, MB_YESNO) = IDYES;
end;
