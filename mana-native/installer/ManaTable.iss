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
  #define InstallerAppName "Mana Table Installer Test"
  #define InstallerDefaultDirectory SmokeTestInstallDirectory
#else
  #define InstallerAppId "{{30195D50-8D96-4E5E-9378-74C76B5D917C}"
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
Filename: "{app}\ManaTable.exe"; WorkingDir: "{app}"; Flags: nowait skipifsilent

[Code]
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Desktop shortcuts default on; /TASKS= or /MERGETASKS=!desktopicon can opt out. }
  Result := PageID = wpSelectTasks;
end;

{ Profiles live in LocalAppData\ManaTableNative. They are deliberately absent
  from Files, Registry, InstallDelete, and UninstallDelete, including upgrades. }
