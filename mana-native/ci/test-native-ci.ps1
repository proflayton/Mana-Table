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
function New-PackageFixture([string]$Name, [string]$Mutation) {
    $directory = Join-Path $TestDirectory $Name
    $package = Join-Path $directory 'input/package'
    New-Item -ItemType Directory -Path (Join-Path $package 'engine'), (Join-Path $directory 'harness') -Force | Out-Null
    $jar = Join-Path $package 'engine/forge-engine.jar'
    [IO.File]::WriteAllText($jar, 'verified shared engine', $utf8)
    $jarHash = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash
    $hashes = [ordered]@{}
    foreach ($name in @('ManaTable.dll','Mana.Game.dll','Mana.Client.dll','Mana.Contracts.dll','Mana.Magic.dll','Mana.Forge.dll','Mana.Renderer.dll','Mana.Scenarios.dll')) {
        $file = Join-Path $package $name
        [IO.File]::WriteAllText($file, ('verified bytes ' + $name), $utf8)
        $hashes[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    if ($Mutation -eq 'unbound-game') { $hashes.Remove('Mana.Game.dll') }
    $source = if ($Mutation -eq 'source') { 'b' * 40 } else { $env:GITHUB_SHA }
    Write-Json (Join-Path $package 'build.json') @{sourceRevision=$source; engineBaseRevision=$env:GITHUB_SHA; version=$env:MANA_RELEASE_VERSION; release=$true; engineSha256=$jarHash; clientFilesSha256=$hashes}
    Write-Json (Join-Path $directory 'ci-engine-manifest.json') @{schemaVersion=1; sourceRevision=$env:GITHUB_SHA; engine=@{sha256=$jarHash; bytes=(Get-Item -LiteralPath $jar).Length}}
    $harness = Join-Path $directory 'harness/Mana.Table.Tests.exe'
    [IO.File]::WriteAllText($harness, 'verified native test harness', $utf8)
    Write-Json (Join-Path $directory 'ci-harness-manifest.json') @{sourceRevision=$env:GITHUB_SHA; files=@(@{path='Mana.Table.Tests.exe'; sha256=(Get-FileHash -LiteralPath $harness -Algorithm SHA256).Hash})}
    if ($Mutation -eq 'engine') { [IO.File]::AppendAllText($jar, 'changed bytes', $utf8) }
    if ($Mutation -eq 'client') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Client.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'game') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Game.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'missing-game') { Remove-Item -LiteralPath (Join-Path $package 'Mana.Game.dll') }
    if ($Mutation -eq 'renderer') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Renderer.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'scenarios') { [IO.File]::AppendAllText((Join-Path $package 'Mana.Scenarios.dll'), 'changed bytes', $utf8) }
    if ($Mutation -eq 'harness') { [IO.File]::AppendAllText($harness, 'changed bytes', $utf8) }
    $archive = Join-Path $directory 'package-windows-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Fastest, $true)
    $checksum = if ($Mutation -eq 'archive') { '0' * 64 } else { (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
    [IO.File]::WriteAllText(($archive + '.sha256'), $checksum, $utf8)
    return $directory
}
try {
    $env:GITHUB_SHA = 'a' * 40
    $env:MANA_RELEASE_VERSION = '0.2.123'
    $env:GITHUB_ENV = Join-Path $TestDirectory 'github-env.txt'
    [object[]]$inventory = & (Join-Path $PSScriptRoot 'native-ui.ps1') -ListOnly | ConvertFrom-Json
    if ($inventory.Count -ne 10 -or @($inventory.name | Sort-Object -Unique).Count -ne 10 -or @($inventory.shard | Sort-Object -Unique).Count -ne 4) { throw 'Critical native suite discovery is incomplete or duplicated.' }
    $checks++
    [object[]]$inventory = & (Join-Path $PSScriptRoot 'native-ui.ps1') -ListOnly -Extended | ConvertFrom-Json
    if ($inventory.Count -ne 46 -or @($inventory.name | Sort-Object -Unique).Count -ne 46 -or @($inventory.shard | Sort-Object -Unique).Count -ne 12) { throw 'Extended native suite discovery is incomplete or duplicated.' }
    $checks++
    $valid = New-PackageFixture 'valid' ''
    & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $valid -RequireHarness
    $checks++
    foreach ($mutation in @('source','engine','client','game','missing-game','unbound-game','renderer','scenarios','harness','archive')) {
        $fixture = New-PackageFixture $mutation $mutation
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $fixture -RequireHarness }
        catch { $rejected = $true; Write-Output ("Rejected $mutation mutation: " + $_.Exception.Message) }
        if (-not $rejected) { throw "Native artifact mutation was accepted: $mutation" }
        $checks++
    }
    Write-Output "$checks native CI partition/provenance fixture checks passed. No UI, downloaded code or network was used."
} finally {
    $env:GITHUB_SHA = $saved.SHA
    $env:MANA_RELEASE_VERSION = $saved.Version
    $env:GITHUB_ENV = $saved.Environment
}
