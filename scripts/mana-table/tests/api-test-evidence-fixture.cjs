const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { compareTrial } = require('../compare-api-test-isolation.cjs');

const digest = value => crypto.createHash('sha256').update(value).digest('hex');
const write = (file, value) => fs.writeFileSync(file, JSON.stringify(value) + '\n');
const identity = row => ({ className: row.className, methodName: row.methodName,
  parameterTypes: row.parameterTypes, parameterSha256: row.parameterSha256, status: row.status });
function persist(record) {
  const counts = new Map(), summary = { passed: 0, failed: 0, skipped: 0, configurationFailed: 0, configurationSkipped: 0 };
  for (const row of record.rows) {
    if (row.kind === 'test') {
      const key = JSON.stringify(identity(row));
      counts.set(key, (counts.get(key) || 0) + 1); summary[row.status]++;
    } else if (row.status === 'failed') summary.configurationFailed++;
    else if (row.status === 'skipped') summary.configurationSkipped++;
  }
  record.receipt.invocations = [...counts].map(([key, count]) => ({ ...JSON.parse(key), count }));
  record.receipt.summary = summary;
  write(path.join(record.workspace, 'process-start.json'), record.start);
  fs.writeFileSync(path.join(record.workspace, 'invocations.jsonl'), record.rows.map(row => JSON.stringify(row)).join('\n') + '\n');
  record.receipt.files = {
    startSha256: digest(fs.readFileSync(path.join(record.workspace, 'process-start.json'))),
    journalSha256: digest(fs.readFileSync(path.join(record.workspace, 'invocations.jsonl')))
  };
  write(path.join(record.workspace, 'receipt.json'), record.receipt);
}
function fork(root, manifest, mode, forkId, pid, classes, start, end) {
  const names = manifest.selectedClassNames;
  const workspace = path.join(root, mode, 'fork-' + forkId, `process-${pid}-${mode}-nonce-${forkId}`);
  const report = path.join(root, 'reports', mode, 'fork-' + forkId);
  fs.mkdirSync(workspace, { recursive: true }); fs.mkdirSync(report, { recursive: true });
  fs.writeFileSync(path.join(report, 'TEST-TestSuite.xml'), '<testsuite/>');
  const base = { schemaVersion: 1, trialId: manifest.trialId, mode, forkId, pid,
    sourceRevision: manifest.sourceRevision, testClassSetSha256: manifest.testClassSetSha256,
    jdk: { ...manifest.jdk }, workspace, startedAtEpochMs: start };
  const executedClasses = classes.map(([index, begin, finish]) => ({ name: names[index], sha256: manifest.selectedClasses?.find(value => value.name === names[index])?.sha256 || digest(names[index]),
    startedAtEpochMs: begin, completedAtEpochMs: finish }));
  const rows = [];
  for (const owner of executedClasses) {
    rows.push({ kind: 'configuration', className: owner.name, methodName: 'beforeClass', parameterTypes: [],
      parameterSha256: digest('configuration'), status: 'passed', count: 1, durationMillis: 0,
      startedAtEpochMs: owner.startedAtEpochMs, completedAtEpochMs: owner.startedAtEpochMs });
    const variations = owner.name === names[0] ? [['java.lang.String', 'C'], ['java.lang.String', 'C'], ['int', '1']] : [[null, 'none']];
    for (const [type, parameter] of variations) {
      rows.push({ kind: 'test', className: owner.name, methodName: 'decide', parameterTypes: type ? [type] : [],
        parameterSha256: digest(parameter), status: 'passed', count: 1, durationMillis: 1,
        startedAtEpochMs: owner.startedAtEpochMs + 1, completedAtEpochMs: owner.startedAtEpochMs + 2 });
    }
  }
  const record = { workspace, report, rows, start: { ...base, completed: false }, receipt: { ...base, completed: true,
    completedAtEpochMs: end, elapsedNanos: (end - start) * 1_000_000, bootstrapNanos: 10_000,
    heapPoolPeakUsedBytes: { 'Fixture heap': 1024 }, executedClasses } };
  persist(record); return record;
}

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-api-isolation-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const names = ['forge.api.PaymentTest', 'forge.api.ChoiceTest', 'forge.api.RevealTest'];
  const manifest = { schemaVersion: 1, trialId: 'fixture-trial', sourceRevision: 'a'.repeat(40),
    testClassSetSha256: 'b'.repeat(64), compiledClassSetSha256: 'c'.repeat(64),
    jdk: { runtimeVersion: '17.0.1+1', vendor: 'Fixture', vmName: 'Fixture VM' },
    selectedClassNames: names, executionOrder: ['serial', 'parallel'], createdAtEpochMs: 1000 };
  write(path.join(root, 'manifest.json'), manifest);
  const serial = fork(root, manifest, 'serial', 1, 101, [[0, 1200, 1400], [1, 1500, 1700], [2, 1800, 2000]], 1100, 2200);
  const left = fork(root, manifest, 'parallel', 1, 102, [[0, 3100, 3400], [1, 3450, 3900]], 3000, 4000);
  const right = fork(root, manifest, 'parallel', 2, 103, [[2, 3200, 3800]], 3020, 4100);
  return { root, manifest, records: [serial, left, right], serial, left, right, compare: () => compareTrial({ root, manifestPath: path.join(root, 'manifest.json') }) };
}
function rewriteReceipt(record) { write(path.join(record.workspace, 'receipt.json'), record.receipt); }

function addTestNgMirrors(record) {
  const mirrors = path.join(record.report, 'junitreports');
  fs.mkdirSync(mirrors);
  for (const owner of record.receipt.executedClasses)
    fs.writeFileSync(path.join(mirrors, 'TEST-' + owner.name + '.xml'), '<testsuite name="' + owner.name + '"/>');
  return mirrors;
}

function relabelFork(record, label, forkId) {
  const previous = path.dirname(record.workspace), next = path.join(path.dirname(previous), label);
  const nextReport = path.join(path.dirname(record.report), label);
  assert.equal(path.dirname(previous), path.dirname(next));
  assert.equal(path.dirname(record.report), path.dirname(nextReport));
  fs.renameSync(previous, next); fs.renameSync(record.report, nextReport);
  record.workspace = path.join(next, path.basename(record.workspace)); record.report = nextReport;
  record.start.workspace = record.receipt.workspace = record.workspace;
  record.start.forkId = record.receipt.forkId = forkId;
  persist(record);
}

function shiftExecution(record, milliseconds) {
  record.start.startedAtEpochMs += milliseconds;
  record.receipt.startedAtEpochMs += milliseconds;
  record.receipt.completedAtEpochMs += milliseconds;
  for (const value of [...record.receipt.executedClasses, ...record.rows]) {
    value.startedAtEpochMs += milliseconds; value.completedAtEpochMs += milliseconds;
  }
  persist(record);
}

module.exports = { fixture, fork, persist, digest, write, rewriteReceipt, addTestNgMirrors, relabelFork, shiftExecution };
