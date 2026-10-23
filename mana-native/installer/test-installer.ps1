[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputPackageDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$CompilerPath,
    [string]$PortableArchive,
    [string]$SourceArchive,
    [string]$SourceReceipt,
    [string]$TestDirectory,
    [switch]$SkipClientLaunch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($SourceArchive -and $SourceReceipt) { throw 'Choose SourceArchive or SourceReceipt, never both.' }
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $TestDirectory) { $TestDirectory = Join-Path $repository ('.tools/installer-smoke-' + [Guid]::NewGuid().ToString('N')) }
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory).TrimEnd('\', '/')
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a new test directory; existing installs and profiles are never reused.' }
if ($TestDirectory -eq [IO.Path]::GetPathRoot($TestDirectory) -or $TestDirectory.Contains('"')) { throw 'Unsafe installer test directory.' }
$installDirectory = Join-Path $TestDirectory 'installed'
$profile = Join-Path $TestDirectory 'localappdata/ManaTableNative'
$smokeId = 'ManaTableNative-Smoke-' + [Guid]::NewGuid().ToString('N')
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $smokeId + '_is1'
$registryPath32 = 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\' + $smokeId + '_is1'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
$sentinel = Join-Path $profile 'keep-my-decks.txt'
$sentinelContent = 'Mana Table installer must keep player decks and settings: ' + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($sentinel, $sentinelContent)
$checks = New-Object 'System.Collections.Generic.List[object]'
function Assert-Installer([bool]$Condition, [string]$Name) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Installer check failed: $Name" }
}
function Wait-InstallerProcess($Process, [string]$Name, [int]$TimeoutSeconds = 240) {
    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
        $Process.Kill()
        throw "$Name exceeded $TimeoutSeconds seconds."
    }
    $Process.Refresh()
    $exitCode = $Process.ExitCode
    Assert-Installer ($null -ne $exitCode -and $exitCode -eq 0) "$Name exits successfully (exit $exitCode)"
}
function Invoke-TestInstall([string]$LogName) {
    $arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=""', '/NORUN',
        ('/DIR="' + $installDirectory + '"'), ('/LOG="' + (Join-Path $TestDirectory $LogName) + '"'))
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Wait-InstallerProcess $process $LogName
}
function Invoke-TestUninstall {
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Isolated test uninstaller not found.' }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $TestDirectory 'uninstall.log') + '"'))
    $process = Start-Process -FilePath $uninstaller -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Wait-InstallerProcess $process 'uninstall'
    # Inno's uninstaller finishes deleting its own bootstrap after returning.
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path -LiteralPath (Join-Path $installDirectory 'ManaTable.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
}
$installed = $false
try {
    $buildArguments = @{
        InputPackageDirectory = $InputPackageDirectory
        Version = $Version
        OutputDirectory = (Join-Path $TestDirectory 'compiler-output')
        SmokeTestAppId = $smokeId
        SmokeTestInstallDirectory = $installDirectory
    }
    if ($CompilerPath) { $buildArguments.CompilerPath = $CompilerPath }
    if ($PortableArchive) { $buildArguments.PortableArchive = $PortableArchive }
    if ($SourceArchive) { $buildArguments.SourceArchive = $SourceArchive }
    if ($SourceReceipt) { $buildArguments.SourceReceipt = $SourceReceipt }
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'build-installer.ps1') @buildArguments
    $installer = Join-Path $TestDirectory 'compiler-output/ManaTable-Setup.exe'
    $manifest = Get-Content -LiteralPath (Join-Path $TestDirectory 'compiler-output/installer-manifest.json') -Raw | ConvertFrom-Json
    Assert-Installer ([bool]$manifest.smokeOnly) 'test installer has isolated application identity'
    $installed = $true
    Invoke-TestInstall 'install.log'
    Assert-Installer (Test-Path -LiteralPath (Join-Path $installDirectory 'ManaTable.exe') -PathType Leaf) 'client installed into isolated directory'
    $registration = $null
    foreach ($key in @($registryPath, $registryPath32)) {
        if (Test-Path -LiteralPath $key) { $registration = Get-ItemProperty -LiteralPath $key }
    }
    Assert-Installer ($null -ne $registration) 'separate per-user uninstall entry exists'
    Assert-Installer ($registration.InstallLocation.TrimEnd('\') -eq $installDirectory) 'uninstall entry points only to isolated install'
    foreach ($relative in @('ManaTable.exe', 'ManaTable.dll', 'Mana.Game.dll', 'build.json', 'SOURCE.txt', 'engine/forge-engine.jar', 'engine/runtime/bin/java.exe')) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory $relative) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath (Join-Path $installDirectory $relative) -Algorithm SHA256).Hash
        Assert-Installer ($expected -eq $actual) ("installed bytes match: $relative")
    }
    # Start-Process can lose the exit code of a very short redirected process in
    # Windows PowerShell 5.1. Start the process directly and retain its handle.
    $javaInfo = New-Object Diagnostics.ProcessStartInfo
    $javaInfo.FileName = Join-Path $installDirectory 'engine/runtime/bin/java.exe'
    $javaInfo.Arguments = '-version'
    $javaInfo.UseShellExecute = $false
    $javaInfo.CreateNoWindow = $true
    $javaInfo.RedirectStandardOutput = $true
    $javaInfo.RedirectStandardError = $true
    $java = New-Object Diagnostics.Process
    $java.StartInfo = $javaInfo
    try {
        $null = $java.Start()
        $javaOutput = $java.StandardOutput.ReadToEndAsync()
        $javaError = $java.StandardError.ReadToEndAsync()
        Wait-InstallerProcess $java 'bundled Java' 30
        [IO.File]::WriteAllText((Join-Path $TestDirectory 'java-output.log'), $javaOutput.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $TestDirectory 'java-version.log'), $javaError.GetAwaiter().GetResult())
    } finally { $java.Dispose() }
    if (-not $SkipClientLaunch) {
        $capture = Join-Path $TestDirectory 'installed-main-menu.png'
        $client = Start-Process -FilePath (Join-Path $installDirectory 'ManaTable.exe') -ArgumentList @('--profile', ('"' + $profile + '"'), '--capture', ('"' + $capture + '"'), '--size', '1280x720') `
            -WorkingDirectory $installDirectory -WindowStyle Hidden -PassThru
        Wait-InstallerProcess $client 'installed client startup' 120
        Assert-Installer ((Test-Path -LiteralPath $capture -PathType Leaf) -and (Get-Item -LiteralPath $capture).Length -gt 1000) 'installed client loads bundled engine and renders main menu'
        Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $profile 'native-error.log'))) 'installed client has no startup error'
    }
    Invoke-TestInstall 'reinstall.log'
    Assert-Installer ([IO.File]::ReadAllText($sentinel) -eq $sentinelContent) 'reinstall preserves player profile'
    Assert-Installer (@(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe').Count -eq 1) 'reinstall reuses stable uninstall identity'
    Invoke-TestUninstall
    $installed = $false
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'ManaTable.exe'))) 'uninstall removes installed client'
    Assert-Installer (-not (Test-Path -LiteralPath $registryPath) -and -not (Test-Path -LiteralPath $registryPath32)) 'uninstall removes only test registration'
    Assert-Installer ([IO.File]::ReadAllText($sentinel) -eq $sentinelContent) 'uninstall preserves player profile'
} finally {
    if ($installed -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
        try { Invoke-TestUninstall } catch { Write-Warning ("Isolated test uninstall failed: " + $_.Exception.Message) }
    }
    $checks.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TestDirectory 'installer-results.json') -Encoding UTF8
}
Write-Output (Join-Path $TestDirectory 'installer-results.json')
