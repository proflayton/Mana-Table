[CmdletBinding()]
param(
    [string]$Scenario = 'crowded-counters.v1',
    [string]$ApplicationPath,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $ApplicationPath) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $PSScriptRoot 'Mana.Table/Mana.Table.csproj') --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'The current experience lab did not build.' }
    }
    $ApplicationPath = Join-Path $PSScriptRoot 'Mana.Table/bin/Debug/net9.0-windows/ManaTable.exe'
}
$ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) { throw 'Build the native app or pass -ApplicationPath pointing to the compiled candidate.' }
if ($Scenario -notmatch '^[a-z0-9.-]+$') { throw 'Use a named experience scenario ID.' }
Start-Process -FilePath $ApplicationPath -ArgumentList @('--experience-lab', $Scenario) -WorkingDirectory (Split-Path -Parent $ApplicationPath) -WindowStyle Normal
