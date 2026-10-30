const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const HEX = /^[a-f0-9]{64}$/;
const CLASS = /^[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*$/;
const MODE_FORK_COUNTS = { serial: 1, parallel: 2 };
const BASE = ['schemaVersion', 'trialId', 'mode', 'forkId', 'pid', 'sourceRevision',
  'testClassSetSha256', 'jdk', 'workspace', 'startedAtEpochMs'];
const IDENTITY = ['className', 'methodName', 'parameterTypes', 'parameterSha256', 'status'];

function requireFact(value, message) { if (!value) throw new Error(message); }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function integer(value, minimum = 0) { return Number.isSafeInteger(value) && value >= minimum; }
function hash(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex'); }
function text(value, maximum = 512) {
  return typeof value === 'string' && value.length > 0 && value.length <= maximum && !/[\0\r\n]/.test(value);
}
function same(left, right) { return JSON.stringify(left) === JSON.stringify(right); }
function fields(value, names) { return Object.fromEntries(names.map(name => [name, value[name]])); }
function keys(value, names) { return object(value) && same(Object.keys(value).sort(), [...names].sort()); }
function directory(file) {
  const stat = fs.lstatSync(file);
  requireFact(stat.isDirectory() && !stat.isSymbolicLink(), 'Workspace and report directories must be real owned directories.');
  return fs.realpathSync(file);
}
function regular(file, maximum = 4 * 1024 * 1024) {
  const stat = fs.lstatSync(file);
  requireFact(stat.isFile() && !stat.isSymbolicLink() && stat.nlink === 1 && stat.size > 0 && stat.size <= maximum,
    'Evidence must be a bounded, nonempty, unlinked regular file.');
  return fs.readFileSync(file);
}
function json(file) { return JSON.parse(regular(file).toString('utf8')); }
function contained(root, file) {
  const relative = path.relative(root, file);
  requireFact(relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative),
    'Evidence must stay inside its trial workspace.');
}
function entries(file, expected) {
  const actual = fs.readdirSync(file).sort();
  requireFact(same(actual, [...expected].sort()), 'Missing, foreign or duplicate fork/report ownership.');
}
function validateJdk(jdk) {
  requireFact(object(jdk) && same(Object.keys(jdk).sort(), ['runtimeVersion', 'vendor', 'vmName'].sort())
    && Object.values(jdk).every(value => text(value, 256)), 'A complete exact JDK identity is required.');
}
function jdkKey(jdk) { return JSON.stringify(fields(jdk, ['runtimeVersion', 'vendor', 'vmName'])); }
function interval(value) {
  requireFact(integer(value.startedAtEpochMs, 1) && integer(value.completedAtEpochMs, value.startedAtEpochMs),
    'A completed execution interval is required.');
}
function invocation(value) {
  requireFact(object(value) && typeof value.className === 'string' && CLASS.test(value.className) && text(value.methodName, 256)
    && Array.isArray(value.parameterTypes) && value.parameterTypes.length <= 128
    && value.parameterTypes.every(type => text(type, 512)) && HEX.test(value.parameterSha256)
    && ['passed', 'failed', 'skipped'].includes(value.status), 'Malformed invocation identity or result.');
  return JSON.stringify(fields(value, IDENTITY));
}
function add(counts, key, count) {
  const total = (counts.get(key) || 0) + count;
  requireFact(integer(total, 1) && total <= 1_000_000, 'Invocation counts exceed the evidence bound.');
  counts.set(key, total);
}
function totals(counts) { return [...counts.values()].reduce((total, count) => total + count, 0); }
function sorted(counts) { return [...counts].sort(([left], [right]) => left.localeCompare(right)); }

function reports(root, mode, forkId, ownedClasses) {
  const owner = path.join(root, 'reports', mode, 'fork-' + forkId);
  directory(owner); contained(root, fs.realpathSync(owner));
  const found = [], mirrors = path.join(owner, 'junitreports');
  let rootReports = 0;
  let inspected = 0;
  function visit(folder, depth) {
    requireFact(depth < 32, 'Diagnostic report nesting exceeds its bound.');
    for (const name of fs.readdirSync(folder)) {
      requireFact(++inspected <= 10_000, 'Diagnostic report inventory exceeds its bound.');
      const file = path.join(folder, name), stat = fs.lstatSync(file);
      requireFact(!stat.isSymbolicLink(), 'Diagnostic reports cannot use linked files or directories.');
      if (stat.isDirectory()) visit(file, depth + 1);
      else {
        requireFact(stat.isFile() && stat.nlink === 1, 'Diagnostic reports must be owned regular files.');
        if (/^TEST-.*\.xml$/.test(name)) {
          const direct = folder === owner;
          const mirror = folder === mirrors && ownedClasses.has(name.slice(5, -4));
          requireFact((direct || mirror) && stat.size > 0 && stat.size <= 32 * 1024 * 1024,
            'Foreign, duplicate or empty TEST XML report.');
          if (direct) rootReports++;
          found.push(path.relative(root, file).split(path.sep).join('/'));
        }
      }
    }
  }
  visit(owner, 0);
  requireFact(rootReports > 0, 'Each actual fork requires its own root Surefire TEST XML diagnostic report.');
  return found.sort();
}

function readFork(root, manifest, mode, forkId, knownPids, knownNonces) {
  const fork = path.join(root, mode, 'fork-' + forkId);
  directory(fork);
  const processes = fs.readdirSync(fork);
  requireFact(processes.length === 1, 'Each fork must retain exactly one fresh process, without orphan or overwritten receipts.');
  const match = /^process-([1-9]\d*)-([A-Za-z0-9_-]{8,80})$/.exec(processes[0]);
  requireFact(match, 'A fresh process-owned workspace is required.');
  const workspace = directory(path.join(fork, processes[0])); contained(root, workspace);
  for (const name of fs.readdirSync(workspace)) {
    const file = path.join(workspace, name), stat = fs.lstatSync(file);
    requireFact(!stat.isSymbolicLink() && (stat.isDirectory() ? ['bootstrap', 'profiles', 'evidence'].includes(name)
      : ['process-start.json', 'invocations.jsonl', 'receipt.json'].includes(name)), 'Foreign process receipt or private workspace entry.');
  }
  requireFact(!knownNonces.has(match[2]), 'Process workspaces cannot reuse a fresh nonce.'); knownNonces.add(match[2]);
  const startBytes = regular(path.join(workspace, 'process-start.json'));
  const journalBytes = regular(path.join(workspace, 'invocations.jsonl'), 32 * 1024 * 1024);
  const start = JSON.parse(startBytes.toString('utf8')), receipt = json(path.join(workspace, 'receipt.json'));
  requireFact(object(start) && object(receipt) && start.completed === false && receipt.completed === true,
    'A started and completed process receipt is required.');
  requireFact(keys(start, [...BASE, 'completed']) && keys(receipt, [...BASE, 'completed', 'completedAtEpochMs',
    'elapsedNanos', 'bootstrapNanos', 'heapPoolPeakUsedBytes', 'executedClasses', 'invocations', 'summary', 'files']),
    'Unexpected process receipt schema.');
  requireFact(receipt.schemaVersion === 1 && receipt.trialId === manifest.trialId && receipt.mode === mode
    && receipt.forkId === forkId && integer(receipt.pid, 1) && String(receipt.pid) === match[1]
    && !knownPids.has(receipt.pid), 'Wrong, duplicate or overwritten process identity.');
  knownPids.add(receipt.pid);
  requireFact(receipt.sourceRevision === manifest.sourceRevision && receipt.testClassSetSha256 === manifest.testClassSetSha256,
    'Process source or compiled test class inventory changed.');
  validateJdk(receipt.jdk);
  requireFact(jdkKey(receipt.jdk) === jdkKey(manifest.jdk), 'Process JDK identity differs from the expected trial.');
  requireFact(typeof receipt.workspace === 'string' && path.isAbsolute(receipt.workspace)
    && !receipt.workspace.split(/[\\/]/).some(part => part === '.' || part === '..')
    && directory(receipt.workspace) === workspace && path.resolve(receipt.workspace) === workspace,
    'Process receipt belongs to a foreign workspace.');
  requireFact(same(fields(start, BASE.filter(key => key !== 'jdk')), fields(receipt, BASE.filter(key => key !== 'jdk')))
    && object(start.jdk) && jdkKey(start.jdk) === jdkKey(receipt.jdk), 'Process start and final receipt identities differ.');
  requireFact(keys(receipt.files, ['startSha256', 'journalSha256']) && receipt.files.startSha256 === hash(startBytes)
    && receipt.files.journalSha256 === hash(journalBytes), 'Process start or invocation journal bytes changed.');
  interval(receipt);
  requireFact(receipt.startedAtEpochMs >= manifest.createdAtEpochMs && integer(receipt.elapsedNanos, 1)
    && integer(receipt.bootstrapNanos) && object(receipt.heapPoolPeakUsedBytes)
    && Object.keys(receipt.heapPoolPeakUsedBytes).length > 0 && Object.keys(receipt.heapPoolPeakUsedBytes).length <= 64
    && Object.entries(receipt.heapPoolPeakUsedBytes).every(([name, bytes]) => text(name, 256) && integer(bytes)),
    'Process timing, freshness or heap telemetry is incomplete.');
  requireFact(Array.isArray(receipt.executedClasses) && receipt.executedClasses.length > 0
    && receipt.executedClasses.length <= 10_000, 'Every actual process must execute selected test classes.');
  const classes = new Map();
  for (const value of receipt.executedClasses) {
    requireFact(object(value) && typeof value.name === 'string' && manifest.selectedClassNames.includes(value.name) && HEX.test(value.sha256)
      && !classes.has(value.name), 'Foreign or duplicate executed class.');
    interval(value);
    requireFact(value.startedAtEpochMs >= receipt.startedAtEpochMs && value.completedAtEpochMs <= receipt.completedAtEpochMs,
      'Class interval escapes its completed process.');
    classes.set(value.name, value);
  }
  const counts = new Map(), observed = new Map(), testedClasses = new Set();
  const summary = { passed: 0, failed: 0, skipped: 0, configurationFailed: 0, configurationSkipped: 0 };
  const lines = journalBytes.toString('utf8').split(/\r?\n/);
  requireFact(lines.pop() === '' && lines.length > 0 && lines.length <= 1_000_000, 'A complete bounded invocation journal is required.');
  for (const line of lines) {
    const row = JSON.parse(line), key = invocation(row);
    requireFact(['test', 'configuration'].includes(row.kind) && row.count === 1 && integer(row.durationMillis),
      'Malformed invocation journal result.');
    interval(row);
    requireFact(row.startedAtEpochMs >= receipt.startedAtEpochMs && row.completedAtEpochMs <= receipt.completedAtEpochMs,
      'Invocation interval escapes its completed process.');
    if (row.kind === 'test') {
      const owner = classes.get(row.className);
      requireFact(owner && row.startedAtEpochMs >= owner.startedAtEpochMs && row.completedAtEpochMs <= owner.completedAtEpochMs,
        'A test invocation must belong to its actual class interval.');
      add(observed, key, 1); testedClasses.add(row.className); summary[row.status]++;
    } else if (row.status === 'failed') summary.configurationFailed++;
    else if (row.status === 'skipped') summary.configurationSkipped++;
  }
  requireFact(Array.isArray(receipt.invocations) && receipt.invocations.length > 0 && receipt.invocations.length <= 100_000,
    'Completed test invocation accounting is required.');
  for (const row of receipt.invocations) {
    const key = invocation(row);
    requireFact(keys(row, [...IDENTITY, 'count']) && integer(row.count, 1) && !counts.has(key), 'Duplicate or malformed aggregated invocation.');
    add(counts, key, row.count);
  }
  requireFact(same(sorted(counts), sorted(observed)), 'Final invocation multiset differs from its retained journal.');
  requireFact(classes.size === testedClasses.size, 'Every executed class must retain an actual test invocation.');
  requireFact(same(fields(receipt.summary || {}, Object.keys(summary)), summary), 'Final result totals differ from the invocation journal.');
  requireFact(summary.passed > 0 && summary.failed === 0 && summary.skipped === 0
    && summary.configurationFailed === 0 && summary.configurationSkipped === 0,
    'Failed, skipped or configuration-failed executions cannot certify a trial.');
  return { counts, classes, metrics: { forkId, pid: receipt.pid, workspace,
    startedAtEpochMs: receipt.startedAtEpochMs, completedAtEpochMs: receipt.completedAtEpochMs,
    elapsedNanos: receipt.elapsedNanos, bootstrapNanos: receipt.bootstrapNanos,
    heapPoolPeakUsedBytes: receipt.heapPoolPeakUsedBytes, reports: reports(root, mode, forkId, classes) } };
}

function compareTrial({ root, manifestPath = path.join(root, 'manifest.json') }) {
  root = directory(path.resolve(root));
  manifestPath = path.resolve(manifestPath); contained(root, manifestPath);
  requireFact(manifestPath === path.join(root, 'manifest.json'), 'The trial must use its own manifest.json.');
  const allowedRoot = ['manifest.json', 'timings', 'comparison.json', 'reports', 'serial', 'parallel'];
  for (const name of fs.readdirSync(root)) {
    const stat = fs.lstatSync(path.join(root, name));
    requireFact(allowedRoot.includes(name) && !stat.isSymbolicLink()
      && (['manifest.json', 'comparison.json'].includes(name) ? stat.isFile() : stat.isDirectory()), 'Foreign root evidence or mode.');
  }
  const manifest = json(manifestPath);
  requireFact(object(manifest) && manifest.schemaVersion === 1 && text(manifest.trialId, 128)
    && /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(manifest.trialId)
    && /^[a-f0-9]{40}$/.test(manifest.sourceRevision) && HEX.test(manifest.testClassSetSha256)
    && HEX.test(manifest.compiledClassSetSha256) && integer(manifest.createdAtEpochMs, 1)
    && Array.isArray(manifest.executionOrder) && same([...manifest.executionOrder].sort(), ['parallel', 'serial']),
    'A complete source-bound trial manifest is required.');
  validateJdk(manifest.jdk);
  requireFact(Array.isArray(manifest.selectedClassNames) && manifest.selectedClassNames.length > 0
    && manifest.selectedClassNames.length <= 10_000 && manifest.selectedClassNames.every(name => typeof name === 'string' && CLASS.test(name))
    && new Set(manifest.selectedClassNames).size === manifest.selectedClassNames.length, 'A unique selected test class inventory is required.');
  const modes = {}, knownPids = new Set(), knownNonces = new Set();
  directory(path.join(root, 'reports'));
  entries(path.join(root, 'reports'), Object.keys(MODE_FORK_COUNTS));
  for (const [mode, forkCount] of Object.entries(MODE_FORK_COUNTS)) {
    directory(path.join(root, mode)); directory(path.join(root, 'reports', mode));
    const owners = fs.readdirSync(path.join(root, mode));
    requireFact(owners.length === forkCount, 'Missing, foreign or duplicate fork/report ownership.');
    const forkIds = owners.map(name => {
      const label = /^fork-([1-9][0-9]{0,8})$/.exec(name);
      requireFact(label, 'Fork owners require canonical numeric labels.');
      return Number(label[1]);
    }).sort((left, right) => left - right);
    entries(path.join(root, 'reports', mode), forkIds.map(id => 'fork-' + id));
    const forks = forkIds.map(id => readFork(root, manifest, mode, id, knownPids, knownNonces));
    const counts = new Map(), classes = new Map();
    for (const fork of forks) {
      for (const [key, count] of fork.counts) add(counts, key, count);
      for (const [name, value] of fork.classes) {
        requireFact(!classes.has(name), 'A test class cannot be owned by two forks in one mode.');
        classes.set(name, value);
      }
    }
    requireFact(same([...classes.keys()].sort(), [...manifest.selectedClassNames].sort()), 'Selected test classes are missing or foreign.');
    modes[mode] = { counts, classes, forks };
  }
  requireFact(same(sorted(modes.serial.counts), sorted(modes.parallel.counts)), 'Serial and parallel invocation multisets differ.');
  for (const [name, value] of modes.serial.classes)
    requireFact(modes.parallel.classes.get(name).sha256 === value.sha256, 'Executed class bytes differ between modes.');
  let overlap;
  for (const left of modes.parallel.forks[0].classes.values()) {
    for (const right of modes.parallel.forks[1].classes.values()) {
      const start = Math.max(left.startedAtEpochMs, right.startedAtEpochMs), end = Math.min(left.completedAtEpochMs, right.completedAtEpochMs);
      if (start < end) { overlap = { classes: [left.name, right.name], startedAtEpochMs: start, completedAtEpochMs: end }; break; }
    }
    if (overlap) break;
  }
  requireFact(overlap, 'Two actual processes must demonstrate overlapping class execution.');
  return { schemaVersion: 1, equivalent: true, trialId: manifest.trialId, sourceRevision: manifest.sourceRevision,
    testClassSetSha256: manifest.testClassSetSha256, compiledClassSetSha256: manifest.compiledClassSetSha256,
    jdk: manifest.jdk, executionOrder: manifest.executionOrder, classes: manifest.selectedClassNames.length,
    invocations: totals(modes.serial.counts), overlap,
    serial: { forks: modes.serial.forks.map(fork => fork.metrics) },
    parallel: { forks: modes.parallel.forks.map(fork => fork.metrics) } };
}

if (require.main === module) {
  try {
    const [root, manifestPath] = process.argv.slice(2);
    requireFact(root && process.argv.length <= 4, 'Use compare-api-test-isolation.cjs <trial-root> [manifest-path].');
    console.log(JSON.stringify(compareTrial({ root, manifestPath })));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { compareTrial };
