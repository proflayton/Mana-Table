'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const C = require('../ai-experiment-contract.cjs');
const E = require('../ai-experiment.cjs');

function fixture(t, mode = 'replay', behavior = '') {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-ai-experiment-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const bin = path.join(root, 'bin'), resources = path.join(root, 'resources'); fs.mkdirSync(bin); fs.mkdirSync(resources);
  fs.writeFileSync(path.join(resources, 'cards.txt'), 'Synthetic test-only resource bytes.');
  const driver = path.join(bin, 'fake-worker.cjs');
  fs.writeFileSync(driver, `const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const args=process.argv.slice(2);const arg=n=>args[args.indexOf(n)+1];
const lockPath=arg('--ai-experiment-worker'),lock=JSON.parse(fs.readFileSync(lockPath)),job=lock.jobs.find(j=>j.id===arg('--job-id')),dir=arg('--attempt-directory');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const result={schemaVersion:1,kind:'ai-experiment-result',lockSha256:hash(fs.readFileSync(lockPath)),jobId:job.id,attemptId:'attempt-0001',reservationToken:arg('--reservation-token'),status:'completed',started:job.mode==='natural',criticalSafetyFailure:false};
if(job.mode==='replay')result.replay={grade:lock.variants[job.arm].attachmentEvaluation?'pass':'fail',admissible:true,eligible:job.replay.expectation.kind!=='ungraded',evaluator:'synthetic-test/1'};
else result.natural={terminalVerified:true,focalPlayerId:10+job.natural.focalSlot,participants:[0,1,2,3].map(slot=>({slot,playerId:10+slot})),outcome:{kind:'win',winningPlayerIds:[job.arm==='candidate'?10+job.natural.focalSlot:10+(job.natural.focalSlot+1)%4],winningTeamIds:[1],gameId:'synthetic-game',ruleReason:'synthetic test only'}};
${behavior}
fs.writeFileSync(path.join(dir,'result.json'),JSON.stringify(result));
const bytes=fs.readFileSync(path.join(dir,'result.json'));
fs.writeFileSync(path.join(dir,'artifacts.json'),JSON.stringify({schemaVersion:1,kind:'ai-experiment-artifacts',files:[{path:'result.json',bytes:bytes.length,sha256:hash(bytes)}]}));`);
  const spec = { schemaVersion: 1, kind: 'ai-experiment-spec', id: 'synthetic-control', hypothesis: 'Synthetic contract test; never a game or policy performance result.', mode,
    primaryEndpoint: mode === 'replay' ? 'acceptable-decision-rate' : 'focal-win-share',
    variants: { baseline: { factoryId: 'casual-commander.v6', attachmentEvaluation: false }, candidate: { factoryId: 'casual-commander.v6', attachmentEvaluation: true }, reference: { factoryId: 'casual-commander.v6', attachmentEvaluation: true } },
    limits: { decisionMs: 3000, workUnits: 4096, matchMs: 1000, turns: 160, workerMs: 31000, captureBytes: 1048576 },
    runtime: { driverPath: driver, dotnetPath: process.execPath }, planningSeed: '-9223372036854775808', policySeed: '9223372036854775807', repetitions: 1 };
  if (mode === 'replay') { const capture = path.join(root, 'capture.json'); fs.writeFileSync(capture, '{}'); spec.replays = [{ id: 'first', path: capture, format: 'owned', expectation: { kind: 'admissible' }, family: 'synthetic', certifiedUndo: false }]; }
  else { spec.runtime.engineJarPath = path.join(root, 'engine.jar'); fs.writeFileSync(spec.runtime.engineJarPath, 'Synthetic engine identity only.'); spec.runtime.resourcesPath = resources; spec.runtime.javaPath = process.execPath;
    spec.decks = [0, 1, 2, 3].map(index => { const file = path.join(root, 'deck-' + index + '.txt'); fs.writeFileSync(file, 'Synthetic deck ' + index); return { id: 'deck-' + index, path: file }; }); }
  const specFile = path.join(root, 'input.json'), out = path.join(root, 'experiment');
  function save() { fs.writeFileSync(specFile, JSON.stringify(spec)); }
  save(); return { root, bin, resources, driver, spec, specFile, out, save };
}

test('strict JSON and schema reject duplicates, unsafe integer seeds, nonfinite and unknown settings', () => {
  for (const value of ['{"x":1,"x":2}', '{"x":1e999}', '{"x":9007199254740993}', '{"x":NaN}', '[[[[', '{"x":1,}', '{"x":true} trailing']) assert.throws(() => C.parse(Buffer.from(value)));
  assert.throws(() => C.parse(Buffer.from('['.repeat(66) + '0' + ']'.repeat(66))), /complexity/);
  assert.throws(() => C.seed('9223372036854775808'));
  assert.throws(() => C.seed('-0'));
  assert.equal(C.seed('-9223372036854775808'), '-9223372036854775808');
  const value = C.parse(Buffer.from('{"__proto__":{"safe":true},"seed":"9223372036854775807"}'));
  assert.equal(Object.getPrototypeOf(value), null); assert.equal(value.seed, '9223372036854775807');
});

test('planner closes actual runtime dependencies and pins exact corpus bytes before evaluation', async t => {
  const f = fixture(t); const lock = await E.plan(f.specFile, f.out);
  assert.equal(lock.jobs.length, 2); assert.equal(lock.jobs[0].seed, lock.jobs[1].seed);
  assert.equal(lock.seeds.policy, '9223372036854775807');
  assert(lock.artifacts.some(row => row.path === f.driver));
  assert(lock.artifacts.some(row => row.path === f.spec.replays[0].path));
  assert.equal(fs.readFileSync(path.join(f.out, 'spec.json'), 'utf8'), fs.readFileSync(f.specFile, 'utf8'));
  await C.verifyLock(f.out);
  fs.writeFileSync(f.spec.replays[0].path, '{"changed":true}');
  await assert.rejects(C.verifyLock(f.out), /artifact changed/i);
});

test('a new unpinned runtime file invalidates the directory closure', async t => {
  const f = fixture(t); await E.plan(f.specFile, f.out); fs.writeFileSync(path.join(f.bin, 'new-dependency.dll'), 'changed');
  await assert.rejects(C.verifyLock(f.out), /closure file set changed/);
});

test('natural schedule crosses every focal deck and seat, with equal arms and fixed opponents', async t => {
  const f = fixture(t, 'natural'); const lock = await E.plan(f.specFile, f.out);
  assert.equal(lock.jobs.length, 32);
  const cells = new Map();
  for (const job of lock.jobs) {
    const own = job.natural.decks[job.natural.focalSlot]; const key = own.id + '/' + job.natural.focalSlot;
    if (!cells.has(key)) cells.set(key, []); cells.get(key).push(job);
  }
  assert.equal(cells.size, 16);
  for (const pair of cells.values()) { assert.deepEqual(pair.map(job => job.arm).sort(), ['baseline', 'candidate']); assert.equal(pair[0].seed, pair[1].seed); assert.deepEqual(pair[0].natural, pair[1].natural); }
  assert.equal(lock.concurrency, 1);
});

test('schema rejects registry, limits, capability and output-root changes rather than silently defaulting', async t => {
  const f = fixture(t);
  for (const change of [s => s.variants.candidate.weight = 8, s => s.variants.baseline.factoryId = 'arbitrary-type', s => s.policySeed = 12, s => s.limits.captureBytes = 0, s => s.replays[0].certifiedUndo = true]) {
    const value = structuredClone(f.spec); change(value); assert.throws(() => C.validateSpec(value));
  }
  await assert.rejects(E.plan(f.specFile, path.join(f.bin, 'output')), /outside immutable/);
});

test('both-arm replay receipts retain paired eligibility and deterministic independent analysis', async t => {
  const f = fixture(t); await E.plan(f.specFile, f.out);
  const first = await E.run(f.out); assert.equal(first.launched, 2); assert.equal(first.report.replay.corrections, 1); assert.equal(first.report.replay.regressions, 0);
  assert.equal(first.report.replay.arms.baseline.scheduledEligible, 1); assert.equal(first.report.replay.arms.candidate.acceptableLower, 1);
  const again = await E.analyze(f.out); assert.equal(first.analysisId, again.analysisId);
  const resumed = await E.run(f.out); assert.equal(resumed.launched, 0); assert.equal(resumed.analysisId, first.analysisId);
  assert.equal(fs.existsSync(path.join(f.out, 'coordinator.lock')), false);
});

test('coordinator refuses concurrent and orphaned ownership; reserved uncertain jobs never relaunch', async t => {
  const f = fixture(t); const lock = await E.plan(f.specFile, f.out);
  const release = E.lease(f.out); await assert.rejects(E.run(f.out), /EEXIST/); release();
  for (const job of lock.jobs) fs.mkdirSync(path.join(f.out, 'jobs', job.id, 'attempt-0001'), { recursive: true });
  const value = await E.run(f.out); assert.equal(value.launched, 0); assert.equal(value.report.replay.incompletePairs, 1); assert.equal(value.report.interpretation, 'inconclusive');
  assert.equal(value.report.replay.arms.candidate.missingOrUngraded, 1);
});

test('missing opposite-arm grades remain in the common denominator and block a false success', async t => {
  const f = fixture(t, 'replay', "if(job.arm==='candidate')process.exit(3);"); await E.plan(f.specFile, f.out);
  const value = await E.run(f.out); assert.equal(value.report.replay.arms.candidate.scheduledEligible, 1);
  assert.equal(value.report.replay.arms.candidate.missingOrUngraded, 1); assert.equal(value.report.replay.incompletePairs, 1);
  assert.notEqual(value.report.interpretation, 'tactical-improvement');
});

test('natural summaries include every planned cell and valid draw/shared-win scoring', async t => {
  const f = fixture(t, 'natural'); const lock = await E.plan(f.specFile, f.out);
  const empty = (await E.analyze(f.out)).report; assert.deepEqual(empty.natural.plannedDifferenceBounds, [-1, 1]);
  assert.equal(empty.natural.arms.candidate.planned.denominator, 16);
  const natural = { terminalVerified: true, focalPlayerId: 11, participants: [0, 1, 2, 3].map(slot => ({ slot, playerId: 10 + slot })), outcome: { gameId: 'typed', kind: 'draw', winningPlayerIds: [], winningTeamIds: [], ruleReason: 'actual typed fixture' } };
  assert.equal(E.score(natural), 0); natural.outcome.kind = 'win'; natural.outcome.winningPlayerIds = [11, 12]; natural.outcome.winningTeamIds = [1, 2]; assert.equal(E.score(natural), .5);
  natural.outcome.kind = 'draw'; assert.throws(() => E.score(natural), /draws/);
  const value = await E.run(f.out); assert.equal(value.launched, lock.jobs.length);
  assert.deepEqual(value.report.natural.plannedDifferenceBounds, [1, 1]); assert.equal(value.report.confidenceIntervals, null); assert.equal(value.report.strengthClaim, false);
});

test('typed terminal outcomes survive cleanup failure while reliability stops further workers', async t => {
  const f = fixture(t, 'natural', "result.cleanupErrors=['synthetic cleanup failure'];"); await E.plan(f.specFile, f.out);
  const value = await E.run(f.out); assert.equal(value.launched, 1); assert.equal(value.report.rows.filter(row => row.score !== null).length, 1);
  assert.equal(value.report.rows.filter(row => row.cleanupFailure).length, 1); assert.equal(value.report.rows.filter(row => !row.attempted).length, 31);
});

test('changed committed evidence and wrong reserved identity are refused', async t => {
  const f = fixture(t, 'replay', "result.jobId='another-job';"); await E.plan(f.specFile, f.out);
  const value = await E.run(f.out); assert.equal(value.launched, 1); assert.equal(value.report.invalid.length, 1);
  const job = value.report.rows.find(row => row.attempted); const receipt = path.join(f.out, 'jobs', job.jobId, 'attempt-0001', 'result.json'); fs.appendFileSync(receipt, ' ');
  const changed = await E.analyze(f.out); assert.equal(changed.report.invalid.length, 1); assert.match(changed.report.invalid[0].error, /evidence changed/);
});

test('lock schedules cannot acquire path traversal, changed settings or missing dependency closures', async t => {
  const f = fixture(t); await E.plan(f.specFile, f.out);
  const file = path.join(f.out, 'lock.json'), original = fs.readFileSync(file, 'utf8');
  for (const change of [lock => lock.jobs[0].id = '../escape', lock => lock.variants.candidate.attachmentEvaluation = false, lock => lock.closureRoots = []]) {
    const lock = JSON.parse(original); change(lock); const bytes = JSON.stringify(lock); fs.writeFileSync(file, bytes); fs.writeFileSync(path.join(f.out, 'lock.sha256'), C.sha(bytes));
    await assert.rejects(C.verifyLock(f.out));
  }
});

test('related case multiplicity cannot inflate the primary macro-family weight', async t => {
  const f = fixture(t, 'replay', "if(job.replay.family==='other')result.replay.grade=job.arm==='candidate'?'fail':'pass';");
  f.spec.replays = [
    { ...f.spec.replays[0], id: 'same-a', family: 'related' },
    { ...f.spec.replays[0], id: 'same-b', family: 'related' },
    { ...f.spec.replays[0], id: 'different', family: 'other' }
  ]; f.save(); await E.plan(f.specFile, f.out); const value = await E.run(f.out);
  assert.equal(value.report.replay.corrections, 2); assert.equal(value.report.replay.regressions, 1);
  assert.deepEqual(value.report.replay.differenceBounds, [0, 0]);
  assert(Math.abs(value.report.replay.rawCaseDifferenceBounds[0] - 1 / 3) < 1e-10);
  assert.equal(value.report.replay.families.length, 2);
});

test('retained analysis needs neither engine nor driver after verified worker evidence commits', async t => {
  const f = fixture(t, 'natural'); await E.plan(f.specFile, f.out);
  const before = await E.run(f.out, 1, 1); fs.unlinkSync(f.spec.runtime.engineJarPath); fs.rmSync(f.resources, { recursive: true }); fs.rmSync(f.bin, { recursive: true });
  const after = await E.analyze(f.out); assert.equal(after.analysisId, before.analysisId);
  await assert.rejects(E.run(f.out), /ENOENT/);
});

test('A/A exact synthetic controls agree and swapping registered settings reverses the comparison', async t => {
  const aa = fixture(t); aa.spec.variants.baseline.attachmentEvaluation = true; aa.save(); await E.plan(aa.specFile, aa.out);
  const same = await E.run(aa.out); assert.deepEqual(same.report.replay.differenceBounds, [0, 0]);
  const swapped = fixture(t); swapped.spec.variants.baseline.attachmentEvaluation = true; swapped.spec.variants.candidate.attachmentEvaluation = false; swapped.save(); await E.plan(swapped.specFile, swapped.out);
  const reversed = await E.run(swapped.out); assert.deepEqual(reversed.report.replay.differenceBounds, [-1, -1]);
});

test('cost records keep wall/session scopes separate and missing samples remain unavailable', async t => {
  const f = fixture(t, 'replay', "result.metrics={evaluationWallMs:4,episodeElapsedMs:3,spentWorkUnits:20};"); await E.plan(f.specFile, f.out);
  const value = await E.run(f.out); assert.equal(value.report.cost.candidate.replayEvaluationWallMs.p95, 4); assert.equal(value.report.cost.candidate.replayEpisodeElapsedMs.p50, 3);
  assert.equal(value.report.cost.candidate.replaySpentWorkUnits.p50, 20); assert.equal(value.report.cost.candidate.naturalPolicyEvaluationWallMs.p50, null);
});

test('watchdog bounds its owned worker retirement when Windows tree-kill is refused', async t => {
  const f = fixture(t, 'replay', "setInterval(()=>{},1000);"); const lock = await E.plan(f.specFile, f.out);
  const attempt = path.join(f.root, 'watchdog'); fs.mkdirSync(attempt); lock.limits.workerMs = 150;
  const start = Date.now(); const value = await E.launch(lock, f.out, lock.jobs[0], attempt, 'a'.repeat(64)); assert.equal(value.timedOut, true); assert.notEqual(value.code, 0);
  assert(Date.now() - start < 5000, 'A failed tree-kill request cannot make retirement wait forever.');
  assert(value.signal || Number.isInteger(value.code), 'The synthetic child must actually retire, not merely be abandoned by this test.');
});

test('bounded execution chunks preserve the complete schedule and resume only new cells', async t => {
  const f = fixture(t); f.spec.repetitions = 2; f.save(); const lock = await E.plan(f.specFile, f.out);
  const first = await E.run(f.out, 1, 1); assert.equal(first.launched, 1); assert.equal(first.report.rows.length, 4); assert.equal(first.report.interpretation, 'inconclusive');
  const snapshot = fs.readFileSync(path.join(f.out, 'jobs', lock.jobs[0].id, 'attempt-0001', 'result.json'));
  const next = await E.run(f.out, 1, 2); assert.equal(next.launched, 2); assert.equal(next.report.rows.filter(row => !row.attempted).length, 1);
  assert.deepEqual(fs.readFileSync(path.join(f.out, 'jobs', lock.jobs[0].id, 'attempt-0001', 'result.json')), snapshot);
  const final = await E.run(f.out, 1, 1); assert.equal(final.launched, 1); assert.equal(final.report.rows.filter(row => !row.attempted).length, 0);
  assert.equal(fs.readFileSync(path.join(f.out, 'lock.json'), 'utf8').includes('max-jobs'), false);
});

test('a rejected partial replay result cannot retain a favorable grade or cost samples', async t => {
  const f = fixture(t, 'replay', "if(job.arm==='candidate'){result.replay.eligible=false;result.metrics={evaluationWallMs:1};}"); await E.plan(f.specFile, f.out);
  const value = await E.run(f.out); assert.equal(value.report.interpretation, 'experiment-invalid');
  assert.equal(value.report.replay.arms.candidate.pass, 0); assert.equal(value.report.replay.arms.candidate.missingOrUngraded, 1);
  assert.equal(value.report.cost.candidate.replayEvaluationWallMs.samples, 0);
});

test('valid negative controls expose guardrail failures rather than claiming invalid experimental evidence', async t => {
  const f = fixture(t, 'replay', "result.criticalSafetyFailure=true;"); await E.plan(f.specFile, f.out);
  const value = await E.run(f.out); assert.equal(value.report.invalid.length, 0); assert.equal(value.report.interpretation, 'guardrail-failed'); assert.equal(value.report.reliability.criticalSafetyFailures.length, 1);
});

test('an interrupted ledger append is retained and cannot be silently concatenated on resume', async t => {
  const f = fixture(t); await E.plan(f.specFile, f.out); fs.appendFileSync(path.join(f.out, 'ledger.jsonl'), '{"event":"partial');
  await assert.rejects(E.run(f.out), /incomplete tail/);
  const value = await E.analyze(f.out); assert.equal(value.report.ledger.valid, false); assert.equal(value.report.interpretation, 'experiment-invalid');
  assert.equal(value.report.rows.filter(row => row.attempted).length, 0);
});

test('an unexpected natural worker failure cannot manufacture a known game start', async t => {
  const f = fixture(t, 'natural', "result.status='provider-failure';result.started=false;result.natural.terminalVerified=false;result.natural.outcome=null;result.details={gameStartUncertain:true};");
  await E.plan(f.specFile, f.out);
  const value = await E.run(f.out, 1, 1), row = value.report.rows.find(item => item.attempted);
  assert.equal(row.started, null); assert.equal(row.score, null);
  assert.equal(value.report.natural.arms[row.arm].startedKnown, 0);
  assert.equal(value.report.natural.arms[row.arm].startUnknown, 1);
  assert.equal(value.report.natural.arms[row.arm].startedOrPossible.unresolved, 1);
});
