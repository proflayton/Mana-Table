[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$TestDirectory,
    [Parameter(Mandatory=$true)][string]$VpkDll,
    [switch]$KeepInstalled,
    [switch]$ShutdownOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory).TrimEnd('\','/')
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a fresh isolated lifecycle directory.' }
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$nativeRoot = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding($false)
$appId = 'ManaTableNative-Smoke-' + [guid]::NewGuid().ToString('N')
$install = Join-Path $TestDirectory 'installed'
$profile = Join-Path $TestDirectory 'profile'
New-Item -ItemType Directory -Path $TestDirectory,$profile | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'sentinel.txt'), 'Keep my decks, settings and chosen profile.', $utf8)
function Quote-Argument([string]$Value) {
    if ($Value -match '[\x00-\x1f]') { throw 'Invalid process argument.' }
    return '"' + [regex]::Replace(([regex]::Replace($Value, '(\\*)"', '$1$1\"')), '(\\+)$', '$1$1') + '"'
}
function Run-Hidden([string]$Exe, [string[]]$Arguments, [string]$Name, [int]$Seconds=60) {
    $output = Join-Path $TestDirectory ($Name + '.stdout.log')
    $errors = Join-Path $TestDirectory ($Name + '.stderr.log')
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Exe; $start.Arguments=($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    $start.WorkingDirectory=$TestDirectory; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $child = New-Object Diagnostics.Process
    $child.StartInfo=$start
    try {
        $null=$child.Start()
        $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($Seconds * 1000)) { $child.Kill(); throw "$Name timed out; see $TestDirectory" }
        [IO.File]::WriteAllText($output,$stdout.GetAwaiter().GetResult(),$utf8)
        [IO.File]::WriteAllText($errors,$stderr.GetAwaiter().GetResult(),$utf8)
        if ($child.ExitCode -ne 0) { throw "$Name failed with exit code $($child.ExitCode); see $TestDirectory" }
    } finally { $child.Dispose() }
}
function Probe([string[]]$Arguments, [string]$Name) {
    $exe = Join-Path $install 'current/Mana.Update.Lifecycle.Tests.exe'
    Run-Hidden $exe (@('--profile',$profile) + $Arguments) $Name
}
function Wait-Healthy([string]$Version) {
    $receipt = Join-Path $profile ('healthy-' + $Version + '.json')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $receipt)) {
        if ($clock.Elapsed.TotalSeconds -gt 45) { throw "SDK restart did not acknowledge $Version; see $profile" }
        Start-Sleep -Milliseconds 100
    }
    $health = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($health.passed -ne $true -or $health.version -cne $Version -or $health.profile -ine $profile -or $health.restarted -ne $true) { throw 'SDK restart health receipt did not match the requested version/profile.' }
}

Write-Output "Publishing the isolated SDK probe for $appId."
$published = Join-Path $TestDirectory 'published'
& dotnet publish (Join-Path $nativeRoot 'Mana.Update.Lifecycle.Tests/Mana.Update.Lifecycle.Tests.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $TestDirectory 'build') -p:NuGetAudit=false -p:RuntimeFrameworkVersion=9.0.13 -o $published *> (Join-Path $TestDirectory 'probe-publish.log')
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle probe publish failed.' }
$releases = @{}
$previous = $null
$versions = @('0.2.701','0.2.702')
if (-not $ShutdownOnly) { $versions += '0.2.703' }
foreach ($version in $versions) {
    $payloadDirectory = Join-Path $TestDirectory ('input-' + $version)
    New-Item -ItemType Directory -Path $payloadDirectory | Out-Null
    Copy-Item -Path (Join-Path $published '*') -Destination $payloadDirectory -Recurse
    [IO.File]::WriteAllText((Join-Path $payloadDirectory 'build.json'), (@{version=$version;release=$true;sourceRevision=('f' * 40)} | ConvertTo-Json), $utf8)
    $payload = New-Object byte[] (32 * 1024 * 1024)
    $random = New-Object Random 41021
    $random.NextBytes($payload)
    $payload[1000] = [byte]([int]$version.Split('.')[2] - 700)
    [IO.File]::WriteAllBytes((Join-Path $payloadDirectory 'payload.bin'), $payload)
    $output = Join-Path $TestDirectory ('release-' + $version)
    $arguments = @{InputPackageDirectory=$payloadDirectory;Version=$version;OutputDirectory=$output;VpkDll=$VpkDll;SmokeTestAppId=$appId;SmokeTestMainExecutable='Mana.Update.Lifecycle.Tests.exe'}
    if ($previous) { $arguments.PreviousFullPackage=$previous; $arguments.PreviousFullSha256=(Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash }
    Write-Output "Packaging genuine SDK release $version."
    & (Join-Path $nativeRoot 'build-update-bundles.ps1') @arguments *> (Join-Path $TestDirectory ('pack-' + $version + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle package $version failed." }
    $index = Get-Content -LiteralPath (Join-Path $output 'releases.win-x64.json') -Raw | ConvertFrom-Json
    $full = @($index.Assets | Where-Object { $_.Type -ceq 'Full' })
    $previous = Join-Path $output $full[0].FileName
    $releases[$version]=$output
}
$report = Get-Content -LiteralPath (Join-Path $releases['0.2.701'] 'update-bundle-report.json') -Raw | ConvertFrom-Json
$setup = Join-Path $releases['0.2.701'] $report.setupFileName
if ($report.packageId -cne $appId -or $report.setupSha256 -ine (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash) { throw 'Isolated setup identity/hash mismatch.' }
if (-not $install.StartsWith($TestDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $appId -notmatch '^ManaTableNative-Smoke-[a-f0-9]{32}$') { throw 'Isolated installation guard failed.' }
Write-Output 'Installing only the unique SDK test identity into its isolated directory.'
Run-Hidden $setup @('--silent','--installto',$install) 'install-sdk-base'
Probe @('--stage','--source',$releases['0.2.702'],'--expect','0.2.702','--dispose-preparation','--result','shutdown-result.json') 'dispose-native-patch'
$shutdownResult = Get-Content -LiteralPath (Join-Path $profile 'shutdown-result.json') -Raw | ConvertFrom-Json
if ($shutdownResult.disposedHelper -ne $true -or $shutdownResult.cancelled -ne $true) { throw 'SDK shutdown returned before the actual owned patch helper exited.' }
Probe @('--stage','--source',$releases['0.2.702'],'--expect','0.2.702','--require-delta','--reject-contained-profile','--result','delta-result.json') 'prepare-delta'
Probe @('--apply') 'apply-delta'
Wait-Healthy '0.2.702'
if ($ShutdownOnly) {
    if (-not $KeepInstalled) { Run-Hidden (Join-Path $install 'Update.exe') @('uninstall','--silent') 'uninstall-sdk-test' }
    $result = [ordered]@{passed=$true;verifiedAtUtc=[DateTime]::UtcNow.ToString('o');appId=$appId;installRoot=$install;profile=$profile;
        realWindowsLocator=$true;genuineSetup=$true;ownedNativeHelperDisposed=$true;disposeMilliseconds=$shutdownResult.disposeMilliseconds;
        containedProfilesRejected=$true;deltaRestart=$true;profilePreserved=$true}
    [IO.File]::WriteAllText((Join-Path $TestDirectory 'shutdown-update-results.json'), ($result | ConvertTo-Json -Depth 8), $utf8)
    Write-Output 'Installed SDK shutdown passed: Dispose reaped its owned native helper; contained profiles rejected; explicit delta restart preserved the external profile.'
    return
}
Probe @('--stage','--source',$releases['0.2.703'],'--expect','0.2.703','--cancel-preparation','--result','cancel-result.json') 'cancel-native-patch'

# The copied bad-patch feed retains the published hash so the real SDK must
# reject it and recover with the genuine full package.
$bad = Join-Path $TestDirectory 'bad-patch-feed'
New-Item -ItemType Directory -Path $bad | Out-Null
Copy-Item -Path (Join-Path $releases['0.2.703'] '*') -Destination $bad -Recurse
$index = Get-Content -LiteralPath (Join-Path $bad 'releases.win-x64.json') -Raw | ConvertFrom-Json
$delta = @($index.Assets | Where-Object { $_.Type -ceq 'Delta' })
if ($delta.Count -ne 1) { throw 'Genuine next delta was not created.' }
$patch = Join-Path $bad $delta[0].FileName
$bytes = [IO.File]::ReadAllBytes($patch); $bytes[[int]($bytes.Length/2)] = $bytes[[int]($bytes.Length/2)] -bxor 1; [IO.File]::WriteAllBytes($patch,$bytes)
Probe @('--stage','--source',$bad,'--expect','0.2.703','--require-full-fallback','--result','fallback-result.json') 'prepare-full-fallback'
Probe @('--observe') 'ordinary-start-preserves-pending'
$observed = Get-Content -LiteralPath (Join-Path $profile 'observed.json') -Raw | ConvertFrom-Json
if ($observed.passed -ne $true -or $observed.currentVersion -cne '0.2.702' -or $observed.downloads -ne 0) { throw 'An ordinary startup applied or downloaded without permission.' }
Probe @('--apply') 'apply-full-fallback'
Wait-Healthy '0.2.703'
if ([IO.File]::ReadAllText((Join-Path $profile 'sentinel.txt')) -cne 'Keep my decks, settings and chosen profile.') { throw 'The update changed the test profile.' }
$installedRoot=$install; $installedProfile=$profile
if (-not $KeepInstalled) { Run-Hidden (Join-Path $install 'Update.exe') @('uninstall','--silent') 'uninstall-sdk-test' }

# A genuine portable artifact has a .portable marker and no cached base. Its
# first update must offer a full transfer and still honor explicit restart.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portable = @(Get-ChildItem -LiteralPath $releases['0.2.701'] -File -Filter '*Portable.zip')
if ($portable.Count -ne 1) { throw 'The genuine portable SDK artifact is missing.' }
$install=Join-Path $TestDirectory 'portable'; $profile=Join-Path $TestDirectory 'portable-profile'
[IO.Compression.ZipFile]::ExtractToDirectory($portable[0].FullName,$install)
New-Item -ItemType Directory -Path $profile | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'sentinel.txt'), 'Keep my decks, settings and chosen profile.', $utf8)
Probe @('--stage','--source',$releases['0.2.702'],'--expect','0.2.702','--result','portable-full-result.json') 'portable-without-base'
$portableResult=Get-Content -LiteralPath (Join-Path $profile 'portable-full-result.json') -Raw | ConvertFrom-Json
if ($portableResult.IsPortable -ne $true -or $portableResult.UsesDelta -ne $false -or @($portableResult.Downloads).Count -ne 1 -or $portableResult.Downloads[0].Type -cne 'Full') { throw 'A portable copy without a base did not use its honest complete-package strategy.' }
Probe @('--apply') 'portable-explicit-restart'
Wait-Healthy '0.2.702'
$result = [ordered]@{passed=$true;verifiedAtUtc=[DateTime]::UtcNow.ToString('o');appId=$appId;installRoot=$installedRoot;profile=$installedProfile;
    realWindowsLocator=$true;genuineSetup=$true;deltaRestart=$true;nativeCancellation=$true;fullFallback=$true;explicitRestart=$true;profilePreserved=$true;
    genuinePortable=$true;missingBaseFullTransfer=$true;portableRestart=$true;portableRoot=$install;portableProfile=$profile}
[IO.File]::WriteAllText((Join-Path $TestDirectory 'installed-update-results.json'), ($result | ConvertTo-Json -Depth 8), $utf8)
Write-Output 'Installed/portable SDK lifecycle passed: delta, owned native cancellation, full fallback, explicit restart and preserved profiles.'
