[CmdletBinding()]
param([string]$Directory,[ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Dot-source only to test the metadata/download boundary with local fixtures.
# The normal script supplies HTTP callbacks for the fixed public repository.
function Get-ManaPreviousUpdate {
    param([string]$Directory,[Version]$Version,[scriptblock]$ReadMetadata,[scriptblock]$CopyAsset)
    $Directory=[IO.Path]::GetFullPath($Directory).TrimEnd('\','/')
    if ((Test-Path -LiteralPath $Directory) -or $Directory -eq [IO.Path]::GetPathRoot($Directory)) { throw 'Use a fresh previous-update directory.' }
    $repository='https://github.com/proflayton/Mana-Table'
    $api='https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30'
    $bytes=& $ReadMetadata $api (2*1024*1024)
    $items=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    $candidates=@(foreach ($item in $items) {
        if ($item.draft -ne $false -or $item.prerelease -ne $false -or $item.tag_name -notmatch '^mana-native-v([0-9]+\.[0-9]+\.[0-9]+)$') { continue }
        $candidateVersion=$null
        if (-not [Version]::TryParse($Matches[1],[ref]$candidateVersion) -or $candidateVersion.ToString(3) -cne $Matches[1]) { continue }
        [pscustomobject]@{Version=$candidateVersion;Release=$item}
    })
    if ($candidates.Count -eq 0) { return [pscustomobject]@{Available=$false;Reason='No published stable native release.'} }
    $selected=$candidates | Sort-Object Version -Descending | Select-Object -First 1
    if ($selected.Version -ge $Version) { return [pscustomobject]@{Available=$false;Reason='The latest published release is not older than this build.'} }
    $release=$selected.Release; $previousVersion=$selected.Version.ToString(3); $tag='mana-native-v'+$previousVersion
    if ($release.html_url -cne "$repository/releases/tag/$tag") { throw 'Previous release page URL is not canonical.' }
    function Asset([string]$Name,[bool]$Optional=$false) {
        $found=@($release.assets | Where-Object { $_.name -ceq $Name })
        if ($found.Count -eq 0 -and $Optional) { return $null }
        if ($found.Count -ne 1 -or $found[0].browser_download_url -cne "$repository/releases/download/$tag/$Name") { throw "Previous release asset is missing, duplicated or redirected: $Name" }
        return $found[0]
    }
    $manifestAsset=Asset 'update-manifest.json' $true
    if ($null -eq $manifestAsset) { return [pscustomobject]@{Available=$false;Reason='Latest native release predates managed updates.'} }
    if ($manifestAsset.size -le 0 -or $manifestAsset.size -gt 4*1024*1024) { throw 'Previous update manifest exceeds its size bound.' }
    $manifestBytes=& $ReadMetadata $manifestAsset.browser_download_url (4*1024*1024)
    if ($null -eq $manifestBytes) { return [pscustomobject]@{Available=$false;Reason='Latest native release has no available managed manifest.'} }
    if ($manifestBytes.Length -ne $manifestAsset.size) { throw 'Previous manifest length differs from its asset.' }
    $manifest=[Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    if ($manifest.updateProtocol -ne 2 -or $manifest.framework -cne 'velopack' -or $manifest.frameworkVersion -cne '1.2.161' -or $manifest.packageId -cne 'ManaTable' -or
        $manifest.channel -cne 'win-x64' -or $manifest.version -cne $previousVersion -or $manifest.sourceRevision -notmatch '^[a-fA-F0-9]{40}$') { throw 'Previous managed update contract is incompatible.' }
    $installerAsset=Asset 'installer-manifest.json'
    $installerBytes=& $ReadMetadata $installerAsset.browser_download_url 65536
    $installer=[Text.Encoding]::UTF8.GetString($installerBytes) | ConvertFrom-Json
    if ($installer.installerProtocol -ne 1 -or $installer.version -cne $previousVersion -or $installer.sourceRevision -cne $manifest.sourceRevision -or
        $installer.architecture -cne 'win-x64' -or $installer.installer -cne 'ManaTable-Setup.exe' -or $installer.smokeOnly -ne $false -or
        $installer.installationFramework -cne 'velopack-bridge' -or $installer.managedPackageId -cne 'ManaTable') { throw 'Previous native installer compatibility manifest does not match the managed release.' }
    $full=$manifest.fullPackage
    if ($full.fileName -cne "ManaTable-$previousVersion-win-x64-full.nupkg" -or $full.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $full.sizeBytes -le 0 -or $full.sizeBytes -gt 750L*1024*1024) { throw 'Previous full package identity is invalid.' }
    $asset=Asset $full.fileName
    if ($asset.size -ne $full.sizeBytes) { throw 'Previous full package size differs from its release asset.' }
    $digestProperty=$asset.PSObject.Properties['digest']
    if ($digestProperty -and $digestProperty.Value -and $digestProperty.Value -cne ('sha256:'+([string]$full.sha256).ToLowerInvariant())) { throw 'Previous full package digest differs from GitHub.' }
    New-Item -ItemType Directory -Path $Directory | Out-Null
    $path=Join-Path $Directory $full.fileName
    $partial=$path+'.partial'
    try {
        & $CopyAsset $asset.browser_download_url $partial ([long]$full.sizeBytes)
        if ((Get-Item -LiteralPath $partial).Length -ne $full.sizeBytes -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $full.sha256) { throw 'Previous full package failed exact size/SHA256 verification.' }
        Move-Item -LiteralPath $partial -Destination $path
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
    return [pscustomobject]@{Available=$true;Version=$previousVersion;SourceRevision=$manifest.sourceRevision;FullPackage=$path;Sha256=([string]$full.sha256).ToLowerInvariant()}
}

if ($MyInvocation.InvocationName -ne '.') {
    if (-not $Directory -or -not $Version) { throw 'Directory and Version are required.' }
    Add-Type -AssemblyName System.Net.Http
    $http=New-Object Net.Http.HttpClient
    $http.Timeout=[TimeSpan]::FromMinutes(10)
    $http.DefaultRequestHeaders.UserAgent.ParseAdd('ManaTable-CI/1.0')
    $http.DefaultRequestHeaders.Add('X-GitHub-Api-Version','2022-11-28')
    $read={ param($Url,$Maximum)
        $response=$http.GetAsync($Url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -eq 404) { return $null }
            $null=$response.EnsureSuccessStatusCode()
            if ($response.Content.Headers.ContentLength -gt $Maximum) { throw 'Previous release metadata is too large.' }
            $inputStream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output=New-Object IO.MemoryStream; $buffer=New-Object byte[] 16384
            try {
                while (($count=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
                    if ($output.Length+$count -gt $Maximum) { throw 'Previous release metadata is too large.' }
                    $output.Write($buffer,0,$count)
                }
                return ,$output.ToArray()
            } finally { $inputStream.Dispose(); $output.Dispose() }
        } finally { $response.Dispose() }
    }
    $copy={ param($Url,$Path,$Maximum)
        $response=$http.GetAsync($Url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $null=$response.EnsureSuccessStatusCode()
            if ($response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $Maximum) { throw 'Previous full package response length changed.' }
            $inputStream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output=[IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            $buffer=New-Object byte[] 65536; $received=0L
            try {
                while (($count=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
                    $received+=$count
                    if ($received -gt $Maximum) { throw 'Previous full package exceeds its published size.' }
                    $output.Write($buffer,0,$count)
                }
                $output.Flush($true)
            } finally { $inputStream.Dispose(); $output.Dispose() }
        } finally { $response.Dispose() }
    }
    try {
        $result=Get-ManaPreviousUpdate -Directory $Directory -Version $Version -ReadMetadata $read -CopyAsset $copy
        $env:MANA_PREVIOUS_FULL=''; $env:MANA_PREVIOUS_SHA=''
        if ($result.Available) { $env:MANA_PREVIOUS_FULL=$result.FullPackage; $env:MANA_PREVIOUS_SHA=$result.Sha256 }
        if ($env:GITHUB_ENV) {
            $utf8=New-Object Text.UTF8Encoding($false)
            [IO.File]::AppendAllText($env:GITHUB_ENV,"MANA_PREVIOUS_FULL=$env:MANA_PREVIOUS_FULL`nMANA_PREVIOUS_SHA=$env:MANA_PREVIOUS_SHA`n",$utf8)
        }
        $result | ConvertTo-Json -Depth 5
    } finally { $http.Dispose() }
}
