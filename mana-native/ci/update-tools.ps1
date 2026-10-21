[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [string]$ArchivePath,
    [switch]$DownloadOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$version = '1.2.161'
$expectedHash = '2b56ce117f803fc70c103cb423bd040e395e40370f6ff6e10818e9ff9c26a323'
$Directory = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
if (-not $ArchivePath) { $ArchivePath = Join-Path $Directory "vpk.$version.nupkg" }
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    Invoke-WebRequest "https://github.com/velopack/velopack/releases/download/$version/vpk.$version.nupkg" -OutFile $ArchivePath
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ine $expectedHash) { throw 'Pinned Velopack CLI checksum mismatch.' }
if ($DownloadOnly) { return }
# Extraction is from the exact pinned official archive. The CLI requires .NET10;
# the application SDK itself continues to target the application's .NET9.
$toolRoot = Join-Path $Directory ('vpk-' + $version + '-' + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $toolRoot)
$cli = Join-Path $toolRoot 'tools/net10.0/any/vpk.dll'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'Pinned Velopack CLI has no expected .NET10 entry point.' }
if ($env:GITHUB_ENV) { "MANA_VPK=$cli" >> $env:GITHUB_ENV }
Write-Output $cli
