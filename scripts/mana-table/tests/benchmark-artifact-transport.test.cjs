const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { prepare, summarize } = require('../benchmark-artifact-transport.cjs');

test('manual transport samples require identical source/download bytes, strict names and exact completed run timings', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-transport-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const source = path.join(root, 'runtime.zip'), checksum = source + '.sha256', directory = path.join(root, 'sample');
  const bytes = Buffer.from('Representative fixture bytes; the manual job uses the pinned real public runtime ZIP.');
  const payload = { tag: 'fixture', file: 'runtime.zip', bytes: bytes.length, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
  const environment = { GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '2', GITHUB_SHA: 'a'.repeat(40), GITHUB_REPOSITORY: 'example/repository' };
  fs.writeFileSync(source, bytes); fs.writeFileSync(checksum, payload.sha256 + '  ' + payload.file + '\n');
  await assert.rejects(prepare(source, checksum, directory, { ...environment, GITHUB_EVENT_NAME: 'pull_request' }, payload), /manual dispatch/);
  await assert.rejects(prepare(source, checksum, directory, environment, { ...payload, sha256: 'b'.repeat(64) }), /pinned public release/);
  const manifest = await prepare(source, checksum, directory, environment, payload);
  const steps = [['upload', manifest.uploadOrder], ['download', manifest.downloadOrder]].flatMap(([operation, order]) => order.map(key => ({
    name: `Benchmark ${operation} ${manifest.samples.find(sample => sample.key === key).label}`, status: 'completed', conclusion: 'success',
    started_at: '2026-10-23T00:00:00Z', completed_at: '2026-10-23T00:00:02Z'
  }))).map((step, index) => ({ ...step, number: index + 1 }));
  const jobs = { total_count: 1, jobs: [{ id: 1, name: 'Benchmark artifact transport', run_id: 123,
    run_attempt: 2, head_sha: environment.GITHUB_SHA, steps }] };
  const artifacts = { total_count: 4, artifacts: manifest.samples.map((sample, index) => ({
    id: index + 1, name: sample.artifact, expired: false, size_in_bytes: bytes.length + (sample.mode === 'wrapped' ? 100 : 0)
  })) };
  for (const sample of manifest.samples) {
    const folder = path.join(directory, 'downloads', sample.key); fs.mkdirSync(folder, { recursive: true });
    fs.writeFileSync(path.join(folder, sample.file), bytes);
  }
  const summarizeFixture = () => summarize(directory, jobs, artifacts, payload);
  const result = await summarizeFixture();
  assert.ok(result.verified); assert.deepEqual(result.samples.map(sample => sample.mode), ['wrapped', 'raw', 'raw', 'wrapped']);
  assert.ok(result.samples.every(sample => sample.sha256 === payload.sha256 && sample.uploadSeconds === 2 && sample.downloadSeconds === 2));
  const first = path.join(directory, 'downloads', manifest.samples[0].key, manifest.samples[0].file);
  fs.appendFileSync(first, '!'); await assert.rejects(summarizeFixture(), /Downloaded bytes differ/); fs.writeFileSync(first, bytes);
  fs.writeFileSync(path.join(path.dirname(first), 'unexpected.zip'), bytes);
  await assert.rejects(summarizeFixture(), /exact single ZIP basename/); fs.unlinkSync(path.join(path.dirname(first), 'unexpected.zip'));
  jobs.jobs[0].steps[0].conclusion = 'failure'; await assert.rejects(summarizeFixture(), /successful completed timing/); jobs.jobs[0].steps[0].conclusion = 'success';
  jobs.jobs[0].steps[0].number = 10; await assert.rejects(summarizeFixture(), /step order differs/); jobs.jobs[0].steps[0].number = 1;
  const manifestPath = path.join(directory, 'manifest.json'); fs.writeFileSync(manifestPath, JSON.stringify({ ...manifest, samples: [] }));
  await assert.rejects(summarizeFixture(), /four-sample plan/); fs.writeFileSync(manifestPath, JSON.stringify(manifest));
  jobs.jobs[0].run_attempt = 1; await assert.rejects(summarizeFixture(), /another source, run or attempt/);
});
