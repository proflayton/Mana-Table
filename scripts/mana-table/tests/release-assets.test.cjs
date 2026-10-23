const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { REPO, context, requiredNames, githubApi, stage, promote, main } = require('../release-assets.cjs');
const SHA = 'a'.repeat(40), OTHER = 'b'.repeat(40);
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const json = value => Buffer.from(JSON.stringify(value));
const clone = value => structuredClone(value);

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'mana-release-stage-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const directory = path.join(root, 'downloads with spaces & $(literal)'), receiptPath = path.join(root, 'small receipt.json');
  await fs.mkdir(directory);
  const env = { GITHUB_REPOSITORY: REPO, GITHUB_REF: 'refs/heads/master', GITHUB_EVENT_NAME: 'push',
    GITHUB_SHA: SHA, GITHUB_RUN_ID: '123456', GITHUB_RUN_NUMBER: '89', GITHUB_RUN_ATTEMPT: '1' };
  const ctx = context(env), names = requiredNames(ctx).concat(`ManaTable-${ctx.version}-win-x64-delta.nupkg`);
  const state = { release: null, calls: [], uploads: [], publications: 0, validations: 0, gates: 0,
    masterReads: 0, metadataReads: [], contents: new Map(), nextAssetId: 200, nativeResult: 'success' };
  async function candidate(rebuilt = false) {
    const build = { version: ctx.version, sourceRevision: SHA, release: true, builtAt: rebuilt ? 'rebuilt' : 'original' };
    const files = new Map([['build.json', json(build)]]);
    for (const name of ['ManaTable-Setup.exe', 'ManaTable-Windows-x64.zip', 'ManaTable-Source.zip',
      `ManaTable-${ctx.version}-win-x64-full.nupkg`, `ManaTable-${ctx.version}-win-x64-delta.nupkg`]) files.set(name, Buffer.from(name + (rebuilt ? ':rebuilt' : ':original')));
    for (const name of ['ManaTable-Setup.exe', 'ManaTable-Windows-x64.zip', 'ManaTable-Source.zip'])
      files.set(name + '.sha256', Buffer.from(hash(files.get(name)) + '  ' + name + '\r\n'));
    const fullName = `ManaTable-${ctx.version}-win-x64-full.nupkg`, deltaName = `ManaTable-${ctx.version}-win-x64-delta.nupkg`;
    files.set('installer-manifest.json', json({ version: ctx.version, sourceRevision: SHA, installerProtocol: 1, smokeOnly: false,
      architecture: 'win-x64', installationFramework: 'velopack-bridge', managedPackageId: 'ManaTable', installer: 'ManaTable-Setup.exe',
      installerSha256: hash(files.get('ManaTable-Setup.exe')).toUpperCase(), installerBytes: files.get('ManaTable-Setup.exe').length,
      portableArchiveSha256: hash(files.get('ManaTable-Windows-x64.zip')).toUpperCase(), sourceArchiveSha256: hash(files.get('ManaTable-Source.zip')).toUpperCase(),
      buildMetadataSha256: hash(files.get('build.json')).toUpperCase() }));
    files.set('releases.win-x64.json', json({ Assets: [fullName, deltaName].map((name, index) => ({ PackageId: 'ManaTable', Version: ctx.version,
      Type: index ? 'Delta' : 'Full', FileName: name, Size: files.get(name).length, SHA256: hash(files.get(name)).toUpperCase() })) }));
    files.set('update-manifest.json', json({ version: ctx.version, sourceRevision: SHA, updateProtocol: 2, framework: 'velopack',
      frameworkVersion: '1.2.161', packageId: 'ManaTable', channel: 'win-x64',
      fullPackage: { fileName: fullName, sizeBytes: files.get(fullName).length, sha256: hash(files.get(fullName)) },
      entries: [{ path: 'lib/app/build.json', sizeBytes: files.get('build.json').length, sha256: hash(files.get('build.json')) },
        { path: 'lib/app/Mana.Game.dll', sizeBytes: 10, sha256: hash(Buffer.from('game bytes')) }] }));
    for (const [name, bytes] of files) await fs.writeFile(path.join(directory, name), bytes);
  }
  await candidate();
  function publicize() {
    state.release.draft = false;
    state.release.html_url = `https://github.com/${REPO}/releases/tag/${ctx.tag}`;
    for (const asset of state.release.assets) asset.browser_download_url = `https://github.com/${REPO}/releases/download/${ctx.tag}/${asset.name}`;
  }
  const deps = {
    validate: async (actualDirectory, actualContext) => {
      assert.equal(actualDirectory, directory); assert.equal(actualContext.sha, SHA);
      state.validations++; if (state.validationError) throw new Error('Exact artifact validation failed.');
      return state.inventory || names;
    },
    gate: async () => { state.gates++; if (state.gateError) throw new Error('Required source CI failed.'); if (state.duringGate) await state.duringGate(); },
    run: async (program, args) => {
      assert.equal(program, 'gh'); assert.deepEqual(args.slice(0, 3), ['release', 'upload', ctx.tag]);
      assert.equal(state.release.draft, true, 'A public release must never reach upload.');
      const files = args.slice(3, args.indexOf('--repo'));
      assert.deepEqual(args.slice(args.indexOf('--repo')), ['--repo', REPO, '--clobber']);
      state.uploads.push(args);
      for (const file of files) {
        assert.equal(path.dirname(file), directory, 'The exact literal path is passed as a process argument.');
        const name = path.basename(file), bytes = await fs.readFile(file), existing = state.release.assets.find(asset => asset.name === name);
        const asset = { id: existing?.id || state.nextAssetId++, name, size: bytes.length, state: 'uploaded', digest: 'sha256:' + hash(bytes) };
        state.contents.set(asset.id, bytes);
        state.release.assets = state.release.assets.filter(item => item.name !== name).concat(asset);
        if (state.uploadError) return { code: 1, stdout: Buffer.alloc(0), stderr: Buffer.from('controlled upload failure') };
      }
      return { code: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
    },
    api: async (resource, options = {}) => {
      state.calls.push({ resource, method: options.method || 'GET', input: options.input });
      if (resource.endsWith('/git/ref/heads/master')) {
        state.masterReads++; return { object: { sha: state.staleAt && state.masterReads >= state.staleAt ? OTHER : SHA } };
      }
      if (resource.endsWith('/git/ref/tags/' + ctx.tag)) return state.wrongTag ? { object: { type: 'commit', sha: OTHER } }
        : state.release && !state.release.draft ? { object: { type: 'commit', sha: SHA } } : null;
      if (resource.endsWith(`/actions/runs/${ctx.runId}`)) return { id: ctx.runId, run_number: ctx.runNumber, run_attempt: Number(env.GITHUB_RUN_ATTEMPT),
        head_sha: SHA, head_branch: 'master', event: env.GITHUB_EVENT_NAME, repository: { full_name: REPO }, path: '.github/workflows/mana-table-native-release.yml',
        status: 'in_progress', ...state.runMutation };
      if (resource.endsWith('/jobs?filter=latest&per_page=100')) return { total_count: state.missingNative ? 0 : 1,
        jobs: state.missingNative ? [] : [{ name: 'Verify and package Windows x64', run_id: ctx.runId, head_sha: SHA, status: 'completed', conclusion: state.nativeResult }] };
      if (resource.endsWith('/releases') && options.method === 'POST') {
        assert.equal(options.input.draft, true); assert.equal(options.input.target_commitish, SHA);
        for (const text of [`releases/download/${ctx.tag}/ManaTable-Setup.exe`, 'Java, .NET', 'same version', 'decks and settings are preserved', 'extract the whole folder'])
          assert.ok(options.input.body.includes(text), 'Retain the friend-facing download and update instructions.');
        state.release = { id: 67, ...options.input, assets: [] }; return clone(state.release);
      }
      const listing = resource.match(/\/releases\?per_page=100&page=(\d+)$/);
      if (listing) return state.listPages ? state.listPages(Number(listing[1])) : state.release ? [clone(state.release)] : [];
      const assetId = resource.match(/\/releases\/assets\/(\d+)$/);
      if (assetId) {
        assert.equal(options.raw, true); const id = Number(assetId[1]); state.metadataReads.push(id);
        return state.contents.get(id);
      }
      if (resource.endsWith('/releases/tags/' + ctx.tag)) return clone(state.release);
      if (resource.endsWith('/releases/67')) {
        if (options.method === 'PATCH') {
          assert.deepEqual(options.input, { draft: false, make_latest: 'true' });
          state.publications++; publicize();
        }
        if (state.releaseReadMutation) state.releaseReadMutation();
        return clone(state.release);
      }
      throw new Error('Unexpected release request: ' + resource);
    },
  };
  const args = { env, directory, receiptPath, deps };
  async function receipt() { return JSON.parse(await fs.readFile(receiptPath, 'utf8')); }
  async function tamperReceipt(change) { const value = await receipt(); change(value); await fs.writeFile(receiptPath, JSON.stringify(value)); }
  async function tamperMetadata(name, change, updateReceipt = false) {
    const asset = state.release.assets.find(item => item.name === name), value = JSON.parse(state.contents.get(asset.id));
    change(value); const bytes = json(value); state.contents.set(asset.id, bytes); asset.size = bytes.length; asset.digest = 'sha256:' + hash(bytes);
    if (updateReceipt) await tamperReceipt(value => Object.assign(value.assets.find(item => item.name === name), { sizeBytes: bytes.length, sha256: hash(bytes) }));
  }
  return { args, ctx, state, receipt, tamperReceipt, tamperMetadata, candidate, publicize };
}

test('stage validates local production files, uploads only a private draft and binds a small exact-run receipt', async t => {
  const f = await fixture(t), result = await stage(f.args), receipt = await f.receipt();
  assert.equal(result.state, 'staged'); assert.equal(f.state.release.draft, true); assert.equal(f.state.publications, 0);
  assert.equal(f.state.validations, 1); assert.equal(f.state.gates, 0); assert.equal(f.state.uploads.length, 1);
  assert.equal(receipt.workflowRunId, f.ctx.runId); assert.equal(receipt.sourceRevision, SHA); assert.equal(receipt.releaseId, 67);
  assert.equal(receipt.assets.length, 11); assert.ok(Buffer.byteLength(JSON.stringify(receipt)) < 4096);
  for (const item of receipt.assets) assert.equal(item.sha256, f.state.release.assets.find(asset => asset.name === item.name).digest.slice(7));
});
test('PR, fork, nonmaster and malformed run contexts fail before any API call or local validation', async t => {
  for (const mutation of [{ GITHUB_EVENT_NAME: 'pull_request' }, { GITHUB_REPOSITORY: 'else/repo' }, { GITHUB_REF: 'refs/heads/feature' },
    { GITHUB_SHA: 'short' }, { GITHUB_RUN_ID: '0123' }, { GITHUB_RUN_NUMBER: 'NaN' }, { GITHUB_RUN_ATTEMPT: '' }]) {
    const f = await fixture(t);
    await assert.rejects(stage({ ...f.args, env: { ...f.args.env, ...mutation } }));
    assert.equal(f.state.calls.length, 0); assert.equal(f.state.validations, 0);
  }
});
test('wrong source/run/workflow provenance cannot create a draft', async t => {
  for (const mutation of [{ head_sha: OTHER }, { id: 99 }, { run_number: 90 }, { run_attempt: 2 }, { event: 'pull_request' },
    { repository: { full_name: 'other/repo' } }, { path: '.github/workflows/other.yml' }, { status: 'completed', conclusion: 'failure' }]) {
    const f = await fixture(t); f.state.runMutation = mutation;
    await assert.rejects(stage(f.args), /provenance/); assert.equal(f.state.release, null);
  }
});
test('stale master before validation, before upload or after upload leaves no promotion receipt', async t => {
  for (const staleAt of [1, 3, 4]) {
    const f = await fixture(t); f.state.staleAt = staleAt;
    await assert.rejects(stage(f.args), /Master advanced/);
    assert.equal(f.state.publications, 0); assert.ok(!f.state.release || f.state.release.draft);
    assert.equal(f.state.uploads.length, staleAt === 4 ? 1 : 0);
    await assert.rejects(fs.stat(f.args.receiptPath), { code: 'ENOENT' });
  }
});
test('artifact-validator failure and invalid inventories cannot upload', async t => {
  for (const mode of ['validator', 'extra', 'missing', 'duplicate']) {
    const f = await fixture(t), names = requiredNames(f.ctx);
    if (mode === 'validator') f.state.validationError = true;
    else f.state.inventory = mode === 'extra' ? names.concat('../unexpected.exe') : mode === 'missing' ? names.slice(1) : names.concat(names[0]);
    await assert.rejects(stage(f.args)); assert.equal(f.state.uploads.length, 0); assert.equal(f.state.release, null);
  }
});
test('partial uploads stay private and do not emit a promotable receipt', async t => {
  const f = await fixture(t); f.state.uploadError = true;
  await assert.rejects(stage(f.args), /upload failed/); assert.equal(f.state.release.draft, true);
  await assert.rejects(promote(f.args), { code: 'ENOENT' }); assert.equal(f.state.publications, 0);
});
test('unexpected assets in an existing draft are rejected rather than silently deleted', async t => {
  const f = await fixture(t); await stage(f.args);
  f.state.release.assets.push({ id: 999, name: 'unexpected.exe', size: 10, state: 'uploaded', digest: 'sha256:' + '0'.repeat(64) });
  await assert.rejects(stage(f.args), /unexpected/); assert.equal(f.state.uploads.length, 1);
});
test('draft discovery uses authenticated listings before a Git tag exists and refuses duplicate tags or capped pages', async t => {
  const f = await fixture(t); await stage(f.args);
  assert.equal(await f.args.deps.api(`/repos/${REPO}/git/ref/tags/${f.ctx.tag}`), null);
  const creates = f.state.calls.filter(call => call.method === 'POST').length;
  await stage(f.args);
  assert.equal(f.state.calls.filter(call => call.method === 'POST').length, creates, 'A tagless private draft is reused.');
  assert.ok(f.state.calls.some(call => call.resource.includes('/releases?per_page=100&page=1')));
  assert.ok(f.state.calls.every(call => !call.resource.includes('/releases/tags/')));
  f.state.listPages = () => [clone(f.state.release), { ...clone(f.state.release), id: 68 }];
  await assert.rejects(stage(f.args), /Multiple releases/);
  f.state.listPages = page => Array.from({ length: 100 }, (_, index) => ({ id: page * 100 + index, tag_name: 'unrelated' }));
  await assert.rejects(stage(f.args), /exceeded its bound/);
  assert.equal(f.state.calls.filter(call => call.method === 'POST').length, creates);
});
test('final promotion checks prerequisites and actual native aggregate, reads only metadata and publishes the same draft', async t => {
  const f = await fixture(t); await stage(f.args); f.state.metadataReads = [];
  const result = await promote(f.args);
  assert.equal(result.state, 'published'); assert.equal(f.state.gates, 1); assert.equal(f.state.publications, 1);
  assert.equal(f.state.uploads.length, 1, 'No large files are uploaded or redownloaded at promotion.');
  const allowed = new Set(f.state.release.assets.filter(asset => asset.name.endsWith('.json')).map(asset => asset.id));
  assert.ok(f.state.metadataReads.every(id => allowed.has(id)));
  assert.equal(result.releaseId, (await f.receipt()).releaseId);
});
test('tampered receipt identity, run, build and asset declarations fail closed', async t => {
  for (const change of [r => r.repository = 'other/repo', r => r.sourceRevision = OTHER, r => r.version = '0.2.90', r => r.tag = 'other',
    r => r.workflowRunId++, r => r.workflowRunNumber++, r => r.stagedRunAttempt++, r => r.releaseId++, r => r.buildMetadataSha256 = '0'.repeat(64),
    r => r.assets[0].sizeBytes++, r => r.assets[0].sha256 = '0'.repeat(64), r => r.assets[0].name = 'another.exe', r => r.assets.push(r.assets[0])]) {
    const f = await fixture(t); await stage(f.args); await f.tamperReceipt(change);
    await assert.rejects(promote(f.args)); assert.equal(f.state.publications, 0); assert.equal(f.state.release.draft, true);
  }
});
test('missing, extra, duplicate, incomplete or changed remote assets cannot be promoted', async t => {
  for (const change of [r => r.assets.pop(), r => r.assets.push({ ...r.assets[0], name: 'extra.exe' }), r => r.assets.push(r.assets[0]),
    r => delete r.assets[0].digest, r => r.assets[0].digest = 'sha256:' + '0'.repeat(64), r => r.assets[0].size++,
    r => r.assets[0].state = 'new', r => r.target_commitish = OTHER, r => r.tag_name = 'wrong']) {
    const f = await fixture(t); await stage(f.args); change(f.state.release);
    await assert.rejects(promote(f.args)); assert.equal(f.state.publications, 0);
  }
});
test('coherently rehashed receipts cannot hide wrong manifest source, build or package identity', async t => {
  for (const [name, change] of [['installer-manifest.json', r => r.sourceRevision = OTHER],
    ['installer-manifest.json', r => r.buildMetadataSha256 = '0'.repeat(64)], ['releases.win-x64.json', r => r.Assets[0].Version = '0.2.90'],
    ['update-manifest.json', r => r.sourceRevision = OTHER], ['update-manifest.json', r => r.entries[0].sha256 = '0'.repeat(64)]]) {
    const f = await fixture(t); await stage(f.args); await f.tamperMetadata(name, change, true);
    await assert.rejects(promote(f.args)); assert.equal(f.state.publications, 0);
  }
});
test('failed prerequisites or missing/failed/skipped native aggregate retain the private draft', async t => {
  for (const mode of ['source-failure', 'missing-native', 'failure', 'skipped', 'cancelled']) {
    const f = await fixture(t); await stage(f.args);
    if (mode === 'source-failure') f.state.gateError = true;
    else if (mode === 'missing-native') f.state.missingNative = true;
    else f.state.nativeResult = mode;
    await assert.rejects(promote(f.args)); assert.equal(f.state.release.draft, true); assert.equal(f.state.publications, 0);
  }
});
test('master advancement or asset mutation while source checks are pending prevents publication', async t => {
  for (const mode of ['master', 'asset']) {
    const f = await fixture(t); await stage(f.args);
    f.state.duringGate = () => { if (mode === 'master') f.state.staleAt = f.state.masterReads + 1; else f.state.release.assets.pop(); };
    await assert.rejects(promote(f.args)); assert.equal(f.state.publications, 0);
  }
});
test('a wrong existing tag blocks stage and final promotion', async t => {
  const f = await fixture(t); f.state.wrongTag = true;
  await assert.rejects(stage(f.args), /tag points/); assert.equal(f.state.release, null);
  f.state.wrongTag = false; await stage(f.args); f.state.wrongTag = true;
  await assert.rejects(promote(f.args), /tag points/); assert.equal(f.state.publications, 0);
});
test('a release becoming public before upload is never clobbered', async t => {
  const f = await fixture(t); f.state.releaseReadMutation = () => f.publicize();
  await assert.rejects(stage(f.args), /became public/); assert.equal(f.state.uploads.length, 0);
});
test('published reruns verify and preserve original bytes even when local rebuilds differ', async t => {
  const f = await fixture(t); await stage(f.args); await promote(f.args);
  const original = clone(f.state.release.assets), originalBuild = (await f.receipt()).buildMetadataSha256;
  await f.candidate(true);
  const result = await stage(f.args);
  assert.equal(result.state, 'already-published'); assert.deepEqual(f.state.release.assets, original);
  assert.equal(f.state.uploads.length, 1); assert.equal((await f.receipt()).buildMetadataSha256, originalBuild);
  assert.equal((await promote(f.args)).state, 'already-published'); assert.equal(f.state.publications, 1);
});
test('a successful stage from an earlier attempt can finish a failed-job rerun of the same source/run', async t => {
  const f = await fixture(t); await stage(f.args); f.args.env.GITHUB_RUN_ATTEMPT = '2';
  assert.equal((await promote(f.args)).state, 'published'); assert.equal(f.state.publications, 1);
});
test('GitHub JSON writes use stdin and structured arguments, never shell-interpolated content', async () => {
  const calls = [], api = githubApi(async (program, args, options) => {
    calls.push({ program, args, options }); return { code: 0, stdout: json({ id: 7 }), stderr: Buffer.alloc(0) };
  });
  const body = { body: 'literal `command` $(do-not-run)\nsecond line', draft: true };
  await api(`/repos/${REPO}/releases`, { method: 'POST', input: body });
  assert.equal(calls[0].program, 'gh'); assert.deepEqual(calls[0].args.slice(-2), ['--input', '-']);
  assert.deepEqual(JSON.parse(calls[0].options.input), body);
  await assert.rejects(api('/repos/other/repo/releases'));
});
test('helper CLI rejects ambiguous flags and promotion with full-download inputs', async () => {
  await assert.rejects(main(['stage', '--receipt', 'a', '--receipt', 'b']));
  await assert.rejects(main(['promote', '--directory', 'downloads', '--receipt', 'a']));
  await assert.rejects(main(['publish']));
});
