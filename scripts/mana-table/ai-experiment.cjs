'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawn, execFileSync } = require('node:child_process');
const C = require('./ai-experiment-contract.cjs');
const { assert, keys, integer, sha, canonical, read, identity, files, within, validateSpec, schedule, verifyLock, atomic, append } = C;

async function plan(specPath, directory) {
  specPath = path.resolve(specPath); directory = path.resolve(directory);
  assert(fs.lstatSync(specPath).isFile() && fs.statSync(specPath).size <= C.MAX_JSON, 'Spec exceeds its bounded file size.');
  const raw = fs.readFileSync(specPath), spec = validateSpec(C.parse(raw));
  const base = path.dirname(specPath), resolve = file => path.resolve(base, file);
  const runtime = Object.fromEntries(Object.entries(spec.runtime).map(([key, value]) => [key, key === 'sourceRevision' ? value : resolve(value)]));
  if (spec.mode === 'natural' && !runtime.javaPath) {
    assert(process.env.JAVA_HOME, 'Natural experiments require runtime.javaPath or JAVA_HOME.');
    runtime.javaPath = path.join(process.env.JAVA_HOME, 'bin', process.platform === 'win32' ? 'java.exe' : 'java');
  }
  const roots = [...new Set([path.dirname(runtime.driverPath), ...(runtime.resourcesPath ? [runtime.resourcesPath] : [])])];
  assert(!roots.some(root => directory === root || within(root, directory)), 'Keep experiment outputs outside immutable artifact directories.');
  const inputs = (spec.mode === 'replay' ? spec.replays : spec.decks).map(item => ({ ...item, path: resolve(item.path) }));
  const paths = new Set([runtime.driverPath, runtime.dotnetPath, process.execPath, ...(runtime.engineJarPath ? [runtime.engineJarPath] : []), ...(runtime.javaPath ? [runtime.javaPath] : []), __filename, require.resolve('./ai-experiment-contract.cjs')]);
  for (const root of roots) files(root).forEach(file => paths.add(file));
  inputs.forEach(item => paths.add(item.path));
  assert(paths.size <= 250000, 'Artifact closure exceeds file-count limit.');
  const artifacts = []; let bytes = 0;
  for (const file of [...paths].sort()) { const item = await identity(file); bytes += item.bytes; assert(bytes <= 64 * 1024 ** 3, 'Artifact closure exceeds 64 GiB.'); artifacts.push(item); }
  const indexed = new Map(artifacts.map(item => [item.path, item]));
  inputs.forEach(item => { item.sha256 = indexed.get(item.path).sha256; });
  const jobs = schedule(spec, inputs), scheduleBytes = jobs.map(job => JSON.stringify(job)).join('\n') + '\n';
  const version = (executable, args) => { try { return { observed: execFileSync(executable, args, { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 10000, maxBuffer: 65536 }).trim(), unavailable: false }; } catch (error) { return { observed: typeof error.stderr === 'string' && error.status === 0 ? error.stderr.trim() : null, unavailable: true }; } };
  const lock = { schemaVersion: 1, kind: 'ai-experiment-lock', id: spec.id, hypothesis: spec.hypothesis, mode: spec.mode, primaryEndpoint: spec.primaryEndpoint,
    specSha256: sha(raw), sourceDirectory: base, createdAtUtc: new Date().toISOString(), variants: spec.variants, limits: spec.limits, runtime,
    seeds: { planning: spec.planningSeed, policy: spec.policySeed }, concurrency: 1, seedAlgorithm: 'sha256-signed-big-endian-64/1',
    artifactIdentity: 'bytes-verified; sourceRevision-is-build-metadata-assertion-not-clean-source-attestation',
    environment: { platform: process.platform, arch: process.arch, node: process.version, nodePath: process.execPath, cpuModels: [...new Set(os.cpus().map(cpu => cpu.model))], totalMemoryBytes: os.totalmem() },
    runtimeVersions: { dotnet: version(runtime.dotnetPath, ['--version']), ...(runtime.javaPath ? { java: version(runtime.javaPath, ['--version']) } : {}) },
    closureRoots: roots, artifacts, jobs, scheduleSha256: sha(scheduleBytes),
    coordinator: { scriptSha256: sha(fs.readFileSync(__filename)), contractSha256: sha(fs.readFileSync(require.resolve('./ai-experiment-contract.cjs'))) },
    estimate: { jobs: jobs.length, maximumWorkerMs: jobs.length * spec.limits.workerMs, maximumAttemptCaptureBytes: jobs.length * (spec.limits.captureBytes + 65536 + 262144), coordinatorOutputBytesPerAttempt: 65536 },
    analysis: { id: 'descriptive-first-attempts/1', confidenceIntervals: false, plannedWeights: 'natural: equal cells/blocks; replay: equal declared families, equal cases/repetitions within family; no completed-only substitution', primaryAttempts: 'first-reservation-only' } };
  const lockBytes = JSON.stringify(lock, null, 2) + '\n'; assert(Buffer.byteLength(lockBytes) <= C.MAX_LOCK, 'Lock exceeds 64 MiB.');
  fs.mkdirSync(directory, { recursive: true }); assert(fs.readdirSync(directory).length === 0, 'Planning needs a fresh empty output directory.');
  atomic(path.join(directory, 'spec.json'), raw.toString('utf8')); atomic(path.join(directory, 'schedule.jsonl'), scheduleBytes);
  atomic(path.join(directory, 'lock.json'), lockBytes); atomic(path.join(directory, 'lock.sha256'), sha(lockBytes) + '\n');
  atomic(path.join(directory, 'ledger.jsonl'), '');
  return lock;
}

function validateResult(result, job, reservation, hash) {
  keys(result, ['schemaVersion', 'kind', 'lockSha256', 'jobId', 'attemptId', 'reservationToken', 'status', 'started', 'criticalSafetyFailure'], ['replay', 'natural', 'metrics', 'details', 'error', 'cleanupErrors']);
  assert(result.schemaVersion === 1 && result.kind === 'ai-experiment-result' && result.lockSha256 === hash && result.jobId === job.id && result.attemptId === reservation.attemptId && result.reservationToken === reservation.reservationToken, 'Worker result identity does not match its reserved attempt.');
  assert(C.STATUSES.includes(result.status) && typeof result.started === 'boolean' && typeof result.criticalSafetyFailure === 'boolean', 'Invalid worker status.');
  if (result.replay) { objectOnly(result.replay, ['grade', 'admissible', 'eligible', 'evaluator']); assert(['pass', 'fail', 'ungraded'].includes(result.replay.grade) && typeof result.replay.admissible === 'boolean', 'Invalid replay grade.'); if (result.replay.eligible !== undefined) assert(typeof result.replay.eligible === 'boolean', 'Invalid eligibility.'); }
  if (result.status === 'completed' && job.mode === 'replay') assert(result.replay && !result.natural, 'Completed replay needs its paired grade.');
  if (result.status === 'completed' && job.mode === 'natural') assert(result.started && result.natural?.terminalVerified, 'Completed natural game requires a verified terminal outcome.');
  if (result.natural?.terminalVerified) score(result.natural);
  return result;
}
function objectOnly(value, allowed) { assert(value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(key => allowed.includes(key)), 'Unsupported result body field.'); }
function score(natural) {
  assert(natural.terminalVerified === true && natural.outcome, 'No verified natural outcome.');
  const { participants, focalPlayerId, outcome } = natural;
  assert(Array.isArray(participants) && participants.length === 4 && participants.every(row => Number.isSafeInteger(row.slot) && row.slot >= 0 && row.slot < 4 && Number.isSafeInteger(row.playerId) && row.playerId >= 0)
    && new Set(participants.map(row => row.slot)).size === 4 && new Set(participants.map(row => row.playerId)).size === 4 && participants.some(row => row.playerId === focalPlayerId), 'Invalid actual participant binding.');
  const winners = outcome.winningPlayerIds, teams = outcome.winningTeamIds;
  assert(['win', 'draw'].includes(outcome.kind) && typeof outcome.gameId === 'string' && outcome.gameId && typeof outcome.ruleReason === 'string' && outcome.ruleReason
    && Array.isArray(winners) && winners.length <= 4 && new Set(winners).size === winners.length && winners.every(id => participants.some(row => row.playerId === id))
    && Array.isArray(teams) && teams.length <= 4 && new Set(teams).size === teams.length && teams.every(id => Number.isSafeInteger(id) && id >= 0), 'Invalid typed natural outcome.');
  if (outcome.kind === 'draw') { assert(winners.length === 0 && teams.length === 0, 'Typed draws cannot have synthetic winners.'); return 0; }
  assert(winners.length > 0 && teams.length > 0, 'Typed Win requires actual winners.'); return winners.includes(focalPlayerId) ? 1 / winners.length : 0;
}
async function receipt(directory, job, lockSha256) {
  const reservation = read(path.join(directory, 'reservation.json'));
  assert(reservation.schemaVersion === 1 && reservation.kind === 'ai-experiment-reservation' && reservation.lockSha256 === lockSha256 && reservation.jobId === job.id && reservation.attemptId === 'attempt-0001' && /^[a-f0-9]{64}$/.test(reservation.reservationToken), 'Invalid durable reservation.');
  const record = read(path.join(directory, 'artifacts.json'));
  keys(record, ['schemaVersion', 'kind', 'files']); assert(record.schemaVersion === 1 && record.kind === 'ai-experiment-artifacts' && Array.isArray(record.files) && record.files.length > 0 && record.files.length <= 10000, 'Invalid worker artifact receipt.');
  const seen = new Set(); let result;
  for (const file of record.files) { keys(file, ['path', 'bytes', 'sha256']); assert(typeof file.path === 'string' && !path.isAbsolute(file.path), 'Artifact receipt paths must be relative.'); const full = path.resolve(directory, file.path); assert(within(directory, full) && file.path !== 'artifacts.json' && !seen.has(full), 'Duplicate or escaped artifact receipt.'); seen.add(full); const actual = await identity(full); assert(actual.sha256 === file.sha256 && actual.bytes === file.bytes, 'Worker evidence changed after commitment.'); if (file.path.replaceAll('\\', '/') === 'result.json') result = validateResult(read(full), job, reservation, lockSha256); }
  assert(result, 'Worker receipt must include its exact result.json.');
  return { result, reservation, artifactsSha256: sha(fs.readFileSync(path.join(directory, 'artifacts.json'))), resultSha256: sha(fs.readFileSync(path.join(directory, 'result.json'))) };
}

function lease(directory) {
  const file = path.join(directory, 'coordinator.lock'), token = crypto.randomBytes(32).toString('hex');
  const fd = fs.openSync(file, 'wx'); try { fs.writeFileSync(fd, JSON.stringify({ schemaVersion: 1, token, pid: process.pid, createdAtUtc: new Date().toISOString(), recovery: 'Never automatically take over an orphaned lease; reconcile workers and receipts first.' })); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
  return () => { assert(read(file).token === token, 'Coordinator ownership changed.'); fs.unlinkSync(file); };
}
function terminate(child) {
  if (!child.pid) return true;
  if (process.platform === 'win32') {
    try { execFileSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 1000 }); return true; }
    catch { try { child.kill('SIGKILL'); } catch { } return false; }
  }
  try { process.kill(-child.pid, 'SIGKILL'); return true; } catch { try { child.kill('SIGKILL'); } catch { } return false; }
}
async function launch(lock, directory, job, attempt, token) {
  const args = [lock.runtime.driverPath, '--ai-experiment-worker', path.join(directory, 'lock.json'), '--job-id', job.id, '--attempt-directory', attempt, '--reservation-token', token];
  const outputs = ['stdout', 'stderr'].map(name => fs.openSync(path.join(attempt, 'coordinator-' + name + '.log'), 'wx'));
  const child = spawn(lock.runtime.dotnetPath, args, { windowsHide: true, shell: false, detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe'] });
  let bytes = 0, overflow = false, timedOut = false, ioFailure = null, retirementUnknown = false, forceWait = null, forceResolve;
  const forceRetirement = () => {
    retirementUnknown ||= !terminate(child);
    forceWait ??= setTimeout(() => { retirementUnknown = true; child.unref(); child.stdout.destroy(); child.stderr.destroy(); forceResolve?.({ code: null, signal: null, retirementUnknown: true }); }, 1500);
  };
  [child.stdout, child.stderr].forEach((stream, index) => stream.on('data', chunk => {
    try { const available = Math.max(0, Math.min(chunk.length, 65536 - bytes)); if (available) { fs.writeSync(outputs[index], chunk.subarray(0, available)); bytes += available; }
      if (available < chunk.length) { overflow = true; forceRetirement(); }
    } catch (error) { ioFailure = error.message; forceRetirement(); }
  }));
  const timer = setTimeout(() => { timedOut = true; forceRetirement(); }, lock.limits.workerMs);
  let outcome;
  try { outcome = await new Promise(resolve => { forceResolve = resolve; child.once('error', error => resolve({ code: null, error: error.message })); child.once('close', (code, signal) => resolve({ code, signal })); }); }
  finally { clearTimeout(timer); if (forceWait) clearTimeout(forceWait); outputs.forEach(fd => { fs.fsyncSync(fd); fs.closeSync(fd); }); }
  return { ...outcome, timedOut, captureOverflow: overflow, ioFailure, retirementUnknown, capturedOutputBytes: bytes, pid: child.pid ?? null };
}
async function run(directory, jobs = 1, maxJobs = 10000) {
  directory = path.resolve(directory); integer(jobs, 1, 1, 'V1 concurrency (--jobs)'); integer(maxJobs, 1, 10000, 'execution chunk (--max-jobs)'); const release = lease(directory);
  let ownershipUncertain = false;
  try {
    const initial = await verifyLock(directory); assert(initial.lock.concurrency === 1, 'Unsupported locked concurrency.');
    const ledger = path.join(directory, 'ledger.jsonl'); C.ledger(ledger); let launched = 0;
    for (const job of initial.lock.jobs) {
      if (launched >= maxJobs) break;
      const attempt = path.join(directory, 'jobs', job.id, 'attempt-0001');
      if (fs.existsSync(path.dirname(attempt))) continue; // A durable/uncertain reservation is never an unstarted retry.
      const { lock, lockSha256 } = await verifyLock(directory); assert(lockSha256 === initial.lockSha256, 'Lock changed during coordinator execution.');
      fs.mkdirSync(path.dirname(attempt), { recursive: true }); fs.mkdirSync(attempt);
      const reservation = { schemaVersion: 1, kind: 'ai-experiment-reservation', lockSha256, jobId: job.id, attemptId: 'attempt-0001', reservationToken: crypto.randomBytes(32).toString('hex'), createdAtUtc: new Date().toISOString() };
      atomic(path.join(attempt, 'reservation.json'), reservation); append(ledger, { event: 'reserved', ...reservation });
      append(ledger, { event: 'launch-requested', jobId: job.id, reservationToken: reservation.reservationToken, atUtc: new Date().toISOString() });
      let processResult;
      try { processResult = await launch(lock, directory, job, attempt, reservation.reservationToken); }
      catch (error) { ownershipUncertain = true; throw error; }
      launched++;
      ownershipUncertain ||= processResult.retirementUnknown;
      let committed = null, failure = null;
      try { committed = await receipt(attempt, job, lockSha256); const captureSize = files(attempt).filter(file => !path.basename(file).startsWith('coordinator-')).reduce((sum, file) => sum + fs.statSync(file).size, 0); assert(captureSize <= lock.limits.captureBytes, 'Worker exceeded its locked evidence capture budget.'); assert(processResult.code === 0 && !processResult.timedOut && !processResult.captureOverflow && !processResult.ioFailure, 'Worker did not exit cleanly with its complete receipt.'); }
      catch (error) { failure = error.message; }
      ownershipUncertain ||= Boolean(processResult.pid && (!committed || committed.result.cleanupErrors?.length));
      const coordinator = { schemaVersion: 1, kind: 'ai-experiment-coordinator-result', jobId: job.id, lockSha256, process: processResult,
        receiptVerified: failure === null, failure, resultSha256: committed?.resultSha256 ?? null, artifactsSha256: committed?.artifactsSha256 ?? null };
      atomic(path.join(attempt, 'coordinator-result.json'), coordinator); append(ledger, { event: 'worker-retired', ...coordinator });
      if (failure || committed.result.criticalSafetyFailure || committed.result.status === 'evidence-failure' || committed.result.cleanupErrors?.length) break;
    }
    return { launched, ...await analyze(directory) };
  } finally { if (!ownershipUncertain) release(); }
}

function bounds(rows, startedOnly) {
  const selected = rows.filter(row => !startedOnly || row.started === true || row.started === null && row.attempted);
  const observed = selected.filter(row => row.score !== null), total = selected.length, O = observed.reduce((sum, row) => sum + row.score, 0), U = total - observed.length;
  return { denominator: total, observed: observed.length, unresolved: U, lower: total ? O / total : null, upper: total ? (O + U) / total : null,
    completedOnlyMean: observed.length ? O / observed.length : null, completedOnlyInterpretation: 'descriptive, potentially selected; never primary' };
}
function diff(a, b) { return a.denominator && b.denominator ? [b.lower - a.upper, b.upper - a.lower] : null; }
async function analyze(directory) {
  directory = path.resolve(directory); const { lock, lockSha256 } = await verifyLock(directory, { verifyArtifacts: false });
  assert(lock.coordinator?.scriptSha256 === sha(fs.readFileSync(__filename)) && lock.coordinator?.contractSha256 === sha(fs.readFileSync(require.resolve('./ai-experiment-contract.cjs'))), 'Use the locked analyzer code for this descriptive analysis.');
  const rows = [], invalid = []; let ledgerEvidence;
  try { const committed = C.ledger(path.join(directory, 'ledger.jsonl')); ledgerEvidence = { bytes: committed.bytes, records: committed.records, valid: true }; }
  catch (error) { ledgerEvidence = { valid: false, error: error.message }; }
  for (const job of lock.jobs) {
    const attempt = path.join(directory, 'jobs', job.id, 'attempt-0001');
    const row = { jobId: job.id, arm: job.arm, mode: job.mode, blockId: job.blockId, family: job.replay?.family ?? null,
      pairId: job.id.replace(/-(baseline|candidate)$/, ''), stratum: job.natural ? `deck-${job.natural.decks[job.natural.focalSlot].id}-seat-${job.natural.focalSlot}` : job.replay.id,
      attempted: fs.existsSync(path.dirname(attempt)), started: false, status: 'not-launched', score: null, grade: null, eligible: job.replay ? job.replay.expectation.kind !== 'ungraded' : null };
    if (row.attempted) {
      row.started = null; row.status = 'interrupted';
      if (!fs.existsSync(path.join(attempt, 'artifacts.json'))) row.interruption = 'No atomic committed worker receipt; start/cleanup remain unknown.';
      else try {
        const value = await receipt(attempt, job, lockSha256); const result = value.result;
        const processFile = path.join(attempt, 'coordinator-result.json');
        if (fs.existsSync(processFile)) { const processResult = read(processFile); assert(processResult.lockSha256 === lockSha256 && processResult.jobId === job.id && processResult.receiptVerified === true && processResult.resultSha256 === value.resultSha256 && processResult.artifactsSha256 === value.artifactsSha256, 'Coordinator did not verify this immutable worker receipt.'); }
        // A committed atomic worker receipt can be reconciled after a coordinator
        // crash without launching that job again.
        row.status = result.status;
        row.started = result.details?.gameStartUncertain === true
          || job.mode === 'natural' && result.started && result.details?.gameStartVerified === false ? null : result.started;
        row.criticalSafetyFailure = result.criticalSafetyFailure; row.cleanupFailure = Boolean(result.cleanupErrors?.length);
        if (job.mode === 'natural' && result.natural?.terminalVerified) { assert(result.natural.participants.some(item => item.slot === job.natural.focalSlot && item.playerId === result.natural.focalPlayerId), 'Focal assignment does not match its locked slot.'); row.score = score(result.natural); row.draw = result.natural.outcome.kind === 'draw'; row.soleWin = result.natural.outcome.kind === 'win' && result.natural.outcome.winningPlayerIds.length === 1 && result.natural.outcome.winningPlayerIds.includes(result.natural.focalPlayerId); }
        row.metrics = result.metrics ?? null;
        if (job.mode === 'replay') { row.incompatible = Boolean(result.details?.incompatible); row.evaluatorEligible = result.replay?.eligible ?? null; row.grade = result.replay?.grade ?? 'ungraded'; row.admissible = result.replay?.admissible ?? null;
          assert(result.replay?.eligible !== false || row.grade === 'ungraded', 'An ineligible worker grade cannot claim pass/fail.'); }
      } catch (error) {
        invalid.push({ jobId: job.id, error: error.message });
        Object.assign(row, { status: 'evidence-failure', started: null, score: null, grade: null, metrics: null, admissible: null, draw: false, soleWin: false, criticalSafetyFailure: false, cleanupFailure: false });
      }
    }
    rows.push(row);
  }
  const natural = rows.filter(row => row.mode === 'natural'), replay = rows.filter(row => row.mode === 'replay');
  const arms = Object.fromEntries(['baseline', 'candidate'].map(arm => { const own = natural.filter(row => row.arm === arm); return [arm, { scheduled: own.length, startedKnown: own.filter(row => row.started === true).length, startUnknown: own.filter(row => row.started === null).length,
    planned: bounds(own, false), startedOrPossible: bounds(own, true), verifiedDraws: own.filter(row => row.draw).length, soleWins: own.filter(row => row.soleWin).length,
    statuses: Object.fromEntries([...new Set(own.map(row => row.status))].sort().map(status => [status, own.filter(row => row.status === status).length])) }]; }));
  const pairs = [];
  for (const id of [...new Set(replay.map(row => row.pairId))]) {
    const a = replay.find(row => row.pairId === id && row.arm === 'baseline'), b = replay.find(row => row.pairId === id && row.arm === 'candidate');
    assert(a && b && a.eligible === b.eligible, 'Locked paired replay schedule is inconsistent.');
    pairs.push({ pairId: id, family: a.family, eligible: a.eligible, baseline: a.grade, candidate: b.grade,
      complete: ['pass', 'fail'].includes(a.grade) && ['pass', 'fail'].includes(b.grade),
      correction: a.grade === 'fail' && b.grade === 'pass', regression: a.grade === 'pass' && b.grade === 'fail' });
  }
  const eligible = pairs.filter(pair => pair.eligible), grades = {};
  for (const arm of ['baseline', 'candidate']) { const pass = eligible.filter(pair => pair[arm] === 'pass').length, fail = eligible.filter(pair => pair[arm] === 'fail').length; grades[arm] = { scheduledEligible: eligible.length, pass, fail, missingOrUngraded: eligible.length - pass - fail, acceptableLower: eligible.length ? pass / eligible.length : null, acceptableUpper: eligible.length ? (eligible.length - fail) / eligible.length : null }; }
  const families = [...new Set(eligible.map(pair => pair.family))].sort().map(family => {
    const members = eligible.filter(pair => pair.family === family), result = { family, scheduledPairs: members.length, corrections: members.filter(pair => pair.correction).length, regressions: members.filter(pair => pair.regression).length, incompletePairs: members.filter(pair => !pair.complete).length };
    for (const arm of ['baseline', 'candidate']) { const pass = members.filter(pair => pair[arm] === 'pass').length, fail = members.filter(pair => pair[arm] === 'fail').length; result[arm] = { pass, fail, missingOrUngraded: members.length - pass - fail, acceptableLower: pass / members.length, acceptableUpper: (members.length - fail) / members.length }; }
    return result;
  });
  const macro = {};
  for (const arm of ['baseline', 'candidate']) macro[arm] = { families: families.length, acceptableLower: families.length ? families.reduce((sum, family) => sum + family[arm].acceptableLower, 0) / families.length : null, acceptableUpper: families.length ? families.reduce((sum, family) => sum + family[arm].acceptableUpper, 0) / families.length : null };
  function measurement(own, name, scope) {
    const samples = [], missingJobs = [];
    for (const row of own) { const value = row.metrics?.[name]; const values = Array.isArray(value) ? value : typeof value === 'number' ? [value] : null;
      if (!values || !values.length) { missingJobs.push(row.jobId); continue; }
      assert(values.length <= 16384 && values.every(item => typeof item === 'number' && Number.isFinite(item) && item >= 0), 'Invalid bounded cost samples.'); samples.push(...values); }
    samples.sort((a, b) => a - b); const quantile = p => samples.length ? samples[Math.max(0, Math.ceil(p * samples.length) - 1)] : null;
    return { scope, samples: samples.length, jobsWithSamples: own.length - missingJobs.length, missingJobs, p50: quantile(.5), p95: quantile(.95), p99: quantile(.99) };
  }
  const cost = Object.fromEntries(['baseline', 'candidate'].map(arm => { const own = rows.filter(row => row.arm === arm); return [arm, {
    replayEvaluationWallMs: measurement(own.filter(row => row.mode === 'replay'), 'evaluationWallMs', 'Current replay session/validation plus policy stopwatch wall time; not CPU.'),
    replayEpisodeElapsedMs: measurement(own.filter(row => row.mode === 'replay'), 'episodeElapsedMs', 'Single replay session cumulative episode elapsed, one sample per job; never summed across refresh records.'),
    replaySpentWorkUnits: measurement(own.filter(row => row.mode === 'replay'), 'spentWorkUnits', 'Exact granted minus remaining replay work; missing remains unknown.'),
    naturalPolicyEvaluationWallMs: measurement(own.filter(row => row.mode === 'natural'), 'policyEvaluationMsSamples', 'Measured inner-policy wall spans, exactly once per evaluation; audience reported by worker metrics.policyMeasurementAudience.'),
    naturalEpisodePolicyWallMs: measurement(own.filter(row => row.mode === 'natural'), 'episodePolicyWallMsSamples', 'Inner-policy evaluation wall spans grouped once per episode; not AiDecisionRecord.Elapsed or CPU.'),
    naturalWorkConsumedUnits: measurement(own.filter(row => row.mode === 'natural'), 'workConsumedSamples', 'Measured policy work consumption samples; omitted samples unavailable.'),
    naturalGameWallMs: measurement(own.filter(row => row.mode === 'natural'), 'elapsedMs', 'Natural game worker wall duration, includes workload/path differences.')
  }]; }));
  const report = { schemaVersion: 1, kind: 'ai-experiment-report', experimentId: lock.id, lockSha256, hypothesis: lock.hypothesis, primaryEndpoint: lock.primaryEndpoint,
    interpretation: invalid.length || !ledgerEvidence.valid ? 'experiment-invalid' : rows.some(row => row.criticalSafetyFailure || row.cleanupFailure) ? 'guardrail-failed'
      : rows.some(row => !row.attempted || row.status !== 'completed') || eligible.some(pair => !pair.complete) || natural.some(row => row.score === null) ? 'inconclusive' : 'descriptive-only',
    confidenceIntervals: null, strengthClaim: false, humanReview: 'pending', analysisMethod: lock.analysis, ledger: ledgerEvidence,
    reliability: { criticalSafetyFailures: rows.filter(row => row.criticalSafetyFailure).map(row => row.jobId), cleanupFailures: rows.filter(row => row.cleanupFailure).map(row => row.jobId), policyFailures: rows.filter(row => row.status === 'policy-failure').map(row => row.jobId), interruptedAttempts: rows.filter(row => row.attempted && row.status === 'interrupted').map(row => row.jobId) },
    natural: { arms, plannedDifferenceBounds: diff(arms.baseline.planned, arms.candidate.planned), startedOrPossibleDifferenceBounds: diff(arms.baseline.startedOrPossible, arms.candidate.startedOrPossible),
      startedDenominatorMeaning: 'known started plus reserved attempts whose start is unknown; show startUnknown separately',
      strata: [...new Set(natural.map(row => row.stratum))].sort().map(stratum => ({ stratum, baseline: bounds(natural.filter(row => row.arm === 'baseline' && row.stratum === stratum), false), candidate: bounds(natural.filter(row => row.arm === 'candidate' && row.stratum === stratum), false) })) },
    replay: { pairs, arms: grades, families, macroFamily: macro, corrections: eligible.filter(pair => pair.correction).length, regressions: eligible.filter(pair => pair.regression).length, incompletePairs: eligible.filter(pair => !pair.complete).length,
      differenceBounds: families.length ? [macro.candidate.acceptableLower - macro.baseline.acceptableUpper, macro.candidate.acceptableUpper - macro.baseline.acceptableLower] : null,
      rawCaseDifferenceBounds: eligible.length ? [grades.candidate.acceptableLower - grades.baseline.acceptableUpper, grades.candidate.acceptableUpper - grades.baseline.acceptableLower] : null,
      interpretation: 'same locked common eligible cases; primary weights declared families equally; no missing/ungraded arm result dropped; finite corpus finding, not on-policy game value' }, cost, rows, invalid,
    limitations: ['No confirmation-grade intervals in V1.', 'Natural rules randomness uncontrolled; policy seed does not pair shuffles.', 'First reservations only; diagnostic replacement attempts never overwrite primary data.', 'Private development evidence; no release permission or public aggregate.'] };
  const reportBytes = JSON.stringify(report, null, 2) + '\n', analysisId = sha(reportBytes).slice(0, 24), output = path.join(directory, 'analysis', analysisId);
  fs.mkdirSync(output, { recursive: true });
  const reportFile = path.join(output, 'summary.json'); if (fs.existsSync(reportFile)) assert(fs.readFileSync(reportFile, 'utf8') === reportBytes, 'Analysis identity collision.'); else atomic(reportFile, reportBytes);
  const markdown = `# AI experiment ${lock.id}\n\n${lock.hypothesis}\n\nResult: **${report.interpretation}**. No strength-superiority or release claim.\n\n- Guardrails: ${report.reliability.criticalSafetyFailures.length} critical safety failures; ${report.reliability.cleanupFailures.length} cleanup failures; ${report.reliability.policyFailures.length} policy failures\n- Primary: ${lock.primaryEndpoint}\n- Jobs: ${rows.length}; reserved: ${rows.filter(row => row.attempted).length}; invalid evidence: ${invalid.length}\n- Replay corrections/regressions: ${report.replay.corrections}/${report.replay.regressions}; incomplete eligible pairs: ${report.replay.incompletePairs}\n- Natural planned difference bounds: ${JSON.stringify(report.natural.plannedDifferenceBounds)}\n\nSee summary.json for every planned denominator, attempt, stratum, unsupported grade and missing-outcome bound. Confidence intervals are unavailable in V1. Detailed evidence is host-private.\n`;
  if (!fs.existsSync(path.join(output, 'report.md'))) atomic(path.join(output, 'report.md'), markdown);
  return { analysisId, reportPath: reportFile, report };
}
async function main(args) {
  const [operation, target, ...rest] = args; assert(target && ['plan', 'run', 'analyze'].includes(operation), 'Use plan SPEC --profile OUT | run LOCKEDDIR --jobs 1 | analyze LOCKEDDIR.');
  if (operation === 'plan') { assert(rest.length === 2 && rest[0] === '--profile', 'Planning requires --profile fresh-directory.'); const lock = await plan(target, rest[1]); console.log(JSON.stringify({ planned: lock.jobs.length, directory: path.resolve(rest[1]), maximumCaptureBytes: lock.estimate.maximumAttemptCaptureBytes })); }
  if (operation === 'run') {
    const options = new Map(); assert(rest.length % 2 === 0, 'Run options need explicit values.');
    for (let index = 0; index < rest.length; index += 2) { assert(['--jobs', '--max-jobs'].includes(rest[index]) && !options.has(rest[index]) && /^[1-9][0-9]*$/.test(rest[index + 1]), 'Unknown, duplicate or malformed run option.'); options.set(rest[index], Number(rest[index + 1])); }
    const value = await run(target, options.get('--jobs') ?? 1, options.get('--max-jobs') ?? 10000); console.log(JSON.stringify({ launched: value.launched, analysisId: value.analysisId, reportPath: value.reportPath }));
  }
  if (operation === 'analyze') { assert(rest.length === 0, 'Unexpected analyze arguments.'); const value = await analyze(target); console.log(JSON.stringify({ analysisId: value.analysisId, reportPath: value.reportPath })); }
}
if (require.main === module) main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
module.exports = { plan, run, analyze, validateResult, score, receipt, lease, launch, main };
