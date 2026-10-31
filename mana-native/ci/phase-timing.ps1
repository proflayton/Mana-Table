# Optional packaging evidence only. Every observer failure is contained so the
# original operation and its exit checks remain authoritative.
function New-ManaPhaseTiming {
    param($ReceiptPath, $SourceRevision, $Version, $Operation)
    try {
        if ($ReceiptPath -isnot [string] -or $SourceRevision -isnot [string] -or
            $Version -isnot [string] -or $Operation -isnot [string] -or
            $SourceRevision -cnotmatch '\A[a-f0-9]{40}\z' -or
            $Version.Length -gt 23 -or $Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\z' -or
            $Operation -cnotin @('package-release', 'package-preview')) { return $null }
        $path = [IO.Path]::GetFullPath($ReceiptPath)
        # A reused output name must not destroy an earlier failed attempt's evidence.
        $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $file.Dispose()
        return [pscustomobject]@{
            Path = $path; SourceRevision = $SourceRevision; Version = $Version; Operation = $Operation
            Enabled = $true
            Written = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            Phases = @('native-publish', 'client-copy', 'engine-resources', 'runtime-build', 'support-files',
                'preview-source', 'build-hashes', 'source-archive', 'source-hash', 'safety-scan',
                'package-metadata', 'runtime-archive', 'runtime-hash', 'final-manifest')
        }
    } catch { return $null }
}

function Start-ManaPhase {
    param($Timing, $Phase)
    try {
        if (-not $Timing -or -not $Timing.Enabled -or $Phase -isnot [string] -or $Phase -cnotin $Timing.Phases -or
            $Timing.Written.Contains($Phase)) { return $null }
        return [pscustomobject]@{ Phase = $Phase; Timer = [Diagnostics.Stopwatch]::StartNew() }
    } catch { return $null }
}

function Complete-ManaPhase {
    param($Timing, $Token, $Succeeded)
    try {
        if (-not $Timing -or -not $Timing.Enabled -or -not $Token -or $Succeeded -isnot [bool] -or
            $Token.Phase -cnotin $Timing.Phases -or $Timing.Written.Count -ge 14 -or
            -not $Timing.Written.Add($Token.Phase)) { return }
        $Token.Timer.Stop()
        $record = [ordered]@{
            schema = 1; sourceRevision = $Timing.SourceRevision; version = $Timing.Version
            operation = $Timing.Operation; phase = $Token.Phase
            status = if ($Succeeded) { 'completed' } else { 'failed' }
            elapsedMilliseconds = [long]$Token.Timer.ElapsedMilliseconds
        }
        $line = ($record | ConvertTo-Json -Compress) + [Environment]::NewLine
        [IO.File]::AppendAllText($Timing.Path, $line, (New-Object Text.UTF8Encoding($false)))
    } catch {
        # Stop observing after an I/O or serialization failure; never retry or
        # replace the packaging exception with an evidence-writer exception.
        try { if ($Timing) { $Timing.Enabled = $false } } catch { }
    }
}
