; Keep this identity stable: future installers upgrade the same per-user app.
; Smoke tests override both the identity and directory before compiling.
#ifndef PackageDirectory
  #error PackageDirectory is required
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
Source: "{#PackageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Mana Table"; Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mana Table"; Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"; Flags: nowait skipifsilent; Check: IsNormalInstall
Filename: "{app}\ManaTable.exe"; Parameters: "{code:UpdateRestartParameters}"; WorkingDir: "{app}"; Flags: nowait; Check: ShouldRestartAfterUpdate; BeforeInstall: ReleaseUpdateGate

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
  if (Result = '') and (CompareText(NormalizedPath(WizardDirValue), UpdateDirectory) <> 0) then
    Result := 'The update destination changed after verification. No application files were changed.';
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

{ Profiles live in LocalAppData\ManaTableNative. They are deliberately absent
  from Files, Registry, InstallDelete, and UninstallDelete, including upgrades. }
