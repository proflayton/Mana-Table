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
# Exercise the actual request objects, without reading a real credential or
# sending any request. An API credential must never follow public asset URLs.
$fixtureToken='fixture.read-only-token'
$request=New-ManaPreviousUpdateRequest -Url $api -Token $fixtureToken
try {
    $authorization=$request.Headers.Authorization
    if($request.Method.Method -cne 'GET' -or $request.RequestUri.OriginalString -cne $api -or $null -ne $request.Content -or $request.Headers.UserAgent.Count -eq 0 -or
        $null -eq $authorization -or $authorization.Scheme -cne 'Bearer' -or $authorization.Parameter -cne $fixtureToken){throw 'The exact releases lookup must be a scoped authenticated GET without a body.'}
} finally {$request.Dispose()}
$checks++
$publicUrls=@(($rootUrl+'update-manifest.json'),($rootUrl+'installer-manifest.json'),($rootUrl+$fullName))
foreach($publicUrl in $publicUrls){
    foreach($credential in @($fixtureToken,$null)){
        $request=New-ManaPreviousUpdateRequest -Url $publicUrl -Token $credential
        try {
            if($request.Method.Method -cne 'GET' -or $request.RequestUri.OriginalString -cne $publicUrl -or $null -ne $request.Content -or
                $null -ne $request.Headers.Authorization){throw 'A canonical public release asset must remain an anonymous GET, including after an authenticated lookup.'}
        } finally {$request.Dispose()}
        $checks++
    }
}
function Expect-RequestRejection([string]$Url,[AllowNull()][string]$Token,[switch]$Credential){
    $unexpected=$null; $rejected=$false; $message=''
    try {$unexpected=New-ManaPreviousUpdateRequest -Url $Url -Token $Token}
    catch {$rejected=$true; $message=$_.Exception.Message}
    finally {if($null -ne $unexpected){$unexpected.Dispose()}}
    if(-not $rejected){throw 'An unsupported previous-update request or credential was accepted.'}
    if($Credential -and $message -notmatch 'GH_TOKEN'){throw 'A missing or malformed credential must produce an explicit GH_TOKEN diagnostic.'}
    if(-not [string]::IsNullOrWhiteSpace($Token) -and $message.Contains($Token)){throw 'A rejected credential appeared in the diagnostic.'}
}
foreach($credential in @($null,'','   ','fixture token',"fixture`r`nInjected: value",'fixture!token')){
    Expect-RequestRejection -Url $api -Token $credential -Credential
    $checks++
}
foreach($unsupportedUrl in @(
    'https://untrusted.invalid/repos/proflayton/Mana-Table/releases?per_page=30',
    'https://api.github.com.untrusted.invalid/repos/proflayton/Mana-Table/releases?per_page=30',
    'https://api.github.com/repos/else/Other/releases?per_page=30',
    'https://api.github.com/repos/Proflayton/Mana-Table/releases?per_page=30',
    'https://api.github.com/repos/proflayton/Mana-Table/releases/?per_page=30',
    'https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=10',
    'https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30&page=1',
    'https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=%33%30',
    'http://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30',
    'https://api.github.com:443/repos/proflayton/Mana-Table/releases?per_page=30',
    'https://api.github.com@untrusted.invalid/repos/proflayton/Mana-Table/releases?per_page=30',
    ($api+'#fragment'),
    ($api+"`n"),
    ($rootUrl.Replace('github.com','untrusted.invalid')+'update-manifest.json'),
    ($rootUrl.Replace('github.com','github.com.untrusted.invalid')+'update-manifest.json'),
    ($rootUrl.Replace('proflayton/Mana-Table','else/Other')+'update-manifest.json'),
    ($rootUrl.Replace('https://','http://')+'update-manifest.json'),
    ($rootUrl.Replace('https://github.com','https://user@github.com')+'update-manifest.json'),
    ($rootUrl.Replace('github.com/','github.com:443/')+'update-manifest.json'),
    ($rootUrl+'update-manifest.json?download=1'),
    ($rootUrl+'update-manifest.json#fragment'),
    ($rootUrl+"update-manifest.json`n"),
    ($rootUrl+'ManaTable-0.2.122-win-x64-full.nupkg'),
    ($rootUrl+'../update-manifest.json'),
    ($rootUrl+'ManaTable-Setup.exe'),
    (' '+$rootUrl+'update-manifest.json'))){
    Expect-RequestRejection -Url $unsupportedUrl -Token $fixtureToken
    $checks++
}
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
Write-Output "$checks previous-update request-scope, discovery and rejection checks passed. No network or downloaded code was used."
