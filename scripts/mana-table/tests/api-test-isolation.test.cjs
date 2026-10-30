const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { fixture, persist, digest, write, rewriteReceipt, addTestNgMirrors, relabelFork, shiftExecution } = require('./api-test-evidence-fixture.cjs');

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

test('a reverse-order serial JVM can own the returned Surefire fork-2 label after two parallel JVMs', t => {
  const f = fixture(t);
  f.manifest.executionOrder = ['parallel', 'serial']; write(path.join(f.root, 'manifest.json'), f.manifest);
  relabelFork(f.serial, 'fork-2', 2);
  shiftExecution(f.serial, 4000); shiftExecution(f.left, -2000); shiftExecution(f.right, -2000);
  const result = f.compare();
  assert.equal(result.invocations, 5); assert.equal(result.classes, 3);
  assert.deepEqual(result.executionOrder, ['parallel', 'serial']);
  assert.deepEqual(result.serial.forks.map(fork => fork.forkId), [2]);
  assert.deepEqual(result.parallel.forks.map(fork => fork.forkId), [1, 2]);
  assert.notEqual(result.serial.forks[0].pid, result.parallel.forks[1].pid,
    'A reused allocator label does not reuse an actual test process.');
});

test('numeric fork labels identify owners rather than mode-local ordinal ranges', t => {
  const f = fixture(t);
  relabelFork(f.serial, 'fork-9', 9); relabelFork(f.left, 'fork-17', 17); relabelFork(f.right, 'fork-8', 8);
  assert.deepEqual(f.compare().parallel.forks.map(fork => fork.forkId), [8, 17]);
});

for (const label of ['fork-0', 'fork-01', 'Fork-1', 'fork-1e0', 'fork-+1', 'fork-1000000000', 'fork-1.extra'])
  test('rejects a noncanonical fork owner label: ' + label, t => {
    const f = fixture(t); relabelFork(f.serial, label, 1);
    assert.throws(f.compare, /canonical numeric labels/);
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
  ['a missing process owner', f => { fs.renameSync(path.dirname(f.right.workspace), path.join(f.root, 'timings')); }],
  ['an additional serial process owner', f => { fs.mkdirSync(path.join(f.root, 'serial', 'fork-2')); }],
  ['a missing report owner', f => { fs.renameSync(f.right.report, path.join(f.root, 'timings')); }],
  ['report labels that disagree with process owners', f => { fs.renameSync(f.right.report, path.join(path.dirname(f.right.report), 'fork-3')); }],
  ['a relabeled owner with its old receipt ID', f => { relabelFork(f.serial, 'fork-2', 1); }],
  ['a workspace path alias after otherwise valid owner relabeling', f => { relabelFork(f.serial, 'fork-2', 2); f.serial.start.workspace = f.serial.receipt.workspace = path.dirname(f.serial.workspace) + path.sep + '.' + path.sep + path.basename(f.serial.workspace); persist(f.serial); }],
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
