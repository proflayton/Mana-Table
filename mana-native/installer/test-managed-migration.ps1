[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InputPackageDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$PortableArchive,
    [Parameter(Mandatory=$true)][string]$SourceArchive,
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$VpkDll,
    [Parameter(Mandatory=$true)][string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory).TrimEnd('\','/')
$InputPackageDirectory = [IO.Path]::GetFullPath($InputPackageDirectory)
if ((Test-Path -LiteralPath $TestDirectory) -or $TestDirectory -eq [IO.Path]::GetPathRoot($TestDirectory)) { throw 'Use a fresh isolated migration directory.' }
$smokeId = 'ManaTableNative-Smoke-' + [guid]::NewGuid().ToString('N')
$legacy = Join-Path $TestDirectory 'legacy app'
$managed = Join-Path $TestDirectory 'managed app'
$profile = Join-Path $TestDirectory 'same player profile'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $smokeId + '_is1'
$checks = New-Object 'System.Collections.Generic.List[object]'
$utf8 = New-Object Text.UTF8Encoding($false)
New-Item -ItemType Directory -Path $TestDirectory,$profile | Out-Null
$sentinel = Join-Path $profile 'keep-my-decks.txt'
[IO.File]::WriteAllText($sentinel,$smokeId,$utf8)
function Check([bool]$Condition,[string]$Message) {
    $checks.Add([pscustomobject]@{passed=$Condition;name=$Message})
    if (-not $Condition) { throw $Message }
}
function Quote([string]$Value) {
    if ($Value.Contains('"')) { throw 'Quotes are not allowed in fixture paths.' }
    return '"' + [regex]::Replace($Value,'(\\+)$','$1$1') + '"'
}
function Run([string]$File,[string[]]$Arguments,[int]$Timeout=120) {
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$File
    $info.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $process=New-Object Diagnostics.Process
    $process.StartInfo=$info
    try {
        if(-not $process.Start()){throw "Could not start $File"}
        if(-not $process.WaitForExit($Timeout*1000)){throw "Migration fixture timed out: $File"}
        return $process.ExitCode
    } finally {$process.Dispose()}
}
function Bridge([string]$Name,[string]$PlayerProfile=$profile) {
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName='powershell.exe'
    $info.Arguments='-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 2"'
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $parent=New-Object Diagnostics.Process
    $parent.StartInfo=$info
    try {
        if(-not $parent.Start()){throw 'Could not start isolated parent.'}
        return Run $bridge @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/NOCLOSEAPPLICATIONS','/NOFORCECLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',
            '/NORUN','/MANATABLETESTQUIET=1','/MANATABLEUPDATE=1',('/MANATABLEPID='+$parent.Id),('/MANATABLEPROFILE='+$PlayerProfile),('/DIR='+$legacy),('/LOG='+(Join-Path $TestDirectory ($Name+'.log'))))
    } finally {$parent.Dispose()}
}
try {
    # This small genuine Inno predecessor is intentionally never launched. Its
    # real registration, files and uninstaller exercise the migration boundary.
    $old=Join-Path $TestDirectory 'legacy payload'
    $oldOutput=Join-Path $TestDirectory 'legacy compiler'
    New-Item -ItemType Directory -Path $old,$oldOutput | Out-Null
    Copy-Item -LiteralPath (Join-Path $InputPackageDirectory 'ManaTable.exe') -Destination $old
    [IO.File]::WriteAllText((Join-Path $old 'legacy-marker.txt'),$smokeId,$utf8)
    & $CompilerPath /Qp ('/DPackageDirectory='+$old) /DPackageVersion=0.0.1 ('/DInstallerOutputDirectory='+$oldOutput) ('/DSmokeTestAppId='+$smokeId) ('/DSmokeTestInstallDirectory='+$legacy) (Join-Path $PSScriptRoot 'ManaTable.iss')
    if($LASTEXITCODE -ne 0){throw 'Legacy fixture compilation failed.'}
    Check ((Run (Join-Path $oldOutput 'ManaTable-Setup.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/NORUN','/TASKS=',('/DIR='+$legacy))) -eq 0) 'A private genuine Inno predecessor installs successfully'
    Check (Test-Path -LiteralPath $registry) 'Only the unique smoke legacy identity is registered'
    $oldHash=(Get-FileHash -LiteralPath (Join-Path $legacy 'ManaTable.exe') -Algorithm SHA256).Hash

    $bundles=Join-Path $TestDirectory 'managed bundles'
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'build-update-bundles.ps1') -InputPackageDirectory $InputPackageDirectory -Version $Version -OutputDirectory $bundles -VpkDll $VpkDll -SmokeTestAppId $smokeId
    $report=Get-Content -LiteralPath (Join-Path $bundles 'update-bundle-report.json') -Raw | ConvertFrom-Json
    $setup=Join-Path $bundles $report.setupFileName
    $bridgeOutput=Join-Path $TestDirectory 'bridge compiler'
    $metadata=Get-Content -LiteralPath (Join-Path $InputPackageDirectory 'build.json') -Raw | ConvertFrom-Json
    $buildInstaller=Join-Path (Split-Path -Parent $PSScriptRoot) 'build-installer.ps1'
    $bridgeOptions=@{InputPackageDirectory=$InputPackageDirectory;Version=$Version;PortableArchive=$PortableArchive;SourceArchive=$SourceArchive;
        CompilerPath=$CompilerPath;ManagedSetup=$setup;SmokeTestAppId=$smokeId;SmokeTestInstallDirectory=$legacy;SmokeTestManagedDirectory=$managed}
    $reportPath=Join-Path $bundles 'update-bundle-report.json'
    $originalReport=[IO.File]::ReadAllText($reportPath)
    foreach($property in @('setupSha256','inputBuildMetadataSha256')){
        $badReport=$originalReport|ConvertFrom-Json
        $badReport.$property='0'*64
        [IO.File]::WriteAllText($reportPath,($badReport|ConvertTo-Json -Depth 8),$utf8)
        $rejected=$false
        try { & $buildInstaller @bridgeOptions -OutputDirectory (Join-Path $TestDirectory ('rejected-'+$property)) | Out-Null }
        catch {$rejected=$_.Exception.Message.Contains('Managed Setup and update packages must come from this exact native package and source.')}
        finally {[IO.File]::WriteAllText($reportPath,$originalReport,$utf8)}
        Check $rejected ('The public installer builder rejects mismatched '+$property+' before compiling')
    }
    & $buildInstaller @bridgeOptions -OutputDirectory $bridgeOutput
    $bridge=Join-Path $bridgeOutput 'ManaTable-Setup.exe'

    foreach($unsafeProfile in @(
        @{Name='profile-is-legacy-root';Path=$legacy},
        @{Name='profile-inside-legacy';Path=(Join-Path $legacy 'player profile')},
        @{Name='profile-is-managed-root';Path=$managed},
        @{Name='profile-inside-managed';Path=(Join-Path $managed 'current/player profile')}
    )) {
        Check ((Bridge $unsafeProfile.Name $unsafeProfile.Path) -ne 0) ('The bridge refuses '+$unsafeProfile.Name+' before running managed Setup')
        $refusal=[IO.File]::ReadAllText((Join-Path $TestDirectory ($unsafeProfile.Name+'.log')))
        Check ($refusal.Contains('Choose a player profile outside the Mana Table installation')) ('The '+$unsafeProfile.Name+' refusal explains how to choose a safe external profile')
        Check (-not (Test-Path -LiteralPath $managed)) ('No managed installation was created for '+$unsafeProfile.Name)
        Check ((Test-Path -LiteralPath $registry) -and (Get-FileHash -LiteralPath (Join-Path $legacy 'ManaTable.exe') -Algorithm SHA256).Hash -eq $oldHash) ('The legacy app is unchanged after '+$unsafeProfile.Name)
    }
    Check ([IO.File]::ReadAllText($sentinel) -eq $smokeId) 'Unsafe profile refusals preserve the existing external player profile'

    # The real new executable starts its bundled Forge; a fixture-only corrupt
    # recovery journal makes that health check fail before any legacy uninstall.
    $journals=Join-Path $profile 'decks/workshop'
    New-Item -ItemType Directory -Path $journals -Force | Out-Null
    $brokenJournal=Join-Path $journals ([guid]::NewGuid().ToString()+'.journal')
    [IO.File]::WriteAllText($brokenJournal,'invalid fixture recovery journal',$utf8)
    Check ((Bridge 'health-refused') -ne 0) 'A failed real new-app/Forge health check refuses migration cleanup'
    $refusedLog=[IO.File]::ReadAllText((Join-Path $TestDirectory 'health-refused.log'))
    $reachedHealth=$refusedLog.Contains('Verify the new application and bundled Forge engine') -and
        (Test-Path -LiteralPath (Join-Path $profile 'engine.log')) -and
        (Test-Path -LiteralPath (Join-Path $profile 'native-error.log'))
    Check $reachedHealth 'The failure actually reached the new product and real Forge startup health query'
    Check ((Test-Path -LiteralPath $registry) -and (Get-FileHash -LiteralPath (Join-Path $legacy 'ManaTable.exe') -Algorithm SHA256).Hash -eq $oldHash) 'Failed health retains the old registration and exact application bytes'
    Check ([IO.File]::ReadAllText($sentinel) -eq $smokeId) 'Failed health preserves the existing player profile'
    $unsafeHealthProfile=Join-Path $managed 'current/unsafe health profile'
    $unsafeHealthReceipt=Join-Path $profile 'unsafe-health-receipt.json'
    Check ((Run (Join-Path $managed 'current/ManaTable.exe') @('--installation-health','--profile',$unsafeHealthProfile,
        '--managed-root',$managed,'--expected-package-id',$smokeId,'--expected-version',$Version,'--expected-source',$metadata.sourceRevision,
        '--health-receipt',$unsafeHealthReceipt)) -ne 0) 'The installed health process also refuses a profile inside its managed application tree'
    Check (-not (Test-Path -LiteralPath $unsafeHealthReceipt) -and -not (Test-Path -LiteralPath (Join-Path $unsafeHealthProfile 'engine.log'))) 'An unsafe health profile is refused before Forge startup or a healthy receipt'
    $resolvedJournal=[IO.Path]::GetFullPath($brokenJournal)
    if(-not $resolvedJournal.StartsWith([IO.Path]::GetFullPath($journals)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture journal path.'}
    Remove-Item -LiteralPath $resolvedJournal
    Check ((Bridge 'healthy-migration') -eq 0) 'The protocol1 bridge installs genuine Velopack and completes the health-gated migration'
    $receipt=Get-Content -LiteralPath (Join-Path $profile ('updates/migration-health-'+$Version+'.json')) -Raw | ConvertFrom-Json
    Check ($receipt.healthy -eq $true -and $receipt.packageId -ceq $smokeId -and $receipt.version -ceq $Version -and $receipt.sourceRevision -ceq $metadata.sourceRevision) 'The actual installed product acknowledges the exact managed identity, version and source'
    Check ($receipt.profile -ieq $profile -and $receipt.managedRoot -ieq $managed -and $receipt.catalogMatches -gt 0 -and $receipt.catalogQuery -ceq 'Forest') 'The new app starts its bundled Forge and retains the same custom player profile'
    Check ($receipt.clientSha256 -ieq (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory 'ManaTable.dll') -Algorithm SHA256).Hash -and $receipt.engineSha256 -ieq $metadata.engineSha256) 'Health proof binds the running product and engine to the exact input bytes'
    Check (-not (Test-Path -LiteralPath $registry) -and -not (Test-Path -LiteralPath (Join-Path $legacy 'ManaTable.exe'))) 'Only after health passes does the real legacy uninstaller remove its registration and files'
    Check ((Test-Path -LiteralPath (Join-Path $managed 'Update.exe')) -and (Test-Path -LiteralPath (Join-Path $managed 'current/sq.version'))) 'The result is a genuine managed installation with updater and version manifest'
    Check ([IO.File]::ReadAllText($sentinel) -eq $smokeId) 'Successful migration and legacy uninstall preserve the original player profile'
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) ($smokeId+'.lnk')
    $shell=New-Object -ComObject WScript.Shell
    try {
        $shortcut=$shell.CreateShortcut($shortcutPath)
        try { Check ($shortcut.TargetPath -ieq (Join-Path $managed ($smokeId+'.exe')) -and $shortcut.Arguments.Contains($profile)) 'Shortcuts are restored after legacy uninstall and use the stable managed launcher with the same profile' }
        finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)}
    } finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
} finally {
    # Cleanup is performed exclusively through these isolated uninstallers.
    # All profiles and logs remain as evidence; production identities are absent.
    if(Test-Path -LiteralPath (Join-Path $managed 'Update.exe')) {
        try { [void](Run (Join-Path $managed 'Update.exe') @('uninstall','--silent')) } catch { Write-Warning $_.Exception.Message }
    }
    if(Test-Path -LiteralPath (Join-Path $legacy 'unins000.exe')) {
        try { [void](Run (Join-Path $legacy 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')) } catch { Write-Warning $_.Exception.Message }
    }
    $checks.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TestDirectory 'migration-results.json') -Encoding UTF8
}
Write-Output (Join-Path $TestDirectory 'migration-results.json')
