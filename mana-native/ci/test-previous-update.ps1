[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'previous-update.ps1')
if(-not $TestDirectory){$TestDirectory=Join-Path ([IO.Path]::GetTempPath()) ('mana-previous-update-'+[guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $TestDirectory){throw 'Use a fresh previous-update fixture directory.'}
New-Item -ItemType Directory -Path $TestDirectory | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
$rootUrl='https://github.com/proflayton/Mana-Table/releases/download/mana-native-v0.2.123/'
$api='https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30'
$fullName='ManaTable-0.2.123-win-x64-full.nupkg'
$bytes=$utf8.GetBytes('previous verified full bytes; never executed')
$script:fixturePayloadBytes=$bytes
$hasher=[Security.Cryptography.SHA256]::Create()
try {$hash=([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}
finally {$hasher.Dispose()}
function Fixture([string]$Mutation){
    $contract=@{updateProtocol=2;framework='velopack';frameworkVersion='1.2.161';packageId='ManaTable';channel='win-x64';version='0.2.123';sourceRevision=('a'*40);
        fullPackage=@{fileName=$fullName;sizeBytes=$bytes.Length;sha256=$hash}}
    $installer=@{installerProtocol=1;version='0.2.123';sourceRevision=('a'*40);architecture='win-x64';installer='ManaTable-Setup.exe';smokeOnly=$false;installationFramework='velopack-bridge';managedPackageId='ManaTable'}
    switch($Mutation){
        'identity' {$contract.packageId='OtherApp'}
        'channel' {$contract.channel='other'}
        'framework' {$contract.frameworkVersion='0.0.1'}
        'source' {$installer.sourceRevision='b'*40}
        'smoke' {$installer.smokeOnly=$true}
        'filename' {$contract.fullPackage.fileName='../outside.nupkg'}
        'hash' {$contract.fullPackage.sha256='0'*64}
        'size' {$contract.fullPackage.sizeBytes++}
    }
    $manifestBytes=$utf8.GetBytes(($contract|ConvertTo-Json -Depth 8))
    $installerBytes=$utf8.GetBytes(($installer|ConvertTo-Json -Depth 8))
    $assets=@(
        @{name='update-manifest.json';browser_download_url=($rootUrl+'update-manifest.json');size=$manifestBytes.Length},
        @{name='installer-manifest.json';browser_download_url=($rootUrl+'installer-manifest.json');size=$installerBytes.Length},
        @{name=$fullName;browser_download_url=($rootUrl+$fullName);size=$bytes.Length;digest=('sha256:'+$hash)})
    if($Mutation -eq 'no-manifest'){$assets=@($assets|Where-Object {$_.name -ne 'update-manifest.json'})}
    if($Mutation -eq 'asset-url'){$assets[2].browser_download_url='https://untrusted.invalid/file'}
    if($Mutation -eq 'duplicate'){$assets+=@($assets[2])}
    if($Mutation -eq 'digest'){$assets[2].digest='sha256:'+('0'*64)}
    if($Mutation -eq 'hash'){$assets[2].digest=''}
    $release=@{draft=($Mutation -eq 'draft');prerelease=$false;tag_name='mana-native-v0.2.123';html_url='https://github.com/proflayton/Mana-Table/releases/tag/mana-native-v0.2.123';assets=$assets}
    if($Mutation -eq 'release-url'){$release.html_url='https://untrusted.invalid/release'}
    $script:fixtureApi=$utf8.GetBytes((ConvertTo-Json -InputObject @($release) -Depth 10))
    $script:fixtureManifest=$manifestBytes
    $script:fixtureInstaller=$installerBytes
    $script:fixtureMutation=$Mutation
    $script:fixtureCopies=0
}
$read={param($Url,$Maximum)
    if($Url -ceq $api){return ,$script:fixtureApi}
    if($Url -ceq ($rootUrl+'update-manifest.json')){if($script:fixtureMutation -eq 'manifest-404'){return $null};return ,$script:fixtureManifest}
    if($Url -ceq ($rootUrl+'installer-manifest.json')){return ,$script:fixtureInstaller}
    throw ('Unexpected fixture metadata request: '+$Url)
}
$copy={param($Url,$Path,$Maximum)
    if($Url -cne ($rootUrl+$fullName)){throw 'An untrusted full-package URL was used.'}
    $script:fixtureCopies++
    [IO.File]::WriteAllBytes($Path,$script:fixturePayloadBytes)
}
$checks=0
Fixture 'valid'
$result=Get-ManaPreviousUpdate -Directory (Join-Path $TestDirectory 'valid') -Version '0.2.124' -ReadMetadata $read -CopyAsset $copy
if(-not $result.Available -or $result.Version -cne '0.2.123' -or $result.Sha256 -cne $hash -or $script:fixtureCopies -ne 1){throw 'A verified previous update was not accepted.'}
$checks++
foreach($mutation in @('no-manifest','manifest-404','draft','equal-version','newer-version')){
    Fixture $mutation
    $current=if($mutation -eq 'equal-version'){'0.2.123'}elseif($mutation -eq 'newer-version'){'0.2.122'}else{'0.2.124'}
    $result=Get-ManaPreviousUpdate -Directory (Join-Path $TestDirectory $mutation) -Version $current -ReadMetadata $read -CopyAsset $copy
    if($result.Available -or $script:fixtureCopies -ne 0){throw ('An unavailable/incompatible base was downloaded: '+$mutation)}
    $checks++
}
foreach($mutation in @('identity','channel','framework','source','smoke','filename','hash','size','asset-url','release-url','duplicate','digest')){
    Fixture $mutation
    $rejected=$false
    try{Get-ManaPreviousUpdate -Directory (Join-Path $TestDirectory $mutation) -Version '0.2.124' -ReadMetadata $read -CopyAsset $copy | Out-Null}
    catch{$rejected=$true;Write-Output ('Rejected '+$mutation+': '+$_.Exception.Message)}
    if(-not $rejected){throw ('Accepted an invalid previous update: '+$mutation)}
    $checks++
}
Write-Output "$checks previous-update discovery and rejection checks passed. No network or downloaded code was used."
