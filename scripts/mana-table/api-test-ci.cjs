const fs = require('node:fs');
const path = require('node:path');
const { compareMode } = require('./compare-api-test-isolation.cjs');
const { inventories, ownedRoot, source, readManifest, checkpoint, mark, modeTimings,
  requireMeasuredProcesses, writeNew, validateJdk } = require('./api-test-trial.cjs');

const repoRoot = path.resolve(__dirname, '../..');
const AREA = 'api-test-runs';

function prepare({ repository = repoRoot, root, trialId, sourceRevision, jdk, trialProfile = 'false' }) {
  root = path.resolve(root);
  if (trialProfile !== 'false') throw new Error('api-ci and api-test-trial profiles are mutually exclusive.');
  ownedRoot(repository, root, trialId, AREA);
  validateJdk(jdk);
  source(repository, sourceRevision);
  const inventory = inventories(repository);
  fs.mkdirSync(path.dirname(root), { recursive: true });
  fs.mkdirSync(root); // A run never deletes or reuses previous evidence.
  fs.mkdirSync(path.join(root, 'timings'));
  const manifest = { schemaVersion: 1, kind: 'api-ci', trialId, sourceRevision, jdk, ...inventory,
    executionOrder: ['parallel'], createdAtEpochMs: Date.now() };
  writeNew(path.join(root, 'manifest.json'), manifest);
  mark(root, 'parallel', 'start', manifest);
  return manifest;
}

function certify(root, manifest) {
  const accounting = compareMode({ root });
  const timings = modeTimings(root, manifest, ['parallel']);
  const names = fs.readdirSync(path.join(root, 'timings')).sort();
  if (JSON.stringify(names) !== JSON.stringify(['parallel-end.json', 'parallel-start.json']))
    throw new Error('API CI requires exactly its own start and end measurement.');
  requireMeasuredProcesses(accounting, timings);
  // TestNG supplies complete-class invocation discovery, as in ordinary serial Surefire.
  // Schedule equivalence is measured separately by api-test-trial, not inferred here.
  return { ...accounting, timings, invocationAuthority: 'complete TestNG classes with exact journal/receipt accounting' };
}

function finish({ repository = repoRoot, root }) {
  root = path.resolve(root);
  const manifest = readManifest(root);
  checkpoint(repository, root, manifest, AREA);
  mark(root, 'parallel', 'end', manifest);
  const accounting = certify(root, manifest);
  writeNew(path.join(root, 'accounting.json'), accounting);
  return accounting;
}

/** Always preserve a small truthful status, including when Surefire prevented the finalizer. */
function retain({ repository = repoRoot, root, trialId, sourceRevision, mavenOutcome }) {
  root = path.resolve(root);
  ownedRoot(repository, root, trialId, AREA);
  if (!/^[a-f0-9]{40}$/.test(sourceRevision) || !['success', 'failure', 'cancelled', 'skipped'].includes(mavenOutcome))
    throw new Error('A source-bound Maven step outcome is required.');
  const result = { schemaVersion: 1, trialId, sourceRevision, mavenOutcome, accountingVerified: false };
  try {
    const manifest = readManifest(root);
    if (manifest.trialId !== trialId || manifest.sourceRevision !== sourceRevision)
      throw new Error('The retained manifest belongs to another run or source.');
    source(repository, sourceRevision);
    // The test-phase finalizer already checked compiled inputs at the end of this mode.
    // An extended reactor can compile additional modules after that owned boundary.
    const actual = certify(root, manifest);
    const file = path.join(root, 'accounting.json'), stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1 || stat.size > 4 * 1024 * 1024
        || JSON.stringify(JSON.parse(fs.readFileSync(file, 'utf8'))) !== JSON.stringify(actual))
      throw new Error('The test-phase accounting finalizer did not retain the validated result.');
    result.accountingVerified = true;
    result.classes = actual.classes; result.invocations = actual.invocations;
    result.overlap = actual.overlap;
  } catch (error) {
    result.error = error.message;
  }
  // Even preparation/upstream failures get compact evidence, without claiming a completed API run.
  fs.mkdirSync(root, { recursive: true });
  writeNew(path.join(root, 'retention.json'), result);
  if (mavenOutcome === 'success' && !result.accountingVerified)
    throw new Error('Successful Maven execution has no valid complete API accounting: ' + result.error);
  return result;
}

function parse(args) {
  const [command, ...rest] = args;
  if (!['prepare', 'finish', 'retain'].includes(command)) throw new Error('Expected prepare, finish or retain.');
  const allowed = command === 'prepare'
    ? ['root', 'trial-id', 'source-revision', 'jdk-runtime', 'jdk-vendor', 'jdk-vm', 'trial-profile']
    : command === 'retain' ? ['root', 'trial-id', 'source-revision', 'maven-outcome'] : ['root'];
  const values = {};
  for (let i = 0; i < rest.length; i += 2) {
    const key = rest[i].slice(2);
    if (!rest[i].startsWith('--') || !allowed.includes(key) || Object.hasOwn(values, key)
        || !rest[i + 1] || rest[i + 1].startsWith('--')) throw new Error('Unknown, duplicate or incomplete API CI argument.');
    values[key] = rest[i + 1];
  }
  if (allowed.some(key => !values[key])) throw new Error('Missing required API CI argument.');
  return { command, values };
}

if (require.main === module) {
  try {
    const { command, values: v } = parse(process.argv.slice(2));
    const result = command === 'prepare'
      ? prepare({ root: v.root, trialId: v['trial-id'], sourceRevision: v['source-revision'], trialProfile: v['trial-profile'],
        jdk: { runtimeVersion: v['jdk-runtime'], vendor: v['jdk-vendor'], vmName: v['jdk-vm'] } })
      : command === 'retain' ? retain({ root: v.root, trialId: v['trial-id'], sourceRevision: v['source-revision'], mavenOutcome: v['maven-outcome'] })
        : finish({ root: v.root });
    console.log(JSON.stringify(result));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { prepare, finish, retain, parse };
