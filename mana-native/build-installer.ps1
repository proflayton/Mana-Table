[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(Mandatory = $true)][string]$InputPackageDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$PortableArchive,
    [string]$SourceArchive,
    [string]$ManagedSetup,
    [Parameter(Mandatory = $true, ParameterSetName = 'SmokeTest')]
    [ValidatePattern('^ManaTableNative-Smoke-[a-fA-F0-9]{32}$')][string]$SmokeTestAppId,
    [Parameter(Mandatory = $true, ParameterSetName = 'SmokeTest')][string]$SmokeTestInstallDirectory,
    [Parameter(ParameterSetName = 'SmokeTest')][string]$SmokeTestManagedDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($part in $Version.Split('.')) {
    if ([long]$part -gt 65535) { throw 'Every version component must fit the Windows range 0 through 65535.' }
}
$InputPackageDirectory = [IO.Path]::GetFullPath($InputPackageDirectory).TrimEnd('\', '/')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $InputPackageDirectory -PathType Container)) { throw "Package directory not found: $InputPackageDirectory" }
if ($OutputDirectory.Equals($InputPackageDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($InputPackageDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installer output must be outside the input package.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory for each installer build.' }
foreach ($required in @('ManaTable.exe', 'ManaTable.dll', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'build.json', 'FORGE-LICENSE.txt', 'THIRD-PARTY.txt', 'licenses',
    'SOURCE.txt', 'engine/forge-engine.jar', 'engine/runtime/bin/java.exe', 'engine/forge-res')) {
    if (-not (Test-Path -LiteralPath (Join-Path $InputPackageDirectory $required))) { throw "Incomplete native package: $required" }
}
$metadata = Get-Content -LiteralPath (Join-Path $InputPackageDirectory 'build.json') -Raw | ConvertFrom-Json
function MetadataValue([string]$Name) {
    $property = $metadata.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
if ((MetadataValue 'version') -ne $Version) { throw 'Installer version must match build.json version.' }
if ($PSCmdlet.ParameterSetName -eq 'Release' -and ((MetadataValue 'release') -ne $true -or (MetadataValue 'smokeOnly') -eq $true)) { throw 'Production installers require a genuine production package, not a smoke fixture.' }
if ((MetadataValue 'client') -ne 'MonoGame' -or (MetadataValue 'backend') -ne 'Forge') { throw 'Only native MonoGame/Forge packages can be installed.' }
if (-not (MetadataValue 'sourceUrl')) { throw 'build.json must link the matching release source in sourceUrl.' }
$sourceRevision = MetadataValue 'sourceRevision'
if ($sourceRevision -notmatch '^[a-fA-F0-9]{40}$') { throw 'build.json must record a full sourceRevision.' }
$engineHash = (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory 'engine/forge-engine.jar') -Algorithm SHA256).Hash
if ($engineHash -ne (MetadataValue 'engineSha256')) { throw 'Bundled engine does not match build.json engineSha256.' }
if (-not $PortableArchive) { $PortableArchive = $InputPackageDirectory + '-windows-x64.zip' }
$PortableArchive = [IO.Path]::GetFullPath($PortableArchive)
if (-not (Test-Path -LiteralPath $PortableArchive -PathType Leaf)) { throw "Portable archive not found: $PortableArchive" }
$archiveHash = (Get-FileHash -LiteralPath $PortableArchive -Algorithm SHA256).Hash
if (-not (Test-Path -LiteralPath ($PortableArchive + '.sha256') -PathType Leaf)) { throw 'Portable archive is missing its SHA256 checksum.' }
$recordedArchiveHash = ((Get-Content -LiteralPath ($PortableArchive + '.sha256') -Raw).Trim() -split '\s+')[0]
if ($recordedArchiveHash -notmatch '^[a-fA-F0-9]{64}$' -or $recordedArchiveHash -ne $archiveHash) { throw 'Portable archive checksum does not match.' }
if ((MetadataValue 'sourceArchiveSha256') -notmatch '^[a-fA-F0-9]{64}$') { throw 'build.json must record sourceArchiveSha256.' }
if (-not $SourceArchive) {
    $sourceArchiveName = MetadataValue 'sourceArchive'
    if (-not $sourceArchiveName -or [IO.Path]::GetFileName($sourceArchiveName) -ne $sourceArchiveName) { throw 'build.json sourceArchive must be a filename.' }
    $SourceArchive = Join-Path (Split-Path -Parent $InputPackageDirectory) $sourceArchiveName
}
$SourceArchive = [IO.Path]::GetFullPath($SourceArchive)
if (-not (Test-Path -LiteralPath $SourceArchive -PathType Leaf)) { throw "Source archive not found: $SourceArchive" }
$sourceArchiveHash = (Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash
if ($sourceArchiveHash -ne (MetadataValue 'sourceArchiveSha256')) { throw 'Source archive does not match build.json sourceArchiveSha256.' }

$managedSetupHash = $null
$managedPackageId = if ($SmokeTestAppId) { $SmokeTestAppId } else { 'ManaTable' }
if ($ManagedSetup) {
    $ManagedSetup = [IO.Path]::GetFullPath($ManagedSetup)
    if (-not (Test-Path -LiteralPath $ManagedSetup -PathType Leaf)) { throw 'Genuine managed Setup was not found.' }
    $managedDirectory = Split-Path -Parent $ManagedSetup
    $managedReport = Get-Content -LiteralPath (Join-Path $managedDirectory 'update-bundle-report.json') -Raw | ConvertFrom-Json
    $managedManifest = Get-Content -LiteralPath (Join-Path $managedDirectory 'update-manifest.json') -Raw | ConvertFrom-Json
    $managedSetupHash = (Get-FileHash -LiteralPath $ManagedSetup -Algorithm SHA256).Hash
    if ($managedReport.version -cne $Version -or $managedReport.sourceRevision -cne $sourceRevision -or
        $managedReport.packageId -cne $managedPackageId -or $managedReport.setupFileName -cne [IO.Path]::GetFileName($ManagedSetup) -or
        $managedReport.setupBytes -ne (Get-Item -LiteralPath $ManagedSetup).Length -or $managedReport.setupSha256 -ine $managedSetupHash -or
        $managedReport.inputBuildMetadataSha256 -ine (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory 'build.json') -Algorithm SHA256).Hash -or
        $managedManifest.updateProtocol -ne 2 -or $managedManifest.framework -cne 'velopack' -or
        $managedManifest.version -cne $Version -or $managedManifest.sourceRevision -cne $sourceRevision -or
        $managedManifest.packageId -cne $managedPackageId) { throw 'Managed Setup and update packages must come from this exact native package and source.' }
} elseif ($PSCmdlet.ParameterSetName -eq 'Release') {
    throw 'Production installers must wrap the verified genuine Velopack Setup. Pass -ManagedSetup from build-update-bundles.ps1.'
}

# Ensure the portable download and installer contain the same build, rather than
# accepting an old ZIP with a valid checksum next to a newer unpacked package.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($PortableArchive)
try {
    $prefixes = @('', ([IO.Path]::GetFileName($InputPackageDirectory) + '/'))
    foreach ($relative in @('build.json', 'ManaTable.exe', 'ManaTable.dll', 'engine/forge-engine.jar', 'engine/runtime/bin/java.exe')) {
        $matches = @($archive.Entries | Where-Object {
            $entryName = $_.FullName.Replace('\', '/')
            $prefixes[0] + $relative -eq $entryName -or $prefixes[1] + $relative -eq $entryName
        })
        if ($matches.Count -ne 1) { throw "Portable archive must contain exactly one $relative." }
        $stream = $matches[0].Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $zipHash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '') }
        finally { $hasher.Dispose(); $stream.Dispose() }
        if ($zipHash -ne (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory $relative) -Algorithm SHA256).Hash) {
            throw "Portable archive differs from package: $relative"
        }
    }
} finally { $archive.Dispose() }

if (-not $CompilerPath) {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
    if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe' }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Install Inno Setup 6 or pass -CompilerPath pointing to ISCC.exe.' }
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
# Official ISCC builds can have a 0.0.0.0 file version. Probe the compiler's
# banner, then let its preprocessor report and enforce the precise version.
$probeInfo = New-Object Diagnostics.ProcessStartInfo
$probeInfo.FileName = $CompilerPath
$probeInfo.Arguments = '/?'
$probeInfo.UseShellExecute = $false
$probeInfo.CreateNoWindow = $true
$probeInfo.RedirectStandardOutput = $true
$probeInfo.RedirectStandardError = $true
$probe = New-Object Diagnostics.Process
$probe.StartInfo = $probeInfo
try {
    $null = $probe.Start()
    $probeOutput = $probe.StandardOutput.ReadToEndAsync()
    $probeErrors = $probe.StandardError.ReadToEndAsync()
    if (-not $probe.WaitForExit(10000)) { $probe.Kill(); throw 'Compiler version probe timed out.' }
    $compilerBanner = $probeOutput.GetAwaiter().GetResult() + $probeErrors.GetAwaiter().GetResult()
    if ($compilerBanner -notmatch '(?m)^Inno Setup 6 Command-Line Compiler\s*$') { throw 'Inno Setup 6 ISCC.exe is required.' }
} finally { $probe.Dispose() }
foreach ($path in @($InputPackageDirectory, $OutputDirectory, $SmokeTestInstallDirectory, $SmokeTestManagedDirectory, $ManagedSetup)) {
    if ($path -and $path.Contains('"')) { throw 'Paths cannot contain a double quote.' }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$compilerArguments = @('/Qp', ('/DPackageDirectory=' + $InputPackageDirectory), ('/DPackageVersion=' + $Version),
    ('/DInstallerOutputDirectory=' + $OutputDirectory))
if ($PSCmdlet.ParameterSetName -eq 'SmokeTest') {
    $SmokeTestInstallDirectory = [IO.Path]::GetFullPath($SmokeTestInstallDirectory)
    $compilerArguments += '/DSmokeTestAppId=' + $SmokeTestAppId
    $compilerArguments += '/DSmokeTestInstallDirectory=' + $SmokeTestInstallDirectory
}
if ($ManagedSetup) {
    $compilerArguments += '/DManagedSetupPath=' + $ManagedSetup
    $compilerArguments += '/DManagedSetupSha256=' + $managedSetupHash
    $compilerArguments += '/DManagedPackageId=' + $managedPackageId
    $compilerArguments += '/DPackageSourceRevision=' + $sourceRevision
    if ($PSCmdlet.ParameterSetName -eq 'SmokeTest') {
        if (-not $SmokeTestManagedDirectory) { throw 'A managed smoke bridge requires its own isolated managed directory.' }
        $SmokeTestManagedDirectory = [IO.Path]::GetFullPath($SmokeTestManagedDirectory)
        if ($SmokeTestManagedDirectory.Equals($SmokeTestInstallDirectory, [StringComparison]::OrdinalIgnoreCase) -or
            $SmokeTestManagedDirectory.StartsWith($SmokeTestInstallDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $SmokeTestInstallDirectory.StartsWith($SmokeTestManagedDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The managed installation must be separate from the legacy smoke installation.'
        }
        $compilerArguments += '/DManagedInstallDirectory=' + $SmokeTestManagedDirectory
    }
}
$compilerArguments += Join-Path $PSScriptRoot 'installer/ManaTable.iss'
& $CompilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
$compilerVersion = (Get-Content -LiteralPath (Join-Path $OutputDirectory 'compiler-version.txt') -Raw).Trim()
if ($compilerVersion -notmatch '^6\.') { throw 'Compiler did not report the expected Inno Setup 6 version.' }
$installer = Join-Path $OutputDirectory 'ManaTable-Setup.exe'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Compiler did not produce ManaTable-Setup.exe.' }
$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
($installerHash + '  ManaTable-Setup.exe') | Set-Content -LiteralPath ($installer + '.sha256') -Encoding ASCII

$manifest = [ordered]@{
    version = $Version
    sourceRevision = $sourceRevision
    builtAt = [DateTime]::UtcNow.ToString('o')
    architecture = 'win-x64'
    installer = 'ManaTable-Setup.exe'
    installerProtocol = 1
    installationFramework = $(if ($ManagedSetup) { 'velopack-bridge' } else { 'inno-smoke-fixture' })
    managedPackageId = $(if ($ManagedSetup) { $managedPackageId } else { $null })
    managedSetupSha256 = $managedSetupHash
    installerBytes = (Get-Item -LiteralPath $installer).Length
    installerSha256 = $installerHash
    portableArchive = [IO.Path]::GetFileName($PortableArchive)
    portableArchiveSha256 = $archiveHash
    sourceArchive = [IO.Path]::GetFileName($SourceArchive)
    sourceArchiveSha256 = $sourceArchiveHash
    sourceUrl = MetadataValue 'sourceUrl'
    buildMetadataSha256 = (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory 'build.json') -Algorithm SHA256).Hash
    clientExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $InputPackageDirectory 'ManaTable.exe') -Algorithm SHA256).Hash
    engineSha256 = $engineHash
    compilerVersion = $compilerVersion
    compilerSha256 = (Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash
    smokeOnly = ($PSCmdlet.ParameterSetName -eq 'SmokeTest')
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'installer-manifest.json') -Encoding UTF8
Write-Output $installer
