[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Directory, [switch]$RequireHarness)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Directory = [IO.Path]::GetFullPath($Directory)
$archive = Join-Path $Directory 'package-windows-x64.zip'
$expected = ([IO.File]::ReadAllText($archive + '.sha256')).Trim()
if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expected) { throw 'Native package archive hash mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$unpacked = Join-Path $Directory 'unpacked'
if (Test-Path -LiteralPath $unpacked) { throw 'Native package extraction requires a fresh directory.' }
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpacked)
$package = Join-Path $unpacked 'package'
$build = [IO.File]::ReadAllText((Join-Path $package 'build.json')) | ConvertFrom-Json
if ($build.sourceRevision -cne $env:GITHUB_SHA -or $build.engineBaseRevision -cne $env:GITHUB_SHA -or $build.version -cne $env:MANA_RELEASE_VERSION -or $build.release -ne $true) { throw 'Native package belongs to another source/version or is not a release.' }
$engine = [IO.File]::ReadAllText((Join-Path $Directory 'ci-engine-manifest.json')) | ConvertFrom-Json
$jar = Join-Path $package 'engine/forge-engine.jar'
$jarHash = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash
$launcher = $engine.PSObject.Properties['fixtureLauncher']
if ($engine.schemaVersion -ne 2 -or $engine.sourceRevision -cne $env:GITHUB_SHA -or $engine.engine.sha256 -ine $jarHash -or $build.engineSha256 -ine $jarHash -or $engine.engine.bytes -ne (Get-Item -LiteralPath $jar).Length -or $null -eq $launcher -or $launcher.Value.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $launcher.Value.bytes -le 0) { throw 'Native package differs from the exact-source shared Forge engine or its verification manifest is incomplete.' }
foreach ($name in @('ManaTable.dll','Mana.Game.dll','Mana.Audio.dll','Mana.Client.dll','Mana.Contracts.dll','Mana.Magic.dll','Mana.Forge.dll','Mana.Renderer.dll','Mana.Scenarios.dll')) {
    $declared = $build.clientFilesSha256.PSObject.Properties[$name]
    if ($null -eq $declared -or [string]$declared.Value -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath (Join-Path $package $name) -Algorithm SHA256).Hash -ine $declared.Value) { throw "Native client hash mismatch: $name" }
}
$source = Join-Path $Directory 'package-source.zip'
if (Test-Path -LiteralPath $source) {
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $build.sourceArchiveSha256) { throw 'Native source archive differs from package metadata.' }
}
$receipt = Join-Path $Directory 'ci-source-receipt.json'
if (Test-Path -LiteralPath $receipt) {
    & (Join-Path $PSScriptRoot 'source-receipt.ps1') -InputPackageDirectory $package -Version $env:MANA_RELEASE_VERSION -PortableArchive $archive -ReceiptPath $receipt | Out-Null
} else { $receipt = '' }
if ($RequireHarness) {
    $harnessRoot = Join-Path $Directory 'harness'
    $harness = [IO.File]::ReadAllText((Join-Path $Directory 'ci-harness-manifest.json')) | ConvertFrom-Json
    if ($harness.sourceRevision -cne $env:GITHUB_SHA -or @($harness.files).Count -eq 0) { throw 'Native harness belongs to another source or is empty.' }
    foreach ($file in $harness.files) {
        if ($file.path -match '(^|[\\/])\.\.([\\/]|$)|^[\\/]|:' -or $file.path -notmatch '\S') { throw 'Invalid harness manifest path.' }
        if ((Get-FileHash -LiteralPath (Join-Path $harnessRoot $file.path) -Algorithm SHA256).Hash -ine $file.sha256) { throw "Native harness hash mismatch: $($file.path)" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $harnessRoot 'Mana.Table.Tests.exe'))) { throw 'Native harness executable is missing.' }
}
"MANA_NATIVE_PACKAGE=$package" >> $env:GITHUB_ENV
"MANA_NATIVE_ARCHIVE=$archive" >> $env:GITHUB_ENV
"MANA_NATIVE_SOURCE=$source" >> $env:GITHUB_ENV
"MANA_NATIVE_SOURCE_RECEIPT=$receipt" >> $env:GITHUB_ENV
Write-Output "Verified native package and shared engine for $($build.sourceRevision)."
