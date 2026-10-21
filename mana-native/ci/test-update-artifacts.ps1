[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
if(-not $TestDirectory){$TestDirectory=Join-Path ([IO.Path]::GetTempPath()) ('mana-update-artifacts-'+[guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $TestDirectory){throw 'Use a fresh artifact fixture directory.'}
New-Item -ItemType Directory -Path $TestDirectory | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
$version='0.2.123'; $source='a'*40
function Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 12),$utf8)}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Fixture([string]$Mutation){
    $directory=Join-Path $TestDirectory $Mutation
    $payload=Join-Path $directory 'payload/lib/app'
    New-Item -ItemType Directory -Path $payload | Out-Null
    $build=@{version=$version;sourceRevision=$source;release=$true}
    if($Mutation -eq 'smoke'){$build.smokeOnly=$true}
    Json (Join-Path $directory 'build.json') $build
    Copy-Item -LiteralPath (Join-Path $directory 'build.json') -Destination $payload
    [IO.File]::WriteAllText((Join-Path $payload 'ManaTable.dll'),'verified product bytes',$utf8)
    $entries=@(Get-ChildItem -LiteralPath $payload -File | ForEach-Object {@{path=('lib/app/'+$_.Name);sizeBytes=$_.Length;sha256=(Hash $_.FullName)}})
    $fullName='ManaTable-'+$version+'-win-x64-full.nupkg'
    $fullPath=Join-Path $directory $fullName
    $archive=[IO.Compression.ZipFile]::Open($fullPath,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($entry in $entries){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $payload ([IO.Path]::GetFileName($entry.path))),$entry.path,[IO.Compression.CompressionLevel]::Fastest)}
    } finally {$archive.Dispose()}
    foreach($name in @('ManaTable-Setup.exe','ManaTable-Windows-x64.zip','ManaTable-Source.zip')){
        $path=Join-Path $directory $name
        [IO.File]::WriteAllText($path,('fixture only '+$name),$utf8)
        [IO.File]::WriteAllText(($path+'.sha256'),((Hash $path)+'  '+$name),$utf8)
    }
    $legacy=@{installerProtocol=1;smokeOnly=$false;architecture='win-x64';installationFramework='velopack-bridge';managedPackageId='ManaTable';installer='ManaTable-Setup.exe';version=$version;sourceRevision=$source;
        installerSha256=(Hash (Join-Path $directory 'ManaTable-Setup.exe'));installerBytes=(Get-Item -LiteralPath (Join-Path $directory 'ManaTable-Setup.exe')).Length;
        buildMetadataSha256=(Hash (Join-Path $directory 'build.json'));portableArchiveSha256=(Hash (Join-Path $directory 'ManaTable-Windows-x64.zip'));sourceArchiveSha256=(Hash (Join-Path $directory 'ManaTable-Source.zip'))}
    $asset=@{PackageId='ManaTable';Version=$version;Type='Full';FileName=$fullName;SHA256=(Hash $fullPath);Size=(Get-Item -LiteralPath $fullPath).Length}
    $contract=@{updateProtocol=2;framework='velopack';frameworkVersion='1.2.161';packageId='ManaTable';channel='win-x64';version=$version;sourceRevision=$source;
        fullPackage=@{fileName=$asset.FileName;sizeBytes=$asset.Size;sha256=$asset.SHA256};entries=$entries}
    switch($Mutation){
        'source' {$contract.sourceRevision='b'*40}
        'identity' {$asset.PackageId='OtherApp'}
        'channel' {$contract.channel='other'}
        'framework' {$contract.frameworkVersion='9.9.9'}
        'installer' {$legacy.installationFramework='inno-smoke-fixture'}
        'hash' {$asset.SHA256='0'*64}
        'length' {$asset.Size++}
        'traversal' {$contract.entries[0].path='../outside'}
        'duplicate' {$contract.entries+=@($contract.entries[0])}
        'entry-hash' {$contract.entries[0].sha256='0'*64}
        'missing-entry' {$contract.entries=@($contract.entries[0])}
        'build-binding' {$legacy.buildMetadataSha256='0'*64}
    }
    Json (Join-Path $directory 'installer-manifest.json') $legacy
    Json (Join-Path $directory 'releases.win-x64.json') @{Assets=@($asset)}
    Json (Join-Path $directory 'update-manifest.json') $contract
    return $directory
}
$checks=0
$valid=Fixture 'valid'
$names=@(& (Join-Path $PSScriptRoot 'validate-update-artifacts.ps1') -Directory $valid -Version $version -SourceRevision $source)
if($names.Count -ne 10 -or @($names|Sort-Object -Unique).Count -ne 10 -or $names -contains 'build.json'){throw 'Publication inventory omitted or exposed an asset.'}
$checks++
foreach($mutation in @('smoke','source','identity','channel','framework','installer','hash','length','traversal','duplicate','entry-hash','missing-entry','build-binding')){
    $fixture=Fixture $mutation
    $rejected=$false
    try{& (Join-Path $PSScriptRoot 'validate-update-artifacts.ps1') -Directory $fixture -Version $version -SourceRevision $source | Out-Null}
    catch{$rejected=$true;Write-Output ('Rejected '+$mutation+': '+$_.Exception.Message)}
    if(-not $rejected){throw ('Accepted a damaged release: '+$mutation)}
    $checks++
}
Write-Output "$checks update publication provenance/inventory checks passed. No code was downloaded or executed."
