[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CompilerPath,
    [Parameter(Mandatory = $true)][string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup compiler not found.' }
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a new isolated update test directory.' }
if ($TestDirectory -eq [IO.Path]::GetPathRoot($TestDirectory) -or $TestDirectory.Contains('"')) { throw 'Unsafe update test directory.' }
$smokeId = 'ManaTableNative-Smoke-' + [Guid]::NewGuid().ToString('N')
$mutexName = 'Local\ManaTableNative-' + $smokeId
$registryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $smokeId + '_is1'
$installDirectory = Join-Path $TestDirectory 'installed with spaces'
$profile = Join-Path $TestDirectory 'custom player profile'
$oldPackage = Join-Path $TestDirectory 'old-package'
$newPackage = Join-Path $TestDirectory 'new-package'
$checks = New-Object 'System.Collections.Generic.List[object]'
$holders = New-Object 'System.Collections.Generic.List[object]'
$fixtureProcesses = New-Object 'System.Collections.Generic.List[object]'
$installed = $false
New-Item -ItemType Directory -Path $profile, $oldPackage, $newPackage -Force | Out-Null
$sentinel = Join-Path $profile 'keep-my-decks.txt'
[IO.File]::WriteAllText($sentinel, $smokeId)

function Assert-Update([bool]$Condition, [string]$Name) {
    $checks.Add([pscustomobject]@{name=$Name; passed=$Condition})
    if (-not $Condition) { throw "Update installer check failed: $Name" }
}
function Quote-Argument([string]$Value) {
    if ($Value.Contains('"')) { throw 'Fixture arguments cannot contain double quotes.' }
    return '"' + [regex]::Replace($Value, '(\\+)$', '$1$1') + '"'
}
function Start-FixtureProcess([string]$File, [string[]]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $File
    $info.Arguments = ($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    if (-not $process.Start()) { throw 'Could not start isolated fixture process.' }
    $fixtureProcesses.Add($process)
    return $process
}
function Wait-FixtureProcess($Process, [int]$TimeoutSeconds = 90) {
    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) { throw 'Isolated installer fixture timed out.' }
    $Process.Refresh()
    return $Process.ExitCode
}
function Wait-File([string]$Path, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (Test-Path -LiteralPath $Path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing fixture signal: $Path" }
}
function Wait-Log([string]$Path, [string]$Text) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (Test-Path -LiteralPath $Path) {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = New-Object IO.StreamReader($stream)
            try { if ($reader.ReadToEnd().Contains($Text)) { return } }
            finally { $reader.Dispose() }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Installer never reached expected log event: $Text"
}
function Compile-Fixture([string]$Package, [string]$Version, [string]$Name) {
    $output = Join-Path $TestDirectory $Name
    New-Item -ItemType Directory -Path $output | Out-Null
    & $CompilerPath '/Qp' ('/DPackageDirectory=' + $Package) ('/DPackageVersion=' + $Version) `
        ('/DInstallerOutputDirectory=' + $output) ('/DSmokeTestAppId=' + $smokeId) `
        ('/DSmokeTestInstallDirectory=' + $installDirectory) (Join-Path $PSScriptRoot 'ManaTable.iss') *> (Join-Path $output 'compile.log')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated update installer compilation failed.' }
    return Join-Path $output 'ManaTable-Setup.exe'
}
function Start-Holder([string]$Name, [bool]$Mutex = $false) {
    $ready = Join-Path $TestDirectory ($Name + '.ready')
    $release = Join-Path $TestDirectory ($Name + '.release')
    $arguments = @('--hold', $ready, $release)
    if ($Mutex) { $arguments += $mutexName }
    $process = Start-FixtureProcess (Join-Path $oldPackage 'ManaTable.exe') $arguments
    $holder = [pscustomobject]@{Process=$process; Release=$release}
    $holders.Add($holder)
    Wait-File $ready
    return $holder
}
function Stop-Holder($Holder) {
    [IO.File]::WriteAllText($Holder.Release, 'exit normally')
    if ((Wait-FixtureProcess $Holder.Process 10) -ne 0) { throw 'Controlled fixture parent failed to exit normally.' }
}
function Start-Update([string]$Log, [int]$ParentId, [string]$Directory = $installDirectory,
    [string]$PlayerProfile = $profile, [string[]]$Extra = @()) {
    return Start-FixtureProcess $newInstaller (@('/SP-', '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/NOCLOSEAPPLICATIONS', '/NOFORCECLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS',
        '/MANATABLEUPDATE=1', '/MANATABLETESTQUIET=1', ('/MANATABLEPID=' + $ParentId),
        ('/MANATABLEPROFILE=' + $PlayerProfile), ('/DIR=' + $Directory), ('/LOG=' + $Log)) + $Extra)
}
function Assert-Refusal($Process, [string]$Log, [string]$Expected, [string]$Marker) {
    Assert-Update ((Wait-FixtureProcess $Process) -ne 0) 'unsafe update exits without installing'
    Assert-Update ([IO.File]::ReadAllText($Log).Contains($Expected)) ('refusal explains: ' + $Expected)
    Assert-Update ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload.txt')) -eq $Marker) 'refused update leaves application files unchanged'
}
function Set-FixtureVersion([string]$Version) {
    $user = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $registration = $user.OpenSubKey($registryKey, $true)
        try { $registration.SetValue('DisplayVersion', $Version, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $registration.Dispose() }
    } finally { $user.Dispose() }
}

try {
    # A tiny, graphics-free Windows executable keeps this fixture independent of
    # network access, Forge, the real install and the player's actual profile.
    $stub = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
internal static class UpdateInstallerFixture {
    private const string AppMutexName = "__APP_MUTEX__";
    private static Mutex EnterInstance() {
        using (Mutex gate = new Mutex(false, AppMutexName + "-UpdateGate")) {
            bool owned;
            try { owned = gate.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return null;
            try { return new Mutex(false, AppMutexName); }
            finally { gate.ReleaseMutex(); }
        }
    }
    [STAThread] private static int Main(string[] args) {
        using (Mutex running = EnterInstance()) {
            if (running == null) {
                if (args.Length == 2 && args[0] == "--probe") File.WriteAllText(args[1], "blocked");
                return 23;
            }
            if (args.Length >= 3 && args[0] == "--hold") {
                File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
                DateTime deadline = DateTime.UtcNow.AddMinutes(3);
                while (!File.Exists(args[2]) && DateTime.UtcNow < deadline) Thread.Sleep(25);
                return File.Exists(args[2]) ? 0 : 3;
            }
            if (args.Length == 2 && args[0] == "--probe") {
                File.WriteAllText(args[1], "started");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--profile") {
                Directory.CreateDirectory(args[1]);
                File.WriteAllLines(Path.Combine(args[1], "restart-args.txt"), args);
                File.AppendAllText(Path.Combine(args[1], "restart-count.txt"), "started\n");
                File.WriteAllText(Path.Combine(args[1], "restart-complete.txt"), "done");
                return 0;
            }
            return 2;
        }
    }
}
'@
    $stub = $stub.Replace('__APP_MUTEX__', $mutexName.Replace('\', '\\'))
    Add-Type -TypeDefinition $stub -OutputAssembly (Join-Path $oldPackage 'ManaTable.exe') -OutputType WindowsApplication
    Copy-Item -LiteralPath (Join-Path $oldPackage 'ManaTable.exe') -Destination $newPackage
    [IO.File]::WriteAllText((Join-Path $oldPackage 'payload.txt'), 'old verified payload')
    [IO.File]::WriteAllText((Join-Path $newPackage 'payload.txt'), 'new verified payload')
    $oldInstaller = Compile-Fixture $oldPackage '1.0.0' 'installer-old'
    $newInstaller = Compile-Fixture $newPackage '1.0.1' 'installer-new'
    $installed = $true
    $initial = Start-FixtureProcess $oldInstaller @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/NOICONS', '/TASKS=', ('/DIR=' + $installDirectory), ('/LOG=' + (Join-Path $TestDirectory 'initial.log')))
    Assert-Update ((Wait-FixtureProcess $initial) -eq 0) 'initial isolated install succeeds'
    Assert-Update (-not (Test-Path -LiteralPath (Join-Path $profile 'restart-args.txt'))) 'ordinary silent install does not launch the app'

    $wrong = Join-Path $TestDirectory 'unregistered target'
    $log = Join-Path $TestDirectory 'wrong-target.log'
    Assert-Refusal (Start-Update $log $PID $wrong) $log 'does not match the installed' 'old verified payload'
    Assert-Update (-not (Test-Path -LiteralPath $wrong)) 'target refusal creates no alternate install'
    $log = Join-Path $TestDirectory 'invalid-profile.log'
    Assert-Refusal (Start-Update $log $PID $installDirectory 'relative-profile') $log 'profile path is invalid' 'old verified payload'
    $log = Join-Path $TestDirectory 'close-applications.log'
    Assert-Refusal (Start-Update $log $PID $installDirectory $profile @('/CLOSEAPPLICATIONS')) $log 'leave other running applications open' 'old verified payload'

    $parent = Start-Holder 'parent'
    $log = Join-Path $TestDirectory 'handoff.log'
    $update = Start-Update $log $parent.Process.Id $installDirectory ($profile + '\')
    Wait-Log $log 'waiting for parent process'
    Assert-Update (-not $parent.Process.HasExited) 'installer waits while controlled parent is alive'
    Assert-Update ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload.txt')) -eq 'old verified payload') 'application files remain old while parent runs'
    $probeReceipt = Join-Path $TestDirectory 'blocked-startup.txt'
    $probe = Start-FixtureProcess (Join-Path $oldPackage 'ManaTable.exe') @('--probe', $probeReceipt)
    Assert-Update ((Wait-FixtureProcess $probe) -eq 23 -and [IO.File]::ReadAllText($probeReceipt) -eq 'blocked') 'update gate prevents a new application window during handoff and file replacement'
    $concurrentLog = Join-Path $TestDirectory 'concurrent-update.log'
    Assert-Refusal (Start-Update $concurrentLog $parent.Process.Id) $concurrentLog 'Another Mana Table update or startup is already running' 'old verified payload'
    Stop-Holder $parent
    Assert-Update ((Wait-FixtureProcess $update) -eq 0) 'update succeeds after parent exits normally'
    Assert-Update ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload.txt')) -eq 'new verified payload') 'update replaces the payload in the existing install directory'
    Wait-File (Join-Path $profile 'restart-complete.txt')
    $restart = [IO.File]::ReadAllLines((Join-Path $profile 'restart-args.txt'))
    Assert-Update ($restart.Length -eq 2 -and $restart[0] -eq '--profile' -and
        [IO.Path]::GetFullPath($restart[1]).TrimEnd('\') -eq $profile) 'silent update restarts with the same custom profile and handles spaces and trailing backslashes'
    Assert-Update ([IO.File]::ReadAllLines((Join-Path $profile 'restart-count.txt')).Length -eq 1) 'update restarts exactly once'
    Assert-Update ([IO.File]::ReadAllText($sentinel) -eq $smokeId) 'update preserves player profile content'

    $log = Join-Path $TestDirectory 'equal-version.log'
    Assert-Refusal (Start-Update $log $parent.Process.Id) $log 'already the same or newer' 'new verified payload'
    $newInstaller = $oldInstaller
    $log = Join-Path $TestDirectory 'older-version.log'
    Assert-Refusal (Start-Update $log $parent.Process.Id) $log 'already the same or newer' 'new verified payload'
    $newInstaller = Compile-Fixture $newPackage '1.0.2' 'installer-future'
    Set-FixtureVersion 'invalid-version'
    $log = Join-Path $TestDirectory 'invalid-installed-version.log'
    Assert-Refusal (Start-Update $log $parent.Process.Id) $log 'version could not be verified' 'new verified payload'
    Set-FixtureVersion '1.0.1'

    [IO.File]::WriteAllText((Join-Path $installDirectory 'payload.txt'), 'must not replace after version advance')
    $staleParent = Start-Holder 'stale-parent'
    $log = Join-Path $TestDirectory 'stale-after-wait.log'
    $staleUpdate = Start-Update $log $staleParent.Process.Id
    Wait-Log $log 'waiting for parent process'
    Set-FixtureVersion '1.0.2'
    Stop-Holder $staleParent
    Assert-Refusal $staleUpdate $log 'already the same or newer' 'must not replace after version advance'
    Set-FixtureVersion '1.0.1'

    [IO.File]::WriteAllText((Join-Path $installDirectory 'payload.txt'), 'must not replace with another window')
    $other = Start-Holder 'other-window' $true
    $log = Join-Path $TestDirectory 'other-window.log'
    Assert-Refusal (Start-Update $log $parent.Process.Id) $log 'Another Mana Table window is open' 'must not replace with another window'
    Assert-Update (-not $other.Process.HasExited) 'update never terminates the other application window'
    Stop-Holder $other

    [IO.File]::WriteAllText((Join-Path $installDirectory 'payload.txt'), 'must not replace on timeout')
    $stalled = Start-Holder 'stalled-parent'
    $log = Join-Path $TestDirectory 'shutdown-timeout.log'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $timeout = Start-Update $log $stalled.Process.Id
    Assert-Refusal $timeout $log 'Mana Table is still closing' 'must not replace on timeout'
    Assert-Update ($watch.Elapsed.TotalSeconds -ge 55 -and $watch.Elapsed.TotalSeconds -lt 85) 'shutdown wait has a bounded 60 second deadline'
    Assert-Update (-not $stalled.Process.HasExited) 'shutdown timeout never terminates the parent'
    $probeReceipt = Join-Path $TestDirectory 'allowed-after-timeout.txt'
    $probe = Start-FixtureProcess (Join-Path $oldPackage 'ManaTable.exe') @('--probe', $probeReceipt)
    Assert-Update ((Wait-FixtureProcess $probe) -eq 0 -and [IO.File]::ReadAllText($probeReceipt) -eq 'started') 'refused update releases its startup gate'
    Stop-Holder $stalled
} finally {
    try {
        foreach ($holder in $holders) {
            if (-not $holder.Process.HasExited) {
                [IO.File]::WriteAllText($holder.Release, 'cleanup: exit normally')
                $null = $holder.Process.WaitForExit(10000)
            }
            $holder.Process.Dispose()
        }
        # A failed assertion may have released a waiting installer above. Let its
        # owned process finish before uninstalling this fixture, avoiding a race.
        foreach ($process in $fixtureProcesses) {
            try { if (-not $process.HasExited -and -not $process.WaitForExit(90000)) { throw 'Owned fixture process did not finish during cleanup.' } }
            catch [InvalidOperationException] { }
        }
        if ($installed -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
            $remove = Start-FixtureProcess (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
                ('/LOG=' + (Join-Path $TestDirectory 'uninstall.log')))
            Assert-Update ((Wait-FixtureProcess $remove) -eq 0) 'isolated test installation uninstalls successfully'
            Assert-Update (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'ManaTable.exe'))) 'isolated application is removed'
            $user = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
            try { $registration = $user.OpenSubKey($registryKey); Assert-Update ($null -eq $registration) 'isolated uninstall registration is removed' }
            finally { if ($null -ne $registration) { $registration.Dispose() }; $user.Dispose() }
            Assert-Update ([IO.File]::ReadAllText($sentinel) -eq $smokeId) 'uninstall preserves the custom profile'
        }
    } finally {
        $checks.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TestDirectory 'update-installer-results.json') -Encoding UTF8
    }
}
Write-Output (Join-Path $TestDirectory 'update-installer-results.json')
