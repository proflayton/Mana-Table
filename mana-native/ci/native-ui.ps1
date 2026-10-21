[CmdletBinding()]
param([ValidateRange(1,12)][int]$Shard = 1, [string]$Directory, [string]$ProfileDirectory, [switch]$ListOnly, [switch]$Extended)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$suites = @(

  @{ Name='startup'; Mode='--smoke-engine-startup'; Result='engine-startup-regression.json' },
  @{ Name='adapter'; Mode='--smoke-workshop-adapter'; Result='workshop-adapter-results.json' },
  @{ Name='printings-1000'; Mode='--smoke-printings'; Result='printings-ui-results.json'; Size='1000x740' },
  @{ Name='printings-1540'; Mode='--smoke-printings'; Result='printings-ui-results.json'; Size='1540x980' },
  @{ Name='clutter-1000'; Mode='--smoke-cluttered-changes'; Result='cluttered-changes-results.json'; Size='1000x740' },
  @{ Name='clutter-1540'; Mode='--smoke-cluttered-changes'; Result='cluttered-changes-results.json'; Size='1540x980' },
  @{ Name='onboarding'; Mode='--smoke-onboarding'; Result='onboarding-results.json' },
  @{ Name='loading'; Mode='--smoke-loading'; Result='loading-results.json' },
  @{ Name='recovery-1000'; Mode='--smoke-recovery'; Result='recovery-results.json'; Size='1000x740' },
  @{ Name='recovery-1540'; Mode='--smoke-recovery'; Result='recovery-results.json'; Size='1540x980' },
  @{ Name='state-clarity-1000'; Mode='--smoke-state-clarity'; Result='state-clarity-results.json'; Size='1000x740' },
  @{ Name='state-clarity-1540'; Mode='--smoke-state-clarity'; Result='state-clarity-results.json'; Size='1540x980' },
  @{ Name='deck-experience-1000'; Mode='--smoke-deck-experience'; Result='deck-experience-results.json'; Size='1000x740' },
  @{ Name='deck-experience-1540'; Mode='--smoke-deck-experience'; Result='deck-experience-results.json'; Size='1540x980' },
  @{ Name='priority-1280'; Mode='--smoke-priority-abilities'; Result='priority-ability-results.json' },
  @{ Name='priority-1200'; Mode='--smoke-priority-abilities'; Result='priority-ability-results.json'; Size='1200x900' },
  @{ Name='hand-placement-1000'; Mode='--smoke-hand-placement'; Result='hand-placement-results.json'; Size='1000x740' },
  @{ Name='hand-placement-1540'; Mode='--smoke-hand-placement'; Result='hand-placement-results.json'; Size='1540x980' },
  @{ Name='allocation-1000'; Mode='--smoke-allocation'; Result='allocation-ui-results.json'; Size='1000x740' },
  @{ Name='allocation-1540'; Mode='--smoke-allocation'; Result='allocation-ui-results.json'; Size='1540x980' },
  @{ Name='workshop'; Mode='--smoke-workshop'; Result='workshop-ui-results.json' },
  @{ Name='tuning'; Mode='--smoke-deck-tuning'; Result='deck-tuning-ui-results.json'; Size='1280x900' },
  @{ Name='analysis-adapter'; Mode='--smoke-analysis-adapter'; Result='analysis-adapter-results.json' },
  @{ Name='analysis-1000'; Mode='--smoke-analysis'; Result='analysis-ui-results.json'; Size='1000x740' },
  @{ Name='analysis-1540'; Mode='--smoke-analysis'; Result='analysis-ui-results.json'; Size='1540x980' },
  @{ Name='updates-1000'; Mode='--smoke-updates'; Result='update-ui-results.json'; Size='1000x740' },
  @{ Name='updates-1540'; Mode='--smoke-updates'; Result='update-ui-results.json'; Size='1540x980' },
  @{ Name='commander-tax-1000'; Mode='--smoke-commander-tax'; Result='commander-tax-results.json'; Size='1000x740' },
  @{ Name='commander-tax-1540'; Mode='--smoke-commander-tax'; Result='commander-tax-results.json'; Size='1540x980' },
  @{ Name='equipment-1000'; Mode='--smoke-equipment'; Result='equipment-results.json'; Size='1000x740' },
  @{ Name='equipment-1540'; Mode='--smoke-equipment'; Result='equipment-results.json'; Size='1540x980' },
  @{ Name='card-reading-1000'; Mode='--smoke-card-reading'; Result='reading-results.json'; Size='1000x740' },
  @{ Name='card-reading-1540'; Mode='--smoke-card-reading'; Result='reading-results.json'; Size='1540x980' },
  @{ Name='card-states-1000'; Mode='--smoke-card-states'; Result='card-states-results.json'; Size='1000x740' },
  @{ Name='card-states-1540'; Mode='--smoke-card-states'; Result='card-states-results.json'; Size='1540x980' },
  @{ Name='compact-selection-1000'; Mode='--smoke-compact-selection'; Result='compact-selection-results.json'; Size='1000x740' },
  @{ Name='compact-selection-1540'; Mode='--smoke-compact-selection'; Result='compact-selection-results.json'; Size='1540x980' },
  @{ Name='full-battlefield-1000'; Mode='--smoke-full-battlefield'; Result='full-battlefield-results.json'; Size='1000x740' },
  @{ Name='full-battlefield-1540'; Mode='--smoke-full-battlefield'; Result='full-battlefield-results.json'; Size='1540x980' },
  @{ Name='import'; Mode='--smoke-deck-import'; Result='deck-import-results.json' },
  @{ Name='building'; Mode='--smoke-deck-building'; Result='deck-building-results.json' },
  @{ Name='lobby'; Mode='--smoke-lobby'; Result='lobby-results.json' },
  @{ Name='lobby-ai-1000'; Mode='--smoke-lobby-ai-seats'; Result='lobby-ai-seat-results.json'; Size='1000x740' },
  @{ Name='lobby-ai-1540'; Mode='--smoke-lobby-ai-seats'; Result='lobby-ai-seat-results.json'; Size='1540x980' },
  @{ Name='lobby-three-1000'; Mode='--smoke-lobby-three-player'; Result='lobby-three-player-results.json'; Size='1000x740' },
  @{ Name='lobby-three-1540'; Mode='--smoke-lobby-three-player'; Result='lobby-three-player-results.json'; Size='1540x980' }
)
# GPU/integration suites only; package already runs the pure presentation checks.
$partitions = @(
    @('lobby-ai-1000','card-states-1000','equipment-1000','clutter-1000'),
    @('lobby-ai-1540','onboarding','card-states-1540','equipment-1540','clutter-1540'),
    @('lobby-three-1000','loading','card-reading-1000','compact-selection-1000'),
    @('lobby-three-1540','startup','card-reading-1540','compact-selection-1540'),
    @('adapter','priority-1280','recovery-1000','commander-tax-1000'),
    @('workshop','priority-1200','recovery-1540','commander-tax-1540'),
    @('state-clarity-1000','analysis-1000','deck-experience-1000','updates-1000','printings-1000'),
    @('state-clarity-1540','analysis-1540','deck-experience-1540','updates-1540','printings-1540'),
    @('hand-placement-1000','allocation-1000','tuning'),
    @('hand-placement-1540','allocation-1540','analysis-adapter'),
    @('full-battlefield-1000','import'),
    @('full-battlefield-1540','building','lobby')
)
$allNames = @($suites | ForEach-Object { $_.Name })
$assigned = @($partitions | ForEach-Object { $_ })
if ($suites.Count -ne 46 -or @($allNames | Sort-Object -Unique).Count -ne 46 -or $assigned.Count -ne 46 -or @($assigned | Sort-Object -Unique).Count -ne 46 -or @(Compare-Object $allNames $assigned).Count -ne 0) { throw 'Native UI partitions must cover all 46 suite/size cases exactly once.' }
if (-not $Extended) {
    # Fast release gate; the complete inventory remains available on demand.
    $partitions = @(
        @('startup','loading','analysis-1000','clutter-1000'),
        @('deck-experience-1000','equipment-1000','card-states-1000','printings-1000'),
        @('lobby-ai-1000'),
        @('lobby-three-1000')
    )
}
if ($Shard -gt $partitions.Count) { throw 'The requested shard is not part of this native UI inventory.' }
if ($ListOnly) {
    $inventory = for ($index=0; $index -lt $partitions.Count; $index++) {
        foreach ($name in $partitions[$index]) {
            $suite = @($suites | Where-Object { $_.Name -ceq $name })[0]
            [pscustomobject]@{ shard=$index+1; name=$name; mode=$suite.Mode; size=$(if ($suite.ContainsKey('Size')) { $suite.Size } else { '1280x720' }); result=$suite.Result }
        }
    }
    $inventory | ConvertTo-Json
    return
}
if (-not $Directory -or -not $ProfileDirectory) { throw 'Directory and ProfileDirectory are required to run native UI checks.' }
$Directory = [IO.Path]::GetFullPath($Directory)
$test = Join-Path $Directory 'harness/Mana.Table.Tests.exe'
$engine = Join-Path $env:MANA_NATIVE_PACKAGE 'engine'
New-Item -ItemType Directory -Path $ProfileDirectory -Force | Out-Null
$summary = @()
foreach ($name in $partitions[$Shard-1]) {
    $suite = @($suites | Where-Object { $_.Name -ceq $name })[0]
    $profile = Join-Path $ProfileDirectory $suite.Name
    if (Test-Path -LiteralPath $profile) { throw "Native suite must use a fresh profile: $($suite.Name)" }
    New-Item -ItemType Directory -Path $profile | Out-Null
    $size = if ($suite.ContainsKey('Size')) { $suite.Size } else { '1280x720' }
    $arguments = @($suite.Mode,'--engine-resources',('"' + $engine + '"'),'--profile',('"' + $profile + '"'),'--size',$size)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $test -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(600000)) { $process.Kill($true); throw "Native suite timed out: $($suite.Name)" }
    if ($process.ExitCode -ne 0) { throw "Native suite failed: $($suite.Name). See its native-error.log." }
    $result = Join-Path $profile $suite.Result
    if (-not (Test-Path -LiteralPath $result)) { throw "Missing native suite result: $($suite.Name)" }
    [object[]]$checks = [IO.File]::ReadAllText($result) | ConvertFrom-Json
    if ($checks.Count -eq 0) { throw "Native suite performed no checks: $($suite.Name)" }
    if (@($checks | Where-Object { $_.PSObject.Properties['passed'] -and $_.passed -eq $false }).Count) { throw "Native suite reported a failed check: $($suite.Name)" }
    $summary += @{ suite=$suite.Name; checks=$checks.Count; seconds=[math]::Round($timer.Elapsed.TotalSeconds,1) }
    Write-Output "$($suite.Name): $($checks.Count) checks passed in $([math]::Round($timer.Elapsed.TotalSeconds,1))s"
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ProfileDirectory 'suite-results.json') -Encoding utf8
