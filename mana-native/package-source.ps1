# Shared by the production packager and the small source-export regression.
function Assert-ManaReleaseSource {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Revision)
    if ($Revision -cnotmatch '^[a-f0-9]{40}$') { throw 'A full lowercase committed source revision is required.' }
    $head = & git -c core.excludesfile= -C $Repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $Revision) { throw 'The release checkout must still match its exact source revision.' }
    $changes = & git -c core.excludesfile= -C $Repository status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify that the release checkout is clean.' }
    if ($changes) { throw 'Commit or remove working-tree changes before building a release from its exact source revision.' }
    $attributes = & git -C $Repository rev-parse --path-format=absolute --git-path info/attributes
    if ($LASTEXITCODE -ne 0 -or -not $attributes) { throw 'Could not check repository-local source export attributes.' }
    if ((Test-Path -LiteralPath $attributes) -and (Get-Content -LiteralPath $attributes | Where-Object { $_ -match '^\s*[^\s#]' })) {
        throw 'Repository-local info/attributes can override committed source; remove those overrides before release export.'
    }
}

function New-ManaSourceArchive {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Revision,
        [Parameter(Mandatory)][string]$OutputPath)
    Assert-ManaReleaseSource -Repository $Repository -Revision $Revision
    if (Test-Path -LiteralPath $OutputPath) { throw 'Use a new source archive path for each release.' }
    $previousNoSystem = $env:GIT_ATTR_NOSYSTEM
    try {
        $env:GIT_ATTR_NOSYSTEM = '1'
        # Use committed .gitattributes; no checkout, global or system EOL policy.
        # Explicit committed eol attributes still apply; otherwise text uses LF.
        & git -c core.excludesfile= -c core.attributesfile= -c core.autocrlf=false -c core.eol=lf -C $Repository archive --format=zip "--output=$OutputPath" $Revision
        if ($LASTEXITCODE -ne 0) { throw 'Could not archive the complete committed source.' }
    } finally {
        $env:GIT_ATTR_NOSYSTEM = $previousNoSystem
    }
}
