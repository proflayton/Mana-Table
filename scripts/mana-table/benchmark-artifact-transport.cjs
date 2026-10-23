const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

// Manual dispatch only. This fixed, already-published package is representative
// transport input; it is not evidence for a new app build or public release.
const PAYLOAD = Object.freeze({ tag: 'mana-native-v0.2.89', file: 'ManaTable-Windows-x64.zip', bytes: 140612464,
  sha256: 'e31018482b6a7d1bad125dfedef6eb9f91ea5a504a25b2dc06649c4d44f7c503' });
const UPLOAD_ORDER = ['wrapped-a', 'raw-a', 'raw-b', 'wrapped-b'];
const DOWNLOAD_ORDER = ['raw-a', 'wrapped-a', 'wrapped-b', 'raw-b'];
function samplePlan(attempt) {
  return UPLOAD_ORDER.map(key => ({ key, mode: key.startsWith('raw-') ? 'raw' : 'wrapped',
    label: key.replace('-', ' ').replace(/\b\w/g, value => value.toUpperCase()),
    artifact: `benchmark-${key}-${attempt}${key.startsWith('raw-') ? '.zip' : ''}`, file: `benchmark-${key}-${attempt}.zip` }));
}
function required(condition, message) { if (!condition) throw new Error(message); }
async function fingerprint(file) {
  const stat = fs.lstatSync(file);
  required(stat.isFile() && stat.size > 0, `Expected a nonempty regular file: ${file}`);
  const hash = crypto.createHash('sha256');
  for await (const bytes of fs.createReadStream(file)) hash.update(bytes);
  return { bytes: stat.size, sha256: hash.digest('hex') };
}
function read(file) { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); }
function write(file, value) { fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n'); }
async function prepare(source, checksum, directory, environment = process.env, payload = PAYLOAD) {
  required(environment.GITHUB_EVENT_NAME === 'workflow_dispatch', 'Transport benchmarking requires a manual dispatch.');
  required(/^\d+$/.test(environment.GITHUB_RUN_ID || '') && /^\d+$/.test(environment.GITHUB_RUN_ATTEMPT || '')
    && /^[a-f0-9]{40}$/.test(environment.GITHUB_SHA || ''), 'Missing exact workflow run/attempt/source identity.');
  const actual = await fingerprint(source);
  const declared = fs.readFileSync(checksum, 'utf8').trim().match(/^([a-fA-F0-9]{64})\s+\*?([^\r\n]+)$/);
  required(path.basename(source) === payload.file && declared && declared[2] === payload.file
    && declared[1].toLowerCase() === payload.sha256 && actual.sha256 === payload.sha256 && actual.bytes === payload.bytes,
  'The benchmark input differs from the pinned public release and its checksum.');
  fs.mkdirSync(directory, { recursive: true });
  required(!fs.existsSync(path.join(directory, 'manifest.json')), 'Use a fresh benchmark directory.');
  const attempt = environment.GITHUB_RUN_ATTEMPT;
  const samples = samplePlan(attempt);
  fs.mkdirSync(path.join(directory, 'uploads'), { recursive: true });
  for (const sample of samples)
    fs.copyFileSync(source, path.join(directory, 'uploads', sample.file));
  const manifest = { schema: 1, payload, runId: environment.GITHUB_RUN_ID, attempt,
    sourceRevision: environment.GITHUB_SHA, repository: environment.GITHUB_REPOSITORY,
    runner: environment.RUNNER_NAME || null, runnerOS: environment.RUNNER_OS || null,
    uploadOrder: UPLOAD_ORDER, downloadOrder: DOWNLOAD_ORDER, samples };
  write(path.join(directory, 'manifest.json'), manifest);
  return manifest;
}
function exactlyOne(values, label) { required(values.length === 1, `Expected exactly one ${label}.`); return values[0]; }
function duration(job, name) {
  const step = exactlyOne(job.steps.filter(step => step.name === name), `step named ${name}`);
  const start = Date.parse(step.started_at), end = Date.parse(step.completed_at);
  required(step.status === 'completed' && step.conclusion === 'success' && Number.isFinite(start)
    && Number.isFinite(end) && end >= start, `No successful completed timing for ${name}.`);
  return (end - start) / 1000;
}
async function summarize(directory, jobs, artifacts, payload = PAYLOAD) {
  const manifest = read(path.join(directory, 'manifest.json'));
  required(manifest.schema === 1 && jobs.total_count === jobs.jobs.length && artifacts.total_count === artifacts.artifacts.length,
    'Incomplete benchmark manifest or API inventory; retrieve every page.');
  required(['tag', 'file', 'bytes', 'sha256'].every(key => manifest.payload[key] === payload[key])
    && JSON.stringify(manifest.samples) === JSON.stringify(samplePlan(manifest.attempt))
    && JSON.stringify(manifest.uploadOrder) === JSON.stringify(UPLOAD_ORDER)
    && JSON.stringify(manifest.downloadOrder) === JSON.stringify(DOWNLOAD_ORDER), 'Benchmark manifest differs from its pinned payload and four-sample plan.');
  const job = exactlyOne(jobs.jobs.filter(job => job.name === 'Benchmark artifact transport'), 'benchmark job');
  required(String(job.run_id) === manifest.runId && job.head_sha === manifest.sourceRevision
    && String(job.run_attempt) === manifest.attempt, 'Job timings belong to another source, run or attempt.');
  for (const [operation, order] of [['upload', UPLOAD_ORDER], ['download', DOWNLOAD_ORDER]]) {
    const steps = job.steps.filter(step => step.name.startsWith(`Benchmark ${operation} `));
    required(steps.every(step => Number.isSafeInteger(step.number))
      && new Set(steps.map(step => step.number)).size === steps.length
      && JSON.stringify(steps.sort((a, b) => a.number - b.number).map(step => step.name))
        === JSON.stringify(order.map(key => `Benchmark ${operation} ${manifest.samples.find(sample => sample.key === key).label}`)),
    `Observed ${operation} step order differs from the balanced benchmark plan.`);
  }
  const samples = [];
  for (const sample of manifest.samples) {
    const folder = path.join(directory, 'downloads', sample.key);
    const entries = fs.readdirSync(folder, { withFileTypes: true });
    required(entries.length === 1 && entries[0].isFile() && entries[0].name === sample.file,
      `Unexpected downloaded inventory for ${sample.key}; require the exact single ZIP basename.`);
    const actual = await fingerprint(path.join(folder, sample.file));
    required(actual.bytes === manifest.payload.bytes && actual.sha256 === manifest.payload.sha256,
      `Downloaded bytes differ for ${sample.key}.`);
    const artifact = exactlyOne(artifacts.artifacts.filter(artifact => artifact.name === sample.artifact), `artifact ${sample.artifact}`);
    required(!artifact.expired && Number.isSafeInteger(artifact.size_in_bytes) && artifact.size_in_bytes > 0,
      `Invalid artifact metadata for ${sample.key}.`);
    if (sample.mode === 'raw') required(artifact.size_in_bytes === actual.bytes, `Raw transport was wrapped for ${sample.key}.`);
    samples.push({ ...sample, ...actual, artifactId: artifact.id, artifactBytes: artifact.size_in_bytes,
      uploadSeconds: duration(job, `Benchmark upload ${sample.label}`),
      downloadSeconds: duration(job, `Benchmark download ${sample.label}`) });
  }
  const means = ['wrapped', 'raw'].map(mode => {
    const group = samples.filter(sample => sample.mode === mode);
    return { mode, samples: group.length, uploadSeconds: group.reduce((sum, sample) => sum + sample.uploadSeconds, 0) / group.length,
      downloadSeconds: group.reduce((sum, sample) => sum + sample.downloadSeconds, 0) / group.length };
  });
  const result = { ...manifest, verified: true, jobId: job.id, samples, observedMeans: means,
    timingScope: 'Completed Actions step elapsed seconds, including action startup/metadata/logging and any retries. Hash verification, initial release download, job queueing and checkout are excluded.',
    limitations: 'Two samples per mode in balanced sequential order on one runner. No cold-cache or statistically representative speed claim; network/cache/order effects and second-resolution API timings remain. This is not application build, release, or end-to-end CI duration.' };
  write(path.join(directory, 'results.json'), result);
  return result;
}
if (require.main === module) {
  const [mode, ...args] = process.argv.slice(2);
  const operation = mode === 'prepare' && args.length === 3 ? prepare(...args)
    : mode === 'summarize' && args.length === 3 ? summarize(args[0], read(args[1]), read(args[2]))
      : Promise.reject(new Error('Usage: benchmark-artifact-transport.cjs prepare source.zip source.zip.sha256 directory | summarize directory jobs.json artifacts.json'));
  operation.then(result => {
    console.log(JSON.stringify(result, null, 2));
    if (mode === 'summarize' && process.env.GITHUB_STEP_SUMMARY) {
      const rows = result.samples.map(sample => `| ${sample.label} | ${sample.uploadSeconds} | ${sample.downloadSeconds} | ${sample.artifactBytes} |`);
      fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\nTransport benchmark: ${result.payload.tag}, ${result.payload.bytes} bytes, SHA-256 ${result.payload.sha256}.\n\n| Sample | Upload seconds | Download seconds | Artifact bytes |\n| --- | ---: | ---: | ---: |\n${rows.join('\n')}\n\n${result.timingScope}\n\n${result.limitations}\n`);
    }
  }).catch(error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { PAYLOAD, prepare, summarize };
