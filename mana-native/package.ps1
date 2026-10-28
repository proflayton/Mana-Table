param(
    [string]$EngineResources,
    [string]$OutputDirectory,
    [switch]$SkipBuild,
    [string]$EngineJar,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '0.2.0',
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-source.ps1')
foreach ($part in $Version.Split('.')) { if ([long]$part -gt 65535) { throw 'Version components must be between 0 and 65535.' } }
$revisionOutput = & git -c core.excludesfile= -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or -not $revisionOutput) { throw 'Could not determine the source revision.' }
$revision = $revisionOutput.Trim()
if ($revision -notmatch '^[a-f0-9]{40}$') { throw 'Could not determine the source revision.' }
if ($Release) {
    if ($SkipBuild -or $EngineResources) { throw 'Release packages build fresh application and engine resources; omit -SkipBuild and -EngineResources.' }
    Assert-ManaReleaseSource -Repository $repository -Revision $revision
    if (-not $env:JAVA_HOME -or -not (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME 'bin/jlink.exe'))) { throw 'Set JAVA_HOME to a Windows JDK 17+ containing bin/jlink.exe.' }
} elseif (-not $EngineResources) { throw 'Specify -EngineResources for a preview, or use -Release to build from the checkout and JAVA_HOME.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository ('dist/ManaTable-Native-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory for each package.' }
if (-not $Release) {
    foreach ($required in @('forge-res', 'runtime/bin/java.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $EngineResources $required))) { throw "Missing engine resource: $required" }
    }
}
if (-not $EngineJar) { $EngineJar = Join-Path $repository 'forge-api/target/forge-engine.jar' }
$engineJar = [IO.Path]::GetFullPath($EngineJar)
if (-not (Test-Path -LiteralPath $engineJar)) { throw 'Build the Forge adapter with mvn -pl forge-api -am verify first.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$published = Join-Path $repository '.tools/native-publish'
if ($Release) { $published = Join-Path $repository ('.tools/native-release-' + $Version + '-' + [guid]::NewGuid().ToString('N')) }
if (-not $SkipBuild) {
    & dotnet publish (Join-Path $PSScriptRoot 'Mana.Table/Mana.Table.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false "-p:Version=$Version" "-p:InformationalVersion=$Version+$revision" -p:IncludeSourceRevisionInInformationalVersion=false -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Native publish failed' }
}
if (-not (Test-Path (Join-Path $published 'ManaTable.exe'))) { throw 'No published client found' }
Copy-Item -Path (Join-Path $published '*') -Destination $OutputDirectory -Recurse
$engineDirectory = Join-Path $OutputDirectory 'engine'
New-Item -ItemType Directory -Path $engineDirectory | Out-Null
if ($Release) {
    $resources = Join-Path $engineDirectory 'forge-res'
    $resourcePrefix = 'forge-gui/res/'
    $resourcePaths = @('cardsfolder', 'tokenscripts', 'editions', 'languages', 'blockdata', 'lists', 'setlookup', 'ai', 'formats') | ForEach-Object { $resourcePrefix + $_ }
    $trackedResources = & git -c core.excludesfile= -c core.quotepath=false -C $repository ls-files -- @resourcePaths
    if ($LASTEXITCODE -ne 0 -or -not $trackedResources) { throw 'Could not enumerate tracked engine resources.' }
    . (Join-Path $PSScriptRoot 'package-resources.ps1')
    Copy-ManaEngineResources -Repository $repository -Destination $resources -TrackedResources $trackedResources
    Write-Output 'Building the bundled Java runtime...'
    & (Join-Path $env:JAVA_HOME 'bin/jlink.exe') --add-modules java.se,jdk.unsupported,jdk.crypto.ec --strip-debug --no-header-files --no-man-pages --compress=2 --output (Join-Path $engineDirectory 'runtime')
    if ($LASTEXITCODE -ne 0) { throw 'Bundled Java runtime build failed.' }
} else {
    foreach ($folder in @('forge-res', 'runtime')) { Copy-Item -LiteralPath (Join-Path $EngineResources $folder) -Destination $engineDirectory -Recurse }
}
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
if (-not $Release) {
$source = Join-Path $OutputDirectory 'source/mana-native'
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
    $relative = $file.FullName.Substring($PSScriptRoot.Length).TrimStart('\', '/')
    $destination = Join-Path $source $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$patchPath = Join-Path $OutputDirectory 'source/forge-adapter.patch'
& git -c core.excludesfile= -C $repository diff "--output=$patchPath" -- forge-api/src/main/java forge-game/src/main/java forge-gui/src/main/java forge-ai/src/main/java forge-core/src/main/java
if ($LASTEXITCODE -ne 0) { throw 'Could not export the Forge source patch.' }
# Include new adapter files as source too; git diff alone omits untracked additions.
$adapterSource = Join-Path $OutputDirectory 'source/forge-api'
New-Item -ItemType Directory -Path $adapterSource -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'forge-api/src') -Destination $adapterSource -Recurse
Copy-Item -LiteralPath (Join-Path $repository 'forge-api/pom.xml') -Destination $adapterSource
Copy-Item -LiteralPath (Join-Path $repository 'forge-api/README.md') -Destination $adapterSource
foreach ($module in @('forge-core', 'forge-ai', 'forge-gui')) {
    $moduleSource = Join-Path $OutputDirectory ('source/' + $module)
    New-Item -ItemType Directory -Path $moduleSource -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository ($module + '/src')) -Destination $moduleSource -Recurse
    Copy-Item -LiteralPath (Join-Path $repository ($module + '/pom.xml')) -Destination $moduleSource
}
$documentation = Join-Path $OutputDirectory 'source/docs'
New-Item -ItemType Directory -Path $documentation -Force | Out-Null
foreach ($name in @('Mana-Table-Architecture.md', 'Mana-Table-Testing.md')) {
    Copy-Item -LiteralPath (Join-Path $repository ('docs/Development/' + $name)) -Destination $documentation
}
}
$sourceUrl = 'https://github.com/proflayton/Mana-Table/tree/' + $revision
$build = [ordered]@{ version=$Version; sourceRevision=$revision; engineBaseRevision=$revision; builtAt=(Get-Date).ToUniversalTime().ToString('o'); engineSha256=(Get-FileHash $engineJar -Algorithm SHA256).Hash; client='MonoGame'; backend='Forge'; release=[bool]$Release; sourceUrl=$sourceUrl }
$clientHashes = [ordered]@{}
foreach ($dll in @('ManaTable.dll', 'Mana.Game.dll', 'Mana.Audio.dll', 'Mana.Client.dll', 'Mana.Contracts.dll', 'Mana.Planning.dll', 'Mana.AI.dll', 'Mana.Magic.dll', 'Mana.Forge.dll', 'Mana.Renderer.dll', 'Mana.Scenarios.dll')) {
    $clientHashes[$dll] = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $dll) -Algorithm SHA256).Hash
}
$build['clientFilesSha256'] = $clientHashes
$sourceArchive = $null
$sourceChecksum = $null
if ($Release) {
    $sourceArchive = $OutputDirectory + '-source.zip'
    New-ManaSourceArchive -Repository $repository -Revision $revision -OutputPath $sourceArchive
    $sourceHash = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash
    $sourceChecksum = $sourceArchive + '.sha256'
    $sourceHash | Set-Content -LiteralPath $sourceChecksum -Encoding ASCII
    $build['sourceArchive'] = Split-Path -Leaf $sourceArchive
    $build['sourceArchiveSha256'] = $sourceHash
    $build['sourceArchiveBytes'] = (Get-Item -LiteralPath $sourceArchive).Length
    foreach ($file in Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse) {
        $relative = $file.FullName.Substring($OutputDirectory.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(UserData|\.data|engine-profile|engine-cache|profiles|test-results)(/|$)' -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Player data or a linked file cannot be released: $relative" }
    }
}
$build | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json') -Encoding UTF8
@"
Mana Table $Version (Windows x64, MonoGame)
Source revision: $revision
Complete corresponding source: $sourceUrl
Source download: https://github.com/proflayton/Mana-Table/archive/$revision.zip
Release downloads: https://github.com/proflayton/Mana-Table/releases
Forge upstream: https://github.com/Card-Forge/forge
Forge and the native client are GPL-3.0-or-later; see FORGE-LICENSE.txt.
Bundled third-party runtime notices are retained in licenses/ and engine/runtime/legal/.
Player decks, preferences and artwork are stored separately in %LOCALAPPDATA%\ManaTableNative.
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SOURCE.txt') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = $OutputDirectory + '-windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -Encoding ASCII ($archive + '.sha256')
$manifest = @{ version=$Version; directory=$OutputDirectory; executable='ManaTable.exe'; sourceRevision=$revision; archive=$archive; checksum=($archive + '.sha256') }
if ($Release) { $manifest['sourceArchive'] = $sourceArchive; $manifest['sourceChecksum'] = $sourceChecksum }
New-Item -ItemType Directory -Path (Join-Path $repository 'dist') -Force | Out-Null
$manifest |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $repository 'dist/latest-native.json.tmp')
Move-Item -LiteralPath (Join-Path $repository 'dist/latest-native.json.tmp') -Destination (Join-Path $repository 'dist/latest-native.json') -Force
Write-Output $archive
