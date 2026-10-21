[CmdletBinding()]
param(
    [string]$Repository,
    [string]$ResourcesDirectory,
    [string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'package-resources.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-ScriptArchive([string]$SourceRoot, [string]$PackedRoot, [string]$Folder, [string[]]$Paths) {
    $prefix = 'forge-gui/res/' + $Folder + '/'
    $expected = @($Paths | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
    $archivePath = Join-Path $PackedRoot ($Folder + '/cardsfolder.zip')
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        if ($archive.Entries.Count -ne $expected.Count) { throw "Archive entry count mismatch: $Folder" }
        $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if (-not $names.Add($entry.FullName)) { throw "Duplicate archive entry: $($entry.FullName)" }
        }
        foreach ($relative in $expected) {
            $name = $relative.Substring($prefix.Length)
            $entry = $archive.GetEntry($name)
            if ($null -eq $entry) { throw "Missing archive entry: $name" }
            $source = [IO.File]::OpenRead((Join-Path $SourceRoot $relative))
            $packed = $entry.Open()
            try {
                $sourceHash = [BitConverter]::ToString($hash.ComputeHash($source))
                $packedHash = [BitConverter]::ToString($hash.ComputeHash($packed))
                if ($sourceHash -ne $packedHash) { throw "Archive changed script bytes: $name" }
            } finally { $source.Dispose(); $packed.Dispose() }
        }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $PackedRoot $Folder) -File -Recurse)
        if ($files.Count -ne 1 -or $files[0].Name -ne 'cardsfolder.zip') { throw "Loose script files remain: $Folder" }
        [pscustomobject]@{ folder = $Folder; entries = $expected.Count; sha256Verified = $expected.Count; archiveBytes = (Get-Item -LiteralPath $archivePath).Length }
    } finally { $hash.Dispose(); $archive.Dispose() }
}

if ($Repository -or $ResourcesDirectory) {
    if (-not $Repository -or -not $ResourcesDirectory) { throw 'Pass both Repository and ResourcesDirectory to verify a complete release resource tree.' }
    $tracked = & git -c core.excludesfile= -c core.quotepath=false -C $Repository ls-files -- forge-gui/res/cardsfolder forge-gui/res/tokenscripts
    if ($LASTEXITCODE -ne 0 -or -not $tracked) { throw 'Could not enumerate tracked script sources.' }
} else {
    if (-not $TestDirectory) { $TestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mana-resource-tests-' + [guid]::NewGuid().ToString('N')) }
    if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a new test directory.' }
    $Repository = Join-Path $TestDirectory 'source'
    $ResourcesDirectory = Join-Path $TestDirectory 'packed'
    $tracked = @('forge-gui/res/cardsfolder/a/alpha.txt', ('forge-gui/res/cardsfolder/nested/name ' + [char]0x00e9 + '.txt'), 'forge-gui/res/tokenscripts/elf.txt', 'forge-gui/res/languages/en-US.properties')
    for ($index = 0; $index -lt $tracked.Count; $index++) {
        $file = Join-Path $Repository $tracked[$index]
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $file))
        [IO.File]::WriteAllBytes($file, [byte[]]@(0, 10, 13, 128, 255, $index))
    }
    Copy-ManaEngineResources -Repository $Repository -Destination $ResourcesDirectory -TrackedResources $tracked | Out-Null
    if ([IO.File]::ReadAllBytes((Join-Path $ResourcesDirectory 'languages/en-US.properties'))[5] -ne 3) { throw 'Non-script resource bytes changed.' }
    $rejected = $false
    try { Copy-ManaEngineResources -Repository $Repository -Destination (Join-Path $TestDirectory 'invalid') -TrackedResources @('outside/source.txt') | Out-Null }
    catch { $rejected = $_.Exception.Message -eq 'Unexpected engine resource path.' }
    if (-not $rejected) { throw 'Unexpected resource paths must be rejected.' }
}
foreach ($folder in @('cardsfolder', 'tokenscripts')) { Assert-ScriptArchive $Repository $ResourcesDirectory $folder $tracked }
