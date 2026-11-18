'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const MAX_JSON = 2 * 1024 * 1024, MAX_LOCK = 64 * 1024 * 1024;
const STATUSES = ['completed', 'setup-failure', 'policy-failure', 'provider-failure', 'evidence-failure', 'time-cutoff', 'turn-cutoff', 'interrupted'];
function fail(message) { throw new Error(message); }
function assert(value, message) { if (!value) fail(message); }
function object(value, label) { assert(value && typeof value === 'object' && !Array.isArray(value), label + ' must be an object.'); }
function keys(value, required, optional = [], label = 'record') {
  object(value, label); assert(required.every(key => Object.hasOwn(value, key)), label + ' is missing required properties.');
  assert(Object.keys(value).every(key => [...required, ...optional].includes(key)), label + ' contains unsupported properties.');
}
function integer(value, min, max, label) { assert(Number.isSafeInteger(value) && value >= min && value <= max, label + ' is outside its finite integer range.'); }
function identifier(value) { assert(typeof value === 'string' && /^[a-z0-9][a-z0-9_-]{0,63}$/.test(value), 'Invalid logical identifier.'); return value; }
function text(value, max, label) { assert(typeof value === 'string' && value.trim().length && value.length <= max, 'Invalid ' + label + '.'); }
function seed(value) { assert(typeof value === 'string' && /^-?(0|[1-9][0-9]*)$/.test(value) && value !== '-0', 'Seeds must be canonical signed decimal strings.'); const n = BigInt(value); assert(n >= -(1n << 63n) && n < (1n << 63n), 'Seed outside signed 64-bit range.'); return value; }
function sha(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex'); }
function canonical(value) { return JSON.stringify(value, (key, item) => item && typeof item === 'object' && !Array.isArray(item) ? Object.fromEntries(Object.keys(item).sort().map(name => [name, item[name]])) : item); }

// Small strict JSON parser: reject duplicate/unsafe numeric values before a
// normal JavaScript object can silently erase or round them.
function parse(bytes, max = MAX_JSON) {
  assert(bytes.length <= max, 'JSON exceeds the bounded input limit.');
  const input = new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\uFEFF/, '');
  let at = 0, nodes = 0;
  const whitespace = () => { while (/[\x20\t\r\n]/.test(input[at] || '\0')) at++; };
  function string() {
    const start = at++; let escaped = false;
    while (at < input.length) { const c = input[at++]; if (!escaped && c === '"') return JSON.parse(input.slice(start, at)); if (!escaped && c === '\\') escaped = true; else escaped = false; }
    fail('Unterminated JSON string.');
  }
  function value(depth) {
    assert(depth <= 64 && ++nodes <= 3000000, 'JSON complexity exceeds its bound.'); whitespace();
    if (input[at] === '"') return string();
    if (input[at] === '{') {
      at++; whitespace(); const result = Object.create(null), seen = new Set(); if (input[at] === '}') { at++; return result; }
      while (true) { whitespace(); assert(input[at] === '"', 'Expected JSON property.'); const key = string(); assert(!seen.has(key), 'Duplicate JSON property: ' + key); seen.add(key); whitespace(); assert(input[at++] === ':', 'Expected JSON colon.'); result[key] = value(depth + 1); whitespace(); const end = input[at++]; if (end === '}') return result; assert(end === ',', 'Expected JSON property separator.'); }
    }
    if (input[at] === '[') { at++; whitespace(); const result = []; if (input[at] === ']') { at++; return result; } while (true) { result.push(value(depth + 1)); whitespace(); const end = input[at++]; if (end === ']') return result; assert(end === ',', 'Expected JSON array separator.'); } }
    const token = /^(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/.exec(input.slice(at));
    assert(token, 'Invalid JSON token.'); at += token[0].length; const result = JSON.parse(token[0]);
    if (typeof result === 'number') assert(Number.isFinite(result) && (!Number.isInteger(result) || Number.isSafeInteger(result)), 'Unsafe or nonfinite JSON number; use a decimal seed string.'); return result;
  }
  const result = value(0); whitespace(); assert(at === input.length, 'Trailing JSON data.'); return result;
}
function read(file, max = MAX_JSON) { const stat = fs.lstatSync(file); assert(stat.isFile() && !stat.isSymbolicLink() && stat.size <= max, 'Expected bounded regular JSON file.'); return parse(fs.readFileSync(file), max); }
function regular(file) { const stat = fs.lstatSync(file); assert(stat.isFile() && !stat.isSymbolicLink(), 'Artifacts must be regular files: ' + file); return stat; }
async function identity(file) { const stat = regular(file); const hash = crypto.createHash('sha256'); for await (const chunk of fs.createReadStream(file)) hash.update(chunk); const after = regular(file); assert(after.size === stat.size && after.mtimeMs === stat.mtimeMs, 'Artifact changed during hashing.'); return { path: path.resolve(file), bytes: stat.size, sha256: hash.digest('hex') }; }
function files(root) {
  const out = []; const walk = directory => { const stat = fs.lstatSync(directory); assert(stat.isDirectory() && !stat.isSymbolicLink(), 'Artifact directories cannot be symbolic links.');
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) { const full = path.join(directory, entry.name); assert(!entry.isSymbolicLink(), 'Artifact links are not supported.'); if (entry.isDirectory()) walk(full); else { regular(full); out.push(path.resolve(full)); assert(out.length <= 250000, 'Artifact file count exceeded.'); } }
  }; walk(root); return out;
}
function within(root, file) { const relative = path.relative(root, file); return relative !== '' && !relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative); }
function variant(value) { keys(value, ['factoryId', 'attachmentEvaluation']); assert(value.factoryId === 'casual-commander.v6' && typeof value.attachmentEvaluation === 'boolean', 'Unknown experiment policy variant.'); }
function validateSpec(spec) {
  keys(spec, ['schemaVersion', 'kind', 'id', 'hypothesis', 'mode', 'primaryEndpoint', 'variants', 'limits', 'runtime', 'planningSeed', 'policySeed', 'repetitions'], ['replays', 'decks']);
  assert(spec.schemaVersion === 1 && spec.kind === 'ai-experiment-spec', 'Unsupported experiment spec version.'); identifier(spec.id); text(spec.hypothesis, 4096, 'hypothesis');
  assert(['replay', 'natural'].includes(spec.mode), 'Unknown experiment mode.'); assert(spec.primaryEndpoint === (spec.mode === 'replay' ? 'acceptable-decision-rate' : 'focal-win-share'), 'Primary endpoint incompatible with mode.');
  keys(spec.variants, ['baseline', 'candidate', 'reference']); Object.values(spec.variants).forEach(variant);
  keys(spec.limits, ['decisionMs', 'workUnits', 'matchMs', 'turns', 'workerMs', 'captureBytes']);
  for (const [name, min, max] of [['decisionMs', 1, 60000], ['workUnits', 1, 1000000], ['matchMs', 1, 3600000], ['turns', 1, 10000], ['workerMs', 100, 7200000], ['captureBytes', 1048576, 1073741824]]) integer(spec.limits[name], min, max, name);
  assert(spec.limits.workerMs >= spec.limits.matchMs + 30000, 'Worker deadline must include 30 seconds beyond the match envelope.');
  keys(spec.runtime, ['driverPath', 'dotnetPath'], ['sourceRevision', 'engineJarPath', 'resourcesPath', 'javaPath']);
  for (const [name, item] of Object.entries(spec.runtime)) if (name === 'sourceRevision') assert(/^[a-f0-9]{40}$/.test(item), 'Expected full source revision.'); else text(item, 4096, name);
  seed(spec.planningSeed); seed(spec.policySeed); integer(spec.repetitions, 1, 100, 'repetitions');
  if (spec.mode === 'replay') {
    assert(!Object.hasOwn(spec, 'decks') && Array.isArray(spec.replays) && spec.replays.length > 0 && spec.replays.length <= 1000, 'Replay experiment needs a bounded corpus.');
    const ids = new Set(); for (const item of spec.replays) { keys(item, ['id', 'path', 'format', 'expectation', 'family', 'certifiedUndo'], ['capabilityContext']); identifier(item.id); assert(!ids.has(item.id), 'Duplicate replay id.'); ids.add(item.id); text(item.path, 4096, 'replay path'); text(item.family, 128, 'replay family'); assert(['replay', 'owned'].includes(item.format), 'Unknown replay format.'); keys(item.expectation, ['kind']); assert(['target-self', 'target-opponent', 'cancel', 'complete', 'admissible', 'ungraded'].includes(item.expectation.kind), 'Unknown independent expectation.'); assert(typeof item.certifiedUndo === 'boolean', 'certifiedUndo must be explicit.'); if (item.certifiedUndo) text(item.capabilityContext, 4096, 'capability context'); else if (item.capabilityContext !== undefined) text(item.capabilityContext, 4096, 'capability context'); }
  } else {
    assert(!Object.hasOwn(spec, 'replays') && Array.isArray(spec.decks) && spec.decks.length === 4, 'Natural experiment needs four pinned decks.');
    const ids = new Set(); for (const item of spec.decks) { keys(item, ['id', 'path']); identifier(item.id); text(item.path, 4096, 'deck path'); assert(!ids.has(item.id), 'Duplicate deck id.'); ids.add(item.id); }
    assert(spec.runtime.engineJarPath && spec.runtime.resourcesPath, 'Natural games require engine and resources.');
  }
  assert(spec.repetitions * (spec.mode === 'natural' ? 32 : spec.replays.length * 2) <= 10000, 'Schedule exceeds 10000 jobs.');
  return spec;
}
function derive(base, label) { const hash = crypto.createHash('sha256').update('ai-experiment-seed/1\0' + seed(base) + '\0' + label).digest(); return hash.readBigInt64BE().toString(); }
function schedule(spec, inputs) {
  const jobs = [];
  for (let repeat = 0; repeat < spec.repetitions; repeat++) {
    const blockId = 'block-' + String(repeat).padStart(4, '0');
    const cells = spec.mode === 'replay' ? inputs.map(item => ({ key: item.id, replay: item })) : Array.from({ length: 16 }, (_, n) => { const focalDeck = Math.floor(n / 4), focalSlot = n % 4, rotation = (focalDeck - focalSlot + 4) % 4; return { key: `deck-${focalDeck}-seat-${focalSlot}`, natural: { focalSlot, decks: inputs.map((_, j) => inputs[(j + rotation) % 4]) } }; });
    const ordered = cells.sort((a, b) => derive(spec.planningSeed, blockId + '/' + a.key).localeCompare(derive(spec.planningSeed, blockId + '/' + b.key), 'en'));
    for (const cell of ordered) {
      const first = BigInt(derive(spec.planningSeed, blockId + '/' + cell.key + '/arm')) & 1n ? ['baseline', 'candidate'] : ['candidate', 'baseline'];
      for (const arm of first) jobs.push({ id: `${blockId}-${cell.key}-${arm}`, mode: spec.mode, arm, blockId, seed: derive(spec.policySeed, blockId + '/' + cell.key), ...(cell.replay ? { replay: cell.replay } : { natural: cell.natural }) });
    }
  }
  return jobs;
}
async function verifyLock(directory, { verifyArtifacts = true } = {}) {
  directory = path.resolve(directory); const lockPath = path.join(directory, 'lock.json'); assert(regular(lockPath).size <= MAX_LOCK, 'Lock exceeds its bound.'); const bytes = fs.readFileSync(lockPath); const lock = parse(bytes, MAX_LOCK), lockSha256 = sha(bytes);
  assert(regular(path.join(directory, 'lock.sha256')).size <= 128, 'Invalid lock digest sidecar.');
  assert(fs.readFileSync(path.join(directory, 'lock.sha256'), 'utf8').trim() === lockSha256, 'Lock hash mismatch.');
  assert(lock.schemaVersion === 1 && lock.kind === 'ai-experiment-lock', 'Unsupported experiment lock.');
  const spec = validateSpec(read(path.join(directory, 'spec.json')));
  for (const name of ['id', 'hypothesis', 'mode', 'primaryEndpoint', 'variants', 'limits']) assert(canonical(lock[name]) === canonical(spec[name]), 'Lock disagrees with its original spec: ' + name);
  assert(path.isAbsolute(lock.sourceDirectory) && lock.concurrency === 1 && lock.seeds?.planning === spec.planningSeed && lock.seeds?.policy === spec.policySeed, 'Invalid locked execution settings.');
  assert(Array.isArray(lock.artifacts) && lock.artifacts.length > 0 && lock.artifacts.length <= 250000 && Array.isArray(lock.jobs) && lock.jobs.length > 0 && lock.jobs.length <= 10000, 'Invalid bounded lock inventory.');
  const paths = new Set(); for (const artifact of lock.artifacts) { assert(path.isAbsolute(artifact.path) && !paths.has(artifact.path) && Number.isSafeInteger(artifact.bytes) && artifact.bytes >= 0 && /^[a-f0-9]{64}$/.test(artifact.sha256), 'Duplicate, relative or malformed artifact identity.'); paths.add(artifact.path); if (verifyArtifacts) { const actual = await identity(artifact.path); assert(actual.sha256 === artifact.sha256 && actual.bytes === artifact.bytes, 'Locked artifact changed: ' + artifact.path); } }
  assert(Array.isArray(lock.closureRoots) && lock.closureRoots.includes(path.dirname(lock.runtime.driverPath)) && (!lock.runtime.resourcesPath || lock.closureRoots.includes(lock.runtime.resourcesPath)), 'Runtime dependency closure is incomplete.');
  for (const required of [lock.runtime.driverPath, lock.runtime.dotnetPath, ...(lock.runtime.engineJarPath ? [lock.runtime.engineJarPath] : []), ...(lock.runtime.javaPath ? [lock.runtime.javaPath] : [])]) assert(paths.has(required), 'Runtime executable/artifact is not pinned.');
  assert(paths.has(lock.environment?.nodePath), 'Coordinator Node executable is not pinned.');
  for (const root of lock.closureRoots) { assert(path.isAbsolute(root), 'Closure roots must be absolute.'); if (verifyArtifacts) { const expected = lock.artifacts.filter(item => within(root, item.path)).map(item => item.path).sort(); assert(canonical(files(root).sort()) === canonical(expected), 'Artifact closure file set changed: ' + root); } }
  assert(sha(fs.readFileSync(path.join(directory, 'spec.json'))) === lock.specSha256, 'Original spec bytes changed.');
  assert(sha(fs.readFileSync(path.join(directory, 'schedule.jsonl'))) === lock.scheduleSha256, 'Schedule bytes changed.');
  const inputs = (spec.mode === 'replay' ? spec.replays : spec.decks).map(item => { const resolved = path.resolve(lock.sourceDirectory, item.path), artifact = lock.artifacts.find(row => row.path === resolved); assert(artifact, 'Declared input is absent from the pinned artifact closure.'); return { ...item, path: resolved, sha256: artifact.sha256 }; });
  assert(canonical(schedule(spec, inputs)) === canonical(lock.jobs), 'Lock schedule differs from its declared design.');
  const declaredLines = fs.readFileSync(path.join(directory, 'schedule.jsonl'), 'utf8').trim().split('\n').map(line => parse(Buffer.from(line)));
  assert(canonical(declaredLines) === canonical(lock.jobs), 'Scheduled ledger cells disagree with lock.');
  return { lock, lockSha256, directory };
}
function atomic(file, value) { const bytes = typeof value === 'string' ? value : JSON.stringify(value, null, 2) + '\n'; const temporary = file + '.' + crypto.randomBytes(8).toString('hex') + '.tmp'; const fd = fs.openSync(temporary, 'wx'); try { fs.writeFileSync(fd, bytes); fs.fsyncSync(fd); } finally { fs.closeSync(fd); } assert(!fs.existsSync(file), 'Immutable receipt already exists: ' + file); fs.renameSync(temporary, file); }
function append(file, value) { const fd = fs.openSync(file, 'a'), bytes = Buffer.from(JSON.stringify(value) + '\n'); try { let offset = 0; while (offset < bytes.length) { const written = fs.writeSync(fd, bytes, offset, bytes.length - offset); assert(written > 0, 'Ledger append made no write progress.'); offset += written; } fs.fsyncSync(fd); } finally { fs.closeSync(fd); } }
function ledger(file) {
  assert(regular(file).size <= MAX_LOCK, 'Ledger exceeds its 64 MiB bound.'); const raw = fs.readFileSync(file);
  assert(raw.length === 0 || raw[raw.length - 1] === 10, 'Ledger has an incomplete tail; explicit reconciliation is required before resume.');
  const lines = raw.toString('utf8').split('\n').filter(Boolean); const records = lines.map(line => parse(Buffer.from(line)));
  return { bytes: raw.length, records: records.length, events: records };
}
module.exports = { MAX_JSON, MAX_LOCK, STATUSES, assert, keys, integer, identifier, seed, sha, canonical, parse, read, identity, files, within, validateSpec, schedule, derive, verifyLock, atomic, append, ledger };
