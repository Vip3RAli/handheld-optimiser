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

procedure SecureLogFolder();
begin
  SecureFolder(ExpandConstant('{commonappdata}\HandheldOptimiser'));
  SecureFolder(ExpandConstant('{commonappdata}\HandheldOptimiser\logs'));
end;

// Registers the TDP Engine service, which counts a game's frames (that needs administrator, which the
// game library never has) and sets the power limit. It only starts on demand: the library starts it
// when a game starts with TDP Engine on, and it ends itself once the game closes.
//
// Its permissions are Windows' defaults for a service plus one: the signed-in player (IU) may start
// it (RP). They cannot stop, change or delete it. The exe path is quoted because it has spaces.
procedure RegisterAutoTdp();
var
  Sc, Path: String;
  Code: Integer;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Path := ExpandConstant('{app}\{#ServiceExeName}');

  if not Exec(Sc, 'create {#ServiceName} binPath= "\"' + Path + '\"" start= demand DisplayName= "Handheld Optimiser TDP Engine"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    // Already there from an earlier version: brought up to date instead.
    Exec(Sc, 'config {#ServiceName} binPath= "\"' + Path + '\"" start= demand DisplayName= "Handheld Optimiser TDP Engine"',
      '', SW_HIDE, ewWaitUntilTerminated, Code);

  Exec(Sc, 'description {#ServiceName} "Holds a game at the frame rate chosen in the Handheld Optimiser game library with the lowest power limit. Runs only during games."',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Sc, 'sdset {#ServiceName} D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)',
    '', SW_HIDE, ewWaitUntilTerminated, Code);

  // If it crashes mid-game, Windows starts it again after 5 seconds, and on starting it puts back the
  // operating mode it saved, so the game's power limit is not left set. The count resets after a day.
  Exec(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SecureLogFolder();
    RegisterAutoTdp();
  end;
end;

// The TDP Engine service goes first, so its exe is free to delete. Stopping it puts Armoury Crate's
// operating mode back if a game was being held.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopAutoTdp();
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
