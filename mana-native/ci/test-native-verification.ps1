[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $TestDirectory) { $TestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mana-native-verification-' + [Guid]::NewGuid().ToString('N')) }
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a fresh native verification fixture directory.' }
New-Item -ItemType Directory -Path (Join-Path $TestDirectory 'mana-native/ci') -Force | Out-Null

# Execute the shipping workflow blocks, with command doubles at their external
# boundaries. No copied dispatcher, harness build, application or JVM is used.
$workflow = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../.github/workflows/mana-table-native-release.yml')).Replace("`r`n", "`n")
$job = [regex]::Match($workflow, '(?ms)^  native-verification:\n(?<job>.*?)(?=^  [a-z][a-z0-9-]*:\n|\z)')
if (-not $job.Success) { throw 'Native verification job is missing.' }
function Read-WorkflowBlock([string]$StepName) {
    $steps = @([regex]::Split($job.Groups['job'].Value, '(?m)^      - ') | Where-Object { $_.StartsWith("name: $StepName`n") })
    if ($steps.Count -ne 1) { throw "Expected exactly one shipping step: $StepName" }
    $body = [regex]::Match($steps[0], '(?m)^        run: \|\n(?<body>(?:^          [^\n]*\n?)+)')
    if (-not $body.Success) { throw "Shipping step has no executable block: $StepName" }
    return [scriptblock]::Create(($body.Groups['body'].Value -replace '(?m)^          ', ''))
}
$restoreBlock = Read-WorkflowBlock 'Verify the package and suite-specific harness and engine identity'
$runBlock = Read-WorkflowBlock 'Run the selected unchanged native verification suite'
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $TestDirectory 'mana-native/ci/restore-package.ps1'), @'
param([string]$Directory, [switch]$RequireHarness, [switch]$RequireEngineLauncher)
$fixture.Restores.Add(@{ Directory=$Directory; Harness=[bool]$RequireHarness; Launcher=[bool]$RequireEngineLauncher })
'@, $utf8)
$fixture = @{ Restores=(New-Object 'Collections.Generic.List[object]'); Calls=(New-Object 'Collections.Generic.List[object]'); FailAt=0 }
function dotnet {
    $fixture.Calls.Add([string[]]$args)
    $script:LASTEXITCODE = if ($fixture.Calls.Count -eq $fixture.FailAt) { 9 } else { 0 }
}
function Reset-Fixture([string]$Suite, [int]$FailAt = 0) {
    $fixture.Restores.Clear()
    $fixture.Calls.Clear()
    $fixture.FailAt = $FailAt
    $script:LASTEXITCODE = 0
    $env:MANA_VERIFICATION_SUITE = $Suite
}
function Assert-Arguments($Actual, [string[]]$Expected) {
    if ($Actual.Count -ne $Expected.Count -or (Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual -SyncWindow 0 -CaseSensitive)) {
        throw ('Native verification arguments changed: ' + ($Actual -join ' '))
    }
}
$saved = @{ Suite=$env:MANA_VERIFICATION_SUITE; SHA=$env:GITHUB_SHA; Package=$env:MANA_NATIVE_PACKAGE; Launcher=$env:MANA_NATIVE_FIXTURE_LAUNCHER }
$checks = 0
Push-Location -LiteralPath $TestDirectory
try {
    $env:GITHUB_SHA = 'a' * 40
    $env:MANA_NATIVE_PACKAGE = Join-Path $TestDirectory 'verified-package'
    $env:MANA_NATIVE_FIXTURE_LAUNCHER = Join-Path $TestDirectory 'verified-fixture-launcher.jar'
    foreach ($suite in @('presentation', 'engine')) {
        Reset-Fixture $suite
        & $restoreBlock
        if ($fixture.Restores.Count -ne 1) { throw "$suite must restore exactly once." }
        $call = $fixture.Restores[0]
        if ($call.Directory -cne 'dist/ci-inputs' -or -not $call.Harness -or $call.Launcher -ne ($suite -ceq 'engine')) { throw "$suite requested the wrong restore capabilities." }
        $checks++
    }
    foreach ($suite in @('', 'unknown', 'Presentation', 'ENGINE', 'presentation ')) {
        foreach ($block in @($restoreBlock, $runBlock)) {
            Reset-Fixture $suite
            $rejected = $false
            try { & $block | Out-Null } catch { $rejected = $_.Exception.Message.StartsWith('Unknown native verification suite:') }
            if (-not $rejected -or $fixture.Restores.Count -ne 0 -or $fixture.Calls.Count -ne 0) { throw "Invalid suite '$suite' ran commands or failed without the expected rejection." }
            $checks++
        }
    }
    $modes = @('--smoke-presentation', '--smoke-headless-presentation', '--smoke-headless-clutter', '--smoke-headless-priority-flow')
    Reset-Fixture 'presentation'
    $timings = @(& $runBlock | ConvertFrom-Json)
    if ($fixture.Calls.Count -ne 4 -or $timings.Count -ne 4) { throw 'Presentation must run all four modes exactly once and retain their timings.' }
    for ($index = 0; $index -lt $modes.Count; $index++) {
        Assert-Arguments $fixture.Calls[$index] @('./dist/ci-inputs/harness/Mana.Table.Tests.dll', $modes[$index], '--profile', ('dist/' + $modes[$index].Substring(8) + '-checks'))
        if ($timings[$index].phase -cne $modes[$index] -or $timings[$index].seconds -lt 0) { throw 'Each mode must retain its measured phase.' }
    }
    $checks++
    Reset-Fixture 'engine'
    & $runBlock | Out-Null
    if ($fixture.Calls.Count -ne 1) { throw 'The complete engine journey must run exactly once.' }
    Assert-Arguments $fixture.Calls[0] @('./dist/ci-inputs/harness/Mana.Table.Tests.dll', '--smoke-headless-engine', '--engine-jar', $env:MANA_NATIVE_FIXTURE_LAUNCHER,
        '--production-engine-jar', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine/forge-engine.jar'), '--engine-resources', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine'),
        '--source-revision', $env:GITHUB_SHA, '--source-manifest', 'dist/ci-inputs/ci-engine-manifest.json', '--profile', 'dist/headless-engine-checks')
    $checks++
    foreach ($suite in @('presentation', 'engine')) {
        $count = if ($suite -ceq 'presentation') { 4 } else { 1 }
        for ($failAt = 1; $failAt -le $count; $failAt++) {
            Reset-Fixture $suite $failAt
            $rejected = $false
            try { & $runBlock | Out-Null } catch { $rejected = $_.Exception.Message -cmatch '^(Native check failed:|Production native adapter and packaged Forge command checks failed\.)' }
            if (-not $rejected -or $fixture.Calls.Count -ne $failAt) { throw "$suite must stop at failed command $failAt." }
            $checks++
        }
    }
    Write-Output "$checks shipping native verification dispatch checks passed. No harness, UI, JVM or network was started."
} finally {
    Pop-Location
    $env:MANA_VERIFICATION_SUITE = $saved.Suite
    $env:GITHUB_SHA = $saved.SHA
    $env:MANA_NATIVE_PACKAGE = $saved.Package
    $env:MANA_NATIVE_FIXTURE_LAUNCHER = $saved.Launcher
}
