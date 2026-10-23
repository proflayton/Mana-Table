[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $TestDirectory) { $TestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mana-tools-' + [Guid]::NewGuid().ToString('N')) }
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a fresh pinned tools fixture directory.' }
New-Item -ItemType Directory -Path $TestDirectory | Out-Null
$toolsScript = Join-Path $PSScriptRoot 'tools.ps1'
$innoName = 'innosetup-6.7.3.exe'
$mesaName = 'mesa-llvmpipe-x64-26.2.5.7z'
$pinned = @{
    $innoName = @{ Url='https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'; Hash='9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732' }
    $mesaName = @{ Url='https://github.com/mmozeiko/build-mesa/releases/download/26.2.5/mesa-llvmpipe-x64-26.2.5.7z'; Hash='d1b713ca2a7fba47a50c058099f13c435ddb76a16a422c334e4d787f9755a31d' }
}
$utf8 = New-Object Text.UTF8Encoding($false)
$savedEnvironment = $env:GITHUB_ENV
$checks = 0
$fixture = @{}

# Script-local command doubles exercise the production entry point without
# networking, hashing executable fixtures, extracting archives or starting code.
function Invoke-WebRequest {
    param([string]$Uri, [string]$OutFile)
    $name = [IO.Path]::GetFileName($OutFile)
    if (-not $pinned.ContainsKey($name) -or $Uri -cne $pinned[$name].Url) { throw 'Unexpected pinned tool download.' }
    $fixture.downloads.Add($name)
    if ($fixture.mutation -ne 'missing') { [IO.File]::WriteAllText($OutFile, $(if ($fixture.mutation -eq 'corrupt') { 'corrupt' } else { $name }), $utf8) }
}
function Get-FileHash {
    param([string]$LiteralPath, [string]$Algorithm)
    $name = [IO.Path]::GetFileName($LiteralPath)
    if (-not $pinned.ContainsKey($name) -or $Algorithm -cne 'SHA256') { throw 'Unexpected pinned tool hash check.' }
    $fixture.verified.Add($name)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { throw 'Selected tool archive is missing.' }
    $hash = if ([IO.File]::ReadAllText($LiteralPath) -ceq $name) { $pinned[$name].Hash } else { '0' * 64 }
    return [pscustomobject]@{ Hash=$hash }
}
function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    if ([IO.Path]::GetFileName($LiteralPath) -cne $innoName) { throw 'Unexpected tool publisher check.' }
    $fixture.signatures++
    return [pscustomobject]@{
        Status=$(if ($fixture.mutation -eq 'signature') { 'HashMismatch' } else { 'Valid' })
        SignerCertificate=[pscustomobject]@{ Subject=$(if ($fixture.mutation -eq 'publisher') { 'CN=Other publisher' } else { 'CN=Pyrsys B.V.' }) }
    }
}
function Start-Process {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WindowStyle, [switch]$Wait, [switch]$PassThru)
    if ([IO.Path]::GetFileName($FilePath) -cne $innoName -or $WindowStyle -cne 'Hidden' -or -not $Wait -or -not $PassThru -or
        $ArgumentList -cnotcontains '/PORTABLE=1' -or $ArgumentList -cnotcontains '/VERYSILENT') { throw 'Unexpected installer execution contract.' }
    $fixture.installations++
    return [pscustomobject]@{ ExitCode=0 }
}
function 7z {
    if ($args.Count -ne 4 -or $args[0] -cne 'x' -or [IO.Path]::GetFileName($args[1]) -cne $mesaName -or
        -not $args[2].StartsWith('-o') -or $args[3] -cne '-y') { throw 'Unexpected graphics extraction contract.' }
    $fixture.extractions++
    $destination = $args[2].Substring(2)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $destination 'opengl32.dll'), 'Never executable fixture bytes.', $utf8)
    Set-Variable -Name LASTEXITCODE -Value 0 -Scope 1
}
function New-Fixture([string]$Name, [string[]]$Existing, [string]$Mutation = '') {
    $directory = Join-Path $TestDirectory $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $fixture.downloads = New-Object 'System.Collections.Generic.List[string]'
    $fixture.verified = New-Object 'System.Collections.Generic.List[string]'
    $fixture.signatures = 0; $fixture.installations = 0; $fixture.extractions = 0
    $fixture.mutation = $Mutation
    $env:GITHUB_ENV = Join-Path $directory 'github-env.txt'
    foreach ($name in $Existing) { [IO.File]::WriteAllText((Join-Path $directory $name), $(if ($Mutation -eq 'corrupt') { 'corrupt' } else { $name }), $utf8) }
    return $directory
}
function Assert-Names($Actual, [string[]]$Expected, [string]$Label) {
    if (($Actual -join '|') -cne ($Expected -join '|')) { throw "Unexpected $Label tool selection: $($Actual -join ', ')" }
}
try {
    foreach ($case in @(
        @{ Name='download-default'; Options=@{DownloadOnly=$true}; Expected=@($innoName,$mesaName) },
        @{ Name='download-graphics'; Options=@{DownloadOnly=$true;Graphics=$true}; Expected=@($mesaName) },
        @{ Name='download-installer'; Options=@{DownloadOnly=$true;Installer=$true}; Expected=@($innoName) },
        @{ Name='download-both'; Options=@{DownloadOnly=$true;Graphics=$true;Installer=$true}; Expected=@($innoName,$mesaName) }
    )) {
        $directory = New-Fixture $case.Name @()
        $options = $case.Options
        & $toolsScript -Directory $directory @options
        Assert-Names $fixture.downloads $case.Expected 'downloaded'
        Assert-Names $fixture.verified $case.Expected 'verified'
        if ($fixture.signatures -or $fixture.installations -or $fixture.extractions) { throw 'Download-only mode executed or extracted a tool.' }
        $checks++
    }
    foreach ($case in @(
        @{ Name='graphics-only'; Options=@{Graphics=$true}; Expected=@($mesaName); Installer=0; Graphics=1 },
        @{ Name='installer-only'; Options=@{Installer=$true}; Expected=@($innoName); Installer=1; Graphics=0 },
        @{ Name='both-existing'; Options=@{Graphics=$true;Installer=$true}; Expected=@($innoName,$mesaName); Installer=1; Graphics=1 }
    )) {
        $directory = New-Fixture $case.Name $case.Expected
        $options = $case.Options
        & $toolsScript -Directory $directory @options
        Assert-Names $fixture.downloads @() 'downloaded'
        Assert-Names $fixture.verified $case.Expected 'verified'
        if ($fixture.signatures -ne $case.Installer -or $fixture.installations -ne $case.Installer -or $fixture.extractions -ne $case.Graphics) { throw 'Selected tool was not installed/extracted exactly once.' }
        $environment = [IO.File]::ReadAllText($env:GITHUB_ENV)
        if (($case.Installer -and -not $environment.Contains('MANA_ISCC=')) -or ($case.Graphics -and -not $environment.Contains('SDL_OPENGL_LIBRARY='))) { throw 'Selected tool environment was not exported.' }
        $checks++
    }
    foreach ($download in @($false,$true)) {
        foreach ($selector in @('Graphics','Installer')) {
            foreach ($bad in @('missing','corrupt')) {
                $name = if ($selector -eq 'Graphics') { $mesaName } else { $innoName }
                $directory = New-Fixture ("$selector-$download-$bad") $(if ($bad -eq 'corrupt') { @($name) } else { @() }) $bad
                $options = @{DownloadOnly=$download}; $options[$selector]=$true
                $message = if ($bad -eq 'missing') { 'Selected tool archive is missing.' } else { 'Pinned CI tool checksum mismatch:' }
                $rejected = $false
                try { & $toolsScript -Directory $directory @options }
                catch { $rejected = $_.Exception.Message.Contains($message) }
                if (-not $rejected -or $fixture.signatures -or $fixture.installations -or $fixture.extractions) { throw 'Missing/corrupt selected tool was not rejected before execution.' }
                Assert-Names $fixture.verified @($name) 'verified'
                Assert-Names $fixture.downloads $(if ($download) { @($name) } else { @() }) 'downloaded'
                $checks++
            }
        }
    }
    foreach ($bad in @('signature','publisher')) {
        $directory = New-Fixture $bad @($innoName) $bad
        $rejected = $false
        try { & $toolsScript -Directory $directory -Installer }
        catch { $rejected = $_.Exception.Message.Contains('Unexpected Inno Setup publisher.') }
        if (-not $rejected -or $fixture.signatures -ne 1 -or $fixture.installations -or $fixture.extractions) { throw 'An untrusted installer publisher was accepted.' }
        $checks++
    }
    Write-Output "$checks pinned tool selector/integrity fixture checks passed. No network, downloaded code or external process was used."
} finally { $env:GITHUB_ENV = $savedEnvironment }
