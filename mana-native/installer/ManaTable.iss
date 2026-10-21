; Keep this identity stable: future installers upgrade the same per-user app.
; Smoke tests override both the identity and directory before compiling.
#ifndef PackageDirectory
  #error PackageDirectory is required
#endif
#ifdef ManagedSetupPath
  #ifndef ManagedInstallDirectory
    #define ManagedInstallDirectory "{localappdata}\ManaTable"
  #endif
#endif
#ifndef PackageVersion
  #error PackageVersion is required
#endif
#ifndef InstallerOutputDirectory
  #error InstallerOutputDirectory is required
#endif
#if Ver < EncodeVer(6, 3, 0, 0) || Ver >= EncodeVer(7, 0, 0, 0)
  #error Inno Setup 6.3 or newer within version 6 is required
#endif
#expr SaveStringToFile(AddBackslash(InstallerOutputDirectory) + "compiler-version.txt", DecodeVer(Ver, 4), 0, 0)
#ifdef SmokeTestAppId
  #ifndef SmokeTestInstallDirectory
    #error SmokeTestInstallDirectory is required for isolated installer tests
  #endif
  #define InstallerAppId SmokeTestAppId
  #define InstallerRegistryId SmokeTestAppId
  #define InstallerAppMutex "Local\ManaTableNative-" + SmokeTestAppId
  #define InstallerAppName "Mana Table Installer Test"
  #define InstallerDefaultDirectory SmokeTestInstallDirectory
#else
  #define InstallerAppId "{{30195D50-8D96-4E5E-9378-74C76B5D917C}"
  #define InstallerRegistryId "{30195D50-8D96-4E5E-9378-74C76B5D917C}"
  #define InstallerAppMutex "Local\ManaTableNative-30195D50-8D96-4E5E-9378-74C76B5D917C"
  #define InstallerAppName "Mana Table"
  #define InstallerDefaultDirectory "{localappdata}\Programs\Mana Table"
#endif

[Setup]
AppId={#InstallerAppId}
AppName={#InstallerAppName}
AppVersion={#PackageVersion}
AppPublisher=Mana Table contributors
AppPublisherURL=https://github.com/proflayton/Mana-Table
AppSupportURL=https://github.com/proflayton/Mana-Table/issues
AppUpdatesURL=https://github.com/proflayton/Mana-Table/releases
VersionInfoVersion={#PackageVersion}
VersionInfoDescription=Mana Table Setup
DefaultDirName={#InstallerDefaultDirectory}
DefaultGroupName=Mana Table
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UsePreviousAppDir=yes
UsePreviousTasks=yes
AllowNoIcons=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
WizardStyle=modern
UninstallDisplayName={#InstallerAppName}
UninstallDisplayIcon={app}\ManaTable.exe
UninstallLogMode=append
#ifdef ManagedSetupPath
Uninstallable=no
CreateAppDir=no
#endif
CloseApplications=yes
RestartApplications=no
AppMutex={#InstallerAppMutex}
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=ManaTable-Setup
Compression=lzma2/fast
SolidCompression=yes
SetupLogging=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
#ifdef ManagedSetupPath
Source: "{#ManagedSetupPath}"; DestDir: "{tmp}"; DestName: "ManaTable-Managed-Setup.exe"; Flags: ignoreversion deleteafterinstall
#else
Source: "{#PackageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
#ifndef ManagedSetupPath
Name: "{group}\Mana Table"; Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mana Table"; Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
#ifndef ManagedSetupPath
Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"; Flags: nowait skipifsilent; Check: IsNormalInstall
Filename: "{app}\ManaTable.exe"; Parameters: "{code:UpdateRestartParameters}"; WorkingDir: "{app}"; Flags: nowait; Check: ShouldRestartAfterUpdate; BeforeInstall: ReleaseUpdateGate
#endif

[Code]
const
  SynchronizeAccess = $00100000;
  WaitObjectSignaled = 0;
  WaitAbandoned = 128;
  WaitTimeout = 258;
  ErrorInvalidParameter = 87;
  UpdateWaitMilliseconds = 60000;
  UpdateRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#InstallerRegistryId}_is1';

var
  UpdateValidated: Boolean;
  UpdateDirectory: String;
  UpdateProfile: String;
  UpdateGate: THandle;
  UpdateGateOwned: Boolean;
#ifdef ManagedSetupPath
  ManagedGate: THandle;
  ManagedGateOwned: Boolean;
  MigrationCompleted: Boolean;
#endif

function OpenProcess(DesiredAccess: LongWord; InheritHandle: LongWord; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): LongWord;
  external 'CloseHandle@kernel32.dll stdcall';
function CreateMutexForUpdate(Attributes, InitialOwner: LongWord; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function ReleaseMutexForUpdate(Handle: THandle): LongWord;
  external 'ReleaseMutex@kernel32.dll stdcall';

procedure ReleaseUpdateGate;
begin
#ifdef ManagedSetupPath
  if ManagedGate <> 0 then begin
    if ManagedGateOwned then ReleaseMutexForUpdate(ManagedGate);
    CloseHandle(ManagedGate);
    ManagedGate := 0;
    ManagedGateOwned := False;
  end;
#endif
  if UpdateGate <> 0 then begin
    if UpdateGateOwned then ReleaseMutexForUpdate(UpdateGate);
    CloseHandle(UpdateGate);
    UpdateGate := 0;
    UpdateGateOwned := False;
  end;
end;

function IsUpdateInstall: Boolean;
begin
  Result := ExpandConstant('{param:MANATABLEUPDATE|0}') = '1';
end;

function IsNormalInstall: Boolean;
begin
  Result := not IsUpdateInstall;
end;

function HasSwitch(const Switch: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Switch) = 0 then begin
      Result := True;
      Exit;
    end;
end;

function IsAbsoluteUnquotedPath(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if (Pos('"', Value) <> 0) or (Length(Value) < 3) then Exit;
  for I := 1 to Length(Value) do
    if Ord(Value[I]) < 32 then Exit;
  Result := ((Value[2] = ':') and (Value[3] = '\') and
    (((Value[1] >= 'A') and (Value[1] <= 'Z')) or ((Value[1] >= 'a') and (Value[1] <= 'z')))) or
    (Copy(Value, 1, 2) = '\\');
end;

function NormalizedPath(const Value: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Value));
end;

function ParseInstalledVersion(const Value: String; var Version: Int64): Boolean;
var
  I, Separators, LastSeparator: Integer;
begin
  Result := False;
  Separators := 0;
  LastSeparator := 0;
  for I := 1 to Length(Value) do begin
    if Value[I] = '.' then begin
      if I = LastSeparator + 1 then Exit;
      Separators := Separators + 1;
      LastSeparator := I;
    end else if (Value[I] < '0') or (Value[I] > '9') then Exit;
  end;
  if (LastSeparator = Length(Value)) or (Separators < 2) or (Separators > 3) then Exit;
  Result := StrToVersion(Value, Version);
end;

function UpdateTargetProblem: String;
var
  InstalledDirectory, InstalledVersion: String;
  CurrentVersion, TargetVersion: Int64;
begin
  Result := '';
  if not RegQueryStringValue(HKCU64, UpdateRegistryKey, 'InstallLocation', InstalledDirectory) or
    not IsAbsoluteUnquotedPath(InstalledDirectory) then begin
    Result := 'The installed Mana Table location could not be verified. Download and run the installer manually.';
    Exit;
  end;
  if CompareText(NormalizedPath(InstalledDirectory), UpdateDirectory) <> 0 then begin
    Result := 'The requested update folder does not match the installed Mana Table location.';
    Exit;
  end;
  if not RegQueryStringValue(HKCU64, UpdateRegistryKey, 'DisplayVersion', InstalledVersion) or
    not ParseInstalledVersion(InstalledVersion, CurrentVersion) or
    not StrToVersion('{#PackageVersion}', TargetVersion) then begin
    Result := 'The installed Mana Table version could not be verified. Download and run the installer manually.';
    Exit;
  end;
  if ComparePackedVersion(CurrentVersion, TargetVersion) >= 0 then begin
    Result := 'The installed Mana Table version is already the same or newer than this update. Check for updates again.';
    Exit;
  end;
  if not FileExists(AddBackslash(UpdateDirectory) + 'ManaTable.exe') then
    Result := 'The installed Mana Table application could not be found.';
end;

function RefuseUpdate(const Reason: String): Boolean;
begin
  Log('Mana Table update refused: ' + Reason);
  ReleaseUpdateGate;
  { Protocol failures need an explanation even when ordinary install error
    prompts use /SUPPRESSMSGBOXES. Only isolated smoke builds can omit it. }
#ifdef SmokeTestAppId
  if not HasSwitch('/MANATABLETESTQUIET=1') then
#endif
  MsgBox('Mana Table could not install the update.' + #13#10#13#10 + Reason + #13#10#13#10 +
    'No application files were changed. Reopen Mana Table to continue.', mbError, MB_OK);
  Result := False;
end;

function InitializeSetup: Boolean;
var
  ParentId: Integer;
  ParentHandle: THandle;
  WaitResult, OpenError: LongWord;
  Problem: String;
begin
  Result := True;
  UpdateValidated := False;
  if not IsUpdateInstall then Exit;
  if not HasSwitch('/NOCLOSEAPPLICATIONS') or HasSwitch('/CLOSEAPPLICATIONS') or
    HasSwitch('/FORCECLOSEAPPLICATIONS') then begin
    Result := RefuseUpdate('The update must leave other running applications open. Please try again from Mana Table.');
    Exit;
  end;
  UpdateDirectory := ExpandConstant('{param:DIR|}');
  UpdateProfile := ExpandConstant('{param:MANATABLEPROFILE|}');
  if not IsAbsoluteUnquotedPath(UpdateDirectory) or not IsAbsoluteUnquotedPath(UpdateProfile) then begin
    Result := RefuseUpdate('The update folder or player profile path is invalid.');
    Exit;
  end;
  UpdateDirectory := NormalizedPath(UpdateDirectory);
  UpdateProfile := NormalizedPath(UpdateProfile);
  Problem := UpdateTargetProblem;
  if Problem <> '' then begin Result := RefuseUpdate(Problem); Exit; end;
  { Program briefly owns this same gate while registering its lifetime
    AppMutex. Once Setup owns it, a new window cannot start during file copy. }
  UpdateGate := CreateMutexForUpdate(0, 0, '{#InstallerAppMutex}-UpdateGate');
  if UpdateGate = 0 then begin
    Result := RefuseUpdate('The update could not protect the installation. Please try again.');
    Exit;
  end;
  WaitResult := WaitForSingleObject(UpdateGate, 0);
  UpdateGateOwned := (WaitResult = WaitObjectSignaled) or (WaitResult = WaitAbandoned);
  if not UpdateGateOwned then begin
    Result := RefuseUpdate('Another Mana Table update or startup is already running. Please try again when it finishes.');
    Exit;
  end;
  ParentId := StrToIntDef(ExpandConstant('{param:MANATABLEPID|0}'), 0);
  if ParentId <= 0 then begin
    Result := RefuseUpdate('The running Mana Table process could not be identified.');
    Exit;
  end;
  ParentHandle := OpenProcess(SynchronizeAccess, 0, ParentId);
  if ParentHandle = 0 then begin
    OpenError := DLLGetLastError;
    { A nonzero PID that no longer exists is expected when shutdown finishes
      before Setup has extracted its own startup files. Other errors are unsafe. }
    if OpenError <> ErrorInvalidParameter then begin
      Result := RefuseUpdate('Mana Table shutdown could not be verified (Windows error ' + IntToStr(OpenError) + ').');
      Exit;
    end;
    Log('Mana Table update parent process already exited: ' + IntToStr(ParentId));
  end else begin
    try
      Log('Mana Table update waiting for parent process: ' + IntToStr(ParentId));
      WaitResult := WaitForSingleObject(ParentHandle, UpdateWaitMilliseconds);
      if WaitResult <> WaitObjectSignaled then begin
        if WaitResult = WaitTimeout then
          Result := RefuseUpdate('Mana Table is still closing. Finish or close its windows, then try the update again.')
        else
          Result := RefuseUpdate('Mana Table shutdown could not be verified. Please try the update again.');
        Exit;
      end;
      Log('Mana Table update parent process exited.');
    finally
      CloseHandle(ParentHandle);
    end;
  end;
  Problem := UpdateTargetProblem;
  if Problem <> '' then begin Result := RefuseUpdate(Problem); Exit; end;
  if CheckForMutexes('{#InstallerAppMutex}') then begin
    Result := RefuseUpdate('Another Mana Table window is open. Close every Mana Table window, then try again.');
    Exit;
  end;
  UpdateValidated := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsUpdateInstall then Exit;
  Result := UpdateTargetProblem;
#ifndef ManagedSetupPath
  if (Result = '') and (CompareText(NormalizedPath(WizardDirValue), UpdateDirectory) <> 0) then
    Result := 'The update destination changed after verification. No application files were changed.';
#endif
  if (Result = '') and CheckForMutexes('{#InstallerAppMutex}') then
    Result := 'Another Mana Table window opened. Close every Mana Table window, then try again.';
  if Result <> '' then begin
    Log('Mana Table update refused: ' + Result);
    ReleaseUpdateGate;
  end;
end;

procedure DeinitializeSetup;
begin
  ReleaseUpdateGate;
end;

function ShouldRestartAfterUpdate: Boolean;
begin
  Result := UpdateValidated and IsUpdateInstall;
end;

function UpdateRestartParameters(Param: String): String;
var
  QuotedProfile: String;
  I: Integer;
begin
  { Quotes in paths were rejected above. Double trailing backslashes before
    the closing quote to follow Windows command-line parsing rules. }
  QuotedProfile := UpdateProfile;
  I := Length(UpdateProfile);
  while (I > 0) and (UpdateProfile[I] = '\') do begin
    QuotedProfile := QuotedProfile + '\';
    I := I - 1;
  end;
  Result := '--profile "' + QuotedProfile + '"';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Desktop shortcuts default on; /TASKS= or /MERGETASKS=!desktopicon can opt out. }
  Result := PageID = wpSelectTasks;
end;

#ifdef ManagedSetupPath
function QuoteMigrationPath(Value: String): String;
var
  I: Integer;
begin
  if not IsAbsoluteUnquotedPath(Value) then RaiseException('Invalid installation path.');
  I := Length(Value);
  while (I > 0) and (Value[I] = '\') do begin
    Value := Value + '\';
    I := I - 1;
  end;
  Result := '"' + Value + '"';
end;

procedure RequireMigrationProcess(const FileName, Arguments, Directory: String; const Step: String);
var
  ExitCode: Integer;
begin
  Log('Mana Table migration: ' + Step);
  if not Exec(FileName, Arguments, Directory, SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException('Could not start ' + Step + '. Your previous Mana Table installation is retained.');
  if ExitCode <> 0 then
    RaiseException(Step + ' failed (' + IntToStr(ExitCode) + '). Your previous installation has not been removed unless its own uninstall completed.');
end;

procedure ProtectManagedInstall;
var
  WaitResult: LongWord;
begin
  if CheckForMutexes('Local\ManaTableManaged-{#ManagedPackageId}') then
    RaiseException('Close every Mana Table window before running Setup.');
  ManagedGate := CreateMutexForUpdate(0, 0, 'Local\ManaTableManaged-{#ManagedPackageId}-UpdateGate');
  if ManagedGate = 0 then RaiseException('Could not protect the managed installation.');
  WaitResult := WaitForSingleObject(ManagedGate, 0);
  ManagedGateOwned := (WaitResult = WaitObjectSignaled) or (WaitResult = WaitAbandoned);
  if not ManagedGateOwned then RaiseException('Another managed Mana Table update is running.');
  if CheckForMutexes('Local\ManaTableManaged-{#ManagedPackageId}') then
    RaiseException('Another Mana Table window opened. Close it and try Setup again.');
end;

procedure RequireExternalProfile(const Profile, InstallationRoot: String);
begin
  if (CompareText(Profile, InstallationRoot) = 0) or
    (Pos(UpperCase(AddBackslash(InstallationRoot)), UpperCase(AddBackslash(Profile))) = 1) then
    RaiseException('Choose a player profile outside the Mana Table installation before updating. Your current profile has not been moved.');
end;

procedure MigrateManagedInstallation;
var
  ManagedRoot, Main, Setup, Profile, LegacyRoot, Uninstaller, RegisteredUninstaller, Args, Receipt: String;
  HasLegacy, DesktopShortcut: Boolean;
  WaitResult: LongWord;
  ExitCode: Integer;
begin
  ManagedRoot := NormalizedPath(ExpandConstant('{#ManagedInstallDirectory}'));
  Main := AddBackslash(ManagedRoot) + 'current\ManaTable.exe';
  Setup := ExpandConstant('{tmp}\ManaTable-Managed-Setup.exe');
  Profile := UpdateProfile;
  if Profile = '' then Profile := ExpandConstant('{localappdata}\ManaTableNative');
  Profile := NormalizedPath(Profile);
  RequireExternalProfile(Profile, ManagedRoot);
  HasLegacy := RegQueryStringValue(HKCU64, UpdateRegistryKey, 'InstallLocation', LegacyRoot);
  if HasLegacy then begin
    if not IsAbsoluteUnquotedPath(LegacyRoot) then RaiseException('The legacy install location is invalid.');
    LegacyRoot := NormalizedPath(LegacyRoot);
    RequireExternalProfile(Profile, LegacyRoot);
    if (CompareText(LegacyRoot, ManagedRoot) = 0) or
      (Pos(UpperCase(AddBackslash(LegacyRoot)), UpperCase(AddBackslash(ManagedRoot))) = 1) or
      (Pos(UpperCase(AddBackslash(ManagedRoot)), UpperCase(AddBackslash(LegacyRoot))) = 1) then
      RaiseException('The managed application must live outside the legacy installation.');
    if IsUpdateInstall and (CompareText(LegacyRoot, UpdateDirectory) <> 0) then
      RaiseException('The legacy registration changed during the update.');
    Uninstaller := AddBackslash(LegacyRoot) + 'unins000.exe';
    if not RegQueryStringValue(HKCU64, UpdateRegistryKey, 'UninstallString', RegisteredUninstaller) or
      (CompareText(RemoveQuotes(RegisteredUninstaller), Uninstaller) <> 0) or not FileExists(Uninstaller) then
      RaiseException('The legacy uninstaller could not be verified. Your existing installation is retained.');
    if UpdateGate = 0 then begin
      UpdateGate := CreateMutexForUpdate(0, 0, '{#InstallerAppMutex}-UpdateGate');
      if UpdateGate = 0 then RaiseException('Could not protect the existing installation.');
      WaitResult := WaitForSingleObject(UpdateGate, 0);
      UpdateGateOwned := (WaitResult = WaitObjectSignaled) or (WaitResult = WaitAbandoned);
      if not UpdateGateOwned then RaiseException('Another legacy update is running.');
    end;
    if CheckForMutexes('{#InstallerAppMutex}') then RaiseException('Close every existing Mana Table window first.');
  end;
  ProtectManagedInstall;
  DesktopShortcut := FileExists(ExpandConstant('{userdesktop}\Mana Table.lnk')) or (not HasLegacy and WizardIsTaskSelected('desktopicon'));
  if CompareText(GetSHA256OfFile(Setup), '{#ManagedSetupSha256}') <> 0 then RaiseException('The managed Setup checksum changed.');
  RequireMigrationProcess(Setup, '--silent --installto ' + QuoteMigrationPath(ManagedRoot), ExpandConstant('{tmp}'), 'Install the managed application');
  if not FileExists(Main) then RaiseException('The new managed application was not installed. The previous app is retained.');
  Args := '--profile ' + QuoteMigrationPath(Profile) + ' --managed-root ' + QuoteMigrationPath(ManagedRoot) +
    ' --expected-package-id {#ManagedPackageId} --expected-version {#PackageVersion} --expected-source {#PackageSourceRevision}';
  Receipt := AddBackslash(Profile) + 'updates\migration-health-{#PackageVersion}.json';
  RequireMigrationProcess(Main, '--installation-health ' + Args + ' --health-receipt ' + QuoteMigrationPath(Receipt), ManagedRoot,
    'Verify the new application and bundled Forge engine');
  if not FileExists(Receipt) then RaiseException('The new application did not acknowledge its health check.');
  Log('Mana Table migration: new app and Forge health passed; legacy cleanup may begin.');
  if HasLegacy then begin
    RequireMigrationProcess(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', ExpandConstant('{tmp}'), 'Remove the legacy predecessor after health passes');
    if RegKeyExists(HKCU64, UpdateRegistryKey) then RaiseException('Legacy cleanup did not finish. Reopen Setup to retry.');
  end;
  if DesktopShortcut then Args := Args + ' --desktop-shortcut';
  RequireMigrationProcess(Main, '--installation-shortcuts ' + Args, ManagedRoot, 'Recreate managed application shortcuts');
  ReleaseUpdateGate;
  if not HasSwitch('/NORUN') then begin
    if not Exec(Main, '--profile ' + QuoteMigrationPath(Profile), ManagedRoot, SW_SHOWNORMAL, ewNoWait, ExitCode) then
      RaiseException('The update is installed. Open Mana Table from its shortcut.');
  end;
  Log('Mana Table migration: complete; player profile retained at ' + Profile);
  MigrationCompleted := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then MigrateManagedInstallation;
end;

function GetCustomSetupExitCode: Integer;
begin
  { Inno logs ssPostInstall exceptions but otherwise returns success. The
    canonical updater must never acknowledge a failed health/migration step. }
  if MigrationCompleted then Result := 0 else Result := 1;
end;
#endif

{ Profiles live in LocalAppData\ManaTableNative. They are deliberately absent
  from Files, Registry, InstallDelete, and UninstallDelete, including upgrades. }
