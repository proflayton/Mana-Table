const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { compareMode } = require('../compare-api-test-isolation.cjs');
const { fixture, persist, digest, write, rewriteReceipt, addTestNgMirrors, relabelFork } = require('./api-test-evidence-fixture.cjs');

function removeOwned(fixture, file) {
  const relative = path.relative(fixture.root, file);
  assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
  fs.rmSync(file, { recursive: true, force: true });
}
function ciFixture(t) {
  const f = fixture(t);
  removeOwned(f, path.join(f.root, 'serial')); removeOwned(f, path.join(f.root, 'reports', 'serial'));
  f.manifest.kind = 'api-ci'; f.manifest.executionOrder = ['parallel'];
  f.manifest.selectedClasses = f.manifest.selectedClassNames.map(name => ({ name, sha256: digest(name) }));
  write(path.join(f.root, 'manifest.json'), f.manifest);
  return { ...f, compare: () => compareMode({ root: f.root }) };
}
function manifest(f) { write(path.join(f.root, 'manifest.json'), f.manifest); }

test('normal CI certifies two genuinely overlapping processes and the complete compiled class union without a serial reference', t => {
  const f = ciFixture(t), result = f.compare();
  assert.equal(result.complete, true); assert.equal(result.mode, 'parallel');
  assert.equal(result.classes, 3); assert.equal(result.invocations, 5);
  assert.equal(result.sourceRevision, f.manifest.sourceRevision);
  assert.equal(result.testClassSetSha256, f.manifest.testClassSetSha256);
  assert.equal(result.compiledClassSetSha256, f.manifest.compiledClassSetSha256);
  assert.deepEqual(result.jdk, f.manifest.jdk);
  assert.equal(result.parallel.forks.length, 2);
  assert.notEqual(result.parallel.forks[0].pid, result.parallel.forks[1].pid);
  assert.ok(result.overlap.completedAtEpochMs > result.overlap.startedAtEpochMs);
  assert.equal(Object.hasOwn(result, 'equivalent'), false, 'A one-mode run cannot claim serial multiset equivalence.');
  assert.equal(Object.hasOwn(result, 'serial'), false);
});

test('CI uses actual canonical allocator labels with a report-owner bijection rather than assuming fork ordinals', t => {
  const f = ciFixture(t); relabelFork(f.left, 'fork-9', 9); relabelFork(f.right, 'fork-17', 17);
  assert.deepEqual(f.compare().parallel.forks.map(fork => fork.forkId), [9, 17]);
});

test('root Surefire reports and owned TestNG mirrors are diagnostics rather than additional invocation authority', t => {
  const f = ciFixture(t);
  for (const record of [f.left, f.right]) addTestNgMirrors(record);
  const result = f.compare();
  assert.equal(result.invocations, 5);
  assert.equal(result.parallel.forks.reduce((count, fork) => count + fork.reports.length, 0), 5);
});

test('normal CI accounts actual dynamic rows but leaves exact data-provider parity to the controlled serial and reverse trials', t => {
  const f = ciFixture(t), repeated = f.left.rows.find(row => row.kind === 'test');
  f.left.rows.push({ ...repeated }); persist(f.left);
  assert.equal(f.compare().invocations, 6, 'No fixed invocation count is used to guess the data-provider rows.');
});

const invalid = [
  ['a third process owner', f => fs.mkdirSync(path.join(f.root, 'parallel', 'fork-3'))],
  ['a missing process owner', f => removeOwned(f, path.dirname(f.right.workspace))],
  ['a third report owner', f => fs.mkdirSync(path.join(f.root, 'reports', 'parallel', 'fork-3'))],
  ['a missing report owner', f => removeOwned(f, f.right.report)],
  ['different process and report labels', f => fs.renameSync(f.right.report, path.join(path.dirname(f.right.report), 'fork-3'))],
  ['a relabeled receipt with its old owner ID', f => relabelFork(f.right, 'fork-9', 2)],
  ['a foreign process source', f => { f.right.receipt.sourceRevision = 'd'.repeat(40); rewriteReceipt(f.right); }],
  ['a foreign process JDK', f => { f.right.receipt.jdk.runtimeVersion = '21+1'; rewriteReceipt(f.right); }],
  ['a foreign compiled inventory', f => { f.right.receipt.testClassSetSha256 = digest('other compiled tests'); rewriteReceipt(f.right); }],
  ['missing compiled reactor identity', f => { delete f.manifest.compiledClassSetSha256; manifest(f); }],
  ['a malformed compiled reactor identity', f => { f.manifest.compiledClassSetSha256 = 'not-a-hash'; manifest(f); }],
  ['changed selected class bytes', f => { f.right.receipt.executedClasses[0].sha256 = digest('foreign class'); rewriteReceipt(f.right); }],
  ['missing selected class byte metadata', f => { f.manifest.selectedClasses.pop(); manifest(f); }],
  ['duplicate selected class metadata', f => { f.manifest.selectedClasses.push(f.manifest.selectedClasses[0]); manifest(f); }],
  ['missing selected-class coverage', f => { f.manifest.selectedClassNames.push('forge.api.MissingTest'); f.manifest.selectedClasses.push({ name: 'forge.api.MissingTest', sha256: digest('missing') }); manifest(f); }],
  ['duplicate class ownership even with honest journal rows', f => {
    const owner = f.left.receipt.executedClasses[0]; f.right.receipt.executedClasses.push({ ...owner });
    f.right.rows.push(...f.left.rows.filter(row => row.className === owner.name).map(row => ({ ...row })));
    persist(f.right);
  }],
  ['a missing invocation journal', f => fs.unlinkSync(path.join(f.right.workspace, 'invocations.jsonl'))],
  ['a missing final receipt', f => fs.unlinkSync(path.join(f.right.workspace, 'receipt.json'))],
  ['an incomplete process', f => { f.right.receipt.completed = false; rewriteReceipt(f.right); }],
  ['receipt counts disagreeing with the retained journal', f => { f.right.receipt.invocations[0].count++; rewriteReceipt(f.right); }],
  ['duplicate aggregated invocation rows', f => { f.right.receipt.invocations.push(f.right.receipt.invocations[0]); rewriteReceipt(f.right); }],
  ['a failed invocation with matching accounting', f => { f.right.rows.find(row => row.kind === 'test').status = 'failed'; persist(f.right); }],
  ['a skipped invocation with matching accounting', f => { f.right.rows.find(row => row.kind === 'test').status = 'skipped'; persist(f.right); }],
  ['a failed configuration with matching accounting', f => { f.right.rows.find(row => row.kind === 'configuration').status = 'failed'; persist(f.right); }],
  ['a skipped configuration with matching accounting', f => { f.right.rows.find(row => row.kind === 'configuration').status = 'skipped'; persist(f.right); }],
  ['a mirror-only report owner', f => { addTestNgMirrors(f.right); fs.unlinkSync(path.join(f.right.report, 'TEST-TestSuite.xml')); }],
  ['a TestNG mirror from another owner', f => { const mirrors = addTestNgMirrors(f.right); fs.writeFileSync(path.join(mirrors, 'TEST-' + f.left.receipt.executedClasses[0].name + '.xml'), '<testsuite/>'); }],
  ['a second process in one owner', f => fs.mkdirSync(path.join(path.dirname(f.right.workspace), 'process-999-extra-nonce'))],
  ['a foreign evidence mode', f => fs.mkdirSync(path.join(f.root, 'serial'))],
  ['a trial manifest presented as normal CI', f => { delete f.manifest.kind; f.manifest.executionOrder = ['serial', 'parallel']; manifest(f); }]
];
for (const [description, mutate] of invalid) test('single-mode CI rejects ' + description, t => {
  const f = ciFixture(t); mutate(f); assert.throws(f.compare);
});

test('two process receipts without overlapping actual class execution cannot certify normal CI isolation', t => {
  const f = ciFixture(t);
  const milliseconds = 2000;
  f.right.start.startedAtEpochMs += milliseconds; f.right.receipt.startedAtEpochMs += milliseconds; f.right.receipt.completedAtEpochMs += milliseconds;
  for (const value of [...f.right.receipt.executedClasses, ...f.right.rows]) { value.startedAtEpochMs += milliseconds; value.completedAtEpochMs += milliseconds; }
  persist(f.right); assert.throws(f.compare, /overlapping class execution/);
});

test('CI evidence rejects linked receipt/report paths and a foreign process workspace', t => {
  for (const kind of ['receipt', 'report', 'workspace']) {
    const f = ciFixture(t);
    if (kind === 'workspace') { f.right.receipt.workspace = f.left.workspace; rewriteReceipt(f.right); }
    else {
      const owner = kind === 'receipt' ? path.join(f.right.workspace, 'receipt.json') : f.right.report;
      const saved = path.join(f.root, 'timings'); fs.mkdirSync(saved);
      const target = path.join(saved, 'saved-' + kind); fs.renameSync(owner, target);
      if (kind === 'receipt') fs.linkSync(target, owner); else fs.symlinkSync(target, owner, 'junction');
    }
    assert.throws(f.compare);
  }
});

test('normal CI cannot configure a different mode, one fork, or an external manifest', t => {
  const f = ciFixture(t);
  for (const options of [{ mode: 'serial' }, { expectedForkCount: 1 }, { manifestPath: path.join(f.root, '..', 'manifest.json') }])
    assert.throws(() => compareMode({ root: f.root, ...options }));
});
