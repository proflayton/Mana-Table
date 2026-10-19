const test = require('node:test');
const assert = require('node:assert/strict');
const { REPO, WORKFLOWS, validateSource, validateComparison, iosApplicability, assessJobs, inspect, waitForCI } = require('../scripts/wait-release-ci.cjs');
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
test('accepts both full Java jobs and desktop, never waits on native publication', async () => {
  const f = fixture(); const result = await inspect(f); assert.equal(result.ready, true);
  assert.deepEqual(result.evidence.map(x => x.state), ['passed', 'passed', 'optional']);
  assert.ok(f.calls.every(url => !url.includes('mana-table-native')));
});
test('missing required desktop remains pending', async () => {
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
