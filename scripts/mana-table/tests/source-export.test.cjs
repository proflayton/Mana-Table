const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '../../..');
test('production source export is independent of host EOL and rejects dirty, moved or locally overridden source', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-source-export-'));
  const repository = path.join(directory, 'repo');
  fs.mkdirSync(repository);
  const env = { ...process.env, GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: path.join(directory, 'global-config') };
  function run(executable, args, cwd = repository) {
    const result = spawnSync(executable, args, { cwd, env, encoding: 'utf8', timeout: 30000, windowsHide: true });
    assert.equal(result.error, undefined, result.error?.message);
    assert.equal(result.status, 0, result.stderr || result.stdout);
    return result.stdout.trim();
  }
  try {
    run('git', ['init', '--quiet']);
    run('git', ['config', 'core.autocrlf', 'input']);
    run('git', ['config', 'core.safecrlf', 'false']);
    const attributes = '* text=auto\n*.bin -text\nwindows.cmd text eol=crlf\nunix.sh text eol=lf\n';
    fs.writeFileSync(path.join(repository, '.gitattributes'), attributes);
    fs.writeFileSync(path.join(repository, 'text.txt'), 'one\ntwo\n');
    fs.writeFileSync(path.join(repository, 'windows.cmd'), 'echo one\necho two\n');
    fs.writeFileSync(path.join(repository, 'unix.sh'), 'echo one\necho two\n');
    const binary = Buffer.from([0, 13, 10, 255, 0, 10]);
    fs.writeFileSync(path.join(repository, 'binary.bin'), binary);
    run('git', ['add', '.']);
    run('git', ['-c', 'user.name=Source Export Fixture', '-c', 'user.email=fixture@example.invalid',
      '-c', 'commit.gpgsign=false', '-c', 'core.hooksPath=', 'commit', '--quiet', '-m', 'Fixture']);
    const revision = run('git', ['rev-parse', 'HEAD']);
    const globalAttributes = path.join(directory, 'global-attributes');
    fs.writeFileSync(globalAttributes, '* export-ignore\n');
    run('git', ['config', 'core.attributesfile', globalAttributes]);
    const driver = path.join(directory, 'exercise.ps1');
    fs.writeFileSync(driver, `param($Exporter, $Repository, $Revision, $Directory)
$ErrorActionPreference = 'Stop'
. $Exporter
Add-Type -AssemblyName System.IO.Compression.FileSystem
$exports = @()
foreach ($mode in @('input', 'true')) {
    & git -C $Repository config core.autocrlf $mode
    & git -C $Repository config core.eol crlf
    $archive = Join-Path $Directory ($mode + '.zip')
    New-ManaSourceArchive -Repository $Repository -Revision $Revision -OutputPath $archive
    $members = @{}
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $memory = New-Object IO.MemoryStream
            $stream = $entry.Open()
            try { $stream.CopyTo($memory); $members[$entry.FullName] = [Convert]::ToBase64String($memory.ToArray()) }
            finally { $stream.Dispose(); $memory.Dispose() }
        }
    } finally { $zip.Dispose() }
    $exports += @{ mode=$mode; members=$members }
}
$failures = @{}
[IO.File]::AppendAllText((Join-Path $Repository 'text.txt'), 'dirty')
try { New-ManaSourceArchive -Repository $Repository -Revision $Revision -OutputPath (Join-Path $Directory 'dirty.zip') }
catch { $failures['dirty'] = $_.Exception.Message }
[IO.File]::WriteAllText((Join-Path $Repository 'text.txt'), "one\ntwo\n")
try { New-ManaSourceArchive -Repository $Repository -Revision ('a' * 40) -OutputPath (Join-Path $Directory 'wrong.zip') }
catch { $failures['wrong'] = $_.Exception.Message }
$info = & git -C $Repository rev-parse --path-format=absolute --git-path info/attributes
[IO.File]::WriteAllText($info, '* export-ignore')
try { New-ManaSourceArchive -Repository $Repository -Revision $Revision -OutputPath (Join-Path $Directory 'override.zip') }
catch { $failures['override'] = $_.Exception.Message }
@{ exports=$exports; failures=$failures } | ConvertTo-Json -Depth 8 -Compress
`);
    const actual = JSON.parse(run(process.platform === 'win32' ? 'powershell.exe' : 'pwsh',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', driver,
        '-Exporter', path.join(root, 'mana-native/package-source.ps1'), '-Repository', repository,
        '-Revision', revision, '-Directory', directory]));
    assert.deepEqual(actual.exports[0].members, actual.exports[1].members);
    const members = Object.fromEntries(Object.entries(actual.exports[0].members).map(([name, data]) => [name, Buffer.from(data, 'base64')]));
    assert.deepEqual(Object.keys(members).sort(), ['.gitattributes', 'binary.bin', 'text.txt', 'unix.sh', 'windows.cmd']);
    assert.equal(members['text.txt'].toString(), 'one\ntwo\n');
    assert.equal(members['unix.sh'].toString(), 'echo one\necho two\n');
    assert.equal(members['windows.cmd'].toString(), 'echo one\r\necho two\r\n');
    assert.deepEqual(members['binary.bin'], binary);
    assert.match(actual.failures.dirty, /working-tree changes/);
    assert.match(actual.failures.wrong, /exact source revision/);
    assert.match(actual.failures.override, /info\/attributes/);
    for (const name of ['dirty', 'wrong', 'override']) assert.equal(fs.existsSync(path.join(directory, name + '.zip')), false);
    const packager = fs.readFileSync(path.join(root, 'mana-native/package.ps1'), 'utf8');
    assert.match(packager, /\. \(Join-Path \$PSScriptRoot 'package-source\.ps1'\)/);
    assert.match(packager, /New-ManaSourceArchive -Repository \$repository -Revision \$revision -OutputPath \$sourceArchive/);
    assert.match(packager, /sourceArchiveSha256.*= \$sourceHash/);
    assert.match(packager, /sourceArchiveBytes.*= \(Get-Item -LiteralPath \$sourceArchive\)\.Length/);
  } finally {
    const resolved = path.resolve(directory);
    assert.equal(path.dirname(resolved), path.resolve(os.tmpdir()));
    assert.ok(path.basename(resolved).startsWith('mana-source-export-'));
    fs.rmSync(resolved, { recursive: true });
  }
});
