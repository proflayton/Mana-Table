[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $TestDirectory) { $TestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mana-source-receipt-' + [Guid]::NewGuid().ToString('N')) }
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a fresh source receipt fixture directory.' }
$package = Join-Path $TestDirectory 'input/package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$version = '0.2.123'; $revision = 'a' * 40; $checks = 0
function Json([string]$Path, $Object) { [IO.File]::WriteAllText($Path, ($Object | ConvertTo-Json -Depth 8), $utf8) }
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Reject([scriptblock]$Operation, [string]$Name, [string]$Message = '') {
    $rejected = $false
    try { & $Operation | Out-Null }
    catch { $rejected = -not $Message -or $_.Exception.Message.Contains($Message) }
    if (-not $rejected) { throw "Source receipt check was not rejected: $Name" }
    $script:checks++
}
foreach ($directory in @('licenses', 'engine/runtime/bin', 'engine/forge-res')) {
    New-Item -ItemType Directory -Path (Join-Path $package $directory) -Force | Out-Null
}
$clientHashes = @{}
foreach ($name in @('ManaTable.exe','ManaTable.dll','Mana.Game.dll','Mana.Client.dll','Mana.Contracts.dll','Mana.Magic.dll','Mana.Forge.dll','Mana.Renderer.dll','Mana.Scenarios.dll',
    'coreclr.dll','hostfxr.dll','System.Private.CoreLib.dll','SOURCE.txt','FORGE-LICENSE.txt','THIRD-PARTY.txt','engine/forge-engine.jar','engine/runtime/bin/java.exe')) {
    $file = Join-Path $package $name
    [IO.File]::WriteAllText($file, ('fixture bytes ' + $name), $utf8)
    if ($name -like '*.dll') { $clientHashes[$name] = Hash $file }
}
$sourceRoot = Join-Path $TestDirectory 'source'
New-Item -ItemType Directory -Path $sourceRoot | Out-Null
[IO.File]::WriteAllText((Join-Path $sourceRoot 'fixture-source.txt'), 'Real ZIP fixture; no executable payload.', $utf8)
$sourceArchive = Join-Path $TestDirectory 'package-source.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, $sourceArchive)
$build = @{version=$version;sourceRevision=$revision;engineBaseRevision=$revision;release=$true;client='MonoGame';backend='Forge';
    sourceUrl=('https://github.com/proflayton/Mana-Table/tree/' + $revision);engineSha256=(Hash (Join-Path $package 'engine/forge-engine.jar'));
    clientFilesSha256=$clientHashes;sourceArchive='package-source.zip';sourceArchiveSha256=(Hash $sourceArchive);sourceArchiveBytes=(Get-Item -LiteralPath $sourceArchive).Length}
Json (Join-Path $package 'build.json') $build
$archive = Join-Path $TestDirectory 'package-windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Fastest, $true)
[IO.File]::WriteAllText(($archive + '.sha256'), (Hash $archive), $utf8)
$receiptPath = Join-Path $TestDirectory 'ci-source-receipt.json'
$script = Join-Path $PSScriptRoot 'source-receipt.ps1'
$options = @{InputPackageDirectory=$package;Version=$version;PortableArchive=$archive}
& $script @options -SourceArchive $sourceArchive -OutputPath $receiptPath | Out-Null
$receipt = & $script @options -ReceiptPath $receiptPath
if ($receipt.sourceArchive.sizeBytes -ne $build.sourceArchiveBytes -or $receipt.runtimeArchive.sha256 -ine (Hash $archive)) { throw 'Producer omitted exact source/runtime evidence.' }
$checks++
Reject { & $script @options -SourceArchive (Join-Path $TestDirectory 'missing-source.zip') -OutputPath (Join-Path $TestDirectory 'missing-proof.json') } 'Producer requires actual source ZIP'
$sourceOriginal = [IO.File]::ReadAllBytes($sourceArchive)
[IO.File]::AppendAllText($sourceArchive, 'tampered source ZIP', $utf8)
Reject { & $script @options -SourceArchive $sourceArchive -OutputPath (Join-Path $TestDirectory 'tampered-proof.json') } 'Producer hashes actual source bytes' 'actual source archive must match'
[IO.File]::WriteAllBytes($sourceArchive, $sourceOriginal)
Remove-Item -LiteralPath $sourceArchive
& $script @options -ReceiptPath $receiptPath | Out-Null
$checks++ # The consumer does not require or invent a substitute source ZIP.
Reject { & $script @options -ReceiptPath (Join-Path $TestDirectory 'missing-receipt.json') } 'Missing proof'
foreach ($mutation in @(
    @{Name='schema';Change={param($r) $r.schemaVersion=2}},
    @{Name='purpose';Change={param($r) $r.purpose='production-source-exemption'}},
    @{Name='source';Change={param($r) $r.sourceRevision='b'*40}},
    @{Name='version';Change={param($r) $r.version='0.2.124'}},
    @{Name='build';Change={param($r) $r.buildMetadataSha256='0'*64}},
    @{Name='source-name';Change={param($r) $r.sourceArchive.fileName='another-source.zip'}},
    @{Name='source-hash';Change={param($r) $r.sourceArchive.sha256='0'*64}},
    @{Name='source-size';Change={param($r) $r.sourceArchive.sizeBytes++}},
    @{Name='fractional-size';Change={param($r) $r.sourceArchive.sizeBytes=1.5}},
    @{Name='runtime-name';Change={param($r) $r.runtimeArchive.fileName='another-runtime.zip'}},
    @{Name='runtime-hash';Change={param($r) $r.runtimeArchive.sha256='0'*64}},
    @{Name='runtime-size';Change={param($r) $r.runtimeArchive.sizeBytes++}},
    @{Name='missing-source';Change={param($r) $r.PSObject.Properties.Remove('sourceArchive')}}
)) {
    $bad = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
    & $mutation.Change $bad
    $badPath = Join-Path $TestDirectory ($mutation.Name + '.json')
    Json $badPath $bad
    Reject { & $script @options -ReceiptPath $badPath } $mutation.Name
}
$originalBuild = [IO.File]::ReadAllText((Join-Path $package 'build.json'))
[IO.File]::AppendAllText((Join-Path $package 'build.json'), ' ', $utf8)
Reject { & $script @options -ReceiptPath $receiptPath } 'Build bytes changed' 'Runtime archive and source receipt build.json differ'
[IO.File]::WriteAllText((Join-Path $package 'build.json'), $originalBuild, $utf8)
$runtimeOriginal = [IO.File]::ReadAllBytes($archive)
[IO.File]::AppendAllText($archive, 'tampered runtime ZIP', $utf8)
Reject { & $script @options -ReceiptPath $receiptPath } 'Runtime archive changed'
[IO.File]::WriteAllBytes($archive, $runtimeOriginal)

$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'build-installer.ps1'
$smoke = @{InputPackageDirectory=$package;Version=$version;PortableArchive=$archive;SourceReceipt=$receiptPath;
    SmokeTestAppId=('ManaTableNative-Smoke-' + ('c'*32));SmokeTestInstallDirectory=(Join-Path $TestDirectory 'never installed');
    CompilerPath=(Join-Path $TestDirectory 'compiler-must-not-run.exe')}
Reject { & $installer @smoke -OutputDirectory (Join-Path $TestDirectory 'accepted-proof-stop') } 'Valid smoke receipt reaches compiler requirement' 'Install Inno Setup 6'
Reject { & $installer @smoke -SourceArchive (Join-Path $TestDirectory 'missing-source.zip') -OutputDirectory (Join-Path $TestDirectory 'both-inputs') } 'Archive and receipt are mutually exclusive' 'never both'
# Older real-archive callers need no receipt-only metadata addition.
[IO.File]::WriteAllBytes($sourceArchive, $sourceOriginal)
$legacyBuild = $originalBuild | ConvertFrom-Json
$legacyBuild.PSObject.Properties.Remove('sourceArchiveBytes')
Json (Join-Path $package 'build.json') $legacyBuild
Remove-Item -LiteralPath $archive
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Fastest, $true)
[IO.File]::WriteAllText(($archive + '.sha256'), (Hash $archive), $utf8)
$legacy = @{InputPackageDirectory=$package;Version=$version;PortableArchive=$archive;SourceArchive=$sourceArchive;
    CompilerPath=$smoke.CompilerPath;SmokeTestAppId=$smoke.SmokeTestAppId;SmokeTestInstallDirectory=$smoke.SmokeTestInstallDirectory}
Reject { & $installer @legacy -OutputDirectory (Join-Path $TestDirectory 'legacy-source-stop') } 'Older actual source archive journey remains supported' 'Install Inno Setup 6'
Reject { & $script @options -ReceiptPath $receiptPath } 'Receipt requires recorded source size' 'sourceArchiveBytes'
[IO.File]::WriteAllText((Join-Path $package 'build.json'), $originalBuild, $utf8)
[IO.File]::WriteAllBytes($archive, $runtimeOriginal)
[IO.File]::WriteAllText(($archive + '.sha256'), (Hash $archive), $utf8)
Remove-Item -LiteralPath $sourceArchive
$releaseParameters = @((Get-Command $installer).ParameterSets | Where-Object Name -EQ 'Release' | ForEach-Object { $_.Parameters.Name })
if ($releaseParameters -contains 'SourceReceipt') { throw 'A receipt can bind the production installer parameter set.' }
$checks++
$production = [PowerShell]::Create()
try {
    $null = $production.AddCommand($installer).AddParameter('InputPackageDirectory', $package).AddParameter('Version', $version).
        AddParameter('PortableArchive', $archive).AddParameter('OutputDirectory', (Join-Path $TestDirectory 'production-receipt')).AddParameter('SourceReceipt', $receiptPath)
    $bindingError = $null
    try { $null = $production.Invoke() } catch { $bindingError = $_.Exception.GetBaseException() }
    if ($bindingError -isnot [Management.Automation.ParameterBindingException] -or
        $bindingError.ErrorRecord.FullyQualifiedErrorId -notmatch 'MissingMandatoryParameter') {
        throw 'A receipt-only invocation did not require the isolated smoke identity before executing.'
    }
    $checks++
} finally { $production.Dispose() }
Reject { & $installer -InputPackageDirectory $package -Version $version -PortableArchive $archive -SourceArchive (Join-Path $TestDirectory 'missing-source.zip') -OutputDirectory (Join-Path $TestDirectory 'production-missing-source') } 'Production always requires actual source ZIP' 'Source archive not found'

$saved = @{SHA=$env:GITHUB_SHA;Version=$env:MANA_RELEASE_VERSION;Environment=$env:GITHUB_ENV}
try {
    $env:GITHUB_SHA=$revision; $env:MANA_RELEASE_VERSION=$version; $env:GITHUB_ENV=Join-Path $TestDirectory 'github-env.txt'
    Json (Join-Path $TestDirectory 'ci-engine-manifest.json') @{schemaVersion=1;sourceRevision=$revision;engine=@{sha256=$build.engineSha256;bytes=(Get-Item -LiteralPath (Join-Path $package 'engine/forge-engine.jar')).Length}}
    & (Join-Path $PSScriptRoot 'restore-package.ps1') -Directory $TestDirectory
    if (-not ([IO.File]::ReadAllText($env:GITHUB_ENV)).Contains('MANA_NATIVE_SOURCE_RECEIPT=' + $receiptPath)) { throw 'Restore omitted the verified source receipt environment path.' }
    $checks++
} finally { $env:GITHUB_SHA=$saved.SHA; $env:MANA_RELEASE_VERSION=$saved.Version; $env:GITHUB_ENV=$saved.Environment }
Write-Output "$checks source receipt binding and production-isolation fixture checks passed. No UI, compiler, installed code or network was used."
