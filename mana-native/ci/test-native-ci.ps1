[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $TestDirectory) { $TestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mana-native-ci-tests-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a new native CI fixture directory.' }
New-Item -ItemType Directory -Path $TestDirectory | Out-Null
$saved = @{ SHA=$env:GITHUB_SHA; Version=$env:MANA_RELEASE_VERSION; Environment=$env:GITHUB_ENV }
$utf8 = New-Object Text.UTF8Encoding($false)
$checks = 0
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), $utf8) }
function New-PackageFixture([string]$Name, [string]$Mutation, [switch]$EngineLauncher) {
    $directory = Join-Path $TestDirectory $Name
    $package = Join-Path $directory 'input/package'
    New-Item -ItemType Directory -Path (Join-Path $package 'engine'), (Join-Path $directory 'harness') -Force | Out-Null
    $jar = Join-Path $package 'engine/forge-engine.jar'
    [IO.File]::WriteAllText($jar, 'verified shared engine', $utf8)
    $jarHash = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash
    $hashes = [ordered]@{}
    foreach ($name in @('ManaTable.dll','Mana.Game.dll','Mana.Audio.dll','Mana.Client.dll','Mana.Contracts.dll','Mana.Magic.dll','Mana.Forge.dll','Mana.Renderer.dll','Mana.Scenarios.dll')) {
        $file = Join-Path $package $name
        [IO.File]::WriteAllText($file, ('verified bytes ' + $name), $utf8)
        $hashes[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    if ($Mutation -eq 'unbound-game') { $hashes.Remove('Mana.Game.dll') }
    if ($Mutation -eq 'unbound-audio') { $hashes.Remove('Mana.Audio.dll') }
    $source = if ($Mutation -eq 'source') { 'b' * 40 } else { $env:GITHUB_SHA }
    $build = @{sourceRevision=$source; engineBaseRevision=$env:GITHUB_SHA; version=$env:MANA_RELEASE_VERSION; release=$true; engineSha256=$jarHash; clientFilesSha256=$hashes}
    if ($EngineLauncher) {
        $sourceInput = Join-Path $directory 'source-input'
        New-Item -ItemType Directory -Path $sourceInput | Out-Null
        [IO.File]::WriteAllText((Join-Path $sourceInput 'fixture.txt'), 'Corresponding source fixture', $utf8)
        $sourceArchive = Join-Path $directory 'fixture-source.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($sourceInput, $sourceArchive)
        $build.sourceArchive = 'fixture-source.zip'
        $build.sourceArchiveSha256 = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash
        $build.sourceArchiveBytes = (Get-Item -LiteralPath $sourceArchive).Length
    }
    Write-Json (Join-Path $package 'build.json') $build
    $engineManifest = @{schemaVersion=2; sourceRevision=$env:GITHUB_SHA; engine=@{sha256=$jarHash; bytes=(Get-Item -LiteralPath $jar).Length}; fixtureLauncher=@{sha256=('f' * 64); bytes=1}}
    if ($EngineLauncher) {
        $fixtureLauncher = Join-Path $directory 'forge-engine-fixtures.jar'
        [IO.File]::WriteAllText($fixtureLauncher, 'verified test-only launcher', $utf8)
        $engineManifest.fixtureLauncher = @{sha256=(Get-FileHash -LiteralPath $fixtureLauncher -Algorithm SHA256).Hash; bytes=(Get-Item -LiteralPath $fixtureLauncher).Length}
        if ($Mutation -eq 'launcher-length') { $engineManifest.fixtureLauncher.bytes++ }
        if ($Mutation -eq 'launcher-content') {
            [IO.File]::WriteAllText($fixtureLauncher, 'Verified test-only launcher', $utf8)
        }
        if ($Mutation -eq 'missing-launcher') { Remove-Item -LiteralPath $fixtureLauncher }
        if ($Mutation -eq 'stale-launch-directory') {
            New-Item -ItemType Directory -Path (Join-Path $directory 'engine-launch') | Out-Null
            [IO.File]::WriteAllText((Join-Path $directory 'engine-launch/forge-engine.jar'), 'stale dependency', $utf8)
        }
        if ($Mutation -eq 'unrelated-root-engine') {
            [IO.File]::WriteAllText((Join-Path $directory 'forge-engine.jar'), 'unverified unrelated dependency', $utf8)
        }
    }
    if ($Mutation -eq 'legacy-engine-manifest') { $engineManifest.schemaVersion = 1 }
    if ($Mutation -eq 'missing-fixture-binding') { $engineManifest.Remove('fixtureLauncher') }
    Write-Json (Join-Path $directory 'ci-engine-manifest.json') $engineManifest
    $harness = Join-Path $directory 'harness/Mana.Table.Tests.exe'
    [IO.File]::WriteAllText($harness, 'verified native test harness', $utf8)
    Write-Json (Join-Path $directory 'ci-harness-manifest.json') @{sourceRevision=$env:GITHUB_SHA; files=@(@{path='Mana.Table.Tests.exe'; sha256=(Get-FileHash -LiteralPath $harness -Algorithm SHA256).Hash})}
    if ($Mutation -eq 'engine') { [IO.File]::AppendAllText($jar, 'changed bytes', $utf8) }
    if ($Mutation -eq 'client') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Client.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'game') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Game.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'audio') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Audio.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'missing-audio') { Remove-Item -LiteralPath (Join-Path $package 'Mana.Audio.dll') }
    if ($Mutation -eq 'missing-game') { Remove-Item -LiteralPath (Join-Path $package 'Mana.Game.dll') }
    if ($Mutation -eq 'renderer') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Renderer.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'scenarios') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Scenarios.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'harness') { [IO.File]::AppendAllText($harness, 'changed bytes', $utf8) }
    $archive = Join-Path $directory 'package-windows-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Fastest, $true)
    $checksum = if ($Mutation -eq 'archive') { '0' * 64 } else { (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
    [IO.File]::WriteAllText(($archive + '.sha256'), $checksum, $utf8)
    if ($EngineLauncher) {
        $sourceReceipt = Join-Path $directory 'ci-source-receipt.json'
        & (Join-Path $PSScriptRoot 'source-receipt.ps1') -InputPackageDirectory $package -Version $env:MANA_RELEASE_VERSION -PortableArchive $archive -SourceArchive $sourceArchive -OutputPath $sourceReceipt | Out-Null
        if ($Mutation -eq 'missing-source-receipt') { Remove-Item -LiteralPath $sourceReceipt }
        if ($Mutation -eq 'wrong-source-receipt') {
            $receipt = [IO.File]::ReadAllText($sourceReceipt) | ConvertFrom-Json
            $receipt.sourceRevision = 'b' * 40
            Write-Json $sourceReceipt $receipt
        }
    }
    return $directory
}
try {
    $env:GITHUB_SHA = 'a' * 40
    $env:MANA_RELEASE_VERSION = '0.2.123'
    $env:GITHUB_ENV = Join-Path $TestDirectory 'github-env.txt'
    [object[]]$inventory = & (Join-Path $PSScriptRoot 'native-ui.ps1') -ListOnly | ConvertFrom-Json
    if ($inventory.Count -ne 14 -or @($inventory.name | Sort-Object -Unique).Count -ne 14 -or @($inventory.shard | Sort-Object -Unique).Count -ne 4 -or
        @($inventory | Where-Object name -eq 'card-reading-1000').Count -ne 1 -or
        @($inventory | Where-Object name -eq 'full-battlefield-1000').Count -ne 1 -or
        @($inventory | Where-Object { $_.name -eq 'audio-settings-1000' -and $_.shard -eq 1 }).Count -ne 1 -or
        @($inventory | Where-Object { $_.name -eq 'audio-settings-1540' -and $_.shard -eq 2 }).Count -ne 1) { throw 'Critical native suite discovery is incomplete or duplicated.' }
    $checks++
    [object[]]$inventory = & (Join-Path $PSScriptRoot 'native-ui.ps1') -ListOnly -Extended | ConvertFrom-Json
    if ($inventory.Count -ne 48 -or @($inventory.name | Sort-Object -Unique).Count -ne 48 -or @($inventory.shard | Sort-Object -Unique).Count -ne 12) { throw 'Extended native suite discovery is incomplete or duplicated.' }
    $checks++
    $valid = New-PackageFixture 'valid' ''
    & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $valid -RequireHarness
    $checks++
    foreach ($mutation in @('source','engine','client','game','missing-game','unbound-game','audio','missing-audio','unbound-audio','renderer','scenarios','harness','archive','legacy-engine-manifest','missing-fixture-binding')) {
        $fixture = New-PackageFixture $mutation $mutation
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $fixture -RequireHarness }
        catch {
            $rejected = $mutation -notin @('audio','missing-audio','unbound-audio') -or $_.Exception.Message.Contains('Mana.Audio.dll')
            Write-Output ("Rejected $mutation mutation: " + $_.Exception.Message)
        }
        if (-not $rejected) { throw "Native artifact mutation was accepted: $mutation" }
        $checks++
    }
    foreach ($variant in @('valid-launcher', 'unrelated-root-engine')) {
        $fixture = New-PackageFixture $variant $variant -EngineLauncher
        & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $fixture -RequireHarness -RequireEngineLauncher
        $launcherCopy = Join-Path $fixture 'engine-launch/forge-engine-fixtures.jar'
        $productionCopy = Join-Path $fixture 'engine-launch/forge-engine.jar'
        if ((Get-FileHash -LiteralPath $launcherCopy -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $fixture 'forge-engine-fixtures.jar') -Algorithm SHA256).Hash -or
            (Get-FileHash -LiteralPath $productionCopy -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $fixture 'unpacked/package/engine/forge-engine.jar') -Algorithm SHA256).Hash) { throw 'The launch pair must contain the exact verified launcher and packaged dependency.' }
        if (-not ((Get-Content -LiteralPath $env:GITHUB_ENV) -contains "MANA_NATIVE_FIXTURE_LAUNCHER=$launcherCopy")) { throw 'The adapter must receive the fresh verified launch path.' }
        if (Test-Path -LiteralPath (Join-Path $fixture 'unpacked/package/engine/forge-engine-fixtures.jar')) { throw 'The test-only launcher must not enter the shipping engine directory.' }
        if ($variant -eq 'unrelated-root-engine' -and [IO.File]::ReadAllText((Join-Path $fixture 'forge-engine.jar')) -cne 'unverified unrelated dependency') { throw 'The launch helper must not replace or use an unrelated adjacent engine.' }
        $checks++
    }
    foreach ($mutation in @('launcher-content','launcher-length','missing-launcher','missing-source-receipt','wrong-source-receipt','stale-launch-directory')) {
        $fixture = New-PackageFixture $mutation $mutation -EngineLauncher
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $fixture -RequireHarness -RequireEngineLauncher }
        catch { $rejected = $true; Write-Output ("Rejected $mutation mutation: " + $_.Exception.Message) }
        if (-not $rejected) { throw "Packaged engine input mutation was accepted: $mutation" }
        $checks++
    }
    $withoutHarness = New-PackageFixture 'launcher-without-harness' '' -EngineLauncher
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $withoutHarness -RequireEngineLauncher }
    catch { $rejected = $_.Exception.Message.Contains('source-bound native harness') }
    if (-not $rejected) { throw 'A launcher cannot waive native harness verification.' }
    $checks++
    Write-Output "$checks native CI partition/provenance fixture checks passed. No UI, downloaded code or network was used."
    & (Join-Path $PSScriptRoot 'test-source-receipt.ps1') -TestDirectory (Join-Path $TestDirectory 'source-receipts')
    & (Join-Path $PSScriptRoot 'test-tools.ps1') -TestDirectory (Join-Path $TestDirectory 'tools')
} finally {
    $env:GITHUB_SHA = $saved.SHA
    $env:MANA_RELEASE_VERSION = $saved.Version
    $env:GITHUB_ENV = $saved.Environment
}
