const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { REPO, WORKFLOWS, validateSource, validateComparison, iosApplicability, assessJobs, inspect, waitForCI } = require('../wait-release-ci.cjs');
const SHA = 'a'.repeat(40), OTHER = 'b'.repeat(40);
function fixture(options = {}) {
  const calls = [], observed = new Map();
  const api = async resource => {
    calls.push(resource);
    if (resource.endsWith('/git/ref/heads/master')) return { object: { sha: options.master || SHA } };
    const file = WORKFLOWS.find(spec => resource.includes('/workflows/' + spec.file + '/runs'));
    if (file) {
      let runs = file.required || options.ios ? [{ id: 10 + WORKFLOWS.indexOf(file), run_attempt: options.attempt || 1,
        head_sha: SHA, head_branch: 'master', event: 'push', repository: { full_name: REPO },
        path: '.github/workflows/' + file.file, status: 'completed', conclusion: 'success', html_url: 'https://github.com/' + REPO + '/actions/runs/10' }] : [];
      if (options.runs) runs = options.runs(file, runs);
      for (const run of runs) observed.set(run.id, run);
      return { total_count: runs.length, workflow_runs: runs };
    }
    const detail = resource.match(/\/actions\/runs\/(\d+)$/);
    if (detail) { const run = observed.get(Number(detail[1])); return options.latest ? options.latest(run) : run; }
    const match = resource.match(/\/runs\/(\d+)\/jobs\?filter=latest&per_page=100$/);
    assert.ok(match, 'Unexpected API request ' + resource);
    const spec = WORKFLOWS[Number(match[1]) - 10];
    let jobs = spec.jobs.map(name => ({ name, run_id: Number(match[1]), head_sha: SHA, status: 'completed', conclusion: 'success' }));
    if (options.jobs) jobs = options.jobs(spec, jobs);
    return { total_count: jobs.length, jobs };
  };
  return { api, calls, repo: REPO, sha: SHA, iosRequired: !!options.iosRequired };
}
test('requires exact repository and full source before any request', async () => {
  assert.throws(() => validateSource('somewhere/else', SHA), /restricted/);
  for (const sha of ['', 'abc', SHA.toUpperCase()]) assert.throws(() => validateSource(REPO, sha), /SHA/);
  const f = fixture(); await assert.rejects(inspect({ ...f, repo: 'else/repo' }), /restricted/); assert.equal(f.calls.length, 0);
});
test('accepts both full Java jobs and the shared-tooling context, never waits on native publication', async () => {
  const f = fixture(); const result = await inspect(f); assert.equal(result.ready, true);
  assert.deepEqual(result.evidence.map(x => x.state), ['passed', 'passed', 'optional']);
  assert.ok(f.calls.every(url => !url.includes('mana-table-native')));
});
test('missing required shared-tooling context remains pending', async () => {
  const f = fixture({ runs: (spec, runs) => spec.file === 'mana-table.yml' ? [] : runs });
  assert.equal((await inspect(f)).ready, false);
});
test('queued workflow remains pending without requesting nonexistent job evidence', async () => {
  const f = fixture({ runs: (spec, runs) => runs.map(r => ({ ...r, status: 'queued', conclusion: null })) });
  assert.equal((await inspect(f)).ready, false); assert.ok(!f.calls.some(url => url.includes('/jobs?')));
});
test('in-progress jobs cannot count as a green workflow', async () => {
  const f = fixture({ runs: (spec, runs) => runs.map(r => ({ ...r, status: 'in_progress', conclusion: null })) });
  assert.equal((await inspect(f)).ready, false);
});
test('a failed matrix job blocks before its sibling finishes', async () => {
  const f = fixture({ runs: (spec, runs) => runs.map(r => ({ ...r, status: 'in_progress', conclusion: null })),
    jobs: (spec, jobs) => jobs.map((j, i) => i ? { ...j, status: 'in_progress', conclusion: null } : { ...j, conclusion: 'failure' }) });
  await assert.rejects(inspect(f), /failed or skipped/);
});
test('every non-success completed conclusion blocks publication', async () => {
  for (const conclusion of ['failure', 'cancelled', 'skipped', 'neutral', 'timed_out', 'action_required', null]) {
    const f = fixture({ runs: (spec, runs) => runs.map(r => ({ ...r, conclusion })) });
    await assert.rejects(inspect(f), /failed/);
  }
});
test('a successful workflow must contain both Java versions', async () => {
  const f = fixture({ jobs: (spec, jobs) => spec.file === 'test-build.yaml' ? jobs.slice(0, 1) : jobs });
  await assert.rejects(inspect(f), /Missing required job: Test with Java 21/);
});
test('a skipped job blocks even when the enclosing workflow says success', async () => {
  const f = fixture({ jobs: (spec, jobs) => jobs.map(j => ({ ...j, conclusion: 'skipped' })) });
  await assert.rejects(inspect(f), /failed or skipped/);
});
test('wrong source, branch, event, workflow, or repository responses are rejected', async () => {
  for (const patch of [{ head_sha: OTHER }, { head_branch: 'feature' }, { event: 'pull_request' },
    { path: '.github/workflows/other.yml' }, { repository: { full_name: 'else/repo' } }]) {
    await assert.rejects(inspect(fixture({ runs: (spec, runs) => runs.map(r => ({ ...r, ...patch })) })), /Wrong source/);
  }
});
test('a wrong-source matrix job is rejected', async () => {
  await assert.rejects(inspect(fixture({ jobs: (spec, jobs) => jobs.map(j => ({ ...j, head_sha: OTHER })) })), /Job evidence/);
});
test('fresh latest jobs are used, with no dependency on an undocumented job attempt field', async () => {
  const f = fixture({ attempt: 2 }); assert.equal((await inspect(f)).ready, true);
  assert.ok(f.calls.some(url => url.includes('/jobs?filter=latest')));
});
test('a repaired failed-job rerun retains successful jobs from the earlier attempt', async () => {
  const f = fixture({ attempt: 2, jobs: (spec, jobs) => jobs.map((j, index) => ({ ...j, run_attempt: index ? 2 : 1 })) });
  assert.equal((await inspect(f)).ready, true);
});
test('a rerun started during job inspection must wait for its current attempt', async () => {
  const f = fixture({ latest: run => ({ ...run, run_attempt: 2, status: 'in_progress', conclusion: null }) });
  assert.equal((await inspect(f)).ready, false);
});
test('a newly failed attempt during final inspection blocks publication', async () => {
  await assert.rejects(inspect(fixture({ latest: run => ({ ...run, run_attempt: 2, conclusion: 'failure' }) })), /failed/);
});
test('an observed failed iOS run blocks even when path inference made it optional', async () => {
  const f = fixture({ ios: true, runs: (spec, runs) => spec.required ? runs : runs.map(r => ({ ...r, conclusion: 'failure' })) });
  await assert.rejects(inspect(f), /ios-compat-gate.yml failed/);
});
test('applicable but missing iOS run waits instead of treating absence as success', async () => {
  assert.equal((await inspect(fixture({ iosRequired: true }))).ready, false);
  assert.equal((await inspect(fixture({ iosRequired: true, ios: true }))).ready, true);
});
test('iOS applicability includes root/nested POMs, renames, deletion paths, and capped diffs', () => {
  for (const filename of ['pom.xml', 'forge-api/pom.xml', 'forge-api/X.java', 'forge-gui-ios/a', '.github/workflows/ios-compat-gate.yml'])
    assert.equal(iosApplicability({ files: [{ filename, status: 'removed' }] }), true);
  assert.equal(iosApplicability({ files: [{ filename: 'new.txt', previous_filename: 'old.java' }] }), true);
  assert.equal(iosApplicability({ files: Array.from({ length: 300 }, () => ({ filename: 'readme.txt' })) }), true);
  assert.equal(iosApplicability({ files: [{ filename: 'mana-native/A.cs' }] }), false);
  assert.throws(() => iosApplicability({}), /comparison/);
});
test('real compare response shape binds base and final commit without a head_commit field', () => {
  validateComparison({ base_commit: { sha: OTHER }, status: 'ahead', total_commits: 2, commits: [{ sha: 'c'.repeat(40) }, { sha: SHA }] }, OTHER, SHA);
});
test('compare rejects another base, terminal source, or diverged history', () => {
  const comparison = { base_commit: { sha: OTHER }, status: 'ahead', total_commits: 1, commits: [{ sha: SHA }] };
  assert.throws(() => validateComparison({ ...comparison, base_commit: { sha: SHA } }, OTHER, SHA), /another base/);
  assert.throws(() => validateComparison({ ...comparison, commits: [{ sha: OTHER }] }, OTHER, SHA), /another head/);
  assert.throws(() => validateComparison({ ...comparison, status: 'diverged' }, OTHER, SHA), /diverged/);
});
test('a fabricated head_commit field cannot substitute for complete comparison evidence', () => {
  assert.throws(() => validateComparison({ base_commit: { sha: OTHER }, status: 'ahead', head_commit: { sha: SHA } }, OTHER, SHA), /incomplete/);
});
test('truncated comparison history blocks even if its last returned commit looks correct', () => {
  assert.throws(() => validateComparison({ base_commit: { sha: OTHER }, status: 'ahead', total_commits: 251,
    commits: Array.from({ length: 250 }, () => ({ sha: SHA })) }, OTHER, SHA), /incomplete/);
});
test('identical compare is accepted only for the exact same source and empty history', () => {
  const comparison = { base_commit: { sha: SHA }, status: 'identical', total_commits: 0, commits: [] };
  validateComparison(comparison, SHA, SHA);
  assert.throws(() => validateComparison(comparison, SHA, OTHER), /another head/);
});
test('master advancing blocks before checks and immediately before returning ready', async () => {
  await assert.rejects(inspect(fixture({ master: OTHER })), /Master advanced/);
  const f = fixture(); let masterReads = 0;
  const api = async url => url.endsWith('/git/ref/heads/master') && ++masterReads === 2 ? { object: { sha: OTHER } } : f.api(url);
  await assert.rejects(waitForCI({ ...f, api }), /Master advanced/);
});
test('waits pending then succeeds with a fresh exact-source snapshot', async () => {
  let tick = 0; const snapshots = [], missing = fixture({ runs: () => [] }), ready = fixture();
  const api = url => (tick ? ready.api(url) : missing.api(url));
  const result = await waitForCI({ ...ready, api, now: () => tick, sleep: async ms => { tick += ms; }, pollMs: 20, timeoutMs: 100, report: x => snapshots.push(x.ready) });
  assert.equal(result.ready, true); assert.deepEqual(snapshots, [false, true]);
});
test('bounded timeout rejects permanently missing checks with no real sleeping', async () => {
  let tick = 0; const f = fixture({ runs: () => [] });
  await assert.rejects(waitForCI({ ...f, now: () => tick, sleep: async ms => { tick += ms; }, pollMs: 20, timeoutMs: 100 }), /Timed out/);
  assert.equal(tick, 100);
});
test('network/API errors fail closed rather than manufacturing green evidence', async () => {
  await assert.rejects(inspect({ ...fixture(), api: async () => { throw new Error('HTTP 403'); } }), /HTTP 403/);
});
test('incomplete result pages and duplicate observed failure cannot be hidden', async () => {
  const f = fixture(); const api = async url => { const r = await f.api(url); return r.workflow_runs ? { ...r, total_count: 100 } : r; };
  await assert.rejects(inspect({ ...f, api }), /Incomplete/);
  const duplicate = fixture({ runs: (spec, runs) => runs.length ? [...runs, { ...runs[0], id: 99, conclusion: 'failure' }] : runs });
  await assert.rejects(inspect(duplicate), /failed/);
});

function nativeJobs() {
  const workflow = fs.readFileSync(path.join(__dirname, '../../../.github/workflows/mana-table-native-release.yml'), 'utf8').replace(/\r\n/g, '\n');
  const source = workflow.slice(workflow.indexOf('\njobs:\n') + '\njobs:\n'.length);
  const headings = [...source.matchAll(/^  ([a-z][\w-]*):[ \t]*$/gm)];
  return new Map(headings.map((heading, index) => [heading[1], source.slice(heading.index, headings[index + 1]?.index)]));
}
function dependencies(job) {
  const declaration = job.match(/^    needs:\s*(.+)$/m);
  assert.ok(declaration, 'Required native job must declare its prerequisites.');
  return declaration[1].replace(/^\[|\]$/g, '').split(',').map(value => value.trim());
}
test('full SDK fixture verification starts from tools while product migration retains its exact package', () => {
  const jobs = nativeJobs(), sdk = jobs.get('sdk-updates'), migration = jobs.get('managed-updates');
  assert.ok(sdk && migration, 'Both isolated lifecycle jobs must exist.');
  assert.deepEqual(dependencies(sdk), ['tools']);
  assert.deepEqual(dependencies(migration).sort(), ['package', 'tools']);
  assert.match(sdk, /run: \.\/mana-native\/updates\/test-installed-updates\.ps1 -VpkDll \$env:MANA_VPK -TestDirectory/);
  assert.doesNotMatch(sdk, /-ShutdownOnly|-KeepInstalled|MANA_NATIVE_PACKAGE|restore-package\.ps1/);
  assert.match(migration, /test-managed-migration\.ps1 -InputPackageDirectory \$env:MANA_NATIVE_PACKAGE/);
  for (const [job, directory] of [[sdk, 'mana-managed-updates'], [migration, 'mana-managed-migration']]) {
    assert.match(job, /if: always\(\)\n        uses: actions\/upload-artifact@/);
    for (const extension of ['json', 'log']) assert.ok(job.includes(`${directory}/**/*.${extension}`), 'Retain independent lifecycle evidence.');
  }
});
test('the actual Windows aggregate requires every check and exactly the authorized installer lane', () => {
  const gate = nativeJobs().get('windows'), required = dependencies(gate);
  assert.match(gate, /^    if: \$\{\{ always\(\) && !\(github.event_name == 'workflow_dispatch' && inputs.benchmark_transport\) \}\}$/m);
  for (const job of ['managed-updates', 'sdk-updates']) assert.ok(required.includes(job), `${job} must block the required aggregate.`);
  const embedded = gate.match(/          node <<'NODE'\n([\s\S]*?)^          NODE$/m);
  assert.ok(embedded, 'Exercise the shipping aggregate rather than a second gate implementation.');
  const script = embedded[1].replace(/^          /gm, '');
  function run(installer, job, result, remove) {
    const inactive = installer === 'installer' ? 'installer-release' : 'installer';
    const results = Object.fromEntries(required.map(name => [name, { result: name === job ? result : name === inactive ? 'skipped' : 'success' }]));
    if (remove) delete results[remove];
    const process = { env: { JOB_RESULTS: JSON.stringify(results), INSTALLER_JOB: installer }, exitCode: 0 };
    vm.runInNewContext(script, { process, console: { log() {}, error() {} } }, { timeout: 1000 });
    return process.exitCode;
  }
  for (const installer of ['installer', 'installer-release']) {
    const inactive = installer === 'installer' ? 'installer-release' : 'installer';
    assert.equal(run(installer), 0);
    for (const job of required.filter(name => name !== inactive))
      for (const result of ['failure', 'cancelled', 'skipped', 'neutral', 'timed_out', null])
        assert.equal(run(installer, job, result), 1, `${installer}: ${job}=${result} must remain red.`);
    for (const result of ['success', 'failure', 'cancelled', null]) assert.equal(run(installer, inactive, result), 1);
    for (const missing of ['installer', 'installer-release']) assert.throws(() => run(installer, null, null, missing), /accounted for/);
  }
  assert.throws(() => run('arbitrary-lane'), /Unknown installer lane/);
});

test('private staging runs during verification with release access confined to trusted master', () => {
  const jobs = nativeJobs(), readOnly = jobs.get('installer'), stage = jobs.get('installer-release'), publish = jobs.get('publish');
  for (const job of [readOnly, stage]) assert.deepEqual(dependencies(job), ['package', 'tools']);
  assert.match(readOnly, /contents: read/); assert.match(readOnly, /stage_release: false/);
  assert.match(stage, /contents: write/); assert.match(stage, /stage_release: true/);
  assert.match(stage, /github.repository == 'proflayton\/Mana-Table' && github.ref == 'refs\/heads\/master' && github.event_name != 'pull_request'/);
  assert.match(readOnly, /&& !\(github.repository == 'proflayton\/Mana-Table'/);
  assert.deepEqual(dependencies(publish), ['windows']);
  assert.match(publish, /name: mana-native-release-stage-\$\{\{ github.sha \}\}/);
  assert.match(publish, /release-assets\.cjs promote --receipt stage\/release-stage-receipt.json/);
  assert.doesNotMatch(publish, /mana-native-windows-x64-|gh release upload|path: downloads/);
});

test('installer smoke inputs omit source bytes while production still consumes the actual raw source ZIP', () => {
  const jobs = nativeJobs(), producer = jobs.get('package');
  const metadata = producer.slice(producer.indexOf('name: mana-native-installer-inputs-'));
  assert.match(metadata, /dist\/ci-source-receipt.json/);
  assert.doesNotMatch(metadata, /^            dist\/package-(?:source|windows-x64)\.zip$/m);
  for (const name of ['installer-lifecycle', 'managed-updates']) {
    const job = jobs.get(name);
    assert.match(job, /-SourceReceipt \$env:MANA_NATIVE_SOURCE_RECEIPT/);
    assert.doesNotMatch(job, /name: package-source.zip|\-SourceArchive/);
    assert.match(job, /name: package-windows-x64.zip\n          path: dist\/ci-inputs\n          skip-decompress: true\n          digest-mismatch: error/);
  }
  const production = fs.readFileSync(path.join(__dirname, '../../../.github/workflows/mana-table-native-installer.yml'), 'utf8');
  assert.match(production, /name: package-source.zip/);
  assert.match(production, /build-installer\.ps1 .* -SourceArchive \$env:MANA_NATIVE_SOURCE/);
  assert.doesNotMatch(production, /build-installer\.ps1 .* -SourceReceipt/);
});

test('the transport benchmark is manual-only, read-only, and outside release publication', () => {
  const jobs = nativeJobs(), benchmark = jobs.get('artifact-transport-benchmark');
  assert.match(benchmark, /if: github.event_name == 'workflow_dispatch' && inputs.benchmark_transport/);
  assert.match(benchmark, /contents: read/); assert.doesNotMatch(benchmark, /contents: write|needs:/);
  assert.match(benchmark, /gh release download mana-native-v0\.2\.89/);
  assert.ok(!dependencies(jobs.get('windows')).includes('artifact-transport-benchmark'));
  assert.match(jobs.get('windows'), /inputs.benchmark_transport && 'Release checks not run \(transport benchmark\)' \|\| 'Verify and package Windows x64'/);
  for (const name of ['checks', 'tools', 'package', 'native-ui', 'installer', 'installer-release', 'installer-lifecycle', 'managed-updates', 'sdk-updates', 'update-installer', 'windows', 'publish'])
    assert.match(jobs.get(name), /!\(github.event_name == 'workflow_dispatch' && inputs.benchmark_transport\)/, `${name} must skip benchmark-only runs.`);
});
