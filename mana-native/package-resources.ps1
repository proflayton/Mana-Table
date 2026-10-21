# CardStorageReader accepts cardsfolder.zip inside both script directories.
# Keep the complete tracked scripts in two archives instead of installing tens
# of thousands of individual files. The source archive remains unchanged.
function Copy-ManaEngineResources {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string[]]$TrackedResources
    )
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $prefix = 'forge-gui/res/'
    $archives = @{}
    $counts = @{}
    $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($relative in $TrackedResources) {
            if (-not $relative.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected engine resource path.' }
            $source = Join-Path $Repository $relative
            if ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint) { throw "Engine resources must be regular tracked files: $relative" }
            $resource = $relative.Substring($prefix.Length)
            $separator = $resource.IndexOf('/')
            $folder = if ($separator -ge 0) { $resource.Substring(0, $separator) } else { '' }
            if ($folder -in @('cardsfolder', 'tokenscripts')) {
                if (-not $archives.ContainsKey($folder)) {
                    $directory = Join-Path $Destination $folder
                    [void][IO.Directory]::CreateDirectory($directory)
                    $archives[$folder] = [IO.Compression.ZipFile]::Open((Join-Path $directory 'cardsfolder.zip'), [IO.Compression.ZipArchiveMode]::Create)
                    $counts[$folder] = 0
                }
                $entry = $resource.Substring($separator + 1)
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archives[$folder], $source, $entry, [IO.Compression.CompressionLevel]::Fastest)
                $counts[$folder]++
            } else {
                $target = Join-Path $Destination $resource
                $directory = Split-Path -Parent $target
                if ($directories.Add($directory)) { [void][IO.Directory]::CreateDirectory($directory) }
                [IO.File]::Copy($source, $target, $false)
            }
        }
    } finally {
        foreach ($archive in $archives.Values) { $archive.Dispose() }
    }
    foreach ($folder in @('cardsfolder', 'tokenscripts')) {
        if (-not $counts.ContainsKey($folder)) { throw "No tracked engine scripts found: $folder" }
        Write-Output ("Packed {0} tracked {1} scripts into cardsfolder.zip" -f $counts[$folder], $folder)
    }
}
