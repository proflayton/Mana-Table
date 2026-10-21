[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceRevision
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Directory = [IO.Path]::GetFullPath($Directory)
function Read-Json([string]$Name) { Get-Content -LiteralPath (Join-Path $Directory $Name) -Raw | ConvertFrom-Json }
function Verify-File([string]$Name,[string]$Hash,[long]$Size=-1) {
    if ([IO.Path]::GetFileName($Name) -cne $Name -or $Name -match '[\\/:]' -or $Hash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid release asset identity.' }
    $path = Join-Path $Directory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        ($Size -ge 0 -and (Get-Item -LiteralPath $path).Length -ne $Size) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Hash) { throw "Release asset checksum or length mismatch: $Name" }
}
$build = Read-Json 'build.json'
$smoke = $build.PSObject.Properties['smokeOnly']
if ($build.version -cne $Version -or $build.sourceRevision -cne $SourceRevision -or $build.release -ne $true -or ($smoke -and $smoke.Value)) { throw 'Only this exact production build can be published.' }
$legacy = Read-Json 'installer-manifest.json'
if ($legacy.installerProtocol -ne 1 -or $legacy.smokeOnly -ne $false -or $legacy.architecture -cne 'win-x64' -or
    $legacy.installationFramework -cne 'velopack-bridge' -or $legacy.managedPackageId -cne 'ManaTable' -or
    $legacy.installer -cne 'ManaTable-Setup.exe' -or $legacy.version -cne $Version -or $legacy.sourceRevision -cne $SourceRevision) { throw 'The canonical installer must be the exact production protocol1 migration bridge.' }
Verify-File 'ManaTable-Setup.exe' $legacy.installerSha256 $legacy.installerBytes
if ($legacy.buildMetadataSha256 -ine (Get-FileHash -LiteralPath (Join-Path $Directory 'build.json') -Algorithm SHA256).Hash) { throw 'Installer metadata does not bind this build.' }
$publish = New-Object 'System.Collections.Generic.List[string]'
foreach ($name in @('ManaTable-Setup.exe','ManaTable-Windows-x64.zip','ManaTable-Source.zip')) {
    $checksumName = $name + '.sha256'
    $hash = ((Get-Content -LiteralPath (Join-Path $Directory $checksumName) -Raw).Trim() -split '\s+')[0]
    Verify-File $name $hash
    $publish.Add($name); $publish.Add($checksumName)
}
if ($legacy.portableArchiveSha256 -ine (Get-FileHash -LiteralPath (Join-Path $Directory 'ManaTable-Windows-x64.zip') -Algorithm SHA256).Hash -or
    $legacy.sourceArchiveSha256 -ine (Get-FileHash -LiteralPath (Join-Path $Directory 'ManaTable-Source.zip') -Algorithm SHA256).Hash) { throw 'Canonical downloads differ from the installer inputs.' }
$publish.Add('installer-manifest.json')
$contract = Read-Json 'update-manifest.json'
if ($contract.updateProtocol -ne 2 -or $contract.framework -cne 'velopack' -or $contract.frameworkVersion -cne '1.2.161' -or
    $contract.packageId -cne 'ManaTable' -or $contract.channel -cne 'win-x64' -or
    $contract.version -cne $Version -or $contract.sourceRevision -cne $SourceRevision) { throw 'The incremental update contract does not match this release.' }
$index = Read-Json 'releases.win-x64.json'
$assets = @($index.Assets)
$full = @($assets | Where-Object { $_.Type -ceq 'Full' })
$delta = @($assets | Where-Object { $_.Type -ceq 'Delta' })
if ($full.Count -ne 1 -or $delta.Count -gt 1 -or $assets.Count -ne (1 + $delta.Count)) { throw 'Expected one current full package and at most one current delta.' }
foreach ($asset in $assets) {
    $suffix = $asset.Type.ToLowerInvariant()
    if ($asset.PackageId -cne 'ManaTable' -or $asset.Version -cne $Version -or
        $asset.FileName -cne ('ManaTable-' + $Version + '-win-x64-' + $suffix + '.nupkg') -or $asset.Size -le 0) { throw 'An SDK release asset belongs to another identity, channel or version.' }
    Verify-File $asset.FileName $asset.SHA256 $asset.Size
    $publish.Add($asset.FileName)
}
if ($contract.fullPackage.fileName -cne $full[0].FileName -or $contract.fullPackage.sha256 -ine $full[0].SHA256 -or
    $contract.fullPackage.sizeBytes -ne $full[0].Size) { throw 'The update contract and SDK index disagree about the full package.' }

# Validate the complete published contract against the actual package, including
# metadata/engine bytes. Index and payload are verified again by the client.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($contract.entries)) {
    if (-not $entry.path -or $entry.path -match '(^|/)\.\.?(/|$)|[\\:]|^/' -or $entry.path.EndsWith('/') -or
        $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $entry.sizeBytes -lt 0 -or $expected.ContainsKey($entry.path)) { throw 'Unsafe or duplicate update contract entry.' }
    $expected.Add($entry.path,$entry)
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory $full[0].FileName))
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $expected.ContainsKey($entry.FullName)) { throw "Unlisted full-package entry: $($entry.FullName)" }
        $record = $expected[$entry.FullName]
        $stream=$entry.Open(); $hasher=[Security.Cryptography.SHA256]::Create()
        try { $hash=([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','') }
        finally { $hasher.Dispose(); $stream.Dispose() }
        if ($entry.Length -ne $record.sizeBytes -or $hash -ine $record.sha256) { throw "Update contract entry checksum mismatch: $($entry.FullName)" }
        [void]$expected.Remove($entry.FullName)
    }
    if ($expected.Count -ne 0) { throw 'The full package is missing declared entries.' }
} finally { $zip.Dispose() }
$buildEntry = @($contract.entries | Where-Object { $_.path -ceq 'lib/app/build.json' })
if ($buildEntry.Count -ne 1 -or $buildEntry[0].sha256 -ine $legacy.buildMetadataSha256) { throw 'The incremental payload and migration bridge describe different builds.' }
$publish.Add('releases.win-x64.json'); $publish.Add('update-manifest.json')
$publish.ToArray()
