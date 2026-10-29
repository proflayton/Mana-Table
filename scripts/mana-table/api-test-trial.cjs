const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');

const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const repoRoot = path.resolve(__dirname, '../..');
const MODES = ['serial', 'parallel'];
function writeNew(file, value) { fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n', { flag: 'wx' }); }
function directory(file) {
  if (!fs.lstatSync(file).isDirectory() || fs.lstatSync(file).isSymbolicLink()) throw new Error(`Expected a real directory: ${file}`);
}
function contained(parent, file) {
  const relative = path.relative(parent, file);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Trial path must stay inside its owned directory.');
}
function classFiles(root, allowEmpty = false) {
  directory(root);
  const result = [];
  function visit(folder) {
    for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
      const file = path.join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Compiled inputs cannot contain symbolic links.');
      if (entry.isDirectory()) visit(file);
      else if (entry.isFile() && entry.name.endsWith('.class'))
        result.push({ path: path.relative(root, file).split(path.sep).join('/'), sha256: sha256(fs.readFileSync(file)) });
    }
  }
  visit(root);
  if (!allowEmpty && !result.length) throw new Error(`Compiled classes are missing: ${root}`);
  return result.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function inventoryHash(files) { return sha256(files.map(file => `${file.path}\0${file.sha256}\n`).join('')); }
function inventories(repository) {
  const testFiles = classFiles(path.join(repository, 'forge-api/target/test-classes'));
  for (const module of ['forge-core', 'forge-game', 'forge-ai', 'forge-gui', 'forge-api'])
    classFiles(path.join(repository, module, 'target/classes'));
  // Bind every compiled reactor module, including helpers and upstream tests.
  const all = [];
  for (const module of fs.readdirSync(repository, { withFileTypes: true }).filter(entry => entry.isDirectory() && entry.name.startsWith('forge-'))) {
    for (const kind of ['classes', 'test-classes']) {
      const root = path.join(repository, module.name, 'target', kind);
      if (fs.existsSync(root)) for (const file of classFiles(root, true)) all.push({ path: `${module.name}/target/${kind}/${file.path}`, sha256: file.sha256 });
    }
  }
  all.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  const selectedClassNames = testFiles.filter(file => !file.path.includes('$') && /^(?:Test.*|.*Tests?|.*TestCase)\.class$/.test(path.posix.basename(file.path)))
    .map(file => file.path.slice(0, -6).replaceAll('/', '.'));
  if (!selectedClassNames.length) throw new Error('Default Surefire class selection is empty.');
  return { testClassSetSha256: inventoryHash(testFiles), compiledClassSetSha256: inventoryHash(all), selectedClassNames };
}
function source(repository, expected) {
  if (!/^[0-9a-f]{40}$/.test(expected)) throw new Error('Use the full source revision for the trial.');
  const git = args => execFileSync('git', args, { cwd: repository, encoding: 'utf8', windowsHide: true }).trim();
  if (git(['rev-parse', 'HEAD']) !== expected || git(['status', '--porcelain=v1', '--untracked-files=normal']))
    throw new Error('The trial requires the exact committed source and a clean worktree.');
}
function trialRoot(repository, root, trialId) {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/.test(trialId) || ['.', '..'].includes(trialId)) throw new Error('Invalid trial identity.');
  const base = path.join(repository, 'forge-api/target/api-test-trials');
  contained(base, root);
  if (path.basename(root) !== trialId || path.dirname(root) !== base) throw new Error('Trial root must match its identity directly below api-test-trials.');
  let ancestor = root;
  while (ancestor !== repository) {
    if (fs.existsSync(ancestor) && fs.lstatSync(ancestor).isSymbolicLink()) throw new Error('Trial ancestry cannot contain a symbolic link or junction.');
    ancestor = path.dirname(ancestor);
  }
}
function readManifest(root) {
  directory(root);
  const file = path.join(root, 'manifest.json');
  if (!fs.lstatSync(file).isFile() || fs.lstatSync(file).isSymbolicLink()) throw new Error('Missing regular trial manifest.');
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}
function checkpoint(repository, root, manifest) {
  trialRoot(repository, root, manifest.trialId);
  source(repository, manifest.sourceRevision);
  const actual = inventories(repository);
  if (actual.testClassSetSha256 !== manifest.testClassSetSha256 || actual.compiledClassSetSha256 !== manifest.compiledClassSetSha256)
    throw new Error('Compiled classes changed during the trial.');
  return actual;
}
function mark(root, mode, event, manifest) {
  if (!MODES.includes(mode) || !['start', 'end'].includes(event)) throw new Error('Unknown trial timing boundary.');
  writeNew(path.join(root, 'timings', `${mode}-${event}.json`), {
    schemaVersion: 1, trialId: manifest.trialId, sourceRevision: manifest.sourceRevision,
    testClassSetSha256: manifest.testClassSetSha256, compiledClassSetSha256: manifest.compiledClassSetSha256, mode, event, epochMs: Date.now()
  });
}
function prepare({ repository = repoRoot, root, trialId, sourceRevision, jdk, firstMode = 'serial', secondMode = 'parallel' }) {
  root = path.resolve(root);
  trialRoot(repository, root, trialId);
  if (firstMode === secondMode || !MODES.includes(firstMode) || !MODES.includes(secondMode)) throw new Error('Execute exactly the serial and parallel modes.');
  for (const key of ['runtimeVersion', 'vendor', 'vmName'])
    if (typeof jdk?.[key] !== 'string' || !jdk[key].trim() || /[\r\n]/.test(jdk[key])) throw new Error('Complete exact JDK identity is required.');
  source(repository, sourceRevision);
  const inventory = inventories(repository);
  fs.mkdirSync(path.dirname(root), { recursive: true });
  directory(path.dirname(root));
  fs.mkdirSync(root); // Never reuse or clean a previous trial's evidence.
  fs.mkdirSync(path.join(root, 'timings'));
  const manifest = { schemaVersion: 1, trialId, sourceRevision, jdk, ...inventory, executionOrder: [firstMode, secondMode], createdAtEpochMs: Date.now() };
  writeNew(path.join(root, 'manifest.json'), manifest);
  mark(root, firstMode, 'start', manifest);
  return manifest;
}
function boundary({ repository = repoRoot, root, mode, event }) {
  root = path.resolve(root);
  const manifest = readManifest(root);
  checkpoint(repository, root, manifest);
  const [first, second] = manifest.executionOrder;
  if (event === 'end' && mode === first) {
    if (!fs.existsSync(path.join(root, 'timings', `${first}-start.json`))) throw new Error('First mode did not start.');
  } else if (event === 'start' && mode === second) {
    if (!fs.existsSync(path.join(root, 'timings', `${first}-end.json`))) throw new Error('First mode did not finish.');
  } else throw new Error('Unexpected trial execution order.');
  mark(root, mode, event, manifest);
}
function timings(root, manifest) {
  const result = {};
  for (const mode of MODES) {
    const records = ['start', 'end'].map(event => {
      const file = path.join(root, 'timings', `${mode}-${event}.json`);
      if (!fs.lstatSync(file).isFile() || fs.lstatSync(file).isSymbolicLink()) throw new Error('Timing evidence must be a regular owned file.');
      const record = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (record.schemaVersion !== 1 || record.trialId !== manifest.trialId || record.sourceRevision !== manifest.sourceRevision ||
          record.testClassSetSha256 !== manifest.testClassSetSha256 || record.compiledClassSetSha256 !== manifest.compiledClassSetSha256 ||
          record.mode !== mode || record.event !== event || !Number.isSafeInteger(record.epochMs))
        throw new Error('Timing evidence belongs to another trial.');
      return record;
    });
    if (records[1].epochMs < records[0].epochMs) throw new Error('Trial clock moved backwards.');
    result[mode] = { startedAtEpochMs: records[0].epochMs, completedAtEpochMs: records[1].epochMs, wallSeconds: (records[1].epochMs - records[0].epochMs) / 1000 };
  }
  if (result[manifest.executionOrder[0]].completedAtEpochMs > result[manifest.executionOrder[1]].startedAtEpochMs) throw new Error('Serial control and trial windows overlap.');
  return result;
}
function requireMeasuredProcesses(comparison, measured) {
  for (const mode of MODES) for (const fork of comparison[mode].forks)
    if (fork.startedAtEpochMs < measured[mode].startedAtEpochMs || fork.completedAtEpochMs > measured[mode].completedAtEpochMs)
      throw new Error('A test JVM executed outside its measured mode window.');
}
function finish({ repository = repoRoot, root }) {
  root = path.resolve(root);
  const manifest = readManifest(root);
  checkpoint(repository, root, manifest);
  mark(root, manifest.executionOrder[1], 'end', manifest);
  const comparison = require('./compare-api-test-isolation.cjs').compareTrial({ root, manifestPath: path.join(root, 'manifest.json') });
  const measured = timings(root, manifest);
  requireMeasuredProcesses(comparison, measured);
  const result = { ...comparison, executionOrder: manifest.executionOrder, timings: measured,
    parallelFaster: measured.parallel.wallSeconds < measured.serial.wallSeconds,
    speedup: measured.serial.wallSeconds / measured.parallel.wallSeconds,
    adoption: 'pending: compare both JDKs and a reverse-order confirmation before changing normal CI' };
  writeNew(path.join(root, 'comparison.json'), result);
  return result;
}
function parse(args) {
  const [command, ...rest] = args;
  if (!['prepare', 'boundary', 'finish'].includes(command)) throw new Error('Expected prepare, boundary or finish.');
  const allowed = command === 'prepare' ? ['root', 'trial-id', 'source-revision', 'jdk-runtime', 'jdk-vendor', 'jdk-vm', 'first-mode', 'second-mode'] : command === 'boundary' ? ['root', 'mode', 'event'] : ['root'];
  const values = {};
  for (let i = 0; i < rest.length; i += 2) {
    const key = rest[i].slice(2);
    if (!rest[i].startsWith('--') || !allowed.includes(key) || Object.hasOwn(values, key) || !rest[i + 1] || rest[i + 1].startsWith('--')) throw new Error('Unknown, duplicate or incomplete trial argument.');
    values[key] = rest[i + 1];
  }
  if (allowed.some(key => !values[key])) throw new Error('Missing required trial argument.');
  return { command, values };
}
if (require.main === module) {
  try {
    const { command, values: v } = parse(process.argv.slice(2));
    const result = command === 'prepare' ? prepare({ root: v.root, trialId: v['trial-id'], sourceRevision: v['source-revision'],
      jdk: { runtimeVersion: v['jdk-runtime'], vendor: v['jdk-vendor'], vmName: v['jdk-vm'] }, firstMode: v['first-mode'], secondMode: v['second-mode'] })
      : command === 'boundary' ? boundary({ root: v.root, mode: v.mode, event: v.event }) : finish({ root: v.root });
    console.log(JSON.stringify(result ?? { boundary: 'recorded' }));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { prepare, boundary, finish, inventories, inventoryHash, timings, requireMeasuredProcesses, parse };
