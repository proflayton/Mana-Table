const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { createReadStream } = require('node:fs');
const { execFile } = require('node:child_process');
const { main: waitForSourceCI } = require('./wait-release-ci.cjs');
const { githubRead } = require('./github-read.cjs');

const REPO = 'proflayton/Mana-Table';
const ROOT = path.resolve(__dirname, '../..');
const WORKFLOW = '.github/workflows/mana-table-native-release.yml';
const HEX = /^[a-f0-9]{64}$/;
function assert(value, message) { if (!value) throw new Error(message); }
function object(value) { return value && typeof value === 'object' && !Array.isArray(value); }
function positive(value) { return Number.isSafeInteger(value) && value > 0; }
function number(value) {
  assert(/^[1-9][0-9]*$/.test(value || '') && positive(Number(value)), 'Missing canonical workflow run identity.');
  return Number(value);
}
function context(env) {
  assert(env.GITHUB_REPOSITORY === REPO && env.GITHUB_REF === 'refs/heads/master'
    && ['push', 'workflow_dispatch'].includes(env.GITHUB_EVENT_NAME), 'Release writes require a trusted master push or dispatch.');
  assert(/^[a-f0-9]{40}$/.test(env.GITHUB_SHA || ''), 'A full exact source revision is required.');
  const runNumber = number(env.GITHUB_RUN_NUMBER);
  assert(runNumber <= 2147483647, 'Release version exceeds the native version bound.');
  return { repo: REPO, sha: env.GITHUB_SHA, event: env.GITHUB_EVENT_NAME,
    runId: number(env.GITHUB_RUN_ID), runNumber, attempt: number(env.GITHUB_RUN_ATTEMPT),
    version: '0.2.' + runNumber, tag: 'mana-native-v0.2.' + runNumber };
}
function requiredNames(ctx) {
  return ['ManaTable-Setup.exe', 'ManaTable-Windows-x64.zip', 'ManaTable-Source.zip'].flatMap(name => [name, name + '.sha256'])
    .concat(['installer-manifest.json', 'releases.win-x64.json', 'update-manifest.json', `ManaTable-${ctx.version}-win-x64-full.nupkg`]);
}
function validateInventory(names, ctx) {
  assert(Array.isArray(names) && names.every(name => typeof name === 'string')
    && new Set(names).size === names.length, 'Release inventory is malformed or duplicated.');
  const required = requiredNames(ctx), delta = `ManaTable-${ctx.version}-win-x64-delta.nupkg`;
  assert(required.every(name => names.includes(name)) && names.every(name => required.includes(name) || name === delta)
    && names.length === required.length + Number(names.includes(delta)), 'Release inventory has missing or extra assets.');
}
async function runCommand(program, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = execFile(program, args, { cwd: options.cwd || ROOT, encoding: 'buffer', windowsHide: true,
      timeout: 15 * 60 * 1000, maxBuffer: options.maximumBytes || 4 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error && (typeof error.code !== 'number' || error.killed)) return reject(new Error(`${program} could not complete: ${error.message}`));
      resolve({ code: error?.code || 0, stdout, stderr });
    });
    child.stdin.on('error', () => {});
    child.stdin.end(options.input || '');
  });
}
function githubApi(run, { token = process.env.GH_TOKEN, fetchImpl = fetch, now = Date.now,
  sleep, signalTimeout, report } = {}) {
  let read;
  return async (resource, options = {}) => {
    assert(resource.startsWith('/repos/' + REPO + '/'), 'Unexpected release API repository.');
    if ((options.method || 'GET') === 'GET' && !options.raw && options.input === undefined) {
      read ||= githubRead({ token, repo: REPO, userAgent: 'ManaTable-release-assets',
        fetchImpl, now, sleep, signalTimeout, report });
      try {
        // Each lookup owns one deadline, including its retries. Uploads and the
        // source-CI wait do not consume a later lookup's budget.
        return await read(resource, { deadline: now() + 90000 });
      } catch (error) {
        if (options.allowNotFound && error.status === 404) return null;
        throw error;
      }
    }
    // Preserve exact sidecar bytes and one-shot write/upload ownership.
    const args = ['api', resource];
    if (options.method) args.push('--method', options.method);
    if (options.raw) args.push('--header', 'Accept: application/octet-stream');
    if (options.input !== undefined) args.push('--input', '-');
    const result = await run('gh', args, { input: options.input === undefined ? '' : JSON.stringify(options.input), maximumBytes: options.maximumBytes });
    if (result.code !== 0) {
      let status;
      try { status = JSON.parse(result.stdout.toString('utf8')).status; } catch {}
      if (options.allowNotFound && (Number(status) === 404 || /HTTP 404/.test(result.stderr.toString('utf8')))) return null;
      throw new Error('GitHub release request failed: ' + resource);
    }
    return options.raw ? result.stdout : JSON.parse(result.stdout.toString('utf8'));
  };
}
function services(deps = {}) {
  const run = deps.run || runCommand;
  return { run, api: deps.api || githubApi(run), gate: deps.gate || waitForSourceCI, validate: deps.validate || (async (directory, ctx) => {
    const result = await run('pwsh', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
      path.join(ROOT, 'mana-native/ci/validate-update-artifacts.ps1'), '-Directory', directory,
      '-Version', ctx.version, '-SourceRevision', ctx.sha], { maximumBytes: 64 * 1024 });
    assert(result.code === 0, 'The exact production artifact validator rejected this stage.');
    return result.stdout.toString('utf8').trim().split(/\r?\n/);
  }) };
}
async function assertMaster(ctx, api) {
  const master = await api(`/repos/${REPO}/git/ref/heads/master`);
  assert(master.object?.sha === ctx.sha, 'Master advanced; the release remains unpublished.');
}
async function assertRun(ctx, api) {
  const run = await api(`/repos/${REPO}/actions/runs/${ctx.runId}`);
  assert(run.id === ctx.runId && run.run_number === ctx.runNumber && run.run_attempt === ctx.attempt
    && run.head_sha === ctx.sha && run.head_branch === 'master' && run.event === ctx.event
    && run.repository?.full_name === REPO && run.path?.split('@')[0] === WORKFLOW
    && (run.status === 'in_progress' || run.status === 'completed' && run.conclusion === 'success'),
  'Workflow provenance does not match this release operation. ' + JSON.stringify({
    expected: { id: ctx.runId, run_number: ctx.runNumber, run_attempt: ctx.attempt, head_sha: ctx.sha,
      head_branch: 'master', event: ctx.event, repository: REPO, path: WORKFLOW,
      status: 'in_progress or completed/success' },
    observed: {
      id: positive(run.id) ? run.id : '<invalid>',
      run_number: positive(run.run_number) ? run.run_number : '<invalid>',
      run_attempt: positive(run.run_attempt) ? run.run_attempt : '<invalid>',
      head_sha: typeof run.head_sha === 'string' && /^[a-f0-9]{40}$/.test(run.head_sha) ? run.head_sha : '<invalid>',
      head_branch: run.head_branch === 'master' ? 'master' : '<non-master>',
      event: ['push', 'workflow_dispatch', 'pull_request', 'pull_request_target', 'workflow_run'].includes(run.event) ? run.event : '<other>',
      repository: run.repository?.full_name === REPO ? REPO : '<other>',
      path: typeof run.path === 'string' && run.path.split('@')[0] === WORKFLOW ? WORKFLOW : '<other>',
      status: ['queued', 'requested', 'waiting', 'pending', 'in_progress', 'completed'].includes(run.status) ? run.status : '<other>',
      conclusion: run.conclusion === null ? null : ['success', 'failure', 'neutral', 'cancelled', 'skipped', 'timed_out',
        'action_required', 'stale', 'startup_failure'].includes(run.conclusion) ? run.conclusion : '<other>',
    },
  }));
}
async function assertNativeGate(ctx, api) {
  const response = await api(`/repos/${REPO}/actions/runs/${ctx.runId}/jobs?filter=latest&per_page=100`);
  assert(Array.isArray(response.jobs) && response.total_count === response.jobs.length && response.total_count < 100,
    'Native gate evidence is incomplete.');
  const gates = response.jobs.filter(job => job.name === 'Verify and package Windows x64');
  assert(gates.length === 1 && gates[0].run_id === ctx.runId && gates[0].head_sha === ctx.sha
    && gates[0].status === 'completed' && gates[0].conclusion === 'success', 'The exact-source native aggregate has not passed.');
}
function releaseIdentity(release, ctx) {
  assert(object(release) && positive(release.id) && release.tag_name === ctx.tag && release.target_commitish === ctx.sha
    && typeof release.draft === 'boolean' && release.prerelease === false, 'Release source, tag or identity does not match.');
  if (!release.draft) assert(release.html_url === `https://github.com/${REPO}/releases/tag/${ctx.tag}`, 'Published release URL is not canonical.');
}
async function assertTag(ctx, api, required = false) {
  const tag = await api(`/repos/${REPO}/git/ref/tags/${ctx.tag}`, { allowNotFound: true });
  assert(!tag && !required || tag?.object?.type === 'commit' && tag.object.sha === ctx.sha, 'Release tag points at another source.');
}
async function findRelease(ctx, api) {
  // GET releases/tags/{tag} is documented for published releases. An
  // authenticated listing also includes drafts before their Git tag exists.
  let selected = null;
  const seen = new Set();
  for (let page = 1; page <= 10; page++) {
    const releases = await api(`/repos/${REPO}/releases?per_page=100&page=${page}`);
    assert(Array.isArray(releases) && releases.length <= 100, 'Release discovery is malformed.');
    for (const release of releases) {
      assert(object(release) && positive(release.id) && !seen.has(release.id), 'Release discovery is duplicated or incomplete.');
      seen.add(release.id);
      if (release.tag_name === ctx.tag) { assert(!selected, 'Multiple releases use the staging tag.'); selected = release; }
    }
    if (releases.length < 100) return selected;
  }
  throw new Error('Release discovery exceeded its bound; refusing to create an ambiguous draft.');
}
function digest(asset) {
  assert(typeof asset.digest === 'string' && /^sha256:[a-f0-9]{64}$/.test(asset.digest), 'A release asset is missing its GitHub SHA256 digest.');
  return asset.digest.slice(7);
}
function remoteAssets(release, ctx) {
  assert(Array.isArray(release.assets), 'Release assets are missing.');
  validateInventory(release.assets.map(asset => asset.name), ctx);
  for (const asset of release.assets) {
    assert(positive(asset.id) && positive(asset.size) && asset.size <= 750 * 1024 * 1024
      && asset.state === 'uploaded', 'Release asset identity or upload state is invalid.');
    digest(asset);
    if (!release.draft) assert(asset.browser_download_url === `https://github.com/${REPO}/releases/download/${ctx.tag}/${asset.name}`, 'Published asset URL is not canonical.');
  }
  return new Map(release.assets.map(asset => [asset.name, asset]));
}
async function readMetadata(asset, api, maximum) {
  assert(asset.size <= maximum, 'Release metadata exceeds its size bound.');
  const bytes = await api(`/repos/${REPO}/releases/assets/${asset.id}`, { raw: true, maximumBytes: maximum });
  assert(Buffer.isBuffer(bytes) && bytes.length === asset.size
    && crypto.createHash('sha256').update(bytes).digest('hex') === digest(asset), 'Remote metadata differs from its GitHub digest.');
  return JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
}
async function inspectRelease(release, ctx, api, receipt) {
  releaseIdentity(release, ctx);
  const assets = remoteAssets(release, ctx);
  if (receipt) {
    assert(release.id === receipt.releaseId && assets.size === receipt.assets.length, 'Receipt belongs to another release inventory.');
    for (const entry of receipt.assets) {
      const actual = assets.get(entry.name);
      assert(actual && actual.size === entry.sizeBytes && digest(actual) === entry.sha256, 'A release asset differs from the staged receipt: ' + entry.name);
    }
  }
  // Only small metadata is read here. Large ZIP/nupkg payloads were verified by
  // the production PowerShell validator, then bound by their GitHub digests.
  const [installer, index, contract] = await Promise.all([
    readMetadata(assets.get('installer-manifest.json'), api, 64 * 1024),
    readMetadata(assets.get('releases.win-x64.json'), api, 512 * 1024),
    readMetadata(assets.get('update-manifest.json'), api, 4 * 1024 * 1024),
  ]);
  assert(installer.installerProtocol === 1 && installer.smokeOnly === false && installer.architecture === 'win-x64'
    && installer.installationFramework === 'velopack-bridge' && installer.managedPackageId === 'ManaTable'
    && installer.installer === 'ManaTable-Setup.exe' && installer.version === ctx.version && installer.sourceRevision === ctx.sha
    && typeof installer.buildMetadataSha256 === 'string' && HEX.test(installer.buildMetadataSha256.toLowerCase()), 'Remote installer manifest is not this production build.');
  const buildMetadataSha256 = installer.buildMetadataSha256.toLowerCase();
  for (const [name, hash, size] of [['ManaTable-Setup.exe', installer.installerSha256, installer.installerBytes],
    ['ManaTable-Windows-x64.zip', installer.portableArchiveSha256], ['ManaTable-Source.zip', installer.sourceArchiveSha256]]) {
    const asset = assets.get(name);
    assert(typeof hash === 'string' && hash.toLowerCase() === digest(asset) && (size === undefined || size === asset.size), 'Remote installer input differs from the staged downloads.');
  }
  assert(Array.isArray(index.Assets) && index.Assets.length >= 1 && index.Assets.length <= 2, 'Managed update index is malformed.');
  let full, delta;
  for (const item of index.Assets) {
    assert(item.PackageId === 'ManaTable' && item.Version === ctx.version && ['Full', 'Delta'].includes(item.Type)
      && item.FileName === `ManaTable-${ctx.version}-win-x64-${item.Type.toLowerCase()}.nupkg`, 'Managed update index belongs to another identity.');
    const asset = assets.get(item.FileName);
    assert(asset && asset.size === item.Size && typeof item.SHA256 === 'string' && item.SHA256.toLowerCase() === digest(asset), 'Managed update index does not bind the actual package.');
    if (item.Type === 'Full') { assert(!full, 'Duplicate full package.'); full = item; }
    else { assert(!delta, 'Duplicate delta package.'); delta = item; }
  }
  assert(full && Boolean(delta) === assets.has(`ManaTable-${ctx.version}-win-x64-delta.nupkg`), 'Managed update inventory is incomplete.');
  assert(contract.updateProtocol === 2 && contract.framework === 'velopack' && contract.frameworkVersion === '1.2.161'
    && contract.packageId === 'ManaTable' && contract.channel === 'win-x64' && contract.version === ctx.version
    && contract.sourceRevision === ctx.sha && contract.fullPackage?.fileName === full.FileName
    && contract.fullPackage?.sizeBytes === full.Size && contract.fullPackage?.sha256?.toLowerCase() === full.SHA256.toLowerCase()
    && Array.isArray(contract.entries) && contract.entries.length > 0, 'Remote update contract belongs to another source or package.');
  const entries = new Map();
  for (const entry of contract.entries) {
    assert(typeof entry.path === 'string' && entry.path.length > 0 && !/(^|\/)\.\.?($|\/)|[\\:]|^\/|\/$/.test(entry.path)
      && !entries.has(entry.path.toLowerCase()) && typeof entry.sha256 === 'string' && HEX.test(entry.sha256.toLowerCase())
      && Number.isSafeInteger(entry.sizeBytes) && entry.sizeBytes >= 0, 'Remote update content contract is malformed.');
    entries.set(entry.path.toLowerCase(), entry);
  }
  assert(entries.get('lib/app/build.json')?.sha256?.toLowerCase() === buildMetadataSha256
    && entries.has('lib/app/mana.game.dll'), 'Remote update payload does not bind its build and game assembly.');
  if (receipt) assert(receipt.buildMetadataSha256 === buildMetadataSha256, 'Receipt build metadata has changed.');
  return { buildMetadataSha256,
    assets: [...assets.values()].map(asset => ({ name: asset.name, sizeBytes: asset.size, sha256: digest(asset) })).sort((a, b) => a.name.localeCompare(b.name)) };
}
function validateReceipt(receipt, ctx) {
  assert(object(receipt) && receipt.schemaVersion === 1 && receipt.repository === REPO && receipt.sourceRevision === ctx.sha
    && receipt.version === ctx.version && receipt.tag === ctx.tag && receipt.workflowRunId === ctx.runId
    && receipt.workflowRunNumber === ctx.runNumber && positive(receipt.stagedRunAttempt) && receipt.stagedRunAttempt <= ctx.attempt
    && positive(receipt.releaseId) && HEX.test(receipt.buildMetadataSha256 || '') && Array.isArray(receipt.assets), 'Staging receipt identity is invalid.');
  validateInventory(receipt.assets.map(asset => asset?.name), ctx);
  for (const asset of receipt.assets) assert(object(asset) && positive(asset.sizeBytes) && asset.sizeBytes <= 750 * 1024 * 1024
    && HEX.test(asset.sha256 || ''), 'Staging receipt asset is malformed.');
  return receipt;
}
async function hashFile(file) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}
async function writeReceipt(file, value) {
  await fs.mkdir(path.dirname(file), { recursive: true });
  const temporary = file + '.' + crypto.randomUUID() + '.tmp';
  try { await fs.writeFile(temporary, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 }); await fs.rename(temporary, file); }
  finally { await fs.rm(temporary, { force: true }); }
}
function receiptFor(ctx, releaseId, content) {
  return { schemaVersion: 1, repository: REPO, sourceRevision: ctx.sha, version: ctx.version, tag: ctx.tag,
    workflowRunId: ctx.runId, workflowRunNumber: ctx.runNumber, stagedRunAttempt: ctx.attempt, releaseId, ...content };
}
async function stage({ env = process.env, directory, receiptPath, deps }) {
  const ctx = context(env), svc = services(deps);
  assert(directory && receiptPath, 'Stage requires a downloads directory and receipt path.');
  directory = path.resolve(directory); receiptPath = path.resolve(receiptPath);
  await assertMaster(ctx, svc.api); await assertRun(ctx, svc.api); await assertTag(ctx, svc.api);
  const names = await svc.validate(directory, ctx);
  validateInventory(names, ctx);
  const buildBytes = await fs.readFile(path.join(directory, 'build.json'));
  const build = JSON.parse(buildBytes.toString('utf8').replace(/^\uFEFF/, ''));
  assert(build.version === ctx.version && build.sourceRevision === ctx.sha && build.release === true && !build.smokeOnly, 'Staging build metadata belongs to another candidate.');
  const assets = [];
  for (const name of names) {
    const file = path.join(directory, name), stat = await fs.lstat(file);
    assert(stat.isFile() && !stat.isSymbolicLink() && positive(stat.size) && stat.size <= 750 * 1024 * 1024, 'Staging input is not a bounded regular file.');
    assets.push({ name, sizeBytes: stat.size, sha256: await hashFile(file) });
  }
  const local = { buildMetadataSha256: crypto.createHash('sha256').update(buildBytes).digest('hex'), assets: assets.sort((a, b) => a.name.localeCompare(b.name)) };
  let release = await findRelease(ctx, svc.api);
  if (release) {
    releaseIdentity(release, ctx);
    if (!release.draft) {
      await assertTag(ctx, svc.api, true);
      const content = await inspectRelease(release, ctx, svc.api);
      await writeReceipt(receiptPath, receiptFor(ctx, release.id, content));
      return { state: 'already-published', releaseId: release.id, receiptPath };
    }
    assert(Array.isArray(release.assets) && release.assets.every(asset => names.includes(asset.name))
      && new Set(release.assets.map(asset => asset.name)).size === release.assets.length, 'Existing draft has unexpected or duplicate assets.');
  }
  await assertMaster(ctx, svc.api); await assertRun(ctx, svc.api);
  if (!release) release = await svc.api(`/repos/${REPO}/releases`, { method: 'POST', input: {
    tag_name: ctx.tag, target_commitish: ctx.sha, draft: true, prerelease: false, name: `Mana Table ${ctx.version} for Windows`,
    body: `**Windows players:** download [ManaTable-Setup.exe](https://github.com/${REPO}/releases/download/${ctx.tag}/ManaTable-Setup.exe), open it, and follow the installer. Java, .NET and the card library are included. Start Mana Table from the desktop or Start menu.\n\nEveryone at a multiplayer table should use the same version. Close Mana Table before updating; your decks and settings are preserved. The portable ZIP is also available below: extract the whole folder and open ManaTable.exe.\n\nVersion ${ctx.version} · [Source](https://github.com/${REPO}/commit/${ctx.sha}) · [Verified build](https://github.com/${REPO}/actions/runs/${ctx.runId})\n`,
  } });
  releaseIdentity(release, ctx);
  const beforeUpload = await svc.api(`/repos/${REPO}/releases/${release.id}`);
  releaseIdentity(beforeUpload, ctx);
  assert(beforeUpload.draft, 'The release became public; its assets will not be overwritten.');
  await assertMaster(ctx, svc.api);
  // Master release runs are serialized by the workflow. Promotion depends on
  // this completed stage and its immutable receipt; published releases never
  // reach this upload path. A failed stage leaves only a private draft.
  const uploaded = await svc.run('gh', ['release', 'upload', ctx.tag, ...names.map(name => path.join(directory, name)), '--repo', REPO, '--clobber']);
  assert(uploaded.code === 0, 'Draft upload failed; publication remains blocked.');
  await assertMaster(ctx, svc.api); await assertRun(ctx, svc.api);
  const staged = await svc.api(`/repos/${REPO}/releases/${release.id}`);
  assert(staged.draft, 'The staged release was published before its verification receipt existed.');
  await inspectRelease(staged, ctx, svc.api, receiptFor(ctx, release.id, local));
  await writeReceipt(receiptPath, receiptFor(ctx, release.id, local));
  return { state: 'staged', releaseId: release.id, receiptPath };
}
async function promote({ env = process.env, receiptPath, deps }) {
  const ctx = context(env), svc = services(deps);
  assert(receiptPath, 'Promotion requires a staging receipt.');
  const bytes = await fs.readFile(receiptPath);
  assert(bytes.length > 0 && bytes.length <= 64 * 1024, 'Staging receipt exceeds its size bound.');
  const receipt = validateReceipt(JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, '')), ctx);
  await assertMaster(ctx, svc.api); await assertRun(ctx, svc.api);
  let release = await svc.api(`/repos/${REPO}/releases/${receipt.releaseId}`);
  releaseIdentity(release, ctx);
  if (!release.draft) {
    await inspectRelease(release, ctx, svc.api, receipt); await assertTag(ctx, svc.api, true);
    return { state: 'already-published', releaseId: release.id };
  }
  // Keep all exact-master Java/desktop/applicable-iOS prerequisites. The caller
  // also needs the successful Windows aggregate before this job can run.
  await svc.gate(env);
  await assertRun(ctx, svc.api);
  await assertNativeGate(ctx, svc.api);
  release = await svc.api(`/repos/${REPO}/releases/${receipt.releaseId}`);
  await inspectRelease(release, ctx, svc.api, receipt); await assertTag(ctx, svc.api, !release.draft);
  await assertMaster(ctx, svc.api);
  if (!release.draft) return { state: 'already-published', releaseId: release.id };
  const published = await svc.api(`/repos/${REPO}/releases/${receipt.releaseId}`, { method: 'PATCH', input: { draft: false, make_latest: 'true' } });
  assert(published.draft === false, 'GitHub did not acknowledge public publication.');
  await inspectRelease(published, ctx, svc.api, receipt); await assertTag(ctx, svc.api, true);
  return { state: 'published', releaseId: published.id };
}
async function main(args = process.argv.slice(2), env = process.env) {
  const [operation, ...options] = args;
  assert(['stage', 'promote'].includes(operation), 'Use stage --directory <downloads> --receipt <file> or promote --receipt <file>.');
  const parsed = {};
  for (let index = 0; index < options.length; index += 2) {
    const key = options[index];
    assert(['--directory', '--receipt'].includes(key) && options[index + 1] && !parsed[key], 'Invalid or duplicate release helper option.');
    parsed[key] = options[index + 1];
  }
  assert(operation === 'stage' || !parsed['--directory'], 'Promotion never consumes full release downloads.');
  const result = await ({ stage, promote }[operation])({ env, directory: parsed['--directory'], receiptPath: parsed['--receipt'] });
  console.log(JSON.stringify(result));
  return result;
}
module.exports = { REPO, context, requiredNames, validateReceipt, githubApi, stage, promote, main };
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
