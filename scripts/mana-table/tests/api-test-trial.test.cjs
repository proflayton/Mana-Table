const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { execFileSync, spawnSync } = require('node:child_process');
const { prepare, boundary, inventories, inventoryHash, requireMeasuredProcesses, parse } = require('../api-test-trial.cjs');
const ci = require('../api-test-ci.cjs');
const { fork } = require('./api-test-evidence-fixture.cjs');

function fixture() {
  const repository = fs.mkdtempSync(path.join(os.tmpdir(), 'api-test-trial-'));
  const git = args => execFileSync('git', args, { cwd: repository, encoding: 'utf8', windowsHide: true }).trim();
  git(['init', '-q']);
  fs.writeFileSync(path.join(repository, '.gitignore'), 'target/\n');
  git(['add', '.gitignore']);
  git(['-c', 'user.name=Trial fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture']);
  const sourceRevision = git(['rev-parse', 'HEAD']);
  const output = path.join(repository, 'forge-api/target/test-classes/forge/api');
  fs.mkdirSync(output, { recursive: true });
  for (const name of ['ActualTest.class', 'RulesScenario.class', 'ActualTest$Helper.class']) fs.writeFileSync(path.join(output, name), name);
  for (const module of ['forge-core', 'forge-game', 'forge-ai', 'forge-gui', 'forge-api']) {
    const classes = path.join(repository, module, 'target/classes');
    fs.mkdirSync(classes, { recursive: true });
    fs.writeFileSync(path.join(classes, 'Rules.class'), 'rules');
  }
  const main = path.join(repository, 'forge-game/target/classes');
  const trialId = 'one';
  const root = path.join(repository, 'forge-api/target/api-test-trials', trialId);
  return { repository, root, trialId, sourceRevision, jdk: { runtimeVersion: '17.0.1+1', vendor: 'Fixture', vmName: 'Fixture VM' }, output, main };
}
test('trial inventory binds helpers and upstream compiled bytes without selecting helper classes', () => {
  const f = fixture(), first = inventories(f.repository);
  assert.deepEqual(first.selectedClassNames, ['forge.api.ActualTest']);
  fs.appendFileSync(path.join(f.output, 'ActualTest$Helper.class'), 'changed');
  const second = inventories(f.repository);
  assert.notEqual(second.testClassSetSha256, first.testClassSetSha256);
  fs.appendFileSync(path.join(f.main, 'Rules.class'), 'changed');
  assert.notEqual(inventories(f.repository).compiledClassSetSha256, second.compiledClassSetSha256);
  assert.equal(inventoryHash([{ path: 'A.class', sha256: 'a'.repeat(64) }]), require('node:crypto').createHash('sha256').update('A.class\0' + 'a'.repeat(64) + '\n').digest('hex'));
});
test('trial preparation binds clean source and uses fresh roots without overwriting first evidence', () => {
  const f = fixture(), manifest = prepare(f);
  assert.equal(manifest.sourceRevision, f.sourceRevision);
  assert.deepEqual(manifest.executionOrder, ['serial', 'parallel']);
  assert.equal(JSON.parse(fs.readFileSync(path.join(f.root, 'timings/serial-start.json'))).compiledClassSetSha256, manifest.compiledClassSetSha256);
  assert.throws(() => prepare(f), /EEXIST/);
  assert.equal(JSON.parse(fs.readFileSync(path.join(f.root, 'manifest.json'))).trialId, 'one');
  const dirty = fixture();
  fs.writeFileSync(path.join(dirty.repository, 'uncommitted.java'), 'not committed');
  assert.throws(() => prepare(dirty), /clean worktree/);
  assert.throws(() => prepare({ ...fixture(), sourceRevision: 'a'.repeat(40) }), /exact committed source/);
});
test('checkpoint rejects changed compiled inputs and invalid execution order', () => {
  const f = fixture(); prepare(f);
  assert.throws(() => boundary({ ...f, mode: 'parallel', event: 'start' }), /did not finish/);
  boundary({ ...f, mode: 'serial', event: 'end' });
  assert.throws(() => boundary({ ...f, mode: 'serial', event: 'end' }), /EEXIST/);
  fs.appendFileSync(path.join(f.main, 'Rules.class'), 'stale');
  assert.throws(() => boundary({ ...f, mode: 'parallel', event: 'start' }), /Compiled classes changed/);
});
test('a junction ancestor cannot redirect an otherwise contained trial workspace', () => {
  const f = fixture();
  const original = path.join(f.repository, 'forge-api/target'), saved = path.join(f.repository, 'forge-api/fixture-output');
  assert.ok(path.resolve(saved).startsWith(path.resolve(f.repository) + path.sep));
  fs.renameSync(original, saved);
  fs.symlinkSync(saved, original, process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => prepare(f), /symbolic link or junction/);
});
test('receipts outside a measured mode window cannot certify a speedup', () => {
  const comparison = { serial: { forks: [{ startedAtEpochMs: 110, completedAtEpochMs: 190 }] },
    parallel: { forks: [{ startedAtEpochMs: 310, completedAtEpochMs: 380 }, { startedAtEpochMs: 320, completedAtEpochMs: 390 }] } };
  const measured = { serial: { startedAtEpochMs: 100, completedAtEpochMs: 200 }, parallel: { startedAtEpochMs: 300, completedAtEpochMs: 400 } };
  requireMeasuredProcesses(comparison, measured);
  comparison.parallel.forks[1].startedAtEpochMs = 299;
  assert.throws(() => requireMeasuredProcesses(comparison, measured), /outside its measured mode window/);
  comparison.parallel.forks[1].startedAtEpochMs = 320;
  comparison.parallel.forks[1].completedAtEpochMs = 401;
  assert.throws(() => requireMeasuredProcesses(comparison, measured), /outside its measured mode window/);
});
test('reverse-order confirmation is explicit and rejects malformed identity, JDK and mode inputs', () => {
  const f = fixture();
  assert.deepEqual(prepare({ ...f, firstMode: 'parallel', secondMode: 'serial' }).executionOrder, ['parallel', 'serial']);
  boundary({ ...f, mode: 'parallel', event: 'end' });
  boundary({ ...f, mode: 'serial', event: 'start' });
  assert.throws(() => prepare({ ...fixture(), firstMode: 'parallel', secondMode: 'parallel' }), /exactly the serial and parallel/);
  assert.throws(() => prepare({ ...fixture(), trialId: '../escape' }), /Invalid trial identity/);
  assert.throws(() => prepare({ ...fixture(), jdk: { runtimeVersion: '17' } }), /JDK identity/);
  assert.throws(() => parse(['finish', '--root', 'one', '--root', 'two']), /duplicate/);
  assert.throws(() => parse(['finish', '--root', 'one', '--skip-checks', 'true']), /Unknown/);
});

function ciFixture() {
  const f = fixture();
  return { ...f, root: path.join(f.repository, 'forge-api/target/api-test-runs', f.trialId) };
}
test('normal API preparation binds current compiled class bytes and starts exactly one parallel mode', () => {
  const f = ciFixture(), manifest = ci.prepare(f);
  assert.equal(manifest.kind, 'api-ci'); assert.deepEqual(manifest.executionOrder, ['parallel']);
  assert.equal(manifest.sourceRevision, f.sourceRevision); assert.deepEqual(manifest.jdk, f.jdk);
  assert.deepEqual(manifest.selectedClassNames, ['forge.api.ActualTest']);
  assert.deepEqual(manifest.selectedClasses, [{ name: 'forge.api.ActualTest',
    sha256: require('node:crypto').createHash('sha256').update(fs.readFileSync(path.join(f.output, 'ActualTest.class'))).digest('hex') }]);
  assert.equal(fs.existsSync(path.join(f.root, 'serial')), false);
  const first = fs.readFileSync(path.join(f.root, 'manifest.json'));
  assert.throws(() => ci.prepare(f), /EEXIST/);
  assert.deepEqual(fs.readFileSync(path.join(f.root, 'manifest.json')), first, 'A second attempt cannot overwrite first evidence.');
});
test('normal CI preparation rejects a trial profile, dirty or foreign source, and foreign run-root identity', () => {
  const both = ciFixture();
  assert.throws(() => ci.prepare({ ...both, trialProfile: 'true' }), /profile|mutual|together|exclusive/i);
  assert.equal(fs.existsSync(both.root), false, 'Mutually exclusive profiles must fail before running tests.');
  const dirty = ciFixture(); fs.writeFileSync(path.join(dirty.repository, 'uncommitted.java'), 'dirty');
  assert.throws(() => ci.prepare(dirty), /clean worktree/);
  assert.throws(() => ci.prepare({ ...ciFixture(), sourceRevision: 'a'.repeat(40) }), /exact committed source/);
  const foreign = ciFixture();
  assert.throws(() => ci.prepare({ ...foreign, root: path.join(foreign.repository, 'forge-api/target/api-test-trials', foreign.trialId) }),
    { message: 'Trial path must stay inside its owned directory.' });
});
test('normal CI finish rechecks all compiled reactor bytes before accepting process accounting', () => {
  const f = ciFixture(); ci.prepare(f);
  fs.appendFileSync(path.join(f.main, 'Rules.class'), 'changed');
  assert.throws(() => ci.finish(f), /Compiled classes changed/);
  assert.equal(fs.existsSync(path.join(f.root, 'accounting.json')), false);
});
test('normal CI cannot shorten its prepared class inventory while keeping valid aggregate compiled hashes', () => {
  const f = ciFixture(); fs.writeFileSync(path.join(f.output, 'AnotherTest.class'), 'another');
  const manifest = ci.prepare(f); manifest.selectedClassNames.pop(); manifest.selectedClasses.pop();
  fs.writeFileSync(path.join(f.root, 'manifest.json'), JSON.stringify(manifest));
  assert.throws(() => ci.finish(f), /selected|inventory|Compiled classes changed/i);
  assert.equal(fs.existsSync(path.join(f.root, 'accounting.json')), false);
});
test('an unexecuted normal CI run cannot manufacture accounting or erase its original manifest', () => {
  const f = ciFixture(); ci.prepare(f);
  const original = fs.readFileSync(path.join(f.root, 'manifest.json'));
  assert.throws(() => ci.finish(f));
  assert.equal(fs.existsSync(path.join(f.root, 'accounting.json')), false);
  assert.deepEqual(fs.readFileSync(path.join(f.root, 'manifest.json')), original);
  assert.throws(() => ci.parse(['finish', '--root', f.root, '--root', f.root]), /duplicate/);
  assert.throws(() => ci.parse(['finish', '--root', f.root, '--skip-checks', 'true']), /Unknown/);
});
test('always retention preserves incomplete raw evidence but fails a claimed successful run without its finalizer', () => {
  for (const outcome of ['failure', 'success']) {
    const f = ciFixture(); ci.prepare(f);
    const original = fs.readFileSync(path.join(f.root, 'manifest.json'));
    const retain = () => ci.retain({ ...f, mavenOutcome: outcome });
    if (outcome === 'success') assert.throws(retain, /no valid complete API accounting/);
    else assert.equal(retain().accountingVerified, false);
    const retained = JSON.parse(fs.readFileSync(path.join(f.root, 'retention.json')));
    assert.equal(retained.mavenOutcome, outcome); assert.equal(retained.sourceRevision, f.sourceRevision);
    assert.equal(retained.accountingVerified, false); assert.equal(typeof retained.error, 'string');
    assert.deepEqual(fs.readFileSync(path.join(f.root, 'manifest.json')), original);
    assert.throws(retain, /EEXIST/, 'First retained evidence cannot be overwritten by a later attempt.');
  }
});
function completedCi(t) {
  let now = 5000; t.mock.method(Date, 'now', () => now);
  const f = ciFixture(); fs.writeFileSync(path.join(f.output, 'AnotherTest.class'), 'another');
  const manifest = ci.prepare(f);
  const records = [fork(f.root, manifest, 'parallel', 1, 101, [[0, 5200, 5800]], 5100, 6000),
    fork(f.root, manifest, 'parallel', 2, 102, [[1, 5300, 5900]], 5150, 6100)];
  now = 6200;
  return { ...f, records, accounting: ci.finish(f) };
}
test('successful accounting survives legitimate later extended-reactor compilation after the API finalizer', t => {
  const f = completedCi(t);
  const receipts = f.records.map(record => fs.readFileSync(path.join(record.workspace, 'receipt.json')));
  const classes = path.join(f.repository, 'forge-later/target/classes'); fs.mkdirSync(classes, { recursive: true });
  fs.writeFileSync(path.join(classes, 'Later.class'), 'later reactor module');
  assert.notEqual(inventories(f.repository).compiledClassSetSha256, f.accounting.compiledClassSetSha256);
  const retained = ci.retain({ ...f, mavenOutcome: 'success' });
  assert.equal(retained.accountingVerified, true); assert.equal(retained.invocations, f.accounting.invocations);
  assert.equal(retained.classes, 2);
  for (let index = 0; index < f.records.length; index++)
    assert.deepEqual(fs.readFileSync(path.join(f.records[index].workspace, 'receipt.json')), receipts[index]);
});
test('a later Maven failure retains valid API accounting without relabeling the failed Maven outcome', t => {
  const f = completedCi(t), retained = ci.retain({ ...f, mavenOutcome: 'failure' });
  assert.equal(retained.accountingVerified, true); assert.equal(retained.mavenOutcome, 'failure');
  assert.equal(retained.invocations, f.accounting.invocations);
});
test('honest completed receipts cannot replace a missing test-phase finalizer', t => {
  const f = completedCi(t); fs.unlinkSync(path.join(f.root, 'accounting.json'));
  assert.throws(() => ci.retain({ ...f, mavenOutcome: 'success' }), /no valid complete API accounting/);
  const retained = JSON.parse(fs.readFileSync(path.join(f.root, 'retention.json')));
  assert.equal(retained.accountingVerified, false);
  for (const record of f.records) assert.equal(fs.existsSync(path.join(record.workspace, 'invocations.jsonl')), true);
});

const workflow = fs.readFileSync(path.join(__dirname, '../../../.github/workflows/test-build.yaml'), 'utf8').replaceAll('\r\n', '\n');
test('manual trials cannot cancel or impersonate the required normal Java run', () => {
  const expression = workflow.match(/^  group: \$\{\{ (.+) \}\}$/m)[1];
  const format = (pattern, ...args) => pattern.replace(/\{(\d+)\}/g, (_, n) => args[n]);
  function group(event, trial, id = 1) {
    return vm.runInNewContext(expression, { github: { event_name: event, ref: 'refs/heads/master', run_id: id }, inputs: { api_test_trial: trial }, format });
  }
  assert.equal(group('push', false), 'java-refs/heads/master');
  assert.equal(group('pull_request', true), 'java-refs/heads/master');
  assert.notEqual(group('workflow_dispatch', true), group('push', false));
  assert.notEqual(group('workflow_dispatch', true, 1), group('workflow_dispatch', true, 2));
  assert.match(workflow, /format\('API isolation trial \(Java \{0\}\)'/);
  assert.match(workflow, /if: always\(\) && github.event_name == 'workflow_dispatch' && inputs.api_test_trial/);
  assert.match(workflow, /path: forge-api\/target\/api-test-trials\//);
  const failFast = workflow.match(/^      fail-fast: \$\{\{ (.+) \}\}$/m)[1];
  const selected = (event, trial) => vm.runInNewContext(failFast, { github: { event_name: event }, inputs: { api_test_trial: trial } });
  assert.equal(selected('push', false), true);
  assert.equal(selected('pull_request', true), true);
  assert.equal(selected('workflow_dispatch', true), false, 'A failed trial must retain its sibling JDK evidence.');
});
test('the actual workflow runs one complete reactor and fails closed on invalid order or Maven failure', () => {
  const run = workflow.match(/      - name: Run tests in virtual framebuffer[\s\S]*?        run: \|\n((?:          [^\n]*\n|\n)+)/)[1].replace(/^          /gm, '');
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'api-trial-dispatch-'));
  const script = path.join(directory, 'dispatch.sh');
  const output = path.join(directory, 'args');
  fs.writeFileSync(script, 'set -e\nfunction Xvfb() { :; }\nfunction mvn() { printf "%s\\0" "$@" > "$MANA_TEST_ARGS"; return "$MANA_TEST_EXIT"; }\n' + run);
  const bash = process.platform === 'win32' ? path.join(process.env.ProgramFiles || 'C:/Program Files', 'Git/bin/bash.exe') : 'bash';
  function invoke(overrides = {}) {
    if (fs.existsSync(output)) fs.unlinkSync(output);
    const result = spawnSync(bash, [script.replaceAll('\\', '/')], { windowsHide: true, encoding: 'utf8', timeout: 5000,
      env: { ...process.env, EXTENDED: 'false', API_TEST_TRIAL: 'false', API_TRIAL_ORDER: 'serial-first', API_TRIAL_ID: 'fixture', GITHUB_SHA: 'a'.repeat(40),
        MANA_TEST_ARGS: output.replaceAll('\\', '/'), MANA_TEST_EXIT: '0', ...overrides } });
    if (result.error) throw result.error;
    return { status: result.status, args: fs.existsSync(output) ? fs.readFileSync(output, 'utf8').split('\0').slice(0, -1) : [] };
  }
  const ciArgs = ['-Papi-ci', '-Dmana.tests.trialId=fixture', '-Dmana.tests.sourceRevision=' + 'a'.repeat(40)];
  const normal = ['-B', '-ntp', '-pl', 'forge-api', '-am', 'verify', ...ciArgs];
  assert.deepEqual(invoke(), { status: 0, args: normal });
  assert.deepEqual(invoke({ EXTENDED: 'true' }), { status: 0, args: ['-B', '-ntp', 'test', ...ciArgs] });
  const trial = ['-B', '-ntp', '-pl', 'forge-api', '-am', 'clean', 'verify', '-Papi-test-trial', '-Dmana.tests.trialId=fixture', '-Dmana.tests.sourceRevision=' + 'a'.repeat(40)];
  assert.deepEqual(invoke({ API_TEST_TRIAL: 'true' }), { status: 0, args: trial });
  assert.deepEqual(invoke({ API_TEST_TRIAL: 'true', API_TRIAL_ORDER: 'parallel-first' }), { status: 0,
    args: [...trial, '-Dmana.tests.firstMode=parallel', '-Dmana.tests.firstForkCount=2', '-Dmana.tests.secondMode=serial', '-Dmana.tests.secondForkCount=1'] });
  assert.deepEqual(invoke({ API_TEST_TRIAL: 'true', API_TRIAL_ORDER: 'unknown' }), { status: 1, args: [] });
  assert.equal(invoke({ API_TEST_TRIAL: 'true', MANA_TEST_EXIT: '9' }).status, 9);
  assert.equal(invoke({ MANA_TEST_EXIT: '9' }).status, 9);
});
test('only the API CI profile opts in to two reused processes and certifies after Surefire even at the test phase', () => {
  const pom = fs.readFileSync(path.join(__dirname, '../../../forge-api/pom.xml'), 'utf8').replaceAll('\r\n', '\n');
  const profile = pom.slice(pom.indexOf('<id>api-ci</id>'), pom.indexOf('<id>api-test-trial</id>'));
  const ordinary = pom.slice(0, pom.indexOf('<profiles>'));
  assert.equal([...profile.matchAll(/<id>default-test<\/id>/g)].length, 1);
  assert.match(profile, /<forkCount>2<\/forkCount>/); assert.match(profile, /<reuseForks>true<\/reuseForks>/);
  assert.match(profile, /reports\/parallel\/fork-\$\{surefire.forkNumber\}/);
  assert.doesNotMatch(profile, /<parallel>|<threadCount>|<argLine>|<includes>|<excludes>|<skipTests>|api-second-mode|activeByDefault/);
  assert.doesNotMatch(ordinary, /<forkCount>|<parallel>/);
  assert.match(ordinary, /<mana.tests.mode>default<\/mana.tests.mode>/);
  assert.match(ordinary, /<mana.tests.apiCiProfile>false<\/mana.tests.apiCiProfile>/);
  assert.match(ordinary, /<mana.tests.apiTrialProfile>false<\/mana.tests.apiTrialProfile>/);
  assert.match(profile, /<id>prepare-api-ci<\/id>\s*<phase>process-test-classes<\/phase>/);
  assert.match(profile, /<id>certify-api-ci<\/id>\s*<phase>test<\/phase>/);
  assert.ok(profile.indexOf('<artifactId>maven-surefire-plugin</artifactId>') < profile.indexOf('<id>certify-api-ci</id>'));
  assert.match(profile, /<argument>--trial-profile<\/argument><argument>\$\{mana.tests.apiTrialProfile\}<\/argument>/);
  assert.match(pom, /<argument>--ci-profile<\/argument><argument>\$\{mana.tests.apiCiProfile\}<\/argument>/);
  const parent = fs.readFileSync(path.join(__dirname, '../../../pom.xml'), 'utf8');
  assert.doesNotMatch(parent, /<id>api-ci<\/id>/, 'Upstream modules must retain their existing serial test configuration.');
});
test('both JDKs have Node and always retain compact accounting plus complete first failure evidence', () => {
  const node = workflow.match(/      - uses: actions\/setup-node@[^\n]+\n([\s\S]*?)(?=\n      - |\n      #)/)[1];
  assert.doesNotMatch(node, /^        if:/m);
  assert.match(workflow, /java: \['17', '21'\]/);
  assert.match(workflow, /id: java_tests/);
  const retain = workflow.match(/      - name: Retain complete or incomplete API CI accounting\n([\s\S]*?)(?=\n      - name:)/)[1];
  assert.match(retain, /if: always\(\)/); assert.match(retain, /steps.java_tests.outcome/);
  assert.match(retain, /api-test-ci.cjs retain/); assert.match(retain, /--maven-outcome "\$MAVEN_OUTCOME"/);
  const compact = workflow.match(/      - name: Save compact API CI accounting on every outcome\n([\s\S]*?)(?=\n      - name:)/)[1];
  assert.match(compact, /if: always\(\)/); assert.match(compact, /if-no-files-found: error/);
  for (const file of ['manifest.json', 'accounting.json', 'retention.json', 'timings/', 'process-start.json', 'invocations.jsonl', 'receipt.json'])
    assert.ok(compact.includes(file), file + ' remains available on every outcome.');
  assert.match(compact, /reports\/parallel\/fork-\*\/TEST-\*\.xml/);
  assert.match(compact, /reports\/parallel\/fork-\*\/junitreports\/TEST-\*\.xml/);
  assert.doesNotMatch(compact, /profiles\/|evidence\//, 'Private profiles and decision traces belong to the failure artifact.');
  const failure = workflow.slice(workflow.indexOf('- name: Save failed test reports and decision traces'));
  assert.match(failure, /if: failure\(\)/); assert.match(failure, /forge-api\/target\/api-test-runs\//);
  assert.match(failure, /forge-\*\/target\/surefire-reports\//); assert.match(failure, /forge-api\/target\/rules-regressions\//);
});
test('the opt-in POM retains inherited JVM flags and separates compile, tests and comparison phases', () => {
  const pom = fs.readFileSync(path.join(__dirname, '../../../forge-api/pom.xml'), 'utf8');
  const profile = pom.slice(pom.indexOf('<id>api-test-trial</id>'), pom.indexOf('<id>api-example</id>'));
  assert.doesNotMatch(profile, /<argLine>|<parallel>|<includes>|<excludes>|<skipTests>/);
  for (const [id, phase] of [['prepare-api-test-trial', 'process-test-classes'], ['finish-api-first-mode', 'prepare-package'],
    ['start-api-second-mode', 'pre-integration-test'], ['api-second-mode', 'integration-test'], ['compare-api-test-trial', 'verify']])
    assert.ok(profile.includes(`<id>${id}</id>\n                                <phase>${phase}</phase>`.replaceAll('\n', '\r\n')) ||
      profile.replaceAll('\r\n', '\n').includes(`<id>${id}</id>\n                                <phase>${phase}</phase>`), `${id} keeps its lifecycle boundary.`);
  assert.match(profile, /<mana.tests.firstForkCount>1<\/mana.tests.firstForkCount>/);
  assert.match(profile, /<mana.tests.secondForkCount>2<\/mana.tests.secondForkCount>/);
  assert.equal([...profile.matchAll(/<reuseForks>true<\/reuseForks>/g)].length, 2);
  assert.equal([...profile.matchAll(/fork-\$\{surefire.forkNumber\}/g)].length, 2);
});
