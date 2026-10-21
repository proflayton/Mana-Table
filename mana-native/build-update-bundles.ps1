[CmdletBinding(DefaultParameterSetName='Production')]
param(
    [Parameter(Mandatory=$true)][string]$InputPackageDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$VpkDll,
    [string]$PreviousFullPackage,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PreviousFullSha256,
    [Parameter(Mandatory=$true, ParameterSetName='SmokeTest')]
    [ValidatePattern('^ManaTableNative-Smoke-[a-fA-F0-9]{32}$')][string]$SmokeTestAppId,
    [Parameter(ParameterSetName='SmokeTest')][ValidatePattern('^[A-Za-z0-9.-]+\.exe$')][string]$SmokeTestMainExecutable = 'ManaTable.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8 = New-Object Text.UTF8Encoding($false)
$InputPackageDirectory = [IO.Path]::GetFullPath($InputPackageDirectory).TrimEnd('\','/')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
$VpkDll = [IO.Path]::GetFullPath($VpkDll)
if ((Test-Path -LiteralPath $OutputDirectory) -or $OutputDirectory.StartsWith($InputPackageDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a new output directory outside the immutable input package.'
}
$appId = if ($PSCmdlet.ParameterSetName -eq 'SmokeTest') { $SmokeTestAppId } else { 'ManaTable' }
$mainExe = if ($PSCmdlet.ParameterSetName -eq 'SmokeTest') { $SmokeTestMainExecutable } else { 'ManaTable.exe' }
foreach ($file in @($mainExe,[IO.Path]::ChangeExtension($mainExe,'.dll'),'Mana.Client.dll','Velopack.dll','build.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $InputPackageDirectory $file) -PathType Leaf)) { throw "Missing update package file: $file" }
}
$metadata = Get-Content -LiteralPath (Join-Path $InputPackageDirectory 'build.json') -Raw | ConvertFrom-Json
if ($metadata.version -cne $Version -or $metadata.release -ne $true -or $metadata.sourceRevision -notmatch '^[a-fA-F0-9]{40}$') { throw 'Update payload metadata does not match the requested exact release.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $InputPackageDirectory 'Velopack.dll')).ProductVersion -cne '1.2.161+92d6a1c') { throw 'Update payload must use the pinned Velopack1.2.161 SDK.' }
if (-not (Test-Path -LiteralPath $VpkDll -PathType Leaf)) { throw 'Run ci/update-tools.ps1 to obtain the pinned CLI.' }
$cliHelp = (& dotnet $VpkDll --help | Out-String)
if ($LASTEXITCODE -ne 0 -or $cliHelp -notmatch 'Velopack CLI 1\.2\.161,') { throw 'The Velopack CLI must be version1.2.161.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$previousCopy = $null
if ($PreviousFullPackage) {
    $PreviousFullPackage = [IO.Path]::GetFullPath($PreviousFullPackage)
    if (-not $PreviousFullSha256 -or (Get-FileHash -LiteralPath $PreviousFullPackage -Algorithm SHA256).Hash -ine $PreviousFullSha256) { throw 'The previous full package must have its verified published SHA256.' }
    $previousName = [IO.Path]::GetFileName($PreviousFullPackage)
    if ($previousName -notmatch ('^' + [regex]::Escape($appId) + '-[0-9]+\.[0-9]+\.[0-9]+(?:-win-x64)?-full\.nupkg$')) { throw 'Previous full package belongs to another application/channel.' }
    $previousCopy = Join-Path $OutputDirectory $previousName
    Copy-Item -LiteralPath $PreviousFullPackage -Destination $previousCopy
}
$title = if ($SmokeTestAppId) { $SmokeTestAppId } else { 'Mana Table' }
$arguments = @('pack','--packId',$appId,'--packVersion',$Version,'--packDir',$InputPackageDirectory,'--outputDir',$OutputDirectory,
    '--mainExe',$mainExe,'--packTitle',$title,'--packAuthors','Mana Table contributors','--runtime','win-x64','--channel','win-x64','--delta','BestSpeed','--shortcuts','None')
& dotnet $VpkDll @arguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack update package creation failed.' }
$indexPath = Join-Path $OutputDirectory 'releases.win-x64.json'
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$current = @($index.Assets | Where-Object { $_.Version -ceq $Version -and $_.Type -in @('Full','Delta') })
$full = @($current | Where-Object { $_.Type -ceq 'Full' })
if ($full.Count -ne 1) { throw 'Expected exactly one new full update package.' }
# Each GitHub release index describes only the assets attached to that release.
# Discovery combines the available release indexes into a delta chain.
[IO.File]::WriteAllText($indexPath, (@{ Assets = $current } | ConvertTo-Json -Depth 12), $utf8)
$packagePath = Join-Path $OutputDirectory $full[0].FileName
if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ine $full[0].SHA256 -or (Get-Item -LiteralPath $packagePath).Length -ne $full[0].Size) { throw 'New full package does not match its index.' }
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
$entries = New-Object 'System.Collections.Generic.List[object]'
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if ($name.EndsWith('/')) { continue }
        if ($name -match '(^|/)\.\.?(/|$)|[\\:]|^/' -or -not $seen.Add($name)) { throw "Unsafe or duplicate package entry: $name" }
        $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
        finally { $hasher.Dispose(); $stream.Dispose() }
        $entries.Add([ordered]@{ path=$name; sizeBytes=$entry.Length; sha256=$hash })
    }
} finally { $archive.Dispose() }
$manifest = [ordered]@{
    updateProtocol=2; framework='velopack'; frameworkVersion='1.2.161'; packageId=$appId; channel='win-x64'; version=$Version; sourceRevision=$metadata.sourceRevision
    fullPackage=[ordered]@{fileName=$full[0].FileName; sizeBytes=$full[0].Size; sha256=$full[0].SHA256}
    entries=$entries.ToArray()
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'update-manifest.json'), ($manifest | ConvertTo-Json -Depth 12), $utf8)
if ($previousCopy) {
    $resolvedCopy = [IO.Path]::GetFullPath($previousCopy)
    if (-not $resolvedCopy.StartsWith($OutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove a previous-package copy outside the output directory.' }
    Remove-Item -LiteralPath $resolvedCopy
}
foreach ($asset in $current) {
    $path = Join-Path $OutputDirectory $asset.FileName
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $asset.SHA256) { throw "Update artifact checksum mismatch: $($asset.FileName)" }
}
$setups = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*Setup.exe')
if ($setups.Count -ne 1) { throw 'Expected exactly one genuine Velopack setup executable.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'update-bundle-report.json'), ([ordered]@{
    version=$Version; sourceRevision=$metadata.sourceRevision; framework='Velopack1.2.161'; packageId=$appId
    fullBytes=$full[0].Size; deltaBytes=@($current | Where-Object { $_.Type -ceq 'Delta' } | ForEach-Object { $_.Size }); verifiedFileEntries=$entries.Count
    setupFileName=$setups[0].Name; setupBytes=$setups[0].Length; setupSha256=(Get-FileHash -LiteralPath $setups[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    inputBuildMetadataSha256=(Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory 'build.json') -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 6), $utf8)
Write-Output "Created verified full/delta update artifacts for $appId $Version."
