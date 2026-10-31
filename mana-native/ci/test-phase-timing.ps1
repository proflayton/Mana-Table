[CmdletBinding()]
param([string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $TestDirectory) { $TestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mana-phase-timing-' + [Guid]::NewGuid().ToString('N')) }
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a fresh phase timing fixture directory.' }
New-Item -ItemType Directory -Path $TestDirectory | Out-Null
$revision = 'a' * 40; $version = '0.2.123'; $checks = 0
$utf8 = New-Object Text.UTF8Encoding($false)
$priorExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$savedExitCode = if ($priorExitCode) { $priorExitCode.Value } else { $null }
# Native failures are inspected explicitly below, including when this fixture is
# invoked from PowerShell 7 with its optional native-error preference enabled.
$PSNativeCommandUseErrorActionPreference = $false
function Read-Rows([string]$Path) {
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if (-not $line) { throw 'A phase receipt contains an empty record.' }
        $row = $line | ConvertFrom-Json
        $fields = @($row.PSObject.Properties.Name | Sort-Object)
        $expected = @('schema', 'sourceRevision', 'version', 'operation', 'phase', 'status', 'elapsedMilliseconds') | Sort-Object
        if (($fields -join '|') -cne ($expected -join '|') -or $row.schema -ne 1 -or
            $row.sourceRevision -cne $revision -or $row.version -cne $version -or
            $row.operation -cnotin @('package-release', 'package-preview') -or
            $row.status -cnotin @('completed', 'failed') -or
            $row.elapsedMilliseconds -isnot [ValueType] -or
            [double]::IsNaN([double]$row.elapsedMilliseconds) -or [double]::IsInfinity([double]$row.elapsedMilliseconds) -or
            [double]$row.elapsedMilliseconds -lt 0) { throw 'A phase receipt lost its bounded identity, status or finite elapsed time.' }
        Write-Output $row
    }
}
function New-FixtureTiming([string]$Name, [string]$Operation = 'package-release') {
    $path = Join-Path $TestDirectory ($Name + '.jsonl')
    $values = @(New-ManaPhaseTiming -ReceiptPath $path -SourceRevision $revision -Version $version -Operation $Operation)
    if ($values.Count -ne 1 -or -not $values[0]) { throw "A valid timing constructor must return only its context: $Name" }
    return $values[0]
}
try {
    $loadOutput = @(. (Join-Path $PSScriptRoot 'phase-timing.ps1'))
    if ($loadOutput.Count -ne 0) { throw 'Loading the timing helper polluted its caller output.' }
    $timing = New-FixtureTiming 'success'
    $tokenValues = @(Start-ManaPhase -Timing $timing -Phase 'native-publish')
    if ($tokenValues.Count -ne 1 -or -not $tokenValues[0]) { throw 'Starting a valid phase must return only its token.' }
    $completion = @(Complete-ManaPhase -Timing $timing -Token $tokenValues[0] -Succeeded $true)
    if ($completion.Count -ne 0) { throw 'Completing a phase must not add success-stream values.' }
    $rows = @(Read-Rows $timing.Path)
    if ($rows.Count -ne 1 -or $rows[0].phase -cne 'native-publish' -or $rows[0].status -cne 'completed' -or $rows[0].operation -cne 'package-release') { throw 'A successful package phase was not recorded once.' }
    $checks++

    $assigned = @(& {
        $token = Start-ManaPhase -Timing $timing -Phase 'client-copy'
        $succeeded = $false
        try { Write-Output 'original package result'; $succeeded = $true }
        finally { Complete-ManaPhase -Timing $timing -Token $token -Succeeded $succeeded }
    })
    if ($assigned.Count -ne 1 -or $assigned[0] -cne 'original package result') { throw 'Timing changed an operation assignment or its success-stream output.' }
    $checks++

    $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'The Windows phase-timing fixture requires the shipping PowerShell executable.' }
    $nativeToken = Start-ManaPhase -Timing $timing -Phase 'runtime-build'
    & $windowsPowerShell -NoProfile -NonInteractive -Command 'exit 7'
    $nativeCode = $LASTEXITCODE
    Complete-ManaPhase -Timing $timing -Token $nativeToken -Succeeded ($nativeCode -eq 0)
    if ($nativeCode -ne 7 -or $LASTEXITCODE -ne 7) { throw 'The observer changed the real external process failure exit code.' }
    $nativeRows = @(Read-Rows $timing.Path | Where-Object phase -CEQ 'runtime-build')
    if ($nativeRows.Count -ne 1 -or $nativeRows[0].status -cne 'failed') { throw 'An actual nonzero native command was not retained as a failed phase.' }
    $checks++

    # Block appending after the observer has successfully created a real receipt.
    # PowerShell can wrap an ErrorRecord when rethrowing it even without an
    # observer. Preserve the original exception and all operation error details.
    $blocked = New-FixtureTiming 'blocked-writer'
    $first = Start-ManaPhase -Timing $blocked -Phase 'native-publish'
    Complete-ManaPhase -Timing $blocked -Token $first -Succeeded $true
    $priorReceipt = [IO.File]::ReadAllText($blocked.Path)
    $blockedToken = Start-ManaPhase -Timing $blocked -Phase 'engine-resources'
    $seenError = $null; $caughtError = $null
    $originalError = [Management.Automation.ErrorRecord]::new(
        [InvalidOperationException]::new('Original package operation failed.'),
        'OriginalPackageFailure', [Management.Automation.ErrorCategory]::InvalidOperation, 'package fixture')
    $lock = [IO.File]::Open($blocked.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        try {
            try {
                try { throw $originalError }
                catch { $seenError = $_; throw }
            } finally { Complete-ManaPhase -Timing $blocked -Token $blockedToken -Succeeded $false }
        } catch { $caughtError = $_ }
        if (-not $seenError -or -not $caughtError -or
            -not [object]::ReferenceEquals($caughtError.Exception, $originalError.Exception) -or
            -not [object]::ReferenceEquals($caughtError.Exception, $seenError.Exception) -or
            $caughtError.FullyQualifiedErrorId -cne $seenError.FullyQualifiedErrorId -or
            $caughtError.CategoryInfo.ToString() -cne $seenError.CategoryInfo.ToString() -or
            -not [object]::ReferenceEquals($caughtError.TargetObject, $seenError.TargetObject) -or
            $caughtError.InvocationInfo.ScriptName -cne $seenError.InvocationInfo.ScriptName -or
            $caughtError.InvocationInfo.ScriptLineNumber -ne $seenError.InvocationInfo.ScriptLineNumber -or
            $caughtError.InvocationInfo.OffsetInLine -ne $seenError.InvocationInfo.OffsetInLine -or
            $caughtError.InvocationInfo.Line -cne $seenError.InvocationInfo.Line -or
            $caughtError.ScriptStackTrace -cne $seenError.ScriptStackTrace -or
            $LASTEXITCODE -ne 7) { throw 'An unavailable receipt replaced the original operation error details or exit status.' }
    } finally { $lock.Dispose() }
    if ([IO.File]::ReadAllText($blocked.Path) -cne $priorReceipt -or $blocked.Enabled) { throw 'An observer I/O failure changed retained evidence or kept observation enabled.' }
    $checks++

    # A failed phase can be the final retained row; reopening that path must not
    # erase the first attempt or append a second attempt with ambiguous identity.
    $partial = New-FixtureTiming 'partial-failure' 'package-preview'
    $phase = Start-ManaPhase -Timing $partial -Phase 'client-copy'
    Complete-ManaPhase -Timing $partial -Token $phase -Succeeded $false
    $partialBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($partial.Path))
    $reopened = New-ManaPhaseTiming -ReceiptPath $partial.Path -SourceRevision $revision -Version $version -Operation 'package-preview'
    if ($reopened -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($partial.Path)) -cne $partialBytes) { throw 'A reused receipt path did not preserve its first failed attempt.' }
    $rows = @(Read-Rows $partial.Path)
    if ($rows.Count -ne 1 -or $rows[0].phase -cne 'client-copy' -or $rows[0].status -cne 'failed' -or $rows[0].operation -cne 'package-preview') { throw 'The partial preview receipt lost its failure identity.' }
    $checks++

    $bounded = New-FixtureTiming 'bounded'
    foreach ($invalidPhase in @($null, 123, 'unrecognized', 'NATIVE-PUBLISH', ('x' * 4096))) {
        $phase = Start-ManaPhase -Timing $bounded -Phase $invalidPhase
        if ($phase) { throw 'Malformed or unknown phase input was accepted before the receipt cap.' }
        $extra = @(Complete-ManaPhase -Timing $bounded -Token $phase -Succeeded $false)
        if ($extra.Count -ne 0 -or [IO.File]::ReadAllText($bounded.Path)) { throw 'Malformed phase input produced evidence or caller output.' }
    }
    $phases = @('native-publish', 'client-copy', 'engine-resources', 'runtime-build', 'support-files',
        'preview-source', 'build-hashes', 'source-archive', 'source-hash', 'safety-scan',
        'package-metadata', 'runtime-archive', 'runtime-hash', 'final-manifest')
    foreach ($name in $phases) {
        $phase = Start-ManaPhase -Timing $bounded -Phase $name
        Complete-ManaPhase -Timing $bounded -Token $phase -Succeeded $true
        Complete-ManaPhase -Timing $bounded -Token $phase -Succeeded $false
    }
    foreach ($name in @('native-publish', 'unrecognized', 'NATIVE-PUBLISH', ('x' * 4096))) {
        $phase = Start-ManaPhase -Timing $bounded -Phase $name
        if ($phase) { throw 'A duplicate or unknown package phase was accepted.' }
        $extra = @(Complete-ManaPhase -Timing $bounded -Token $phase -Succeeded $false)
        if ($extra.Count -ne 0) { throw 'An ignored phase polluted caller output.' }
    }
    $rows = @(Read-Rows $bounded.Path)
    if ($rows.Count -ne 14 -or @($rows.phase | Sort-Object -Unique).Count -ne 14 -or
        ($rows.phase -join '|') -cne ($phases -join '|') -or @($rows | Where-Object status -CNE 'completed').Count) { throw 'The finite receipt admitted duplicate phases or changed its retained phase statuses.' }
    $checks++

    foreach ($mutation in @(
        @{ SourceRevision = 'A' * 40; Version = $version; Operation = 'package-release' },
        @{ SourceRevision = $revision; Version = ('1' * 4096); Operation = 'package-release' },
        @{ SourceRevision = $revision; Version = $version; Operation = 'arbitrary operation text' },
        @{ SourceRevision = [pscustomobject]@{ secret = 'not a source revision' }; Version = $version; Operation = 'package-release' }
    )) {
        $invalidPath = Join-Path $TestDirectory ('invalid-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
        $invalid = New-ManaPhaseTiming -ReceiptPath $invalidPath @mutation
        if ($invalid -or (Test-Path -LiteralPath $invalidPath)) { throw 'Malformed observer metadata created a receipt.' }
    }
    foreach ($badContext in @($null, 'not a timing context', [pscustomobject]@{ secret = 'not timing evidence' })) {
        $invalid = Start-ManaPhase -Timing $badContext -Phase 'native-publish'
        $extra = @(Complete-ManaPhase -Timing $badContext -Token ([pscustomobject]@{ arbitrary = 'not a token' }) -Succeeded 'not a boolean')
        if ($invalid -or $extra.Count -ne 0) { throw 'Malformed observer input changed caller output.' }
    }
    $invalidPath = Join-Path $TestDirectory 'invalid-sink.jsonl'
    $invalid = New-ManaPhaseTiming -ReceiptPath ([pscustomobject]@{ path = $invalidPath }) -SourceRevision $revision -Version $version -Operation 'package-release'
    if ($invalid -or (Test-Path -LiteralPath $invalidPath)) { throw 'A malformed sink argument created a receipt.' }
    $unavailablePath = Join-Path $TestDirectory 'absent-parent/receipt.jsonl'
    $unavailable = New-ManaPhaseTiming -ReceiptPath $unavailablePath -SourceRevision $revision -Version $version -Operation 'package-release'
    if ($unavailable -or (Test-Path -LiteralPath $unavailablePath)) { throw 'An unavailable receipt directory did not disable optional observation.' }
    $checks++

    # Exercise the actual production path expression, including the trailing
    # separators which GetFullPath can retain. No package build is needed.
    $parseTokens = $null; $parseErrors = $null
    $packageAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot '../package.ps1'), [ref]$parseTokens, [ref]$parseErrors)
    $pathAssignments = @($packageAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -ceq 'timingReceiptPath'
    }, $true))
    if ($parseErrors.Count -ne 0 -or $pathAssignments.Count -ne 1) { throw 'The package timing sidecar must have one parseable production path expression.' }
    $productionPath = [scriptblock]::Create($pathAssignments[0].Right.Extent.Text)
    $variant = 0
    foreach ($separator in @('', '\', '/')) {
        $fixture = Join-Path $TestDirectory ('payload-exclusion-' + $variant)
        $package = Join-Path $fixture 'package'
        New-Item -ItemType Directory -Path $package -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $package 'payload.txt'), 'Non-executable portable fixture.', $utf8)
        $OutputDirectory = [IO.Path]::GetFullPath($package + $separator)
        $sidecarPath = & $productionPath
        if ($sidecarPath -isnot [string] -or
            [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($sidecarPath)) -ine [IO.Path]::GetFullPath($fixture)) { throw 'The production timing path placed a receipt inside the portable payload.' }
        $sidecar = New-ManaPhaseTiming -ReceiptPath $sidecarPath -SourceRevision $revision -Version $version -Operation 'package-release'
        if (-not $sidecar) { throw 'The production timing path did not create its adjacent receipt.' }
        $phase = Start-ManaPhase -Timing $sidecar -Phase 'runtime-archive'
        $archivePath = $package + '.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($package, $archivePath, [IO.Compression.CompressionLevel]::Fastest, $true)
        Complete-ManaPhase -Timing $sidecar -Token $phase -Succeeded $true
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName.Replace('\', '/') -cne 'package/payload.txt' -or
                @(Read-Rows $sidecarPath).Count -ne 1) { throw 'Optional phase evidence leaked into the portable payload or was not retained beside it.' }
        } finally { $archive.Dispose() }
        $variant++
    }
    $checks++
    Write-Output "$checks phase timing observer/output/failure fixture checks passed. One local exit-7 process; no build, downloaded code or network was used. Fixtures: $TestDirectory"
} catch {
    Write-Host "Phase timing failure evidence retained at $TestDirectory"
    throw
} finally {
    if ($priorExitCode) { Set-Variable -Name LASTEXITCODE -Value $savedExitCode -Scope Global }
    else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
}
