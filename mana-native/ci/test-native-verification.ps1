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
function Read-WorkflowBlock([string]$StepId, [string]$JobText = $job.Groups['job'].Value) {
    $idPattern = '(?m)^        id: ' + [regex]::Escape($StepId) + '$'
    if ([regex]::Matches($JobText, $idPattern).Count -ne 1) { throw "Expected exactly one shipping step ID: $StepId" }
    $steps = @([regex]::Split($JobText, '(?m)^      - ') | Where-Object { [regex]::IsMatch($_, $idPattern) })
    if ($steps.Count -ne 1) { throw "Expected exactly one shipping step ID: $StepId" }
    $body = [regex]::Match($steps[0], '(?m)^        run: \|\n(?<body>(?:^          [^\n]*\n?)+)')
    if (-not $body.Success) { throw "Shipping step has no executable block: $StepId" }
    return [scriptblock]::Create(($body.Groups['body'].Value -replace '(?m)^          ', ''))
}
$restoreBlock = Read-WorkflowBlock 'verify_native_inputs'
$runBlock = Read-WorkflowBlock 'run_native_suite'
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $TestDirectory 'mana-native/ci/restore-package.ps1'), @'
param([string]$Directory, [switch]$RequireHarness, [switch]$RequireEngineLauncher)
$fixture.Restores.Add(@{ Directory=$Directory; Harness=[bool]$RequireHarness; Launcher=[bool]$RequireEngineLauncher })
'@, $utf8)
$fixture = @{ Restores=(New-Object 'Collections.Generic.List[object]'); Calls=(New-Object 'Collections.Generic.List[object]'); FailAt=0; Prefix='valid'; Campaign='valid' }
$prefixPath = Join-Path $TestDirectory 'dist/mixed-lobby-checks/mixed-lobby/prefix-results.json'
$campaignDirectory = Join-Path $TestDirectory 'dist/polled-full-game-checks/mixed-game-campaign'
$campaignPaths = [ordered]@{ manifest=(Join-Path $campaignDirectory 'manifest.json'); result=(Join-Path $campaignDirectory 'match-0/result.json'); owners=(Join-Path $campaignDirectory 'match-0/private-owner-teardown.json') }
function Write-CampaignEvidence {
    $manifest = [ordered]@{ formatVersion=1; profile='smoke'; mode='PolledBackground' }
    $result = [ordered]@{ DriveMode='PolledBackground'; Failure=$null; StopReason='turn-budget';
        Qualification=[ordered]@{ Status='Inconclusive' }; Evidence=[ordered]@{ DiagnosticErrors=@() };
        PublicComparisons=12; FourAudienceCohorts=3; DevelopedSeats=@(0,1,2,3);
        Participants=@([ordered]@{ SlotIndex=0; PlayerId=0; Role='Host'; ProviderAcceptances=3 },
            [ordered]@{ SlotIndex=1; PlayerId=1; Role='Guest'; ProviderAcceptances=4 },
            [ordered]@{ SlotIndex=2; PlayerId=2; Role='Ai'; ProviderAcceptances=5 },
            [ordered]@{ SlotIndex=3; PlayerId=3; Role='Ai'; ProviderAcceptances=6 }) }
    $owners = [ordered]@{ Executing=0; orderedEvidence=[ordered]@{ Pending=0; Retired=$true; MissingOwnedBatch=$false;
        Failure=$null; Enqueued=7; Applied=7; DiscardedAfterFault=0 } }
    switch -CaseSensitive ($fixture.Campaign) {
        'passed' { $result.Qualification.Status='Passed' }
        'time-budget' { $result.StopReason='time-budget' }
        'natural-outcome' { $result.StopReason='natural-outcome' }
        'wrong-profile' { $manifest.profile='natural' }
        'wrong-manifest-mode' { $manifest.mode='ManualStepped' }
        'wrong-result-mode' { $result.DriveMode='ManualStepped' }
        'missing-failure' { $result.Remove('Failure') }
        'failure' { $result.Failure='Retained first failure' }
        'unknown-stop' { $result.StopReason='unknown' }
        'mechanical-stop' { $result.StopReason='mechanical-failure' }
        'failed-status' { $result.Qualification.Status='Failed' }
        'unknown-status' { $result.Qualification.Status='Unknown' }
        'missing-status' { $result.Qualification.Remove('Status') }
        'diagnostic-error' { $result.Evidence.DiagnosticErrors=@('Retained diagnostic error') }
        'no-comparisons' { $result.PublicComparisons=0 }
        'no-cohorts' { $result.FourAudienceCohorts=0 }
        'missing-participant' { $result.Participants=@($result.Participants | Select-Object -First 3) }
        'duplicate-player' { $result.Participants[1].PlayerId=$result.Participants[0].PlayerId }
        'duplicate-slot' { $result.Participants[1].SlotIndex=$result.Participants[0].SlotIndex }
        'wrong-role' { $result.Participants[1].Role='Ai' }
        'unaccepted-role' { $result.Participants[2].ProviderAcceptances=0 }
        'undeveloped-role' { $result.DevelopedSeats=@(0,1,2) }
        'executing-owner' { $owners.Executing=1 }
        'pending-events' { $owners.orderedEvidence.Pending=1 }
        'not-retired' { $owners.orderedEvidence.Retired=$false }
        'string-retired' { $owners.orderedEvidence.Retired='true' }
        'missing-owned-batch' { $owners.orderedEvidence.MissingOwnedBatch=$true }
        'string-missing-batch' { $owners.orderedEvidence.MissingOwnedBatch='false' }
        'inbox-failure' { $owners.orderedEvidence.Failure='Retained overflow' }
        'undrained-events' { $owners.orderedEvidence.Applied=6 }
        'discarded-events' { $owners.orderedEvidence.DiscardedAfterFault=1 }
    }
    $documents = @{ manifest=$manifest; result=$result; owners=$owners }
    foreach ($name in $campaignPaths.Keys) {
        if ($fixture.Campaign -ceq "missing-$name") { continue }
        New-Item -ItemType Directory -Path (Split-Path -Parent $campaignPaths[$name]) -Force | Out-Null
        $text = if ($fixture.Campaign -ceq "empty-$name") { '' }
            elseif ($fixture.Campaign -ceq "oversized-$name") { ' ' * 4194305 }
            elseif ($fixture.Campaign -ceq "malformed-$name") { '{' }
            else { $documents[$name] | ConvertTo-Json -Depth 8 -Compress }
        [IO.File]::WriteAllText($campaignPaths[$name], $text, $utf8)
    }
}
function dotnet {
    $fixture.Calls.Add([string[]]$args)
    $script:LASTEXITCODE = if ($fixture.Calls.Count -eq $fixture.FailAt) { 9 } else { 0 }
    if ($script:LASTEXITCODE -eq 0 -and $args -ccontains '--smoke' -and $fixture.Prefix -cne 'missing') {
        New-Item -ItemType Directory -Path (Split-Path -Parent $prefixPath) -Force | Out-Null
        $prefix = [ordered]@{ formatVersion=1; scenario='real-tcp-two-scripted-humans-two-ai'; evidence='bounded-integration-prefix';
            failure=$null; hostConceded=$true; continued=$true; remoteOwnershipChecked=$true;
            humanUxAcceptance=$false; naturalOutcomeClaimed=$false; deckQualificationClaimed=$false;
            stopReason='observed-host-departure-and-ai-progress' }
        switch -CaseSensitive ($fixture.Prefix) {
            'failure' { $prefix.failure = 'Retained first rules failure' }
            'not-continued' { $prefix.continued = $false }
            'string-continued' { $prefix.continued = 'true' }
            'wrong-scenario' { $prefix.scenario = 'authored-mixed-lobby' }
            'wrong-scope' { $prefix.evidence = 'natural-completion' }
            'ux-claim' { $prefix.humanUxAcceptance = $true }
            'outcome-claim' { $prefix.naturalOutcomeClaimed = $true }
            'qualification-claim' { $prefix.deckQualificationClaimed = $true }
            'missing-failure' { $prefix.Remove('failure') }
        }
        $text = switch -CaseSensitive ($fixture.Prefix) {
            'empty' { '' }
            'oversized' { ' ' * 1048577 }
            'malformed' { '{' }
            default { $prefix | ConvertTo-Json -Compress }
        }
        [IO.File]::WriteAllText($prefixPath, $text, $utf8)
    }
    if ($script:LASTEXITCODE -eq 0 -and $args -ccontains '--qualify-mixed-game') { Write-CampaignEvidence }
}
function Reset-Fixture([string]$Suite, [int]$FailAt = 0) {
    $fixture.Restores.Clear()
    $fixture.Calls.Clear()
    $fixture.FailAt = $FailAt
    $fixture.Prefix = 'valid'
    $fixture.Campaign = 'valid'
    if (Test-Path -LiteralPath $prefixPath) { Remove-Item -LiteralPath $prefixPath }
    foreach ($path in $campaignPaths.Values) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
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
    foreach ($id in @('verify_native_inputs','run_native_suite')) {
        $renamed = $job.Groups['job'].Value -replace '(?m)^(      - )?name: [^\n]+$', '${1}name: A renamed display label'
        if ((Read-WorkflowBlock $id $renamed).ToString() -cne (Read-WorkflowBlock $id).ToString()) { throw "Display label changed the selected block: $id" }
        $checks++
        foreach ($variant in @('missing','duplicate')) {
            $line = "        id: $id"
            $changed = if ($variant -ceq 'missing') { $job.Groups['job'].Value.Replace($line, "        id: other_$id") }
                else { $job.Groups['job'].Value.Replace($line, "$line`n$line") }
            $rejected = $false
            try { Read-WorkflowBlock $id $changed | Out-Null } catch { $rejected = $_.Exception.Message -ceq "Expected exactly one shipping step ID: $id" }
            if (-not $rejected) { throw "$variant machine ID was accepted: $id" }
            $checks++
        }
    }
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
    if ($fixture.Calls.Count -ne 3) { throw 'The headless engine, four-client mixed-lobby and polled campaign journeys must each run exactly once.' }
    Assert-Arguments $fixture.Calls[0] @('./dist/ci-inputs/harness/Mana.Table.Tests.dll', '--smoke-headless-engine', '--engine-jar', $env:MANA_NATIVE_FIXTURE_LAUNCHER,
        '--production-engine-jar', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine/forge-engine.jar'), '--engine-resources', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine'),
        '--source-revision', $env:GITHUB_SHA, '--source-manifest', 'dist/ci-inputs/ci-engine-manifest.json', '--profile', 'dist/headless-engine-checks')
    Assert-Arguments $fixture.Calls[1] @('./dist/ci-inputs/harness/Mana.Table.Tests.dll', '--smoke', '--engine-jar',
        (Join-Path $env:MANA_NATIVE_PACKAGE 'engine/forge-engine.jar'), '--engine-resources', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine'),
        '--profile', 'dist/mixed-lobby-checks')
    Assert-Arguments $fixture.Calls[2] @('./dist/ci-inputs/harness/Mana.Table.Tests.dll', '--qualify-mixed-game', '--mixed-game-mode', 'polled-background',
        '--campaign-profile', 'smoke', '--engine-jar', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine/forge-engine.jar'),
        '--engine-resources', (Join-Path $env:MANA_NATIVE_PACKAGE 'engine'), '--profile', 'dist/polled-full-game-checks')
    $checks++
    foreach ($variant in @('missing', 'empty', 'oversized', 'malformed', 'failure', 'not-continued', 'string-continued',
        'wrong-scenario', 'wrong-scope', 'ux-claim', 'outcome-claim', 'qualification-claim', 'missing-failure')) {
        Reset-Fixture 'engine'
        $fixture.Prefix = $variant
        $rejected = $false
        try { & $runBlock | Out-Null } catch { $rejected = $true }
        if (-not $rejected -or $fixture.Calls.Count -ne 2) { throw "The mixed-lobby gate accepted invalid prefix evidence: $variant" }
        $checks++
    }
    foreach ($variant in @('passed','time-budget','natural-outcome')) {
        Reset-Fixture 'engine'
        $fixture.Campaign = $variant
        & $runBlock | Out-Null
        if ($fixture.Calls.Count -ne 3) { throw "The campaign gate did not accept valid bounded metadata: $variant" }
        $checks++
    }
    $invalidCampaigns = @('wrong-profile','wrong-manifest-mode','wrong-result-mode','missing-failure','failure','unknown-stop','mechanical-stop',
        'failed-status','unknown-status','missing-status','diagnostic-error','no-comparisons','no-cohorts','missing-participant','duplicate-player',
        'duplicate-slot','wrong-role','unaccepted-role','undeveloped-role','executing-owner','pending-events','not-retired','string-retired',
        'missing-owned-batch','string-missing-batch','inbox-failure','undrained-events','discarded-events')
    foreach ($name in $campaignPaths.Keys) { foreach ($invalid in @('missing','empty','oversized','malformed')) { $invalidCampaigns += "$invalid-$name" } }
    foreach ($variant in $invalidCampaigns) {
        Reset-Fixture 'engine'
        $fixture.Campaign = $variant
        $rejected = $false
        try { & $runBlock | Out-Null } catch { $rejected = $true }
        if (-not $rejected -or $fixture.Calls.Count -ne 3) { throw "The polled campaign gate accepted invalid metadata: $variant" }
        $checks++
    }
    foreach ($suite in @('presentation', 'engine')) {
        $count = if ($suite -ceq 'presentation') { 4 } else { 3 }
        for ($failAt = 1; $failAt -le $count; $failAt++) {
            Reset-Fixture $suite $failAt
            $rejected = $false
            try { & $runBlock | Out-Null } catch { $rejected = $_.Exception.Message -cmatch '^(Native check failed:|Production native adapter and packaged Forge command checks failed\.|Production four-client and mixed-lobby native checks failed\.|Default-polled/background-AI campaign smoke failed\.)' }
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
