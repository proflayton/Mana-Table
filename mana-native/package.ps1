param(
    [Parameter(Mandatory = $true)][string]$EngineResources,
    [string]$OutputDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository ('dist/ManaTable-Native-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory for each package.' }
foreach ($required in @('forge-res', 'runtime/bin/java.exe', 'forge-engine.jar')) {
    if (-not (Test-Path -LiteralPath (Join-Path $EngineResources $required))) { throw "Missing engine resource: $required" }
}
$engineJar = Join-Path $repository 'forge-api/target/forge-engine.jar'
if (-not (Test-Path -LiteralPath $engineJar)) { throw 'Build the Forge adapter with mvn -pl forge-api -am verify first.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$published = Join-Path $repository '.tools/native-publish'
if (-not $SkipBuild) {
    & dotnet publish (Join-Path $PSScriptRoot 'Mana.Table/Mana.Table.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Native publish failed' }
}
if (-not (Test-Path (Join-Path $published 'ManaTable.exe'))) { throw 'No published client found' }
Copy-Item -Path (Join-Path $published '*') -Destination $OutputDirectory -Recurse
$engineDirectory = Join-Path $OutputDirectory 'engine'
New-Item -ItemType Directory -Path $engineDirectory | Out-Null
foreach ($folder in @('forge-res', 'runtime')) { Copy-Item -LiteralPath (Join-Path $EngineResources $folder) -Destination $engineDirectory -Recurse }
Copy-Item -LiteralPath $engineJar -Destination (Join-Path $engineDirectory 'forge-engine.jar')
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $OutputDirectory 'FORGE-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'START-HERE.txt') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY.txt') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $OutputDirectory -Recurse
$packageCache = $env:NUGET_PACKAGES
if (-not $packageCache) { $packageCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
foreach ($package in @('monogame.library.sdl', 'monogame.library.openal', 'nvorbis', 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $packageCache $package) -File -Recurse | Where-Object { $_.Name -match 'LICENSE|NOTICE|COPYING' }) {
        $destination = Join-Path $OutputDirectory ('licenses/' + $package + '/' + $notice.Directory.Name)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $notice.FullName -Destination $destination
    }
}
$source = Join-Path $OutputDirectory 'source/mana-native'
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
    $relative = $file.FullName.Substring($PSScriptRoot.Length).TrimStart('\', '/')
    $destination = Join-Path $source $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$patch = & git -C $repository diff -- forge-api/src/main/java forge-gui/src/main/java
$patch | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'source/forge-adapter.patch')
# Include new adapter files as source too; git diff alone omits untracked additions.
$adapterSource = Join-Path $OutputDirectory 'source/forge-api'
New-Item -ItemType Directory -Path $adapterSource -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'forge-api/src') -Destination $adapterSource -Recurse
Copy-Item -LiteralPath (Join-Path $repository 'forge-api/pom.xml') -Destination $adapterSource
Copy-Item -LiteralPath (Join-Path $repository 'forge-api/README.md') -Destination $adapterSource
Copy-Item -LiteralPath (Join-Path $repository 'scenarios') -Destination (Join-Path $OutputDirectory 'source') -Recurse
$documentation = Join-Path $OutputDirectory 'source/docs'
New-Item -ItemType Directory -Path $documentation -Force | Out-Null
foreach ($name in @('Mana-Table-Architecture.md', 'Mana-Table-Testing.md')) {
    Copy-Item -LiteralPath (Join-Path $repository ('docs/Development/' + $name)) -Destination $documentation
}
$revision = & git -C $repository rev-parse HEAD
@{ engineBaseRevision=$revision; builtAt=(Get-Date).ToUniversalTime().ToString('o'); engineSha256=(Get-FileHash $engineJar -Algorithm SHA256).Hash; client='MonoGame'; backend='Forge' } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'build.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = $OutputDirectory + '-windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -Encoding ASCII ($archive + '.sha256')
Write-Output $archive
