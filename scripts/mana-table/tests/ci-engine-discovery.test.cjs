const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { validateContext, inspect, waitForEngine, writeOutput } = require('../wait-ci-engine.cjs');
const REPO = 'proflayton/Mana-Table';
const HEAD = 'a'.repeat(40), CHECKOUT = 'c'.repeat(40), OTHER = 'b'.repeat(40);
function fixture(options = {}) {
  const context = { repo: REPO, checkoutSha: CHECKOUT, headSha: HEAD, event: 'pull_request', ...options.context };
  const run = { id: 10, run_number: 20, run_attempt: 1, head_sha: context.headSha, event: context.event,
    head_branch: context.event === 'push' ? 'master' : 'feature', repository: { id: 5, full_name: context.repo },
    path: '.github/workflows/test-build.yaml', status: 'in_progress', conclusion: null, run_started_at: '2026-10-20T20:00:00Z', ...options.run };
  const artifact = { id: 30, name: 'engine-' + context.checkoutSha, expired: false, size_in_bytes: 100, created_at: '2026-10-20T20:00:01Z',
    workflow_run: { id: run.id, repository_id: 5, head_sha: context.headSha, head_branch: run.head_branch }, ...options.artifact };
  const calls = [];
  const api = async resource => {
    calls.push(resource);
    if (resource.includes('/workflows/test-build.yaml/runs?')) {
      assert.ok(resource.includes('event=' + context.event));
      assert.ok(resource.includes('head_sha=' + context.headSha));
      const runs = options.runs ?? [run];
      return { total_count: runs.length, workflow_runs: runs, ...options.runPage };
    }
    if (resource.endsWith('/artifacts?per_page=100')) {
      const artifacts = options.artifacts ?? [artifact];
      return { total_count: artifacts.length, artifacts, ...options.artifactPage };
    }
    if (resource.endsWith('/attempts/' + run.run_attempt)) return options.attempt ? options.attempt(run) : run;
    assert.equal(resource, '/repos/' + context.repo + '/actions/runs/' + run.id);
    return options.latest ? options.latest(run) : run;
  };
  return { ...context, api, calls, run, artifact };
}
test('PR discovery distinguishes the PR head from the actual merge checkout and permits an early upload', async () => {
  const f = fixture(); const result = await inspect(f);
  assert.equal(result.ready, true); assert.equal(result.runId, 10);
  assert.equal(result.sourceHeadSha, HEAD); assert.equal(result.sourceRevision, CHECKOUT);
  assert.ok(f.calls[0].includes('head_sha=' + HEAD));
});
test('master and manual consumers can reuse only an exact-source push artifact', async () => {
  assert.equal((await inspect(fixture({ context: { event: 'push', headSha: CHECKOUT } }))).ready, true);
  assert.throws(() => validateContext({ repo: REPO, event: 'push', headSha: HEAD, checkoutSha: CHECKOUT }), /exact checkout/);
});
test('invalid source and repository fail before the first API read', async () => {
  for (const patch of [{ repo: 'else/repo/extra' }, { repo: '../repo' }, { headSha: 'short' }, { checkoutSha: CHECKOUT.toUpperCase() }, { event: 'workflow_dispatch' }]) {
    const f = fixture({ context: patch }); await assert.rejects(inspect(f)); assert.equal(f.calls.length, 0);
  }
});
test('fork repositories can read only their own exact-context producer', async () => {
  assert.equal((await inspect(fixture({ context: { repo: 'contributor/Mana-Table' } }))).ready, true);
});
test('another head, workflow, repository, event, or master branch is never accepted', async () => {
  for (const run of [{ head_sha: CHECKOUT }, { event: 'push' }, { path: '.github/workflows/mana-table.yml' },
    { repository: { id: 5, full_name: 'else/repo' } }, { id: 0 }, { run_attempt: 0 }])
    await assert.rejects(inspect(fixture({ run })), /producer belongs/);
  await assert.rejects(inspect(fixture({ context: { event: 'push', headSha: CHECKOUT }, run: { head_branch: 'feature' } })), /producer belongs/);
});
test('artifact provenance must bind the producer run, repository and PR head', async () => {
  for (const patch of [{ id: 11 }, { repository_id: 6 }, { head_sha: CHECKOUT }, { head_branch: 'other' }]) {
    const f = fixture(); f.artifact.workflow_run = { ...f.artifact.workflow_run, ...patch };
    await assert.rejects(inspect(f), /artifact belongs/);
  }
});
test('an old merge artifact or an expired artifact never substitutes for the requested checkout', async () => {
  for (const artifact of [{ name: 'engine-' + OTHER }, { expired: true }]) {
    assert.equal((await inspect(fixture({ artifact }))).ready, false);
    await assert.rejects(inspect(fixture({ artifact, run: { status: 'completed', conclusion: 'success' } })), /Rebuild the producer/);
  }
});
test('the newest producer must upload its artifact instead of borrowing an older matching run', async () => {
  const f = fixture({ artifacts: [] });
  const newer = { ...f.run, id: 11, run_number: 21 };
  const g = fixture({ runs: [f.run, newer], artifacts: [] });
  assert.equal((await inspect(g)).ready, false);
  assert.ok(g.calls.some(url => url.includes('/runs/11/artifacts')));
  assert.ok(g.calls.every(url => !url.includes('/runs/10/artifacts')));
});
test('missing uploads wait, but a completed producer without an upload fails clearly', async () => {
  assert.equal((await inspect(fixture({ artifacts: [] }))).ready, false);
  await assert.rejects(inspect(fixture({ artifacts: [], run: { status: 'completed', conclusion: 'failure' } })), /completed without/);
});
test('available artifacts establish source only, never a green Java or publication gate', async () => {
  const result = await inspect(fixture({ run: { status: 'completed', conclusion: 'failure' } }));
  assert.equal(result.ready, true); assert.equal(Object.hasOwn(result, 'checksPassed'), false);
});
test('incomplete pages and ambiguous artifacts cannot hide missing or wrong evidence', async () => {
  await assert.rejects(inspect(fixture({ runPage: { total_count: 100 } })), /Incomplete/);
  await assert.rejects(inspect(fixture({ artifactPage: { total_count: 100 } })), /Incomplete/);
  const f = fixture(); await assert.rejects(inspect(fixture({ artifacts: [f.artifact, { ...f.artifact, id: 31 }] })), /Ambiguous/);
});
test('a producer attempt changing during lookup must be inspected again', async () => {
  assert.equal((await inspect(fixture({ latest: run => ({ ...run, run_attempt: 2 }) }))).ready, false);
  await assert.rejects(inspect(fixture({ latest: run => ({ ...run, id: 11 }) })), /another run/);
});
test('a rerun cannot borrow the previous attempt upload before replacing it', async () => {
  const f = fixture({ run: { run_attempt: 2, run_started_at: '2026-10-20T20:01:00Z' } });
  assert.equal((await inspect(f)).ready, false);
  await assert.rejects(inspect(fixture({ run: { run_attempt: 2, run_started_at: '2026-10-20T20:01:00Z', status: 'completed' } })), /fresh engine upload/);
  await assert.rejects(inspect(fixture({ attempt: run => ({ ...run, run_attempt: 99 }) })), /attempt belongs/);
});
test('wait polls a pending producer then returns only the fresh source-bound run', async () => {
  let clock = 0; const waiting = fixture({ artifacts: [] }), ready = fixture();
  const result = await waitForEngine({ ...ready, api: url => (clock ? ready : waiting).api(url),
    now: () => clock, sleep: async ms => { clock += ms; }, timeoutMs: 100, pollMs: 20 });
  assert.equal(result.runId, 10); assert.equal(clock, 20);
});
test('permanently missing artifacts have a bounded wait and no old-source fallback', async () => {
  let clock = 0;
  await assert.rejects(waitForEngine({ ...fixture({ runs: [] }), now: () => clock,
    sleep: async ms => { clock += ms; }, timeoutMs: 100, pollMs: 20 }), /Timed out/);
  assert.equal(clock, 100);
});
test('API failures fail closed', async () => {
  await assert.rejects(inspect({ ...fixture(), api: async () => { throw new Error('HTTP 403'); } }), /HTTP 403/);
});

test('every engine lookup shares the original deadline and invalid provenance is never polled again', async () => {
  const f = fixture({ artifact: { workflow_run: { id: 99 } } });
  const deadlines = [];
  await assert.rejects(waitForEngine({ ...f, now: () => 100, timeoutMs: 400,
    api: (resource, options) => { deadlines.push(options.deadline); return f.api(resource); },
    sleep: async () => assert.fail('Invalid provenance must fail immediately.') }), /artifact belongs/);
  assert.deepEqual(deadlines, [500, 500]);
});
test('only a proven numeric run ID may reach GitHub outputs', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-engine-output-'));
  const output = path.join(directory, 'output');
  try {
    assert.throws(() => writeOutput(output, { ready: false, runId: 10 }), /proven/);
    assert.equal(fs.existsSync(output), false);
    assert.throws(() => writeOutput(output, { ready: true, runId: '10\nother=bad' }), /proven/);
    writeOutput(output, { ready: true, runId: 10 });
    assert.equal(fs.readFileSync(output, 'utf8'), 'run-id=10\n');
  } finally { if (fs.existsSync(output)) fs.unlinkSync(output); fs.rmdirSync(directory); }
});
