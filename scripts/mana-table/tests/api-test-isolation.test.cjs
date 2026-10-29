const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
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
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-api-isolation-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const names = ['forge.api.PaymentTest', 'forge.api.ChoiceTest', 'forge.api.RevealTest'];
  const manifest = { schemaVersion: 1, trialId: 'fixture-trial', sourceRevision: 'a'.repeat(40),
    testClassSetSha256: 'b'.repeat(64), compiledClassSetSha256: 'c'.repeat(64),
    jdk: { runtimeVersion: '17.0.1+1', vendor: 'Fixture', vmName: 'Fixture VM' },
    selectedClassNames: names, executionOrder: ['serial', 'parallel'], createdAtEpochMs: 1000 };
  write(path.join(root, 'manifest.json'), manifest);
  const records = [];
  function fork(mode, forkId, pid, classes, start, end) {
    const workspace = path.join(root, mode, 'fork-' + forkId, `process-${pid}-${mode}-nonce-${forkId}`);
    const report = path.join(root, 'reports', mode, 'fork-' + forkId);
    fs.mkdirSync(workspace, { recursive: true }); fs.mkdirSync(report, { recursive: true });
    fs.writeFileSync(path.join(report, 'TEST-TestSuite.xml'), '<testsuite/>');
    const base = { schemaVersion: 1, trialId: manifest.trialId, mode, forkId, pid,
      sourceRevision: manifest.sourceRevision, testClassSetSha256: manifest.testClassSetSha256,
      jdk: { ...manifest.jdk }, workspace, startedAtEpochMs: start };
    const executedClasses = classes.map(([index, begin, finish]) => ({ name: names[index], sha256: digest(names[index]),
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
    persist(record); records.push(record); return record;
  }
  const serial = fork('serial', 1, 101, [[0, 1200, 1400], [1, 1500, 1700], [2, 1800, 2000]], 1100, 2200);
  const left = fork('parallel', 1, 102, [[0, 3100, 3400], [1, 3450, 3900]], 3000, 4000);
  const right = fork('parallel', 2, 103, [[2, 3200, 3800]], 3020, 4100);
  return { root, manifest, records, serial, left, right, compare: () => compareTrial({ root, manifestPath: path.join(root, 'manifest.json') }) };
}
function rewriteReceipt(record) { write(path.join(record.workspace, 'receipt.json'), record.receipt); }

function addTestNgMirrors(record) {
  const mirrors = path.join(record.report, 'junitreports');
  fs.mkdirSync(mirrors);
  for (const owner of record.receipt.executedClasses)
    fs.writeFileSync(path.join(mirrors, 'TEST-' + owner.name + '.xml'), '<testsuite name="' + owner.name + '"/>');
  return mirrors;
}

test('matches exact dynamic invocation multisets and independently owned overlapping processes', t => {
  const f = fixture(t), result = f.compare();
  assert.equal(result.equivalent, true); assert.equal(result.invocations, 5); assert.equal(result.classes, 3);
  assert.equal(result.serial.forks.length, 1); assert.equal(result.parallel.forks.length, 2);
  assert.ok(result.overlap.completedAtEpochMs > result.overlap.startedAtEpochMs);
  assert.equal(result.compiledClassSetSha256, f.manifest.compiledClassSetSha256);
  assert.equal(result.parallel.forks[0].heapPoolPeakUsedBytes['Fixture heap'], 1024);
  for (const record of [f.serial, f.left]) {
    const repeated = record.rows.find(row => row.kind === 'test');
    record.rows.push({ ...repeated }, { ...repeated }); persist(record);
  }
  assert.equal(f.compare().invocations, 7, 'No permanent expected invocation count is embedded in the comparator.');
});

test('each JDK trial binds its own compiled bytes rather than requiring Java 17 and 21 hashes to match', t => {
  const f = fixture(t);
  f.manifest.jdk.runtimeVersion = '21.0.1+1'; f.manifest.testClassSetSha256 = digest('JDK21 compiled tests');
  f.manifest.compiledClassSetSha256 = digest('JDK21 reactor'); write(path.join(f.root, 'manifest.json'), f.manifest);
  for (const record of f.records) {
    record.start.jdk = { ...f.manifest.jdk }; record.receipt.jdk = { ...f.manifest.jdk };
    record.start.testClassSetSha256 = record.receipt.testClassSetSha256 = f.manifest.testClassSetSha256;
    for (const owner of record.receipt.executedClasses) owner.sha256 = digest('21:' + owner.name);
    persist(record);
  }
  assert.equal(f.compare().jdk.runtimeVersion, '21.0.1+1');
});

test('retains real Surefire root reports and owned one-level TestNG junitreports mirrors as separate diagnostics', t => {
  const f = fixture(t);
  for (const record of f.records) {
    if (record.receipt.mode === 'parallel') {
      fs.unlinkSync(path.join(record.report, 'TEST-TestSuite.xml'));
      for (const owner of record.receipt.executedClasses)
        fs.writeFileSync(path.join(record.report, 'TEST-' + owner.name + '.xml'), '<testsuite><testcase/></testsuite>');
    }
    addTestNgMirrors(record);
    fs.mkdirSync(path.join(record.report, 'Surefire suite'));
    fs.writeFileSync(path.join(record.report, 'Surefire suite', 'Surefire test.xml'), '<suite/>');
  }
  const result = f.compare();
  assert.equal(result.invocations, 5, 'Report mirrors do not add authoritative invocations.');
  assert.deepEqual(result.serial.forks[0].reports, ['reports/serial/fork-1/TEST-TestSuite.xml',
    ...f.serial.receipt.executedClasses.map(owner => 'reports/serial/fork-1/junitreports/TEST-' + owner.name + '.xml')].sort());
  assert.equal(result.parallel.forks[0].reports.length, f.left.receipt.executedClasses.length * 2);
});

const mutations = [
  ['changed source identity', f => { f.left.receipt.sourceRevision = 'd'.repeat(40); rewriteReceipt(f.left); }],
  ['changed compiled test inventory', f => { f.left.receipt.testClassSetSha256 = 'd'.repeat(64); rewriteReceipt(f.left); }],
  ['foreign trial identity', f => { f.left.receipt.trialId = 'another-trial'; rewriteReceipt(f.left); }],
  ['mixed JDK runtimes', f => { f.left.receipt.jdk.runtimeVersion = '21+1'; rewriteReceipt(f.left); }],
  ['wrong fork identity', f => { f.right.receipt.forkId = 1; rewriteReceipt(f.right); }],
  ['a copied receipt from another process', f => { fs.copyFileSync(path.join(f.left.workspace, 'receipt.json'), path.join(f.right.workspace, 'receipt.json')); }],
  ['foreign workspace metadata', f => { f.right.receipt.workspace = f.left.workspace; rewriteReceipt(f.right); }],
  ['unfinished process', f => { f.right.receipt.completed = false; rewriteReceipt(f.right); }],
  ['missing final receipt', f => { fs.unlinkSync(path.join(f.right.workspace, 'receipt.json')); }],
  ['changed retained start bytes', f => { fs.appendFileSync(path.join(f.right.workspace, 'process-start.json'), ' '); }],
  ['changed retained journal bytes', f => { fs.appendFileSync(path.join(f.right.workspace, 'invocations.jsonl'), '\n'); }],
  ['start/final ownership mismatch even after rehashing', f => { f.right.start.pid = 999; persist(f.right); }],
  ['forged result totals', f => { f.right.receipt.summary.passed++; rewriteReceipt(f.right); }],
  ['duplicate aggregate receipt rows', f => { f.left.receipt.invocations.push(f.left.receipt.invocations[0]); rewriteReceipt(f.left); }],
  ['a missing repeated invocation', f => { f.left.rows.splice(f.left.rows.findIndex(row => row.kind === 'test'), 1); persist(f.left); }],
  ['changed parameter identity at the same total count', f => { f.left.rows.find(row => row.kind === 'test').parameterSha256 = digest('different parameter'); persist(f.left); }],
  ['changed overload signature at the same total count', f => { f.left.rows.find(row => row.kind === 'test').parameterTypes = ['java.lang.Object']; persist(f.left); }],
  ['final invocations that disagree with the journal', f => { f.left.receipt.invocations[0].count++; rewriteReceipt(f.left); }],
  ['a failed test even when accounting agrees', f => { f.left.rows.find(row => row.kind === 'test').status = 'failed'; persist(f.left); }],
  ['a skipped test even when accounting agrees', f => { f.left.rows.find(row => row.kind === 'test').status = 'skipped'; persist(f.left); }],
  ['a configuration failure', f => { f.left.rows.find(row => row.kind === 'configuration').status = 'failed'; persist(f.left); }],
  ['a skipped configuration', f => { f.left.rows.find(row => row.kind === 'configuration').status = 'skipped'; persist(f.left); }],
  ['foreign executed classes', f => { f.right.receipt.executedClasses[0].name = 'forge.api.ForeignTest'; rewriteReceipt(f.right); }],
  ['duplicate class ownership across forks', f => { f.right.receipt.executedClasses.push(f.left.receipt.executedClasses[0]); rewriteReceipt(f.right); }],
  ['class bytes that changed between modes', f => { f.right.receipt.executedClasses[0].sha256 = digest('other class bytes'); rewriteReceipt(f.right); }],
  ['selected classes omitted from the trial', f => { f.manifest.selectedClassNames.push('forge.api.MissingTest'); write(path.join(f.root, 'manifest.json'), f.manifest); }],
  ['duplicate selected classes', f => { f.manifest.selectedClassNames.push(f.manifest.selectedClassNames[0]); write(path.join(f.root, 'manifest.json'), f.manifest); }],
  ['non-string class identities', f => { f.manifest.selectedClassNames[0] = null; write(path.join(f.root, 'manifest.json'), f.manifest); }],
  ['a process predating its fresh manifest', f => { f.right.start.startedAtEpochMs = f.right.receipt.startedAtEpochMs = 900; persist(f.right); }],
  ['late class publication outside the final process interval', f => { f.right.receipt.executedClasses[0].completedAtEpochMs = 5000; rewriteReceipt(f.right); }],
  ['missing heap telemetry', f => { delete f.right.receipt.heapPoolPeakUsedBytes; rewriteReceipt(f.right); }],
  ['missing per-fork XML diagnostics', f => { fs.unlinkSync(path.join(f.right.report, 'TEST-TestSuite.xml')); }],
  ['empty per-fork XML diagnostics', f => { fs.writeFileSync(path.join(f.right.report, 'TEST-TestSuite.xml'), ''); }],
  ['a foreign report fork', f => { fs.mkdirSync(path.join(f.root, 'reports', 'parallel', 'fork-3')); }],
  ['nested duplicate TEST reports', f => { fs.mkdirSync(path.join(f.right.report, 'foreign')); fs.copyFileSync(path.join(f.right.report, 'TEST-TestSuite.xml'), path.join(f.right.report, 'foreign', 'TEST-TestSuite.xml')); }],
  ['TestNG mirrors without a root Surefire report', f => { addTestNgMirrors(f.right); fs.unlinkSync(path.join(f.right.report, 'TEST-TestSuite.xml')); }],
  ['empty TestNG mirrors', f => { const mirrors = addTestNgMirrors(f.right); fs.writeFileSync(path.join(mirrors, 'TEST-' + f.right.receipt.executedClasses[0].name + '.xml'), ''); }],
  ['a TestNG mirror owned by another fork', f => { const mirrors = addTestNgMirrors(f.right); fs.writeFileSync(path.join(mirrors, 'TEST-' + f.left.receipt.executedClasses[0].name + '.xml'), '<testsuite/>'); }],
  ['duplicate TestNG reports below the permitted mirror level', f => { const mirrors = addTestNgMirrors(f.right), nested = path.join(mirrors, 'nested'); fs.mkdirSync(nested); fs.copyFileSync(path.join(mirrors, 'TEST-' + f.right.receipt.executedClasses[0].name + '.xml'), path.join(nested, 'TEST-' + f.right.receipt.executedClasses[0].name + '.xml')); }],
  ['an orphan second process in one fork', f => { fs.mkdirSync(path.join(path.dirname(f.right.workspace), 'process-999-orphan-nonce')); }],
  ['a foreign execution mode', f => { fs.mkdirSync(path.join(f.root, 'default')); }],
  ['a foreign fork workspace', f => { fs.mkdirSync(path.join(f.root, 'parallel', 'fork-3')); }],
  ['an extra process receipt file', f => { fs.copyFileSync(path.join(f.right.workspace, 'receipt.json'), path.join(f.right.workspace, 'receipt-copy.json')); }]
];
for (const [description, mutate] of mutations) test('rejects ' + description, t => {
  const f = fixture(t); mutate(f); assert.throws(f.compare);
});

test('two PIDs without overlapping actual class execution do not demonstrate isolation under concurrent load', t => {
  const f = fixture(t);
  f.right.start.startedAtEpochMs = f.right.receipt.startedAtEpochMs = 5000;
  f.right.receipt.completedAtEpochMs = 6100;
  for (const owner of f.right.receipt.executedClasses) { owner.startedAtEpochMs += 2000; owner.completedAtEpochMs += 2000; }
  for (const row of f.right.rows) { row.startedAtEpochMs += 2000; row.completedAtEpochMs += 2000; }
  persist(f.right);
  assert.throws(f.compare, /overlapping class execution/);
});

test('separate fork receipts cannot reuse an actual PID or process nonce', t => {
  for (const kind of ['pid', 'nonce']) {
    const f = fixture(t), previous = f.right.workspace;
    const name = kind === 'pid' ? 'process-102-new-fresh-nonce' : 'process-103-parallel-nonce-1';
    f.right.workspace = path.join(path.dirname(previous), name);
    fs.renameSync(previous, f.right.workspace);
    f.right.start.workspace = f.right.receipt.workspace = f.right.workspace;
    if (kind === 'pid') f.right.start.pid = f.right.receipt.pid = 102;
    persist(f.right);
    assert.throws(f.compare, /duplicate|reuse/);
  }
});

test('linked process or report evidence cannot escape its owned workspace', t => {
  for (const kind of ['process', 'reports']) {
    const f = fixture(t), owner = kind === 'process' ? f.right.workspace : f.right.report;
    const moved = path.join(f.root, 'timings'); fs.mkdirSync(moved);
    const target = path.join(moved, 'linked-evidence'); fs.renameSync(owner, target);
    fs.symlinkSync(target, owner, 'junction');
    assert.throws(f.compare, /owned|linked|directory/);
  }
});
