[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [Parameter(Mandatory=$true)][string]$InputPackageDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$PortableArchive,
    [Parameter(Mandatory=$true, ParameterSetName='Create')][string]$SourceArchive,
    [Parameter(Mandatory=$true, ParameterSetName='Create')][string]$OutputPath,
    [Parameter(Mandatory=$true, ParameterSetName='Validate')][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Value($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function Read-Json([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt 65536) { throw 'Source validation metadata must be a bounded JSON file.' }
    return [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
}
function Positive-Bytes($Number) { return ($Number -is [int] -or $Number -is [long]) -and $Number -gt 0 }
$InputPackageDirectory = [IO.Path]::GetFullPath($InputPackageDirectory).TrimEnd('\', '/')
$PortableArchive = [IO.Path]::GetFullPath($PortableArchive)
$buildPath = Join-Path $InputPackageDirectory 'build.json'
$build = Read-Json $buildPath
$revision = Value $build 'sourceRevision'
$sourceName = Value $build 'sourceArchive'
$sourceHash = Value $build 'sourceArchiveSha256'
$sourceBytes = Value $build 'sourceArchiveBytes'
if ((Value $build 'version') -cne $Version -or $Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    $revision -notmatch '^[a-fA-F0-9]{40}$' -or (Value $build 'release') -ne $true -or (Value $build 'smokeOnly') -eq $true -or
    $sourceName -isnot [string] -or $sourceName -notmatch '^[^\\/:]+\.zip$' -or
    $sourceHash -notmatch '^[a-fA-F0-9]{64}$' -or -not (Positive-Bytes $sourceBytes)) {
    throw 'Source receipts require exact release version, revision, source filename, SHA256 and sourceArchiveBytes in build.json.'
}
$buildHash = (Get-FileHash -LiteralPath $buildPath -Algorithm SHA256).Hash
$runtime = Get-Item -LiteralPath $PortableArchive
if ($runtime.PSIsContainer -or $runtime.Length -le 0) { throw 'Source receipts require the real runtime archive.' }
$runtimeHash = (Get-FileHash -LiteralPath $PortableArchive -Algorithm SHA256).Hash
# The receipt describes the same build.json that the runtime archive transports.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($PortableArchive)
try {
    $prefix = [IO.Path]::GetFileName($InputPackageDirectory) + '/build.json'
    $entries = @($zip.Entries | Where-Object { $name = $_.FullName.Replace('\', '/'); $name -ceq $prefix -or $name -ceq 'build.json' })
    if ($entries.Count -ne 1 -or $entries[0].Length -gt 65536) { throw 'Runtime archive must contain exactly one bounded build.json.' }
    $stream = $entries[0].Open(); $hasher = [Security.Cryptography.SHA256]::Create()
    try { $embeddedHash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
    if ($embeddedHash -ine $buildHash) { throw 'Runtime archive and source receipt build.json differ.' }
} finally { $zip.Dispose() }
if ($PSCmdlet.ParameterSetName -eq 'Create') {
    $source = Get-Item -LiteralPath ([IO.Path]::GetFullPath($SourceArchive))
    if ($source.PSIsContainer -or $source.Name -cne $sourceName -or $source.Length -ne $sourceBytes -or
        (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash -ine $sourceHash) {
        throw 'The actual source archive must match build.json filename, size and SHA256 before issuing a receipt.'
    }
    $receipt = [ordered]@{schemaVersion=1;purpose='native-smoke-source-validation';sourceRevision=$revision;version=$Version;
        buildMetadataSha256=$buildHash;sourceArchive=@{fileName=$sourceName;sizeBytes=$sourceBytes;sha256=$sourceHash};
        runtimeArchive=@{fileName=$runtime.Name;sizeBytes=$runtime.Length;sha256=$runtimeHash}}
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $OutputPath) { throw 'Use a fresh source-validation receipt output path.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
    [IO.File]::WriteAllText($OutputPath, ($receipt | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Write-Output $OutputPath
} else {
    $receipt = Read-Json $ReceiptPath
    $recordedSource = Value $receipt 'sourceArchive'; $recordedRuntime = Value $receipt 'runtimeArchive'
    if ((Value $receipt 'schemaVersion') -ne 1 -or (Value $receipt 'purpose') -cne 'native-smoke-source-validation' -or
        (Value $receipt 'sourceRevision') -cne $revision -or (Value $receipt 'version') -cne $Version -or
        (Value $receipt 'buildMetadataSha256') -ine $buildHash -or
        (Value $recordedSource 'fileName') -cne $sourceName -or (Value $recordedSource 'sha256') -ine $sourceHash -or
        -not (Positive-Bytes (Value $recordedSource 'sizeBytes')) -or (Value $recordedSource 'sizeBytes') -ne $sourceBytes -or
        (Value $recordedRuntime 'fileName') -cne $runtime.Name -or (Value $recordedRuntime 'sha256') -ine $runtimeHash -or
        -not (Positive-Bytes (Value $recordedRuntime 'sizeBytes')) -or (Value $recordedRuntime 'sizeBytes') -ne $runtime.Length) {
        throw 'Source-validation receipt does not match this exact source, version, build metadata and runtime archive.'
    }
    Write-Output $receipt
}
